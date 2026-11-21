# Copyright (C) 2026 youke1686 (https://github.com/youke1686)
#
# This file is part of FlyDisk.
#
# FlyDisk is free software: you can redistribute it and/or modify
# it under the terms of the GNU General Public License as published by
# the Free Software Foundation, either version 3 of the License, or
# (at your option) any later version.
#
# FlyDisk is distributed in the hope that it will be useful,
# but WITHOUT ANY WARRANTY; without even the implied warranty of
# MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
# GNU General Public License for more details.
#
# You should have received a copy of the GNU General Public License
# along with FlyDisk.  If not, see <https://www.gnu.org/licenses/>.

r"""smoke_dual.py — 双盘同时多线程读写压测（跨进程黑盒）

定位：只验证一件事——两块被加速的盘**同时**受压时，读到的内容是否仍然正确。
不控制 FlyDisk.exe、不改配置；测试文件大小在运行时由 CLI 交互选择。

流程：
  提示两块盘都打开加速 → 输入 A / B 盘符 → 输入每块盘的测试文件大小
  → 两块盘各写一个随机内容文件并建内存哈希表
  → 默认 8 个线程同时压两块盘（每轮对 A、B 各做一次随机读或写），只限总时长
  → 按盘分别打印读 / 写次数与延迟 → 可选「停止加速后整文件回读校验」

产出（与 smoke_stress.py 同格式）：
  docs/测试报告/smoke-dual-<时间戳>.md      报告，头带 Body-SHA256
  docs/测试报告/ops-<时间戳>-a.bin / -b.bin  两块盘各一份操作记录（永不删除）

用法（管理员 PowerShell / cmd）：
  python smoke_dual.py
  python smoke_dual.py --seconds 120 --threads 12

退出码：0 通过 / 1 失败（成功才删测试文件，失败保留供复查）。
"""

import argparse
import ctypes
import hashlib
import os
import random
import struct
import sys
import threading
import time

import smoke_stress as ss

DIR_NAME = "FlyDisk.SmokeDual"
FILE_NAME = "test.bin"
DEFAULT_SIZE = 512 << 20
LAT_CAP = 200_000         # 每个线程每块盘最多记账的延迟样本数


# ======================= 选盘与前置检查 =======================

def _choose_index(volumes, hint):
    """在给定卷列表里按序号选一个（列表由调用方先打印）。"""
    while True:
        try:
            text = input(hint).strip()
        except EOFError:
            raise KeyboardInterrupt
        if not text:
            continue
        try:
            index = int(text)
        except ValueError:
            print("请输入上面的序号。")
            continue
        if 1 <= index <= len(volumes):
            return volumes[index - 1]
        print("序号超出范围。")


def choose_two_volumes():
    volumes = ss.list_candidate_volumes()
    if len(volumes) < 2:
        raise ss.PreconditionError(
            "可选卷不足两个；请确认两块盘都已加速、两个盘符都回来了")
    print("请选择要同时压的两块盘：")
    for i, (root, label, avail, total) in enumerate(volumes, 1):
        name = label or "(无卷标)"
        print(f"  {i}) {root}  {name}  可用 {ss.human(avail)} / 总 {ss.human(total)}")
    first = _choose_index(volumes, "A 盘序号：")
    rest = [v for v in volumes if v[0] != first[0]]
    second = _choose_index(rest, "B 盘序号：")
    return first, second


def volume_disk_numbers(root):
    """该卷所在物理盘号；查不到返回空列表。"""
    handle = ss._open_device("\\\\.\\" + root[:2])
    if handle is None:
        return []
    disks = []
    try:
        out = ctypes.create_string_buffer(1024)
        size = ss._device_io_control(handle, ss.IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                                     None, 0, ctypes.cast(out, ctypes.c_void_p), 1024)
        count = struct.unpack_from("<I", out.raw, 0)[0]
        count = min(count, max(0, (size - 8) // 24))
        for i in range(count):
            disks.append(struct.unpack_from("<I", out.raw, 8 + i * 24)[0])
    except OSError:
        return []
    finally:
        ss.kernel32.CloseHandle(handle)
    return disks


def prompt_size():
    """每块盘的测试文件大小；直接回车用默认值。"""
    while True:
        try:
            text = input(
                f"每块盘的测试文件大小（GiB，可写 512m / 1.5g，直接回车 = "
                f"{DEFAULT_SIZE >> 20} MiB）：")
        except EOFError:
            raise KeyboardInterrupt
        if not text.strip():
            return DEFAULT_SIZE
        size = ss.parse_size_gib(text)
        if size < (64 << 20):
            print("至少要 64 MiB，请重新输入。")
            continue
        return size


def _make_ctx(root, label):
    sector = ss.volume_sector_size(root)
    block = max(ss.ALIGN, sector)
    return {
        "label": label,
        "root": root,
        "sector": sector,
        "block": block,
        "size": 0,
        "nblocks": 0,
        "dir": "",
        "path": "",
        "hashes": bytearray(),
        "ops": None,
    }


def startup(ctx):
    print(f"报告将写入：{ctx['report_path']}")
    print(f"操作记录：{ctx['ops_paths'][0]}")
    print(f"          {ctx['ops_paths'][1]}")
    print("请现在打开加速（把两块盘一起选上），确认两个盘符都能访问后按回车继续 …")
    try:
        input()
    except EOFError:
        raise KeyboardInterrupt

    va, vb = choose_two_volumes()
    ctxs = [_make_ctx(va[0], "A"), _make_ctx(vb[0], "B")]

    # 两块卷必须落在不同的物理盘上，否则测不到"两个会话"的场景
    da = volume_disk_numbers(ctxs[0]["root"])
    db = volume_disk_numbers(ctxs[1]["root"])
    if da and db and set(da) & set(db):
        raise ss.PreconditionError(
            f"A、B 两块卷落在同一块物理盘（磁盘 {sorted(set(da) & set(db))}）；"
            "本测试要求两块不同的物理盘")

    ctx["bus_notes"] = []
    for c in ctxs:
        buses = ss.volume_bus_types(c["root"])
        note = ""
        if buses and ss.BUS_ISCSI not in buses:
            names = "/".join(ss.BUS_NAMES.get(b, str(b)) for b in buses)
            note = f"{c['label']} 盘 {c['root']} 的总线类型是 {names}，这块测的是原盘。"
            print(f"注意：{note}")
        ctx["bus_notes"].append(note)

    size = prompt_size()
    for c in ctxs:
        c["size"] = size + (-size) % c["block"]
        c["nblocks"] = c["size"] // c["block"]
        need = int(c["size"] * 1.2) + (64 << 20)
        avail = ss.free_space(c["root"])
        if avail < need:
            raise ss.PreconditionError(
                f"{c['label']} 盘 {c['root']} 剩余空间不足：需要约 {ss.human(need)}，"
                f"当前 {ss.human(avail)}；请调小测试文件大小")
        c["dir"] = os.path.join(c["root"], DIR_NAME)
        c["path"] = os.path.join(c["dir"], FILE_NAME)
        try:
            os.makedirs(c["dir"], exist_ok=True)
        except OSError as ex:
            raise ss.PreconditionError(f"{c['label']} 盘不可写（{c['root']}）：{ex}")
        print(f"{c['label']}：{c['root']} 扇区 {c['sector']} 字节，"
              f"块 {c['block']} 字节，文件 {ss.human(c['size'])}（{c['nblocks']} 块）")

    try:
        os.makedirs(ctx["report_dir"], exist_ok=True)
    except OSError as ex:
        raise ss.PreconditionError(f"报告目录不可写（{ctx['report_dir']}）：{ex}")

    for c, path in zip(ctxs, ctx["ops_paths"]):
        c["ops"] = ss.OpsLog(path, True)

    for c in ctxs:
        bar = ss.Bar(c["size"], f"gen-{c['label']}", unit="B", unit_scale=True)
        try:
            c["hashes"] = ss.generate(c["path"], c["size"], c["block"], bar)
        finally:
            bar.close()
    return ctxs


# ======================= 双盘并发压测 =======================

def _dual_worker(ctxs, tid, threads, deadline, seed, stop, out):
    """一个线程同时压两块盘：每轮对 A、B 各做一次随机读或写。

    块选择限定在 `块号 % 线程数 == 本线程` 的范围内：对整盘随机，但两个线程
    不会碰同一个块，哈希表更新因此无需加锁。
    """
    rnd = random.Random(seed)
    reads = [0, 0]
    writes = [0, 0]
    lat = [[], []]
    files = [ss.RawFile(c["path"], True) for c in ctxs]
    bufs = [ss.alloc(c["block"]) for c in ctxs]
    try:
        while not stop.is_set() and time.perf_counter() < deadline:
            for i, c in enumerate(ctxs):
                if stop.is_set() or time.perf_counter() >= deadline:
                    break
                block = c["block"]
                nblocks = c["nblocks"]
                if tid >= nblocks:
                    continue
                span = (nblocks - 1 - tid) // threads + 1
                blk = tid + threads * rnd.randrange(span)
                off = blk * block
                buf = bufs[i]
                log = c["ops"]
                t0 = time.perf_counter()
                if rnd.random() < ss.READ_RATIO:
                    seq = log.start(tid, 0, off, block)
                    files[i].read_at(off, buf, block)
                    dt = (time.perf_counter() - t0) * 1000.0
                    data = ctypes.string_at(buf, block)
                    want = bytes(c["hashes"][blk * 8:blk * 8 + 8])
                    got = ss.digest_block(data, 0, block)
                    if got != want:
                        log.end(seq, tid, 0, off, block, want, got, b"\0" * 8, 2)
                        raise ss.VerifyError(
                            c["label"], blk, block,
                            f"哈希不符 期望 {want.hex()} 实际 {got.hex()}")
                    log.end(seq, tid, 0, off, block, want, got, b"\0" * 8, 1)
                    reads[i] += 1
                else:
                    old_hash = bytes(c["hashes"][blk * 8:blk * 8 + 8])
                    seq = log.start(tid, 1, off, block, old_hash)
                    data = os.urandom(block)
                    ctypes.memmove(buf, data, block)
                    files[i].write_at(off, buf, block)
                    files[i].read_at(off, buf, block)
                    dt = (time.perf_counter() - t0) * 1000.0
                    back = ctypes.string_at(buf, block)
                    if back != data:
                        diff = 0
                        while diff < block and back[diff] == data[diff]:
                            diff += 1
                        log.end(seq, tid, 1, off, block, old_hash, b"\0" * 8,
                                old_hash, 2)
                        raise ss.VerifyError(
                            c["label"], blk, block,
                            f"写后回读不符（块内偏移 {diff}）")
                    new_hash = ss.digest_block(back, 0, block)
                    c["hashes"][blk * 8:blk * 8 + 8] = new_hash
                    log.end(seq, tid, 1, off, block, new_hash, b"\0" * 8, old_hash, 1)
                    writes[i] += 1
                if len(lat[i]) < LAT_CAP:
                    lat[i].append(dt)
    finally:
        for f in files:
            f.close()
        for b in bufs:
            ss.free(b)
        out.append((tid, reads, writes, lat))


def run_dual(ctxs, ctx, seconds, threads):
    threads = max(1, min(threads, 64))
    stop = threading.Event()
    errors = []
    out = []
    deadline = time.perf_counter() + seconds
    seed = ctx["seed"]

    def runner(tid):
        try:
            _dual_worker(ctxs, tid, threads, deadline, seed + tid * 7919, stop, out)
        except BaseException as ex:
            errors.append(ex)
            stop.set()

    workers = [threading.Thread(target=runner, args=(i,), daemon=True)
               for i in range(threads)]
    bar = ss.Bar(seconds, f"dual×{threads}", unit="s")
    t0 = time.perf_counter()
    for t in workers:
        t.start()
    try:
        while any(t.is_alive() for t in workers):
            elapsed = min(time.perf_counter() - t0, float(seconds))
            if elapsed > bar.n:
                bar.update(elapsed - bar.n)
            time.sleep(0.2)
        if bar.n < seconds:
            bar.update(seconds - bar.n)
    finally:
        bar.close()
        stop.set()
    for t in workers:
        t.join()

    reads = [sum(item[1][i] for item in out) for i in range(2)]
    writes = [sum(item[2][i] for item in out) for i in range(2)]
    lat = [[x for item in out for x in item[3][i]] for i in range(2)]
    for i, c in enumerate(ctxs):
        p50, p95 = ss.percentiles(lat[i])
        line = (f"[{c['label']}] {c['root']} 读 {reads[i]} / 写 {writes[i]}  不一致 0  "
                f"p50 {p50:.1f}ms p95 {p95:.1f}ms")
        print(line)
        ctx["phases"].append(line)
    if errors:
        raise errors[0]
    line = f"PASS（{seconds}s，{threads} 线程同时压两块盘）"
    print(line)
    ctx["phases"].append(line)


# ======================= 收尾 =======================

def verify_after_stop(ctxs, ctx):
    """可选：停止加速后对两块盘各做一次整文件回读，证明落盘内容也正确。"""
    try:
        text = input("是否做停止加速后的落盘回读校验？(y/N)：").strip().lower()
    except EOFError:
        raise KeyboardInterrupt
    if text not in ("y", "yes"):
        print("跳过落盘回读：本次只证明加速层读写一致。")
        ctx["phases"].append("[disk] 未执行（用户跳过落盘回读）")
        return

    print("请停止加速 → 退出软件 → 把两块盘都重新联机，完成后按回车继续 …")
    while True:
        try:
            input()
        except EOFError:
            raise KeyboardInterrupt
        if ss.flydisk_running():
            print("FlyDisk.exe 仍在运行，请先退出软件后再回车。")
            continue
        bad = []
        for c in ctxs:
            if not os.path.exists(c["path"]):
                bad.append(f"{c['label']} 盘测试文件不可访问")
            elif os.path.getsize(c["path"]) != c["size"]:
                bad.append(f"{c['label']} 盘文件大小与记录不符")
        if bad:
            print("；".join(bad) + "，请确认两块盘都已联机、盘符未变。")
            continue
        break

    for c in ctxs:
        size, secs, lat = ss.sequential_read(c["path"], c["size"], c["block"],
                                             c["hashes"], f"disk-{c['label']}")
        mibs = size / float(1 << 20) / secs if secs > 0 else 0.0
        p50, p95 = ss.percentiles(lat)
        line = (f"[disk-{c['label']}] 未加速回读 {ss.human(size)} / {secs:.1f}s = "
                f"{mibs:.1f} MiB/s  校验 {c['nblocks']}/{c['nblocks']} 通过  "
                f"p50 {p50:.1f}ms p95 {p95:.1f}ms")
        print(line)
        ctx["phases"].append(line)
    ctx["disk_verified"] = True


def cleanup(ctxs):
    """删除测试文件；返回删除失败的错误文本（空串表示全部成功）。"""
    errors = []
    for c in ctxs:
        try:
            if c["path"] and os.path.exists(c["path"]):
                os.remove(c["path"])
            if c["dir"] and os.path.isdir(c["dir"]) and not os.listdir(c["dir"]):
                os.rmdir(c["dir"])
        except OSError as ex:
            errors.append(f"{c['label']} 盘：{ex}")
            print(f"删除 {c['label']} 盘测试文件失败：{ex}")
    return "；".join(errors)


# ======================= 报告 =======================

def build_report_body(ctxs, ctx):
    lines = []
    lines.append("## 结论")
    lines.append("")
    lines.append(ctx.get("conclusion") or "通过")
    lines.append("")

    lines.append("## 环境")
    lines.append("")
    lines.append(f"- 开始时间：{ctx.get('started', '')}")
    for c in ctxs:
        lines.append(f"- 盘 {c['label']}：{c['root']} 测试文件 {c['path']}"
                     f"（{ss.human(c['size'])}，用户指定）")
    for c in ctxs:
        lines.append(f"- 盘 {c['label']} 扇区：{c['sector']} 字节，"
                     f"块大小 / 对齐：{c['block']} 字节，块数 {c['nblocks']}")
    for c, note in zip(ctxs, ctx.get("bus_notes", [])):
        if note:
            lines.append(f"- 提示（盘 {c['label']}）：{note}")
    lines.append(f"- 操作记录（盘 A）：{ctx['ops_paths'][0]}")
    lines.append(f"- 操作记录（盘 B）：{ctx['ops_paths'][1]}（收尾不删除）")
    lines.append(f"- 压测总时长：{ctx.get('seconds', 0)}s，线程数：{ctx.get('threads', 0)}，"
                 f"随机种子：{ctx.get('seed', 0)}")
    lines.append(f"- Python：{sys.version.split()[0]}，"
                 f"tqdm：{'可用' if ss.tqdm is not None else '未安装（用内置进度条）'}")
    lines.append("")

    lines.append("## 阶段结果")
    lines.append("")
    for line in ctx.get("phases", []):
        lines.append(f"- {line}")
    lines.append("")

    lines.append("## 落盘回读")
    lines.append("")
    if ctx.get("disk_verified"):
        lines.append("- 已执行：停止加速、退出软件、两块盘重新联机后各自整文件回读并逐块校验。")
    else:
        lines.append("- 未执行：只验证了加速层读写一致，未验证停止加速后的落盘内容。")
    lines.append("")

    if not ctx.get("all_ok", False) and ctxs:
        lines.append("## 最近的操作记录")
        lines.append("")
        for c, path in zip(ctxs, ctx["ops_paths"]):
            lines.append(f"### 盘 {c['label']}")
            lines.append("")
            recs = ss.dump_ops(path, 100)
            if recs:
                for rec in recs:
                    lines.append(f"- {ss.format_op(rec)}")
            else:
                lines.append("- 没有记录到操作")
            lines.append("")

    if ctx.get("cleanup_error"):
        lines.append("## 收尾")
        lines.append("")
        lines.append(f"- 删除测试文件失败：{ctx['cleanup_error']}")
        lines.append("")

    lines.append("## 说明")
    lines.append("")
    lines.append("- 所有 IO 均带 FILE_FLAG_NO_BUFFERING；写句柄另加 FILE_FLAG_WRITE_THROUGH。")
    lines.append("- 读操作逐块比对哈希；写操作是整块写，写后立即读回逐字节比对。")
    lines.append("- 每个线程同时持有两块盘的句柄，块选择按「块号 % 线程数」分片以避开锁。")
    lines.append("")
    return "\n".join(lines)


def write_report(ctxs, ctx):
    try:
        body = build_report_body(ctxs, ctx)
        data = body.encode("utf-8")
        digest = hashlib.sha256(data).hexdigest()
        header = (
            "# FlyDisk 双盘并发压测报告\n\n"
            f"Body-SHA256: {digest}\n"
            f"Body-Bytes: {len(data)}\n"
            f"Report-At: {ctx.get('started', '')}\n\n---\n\n")
        os.makedirs(ctx["report_dir"], exist_ok=True)
        with open(ctx["report_path"], "w", encoding="utf-8", newline="\n") as f:
            f.write(header)
            f.write(body)
    except Exception:
        # 收尾阶段不打任何内容：报告写不出来也只能放弃
        pass


def teardown(ctxs, ctx):
    """静默收尾：关操作记录、全部通过才删测试文件、写报告；操作记录永不删除。"""
    for c in ctxs:
        log = c.get("ops")
        if log is not None:
            try:
                log.close()
            except OSError:
                pass
    if ctx.get("all_ok"):
        err = cleanup(ctxs)
        if err:
            ctx["cleanup_error"] = err
    elif ctxs:
        print("测试文件已保留，便于复查。")
    write_report(ctxs, ctx)


def parse_args():
    parser = argparse.ArgumentParser(
        description="双盘同时多线程读写压测：先打开加速，再选两块盘",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="测试文件大小在运行时询问；只限总时长，不按操作次数。")
    parser.add_argument("--seconds", type=int, default=60,
                        help="压测总时长（秒，默认 60）")
    parser.add_argument("--threads", type=int, default=8,
                        help="线程数（默认 8，每个线程同时压两块盘）")
    return parser.parse_args()


def main():
    args = parse_args()
    if args.threads < 1 or args.seconds < 1:
        print("参数无效：--threads / --seconds 必须 ≥ 1")
        return 1

    script_dir = os.path.dirname(os.path.abspath(__file__))
    report_dir = os.path.join(script_dir, "docs", "测试报告")
    stamp = time.strftime("%Y%m%d-%H%M%S")
    ctx = {
        "started": time.strftime("%Y-%m-%d %H:%M:%S"),
        "report_dir": report_dir,
        "report_path": os.path.join(report_dir, f"smoke-dual-{stamp}.md"),
        "ops_paths": [os.path.join(report_dir, f"ops-{stamp}-a.bin"),
                      os.path.join(report_dir, f"ops-{stamp}-b.bin")],
        "seconds": args.seconds,
        "threads": args.threads,
        "seed": int(time.time()),
        "phases": [],
        "bus_notes": [],
        "disk_verified": False,
        "all_ok": False,
        "conclusion": "",
        "cleanup_error": "",
    }

    ctxs = []
    code = 1
    try:
        ctxs = startup(ctx)
        run_dual(ctxs, ctx, args.seconds, args.threads)
        verify_after_stop(ctxs, ctx)
        ctx["conclusion"] = "通过：两块盘同时受压期间内容校验一致。"
        print(ctx["conclusion"])
        code = 0
    except ss.PreconditionError as ex:
        ctx["conclusion"] = f"前置条件不满足：{ex}"
        print(ctx["conclusion"])
    except ss.VerifyError as ex:
        ctx["conclusion"] = f"内容不符：{ex}"
        print(ctx["conclusion"])
    except KeyboardInterrupt:
        ctx["conclusion"] = "用户中断"
        print("已中断。")
    except OSError as ex:
        ctx["conclusion"] = f"IO 异常：{ex}"
        print(ctx["conclusion"])
    except Exception as ex:
        ctx["conclusion"] = f"未预期异常：{type(ex).__name__}: {ex}"
        print(ctx["conclusion"])
    finally:
        ctx["all_ok"] = (code == 0)
        teardown(ctxs, ctx)
    return code


if __name__ == "__main__":
    sys.exit(main())
