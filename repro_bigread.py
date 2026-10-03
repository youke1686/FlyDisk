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

r"""repro_bigread.py — 大文件随机读：性能对照（真盘 ⇄ 本盘）+ **内容确认**

定位（两件事，一次跑完）：
  1. **性能对照**：同一份数据分别经「源盘视图」与「本盘视图」随机读，并排比吞吐/延迟，
     用来回答"内存缓存到底有没有提速"。
  2. **内容确认**：每次读到的字节都与"按块重算的期望值"逐字节比对，
     用来抓"缓存返回脏数据"——**这正是淘汰（LRU）上线后最需要盯的一类错**：
     槽位被误归还/重复归还/淘汰后复用，都会让某个块读到别人的数据（静默错数据，不报错）。
     ⇒ 若淘汰路径有 use-after-free 或重复归还，本脚本会明确报出"偏移 X 的块 Y 内容不符"。

内容怎么做到"既能校验、又不占额外磁盘"：
  每个 **4KiB 块**的内容由 `(seed, 块号)` 唯一决定（Mersenne Twister 派生后再压成 0/1 字节）。
  于是任意偏移的期望值都能**独立重算**，校验 1GiB 文件不需要另存一份 1GiB 的期望数据。
  代价：内容形态从"整文件连续随机流"变成"逐块确定"——这是可校验的前提，测量意义不变。
  注：块粒度与缓存块、NO_BUFFERING 对齐粒度一致（都是 4096）。
  ⚠ **性能数字要与历史基线比较时，必须两边同为"关校验"口径（加 `--no-verify`）**：校验本身不进入
    计时窗口（`MiB/s` 仍只含 IO），但 Python 会持续占住一个核，可能拖慢服务端 IO 线程被唤醒的延迟。

两种读法（**唯一的关键开关，别忽略**）：
  · 默认 buffered：普通 read。Windows 的 Cache Manager 会把整个文件缓存进内存，
    第 2 遍起两边的数字都只是"内存到内存"——**此时测不出本层缓存的任何作用**，
    只能当作"常规应用视角"的参考。
  · --direct：CreateFileW 带 FILE_FLAG_NO_BUFFERING，绕过 Windows 文件缓存。
    注意它**只影响内核缓存，不影响本层自己的内存池** ⇒ 这正是我们想要的场景：
      - 第 1 遍：本层冷、内核也冷 → 本盘 ≈ 源盘（都要真读盘）
      - 第 2 遍起：本层已缓存 → 本盘应明显快于源盘，这就是缓存加速的直接证据
    对齐要求：偏移与长度都必须是 4096 的倍数（本脚本自动对齐；文件大小须是 4096 的整数倍）。

本脚本**不做**的事：不测写入（写入一致性交给 fsx）。

进度：文件生成与每遍随机读循环都带进度条（优先用 tqdm；未安装时退化为脚本内置的简易百分比条）。

用法（管理员 cmd）：
  python repro_bigread.py X:\\bigread\\test.bin --direct                  # 只测加速盘（常用）
  python repro_bigread.py X:\\bigread\\test.bin --compare D:\\bigread\\test.bin --direct
                                                                        # 顺带与源盘并排对照
  python repro_bigread.py X:\\bigread\\test.bin --size-mb 1024 --reads 10000 --passes 3 --seed 20260926
  python repro_bigread.py X:\\bigread\\test.bin --delete          # 跑完删除测试文件
  python repro_bigread.py X:\\bigread\\test.bin --no-verify       # 关掉内容确认（只测性能）
  python repro_bigread.py X:\\bigread\\test.bin --reuse --reads 20000
                                                                # 复用上次生成的文件（须同一 seed），
                                                                # 用来排除"刚写完 1GiB 就读"的余波干扰
  python repro_bigread.py X:\\bigread\\test.bin --seq --direct
                                                                # **顺序读**：按长度档的最大值逐块往下读，
                                                                # 一次读完整个文件（读多少次由文件大小定，
                                                                # --reads 在顺序模式下不起作用）
  python repro_bigread.py X:\\bigread\\test.bin --seq --lengths 1m --no-verify
                                                                # 顺序 + 大块（1 MiB 一读）：用来回答
                                                                # "瓶颈是命令太小，还是每命令固定开销"
                                                                # 大块建议配 --no-verify：一次 1 MiB 的内容
                                                                # 重算在 Python 里比这次 IO 还贵

位置参数只有一个：**被测盘**的视图（文件在这里生成）。`--compare <路径>` 是可选的**对照盘**视图；
给了才做两盘并排对照与比值判读，不给就只测被测盘。
**默认跑完保留测试文件**（要删用 `--delete`）——留着它才能验证 L2 的跨重启持久性：

L2 持久性验证（三步）：
  1) repro_bigread.py X:\\bigread\\test.bin --direct        # 读一遍，把块灌进 L2
  2) 正常停服 → 再启服务                                    # L2 账本只在停服时落盘
  3) repro_bigread.py X:\\bigread\\test.bin --reuse --direct
     检查器应显示"已载入上次正常关服留下的账本"，且第 1 遍就有明显 L2 命中。
     若改成**强杀**服务再启，L2 应整层作废（脏即弃）——那是设计预期，不是 bug。

判读（末尾会再打一遍）：
  · buffered 模式下数字会被 Windows 文件缓存盖住，两边差不多是**预期**，不是缓存无效。
  · --direct 下看被测盘的冷→热：第 2 遍显著快于第 1 遍 ⇒ 本层缓存生效。
  · 给了 --compare 时另看比值：被测盘热态应快过对照盘。
  · **看清"冷热差异来自哪里"**：若对照盘第 1 遍也明显偏慢，常见成因是它紧跟在"生成 1GiB"之后——
    SSD 的 GC 与 Windows 的收尾写还在后台跑，会与第一遍的读争盘（表现为长尾变重、平均被拉高）。
    脚本默认就会保留文件：生成一次、隔几十秒再 `--reuse` 跑，就能把"盘本身冷热"与"刚写完的余波"分开。
  · **内容确认**：任何一次"内容不符"都是**真问题**（不是性能噪声）——
    对照盘侧不符说明生成/读回路径有问题；被测盘侧不符说明缓存层在返回脏数据，
    优先怀疑淘汰（槽位归还/复用）、失效（写后未失效）、块索引错位。
    脚本会把不符的偏移与块号一并打出来，并按非零退出码结束。
"""
import ctypes
import os
import random
import sys
import time
import ctypes.wintypes as wt
from ctypes import POINTER, WinDLL, byref, c_longlong, c_void_p, c_wchar_p

try:
    from tqdm import tqdm
except ImportError:                       # 未装 tqdm 也能跑，退化成自带的简易进度条
    tqdm = None

HANDLE, DWORD, BOOL = wt.HANDLE, wt.DWORD, wt.BOOL
kernel32 = WinDLL('kernel32', use_last_error=True)

# ---- Win32 常量 ----
GENERIC_READ = 0x80000000
FILE_SHARE_RWD = 0x1 | 0x2 | 0x4
OPEN_EXISTING = 3
FILE_BEGIN = 0
FILE_FLAG_NO_BUFFERING = 0x20000000       # 绕过 Windows Cache Manager（不影响本层内存池）
MEM_COMMIT = 0x1000
MEM_RELEASE = 0x8000
PAGE_READWRITE = 0x04
INVALID_HANDLE = HANDLE(-1).value & 0xFFFFFFFFFFFFFFFF
SECTOR = 4096                             # NO_BUFFERING 的对齐粒度（本机 4Kn/512e 都安全用 4096）

kernel32.CreateFileW.argtypes = [c_wchar_p, DWORD, DWORD, c_void_p, DWORD, DWORD, HANDLE]
kernel32.CreateFileW.restype = HANDLE
kernel32.ReadFile.argtypes = [HANDLE, c_void_p, DWORD, POINTER(DWORD), c_void_p]
kernel32.ReadFile.restype = BOOL
kernel32.SetFilePointerEx.argtypes = [HANDLE, c_longlong, POINTER(c_longlong), DWORD]
kernel32.SetFilePointerEx.restype = BOOL
kernel32.CloseHandle.argtypes = [HANDLE]
kernel32.CloseHandle.restype = BOOL
kernel32.VirtualAlloc.argtypes = [c_void_p, ctypes.c_size_t, DWORD, DWORD]
kernel32.VirtualAlloc.restype = c_void_p
kernel32.VirtualFree.argtypes = [c_void_p, ctypes.c_size_t, DWORD]
kernel32.VirtualFree.restype = BOOL

# ---- 随机读的长度档（4KiB ~ 256KiB，与 4KB 块缓存对齐，便于观察命中）----
LENGTHS = [4096, 8192, 16384, 32768, 65536, 131072, 262144]

MiB = 1024.0 * 1024.0

BLOCK = 4096                                                   # 内容块粒度（与缓存块一致）
_BIT_TABLE = bytes(b & 1 for b in range(256))                  # 把任意字节压成 0 或 1


# ---- 进度条：有 tqdm 就用 tqdm，没有就用下面这个最小替代（不引入硬依赖）----
class _SimpleBar:
    """tqdm 缺席时的兜底：一行 \r 百分比 + 耗时 + 速率，接口只要求 update/close。"""

    def __init__(self, total, desc, unit):
        self.total, self.desc, self.n, self.t0 = total, desc, 0, time.perf_counter()

    def update(self, n=1):
        self.n += n
        pct = (self.n * 100.0 / self.total) if self.total else 100.0
        el = time.perf_counter() - self.t0
        rate = (self.n / el) if el > 0 else 0.0
        sys.stdout.write(f"\r  {self.desc} {pct:5.1f}%  {self.n}/{self.total}  "
                         f"{el:5.1f}s  {rate:9.0f}/s")
        sys.stdout.flush()

    def close(self):
        sys.stdout.write("\n")
        sys.stdout.flush()


def make_bar(total, desc, unit="次", unit_scale=False):
    """统一进度条接口（update/close）。tqdm 默认写 stderr、跑完自清，不干扰主输出。"""
    if tqdm is not None:
        return tqdm(total=total, desc=desc, unit=unit, unit_scale=unit_scale,
                    ncols=100, leave=False)
    return _SimpleBar(total, desc, unit)


def human(nbytes):
    return f"{nbytes / MiB:.1f} MiB"


def ensure_parent_dir(path):
    """返回 (是否由本脚本新建)。"""
    parent = os.path.dirname(os.path.abspath(path))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent, exist_ok=True)
        return True
    return False


# ======================= 内容：按块确定，可独立重算 =======================

def _rand_block(rnd, n):
    """取 n 个伪随机字节（Python < 3.9 没有 Random.randbytes，退化为慢路径）。"""
    rf = getattr(rnd, 'randbytes', None)
    if rf is not None:
        return rf(n)
    return bytes(rnd.getrandbits(8) for _ in range(n))


def block_content(seed, block_index):
    """块 block_index 的期望内容（BLOCK 个 0/1 字节）。

    由 (seed, block_index) 唯一决定 ⇒ 生成端与校验端各自独立算、结果必然相同，
    因此校验不需要保存期望数据。种子是 int（不是 str），不受 PYTHONHASHSEED 影响。
    """
    rnd = random.Random((seed << 20) ^ block_index)
    return _rand_block(rnd, BLOCK).translate(_BIT_TABLE)


def expected_bytes(seed, offset, length):
    """拼出 [offset, offset+length) 的期望内容（跨块时逐块取，每块只生成一次）。"""
    out = bytearray()
    pos = 0
    while pos < length:
        absolute = offset + pos
        block_index = absolute // BLOCK
        in_block = absolute % BLOCK
        take = min(BLOCK - in_block, length - pos)
        out += block_content(seed, block_index)[in_block:in_block + take]
        pos += take
    return bytes(out)


def first_mismatch(a, b):
    """首个不同字节的下标（都相同则返回较短长度）。"""
    n = min(len(a), len(b))
    for i in range(n):
        if a[i] != b[i]:
            return i
    return n


class VerifyStats:
    """内容确认的统计（每个"视图"一份，便于分辨是源盘侧还是本盘侧出问题）。"""

    MAX_PROBLEMS = 8

    def __init__(self):
        self.checked = 0     # 已确认通过的读次数
        self.bad = 0         # 不符/短读的读次数
        self.problems = []   # 前几条问题描述（避免刷屏）

    def _record(self, text):
        self.bad += 1
        if len(self.problems) < self.MAX_PROBLEMS:
            self.problems.append(text)
        elif len(self.problems) == self.MAX_PROBLEMS:
            self.problems.append("……（后续同类问题不再逐条列出）")

    def verify_read(self, path, got, offset, length, seed):
        """把读到的字节与期望值比对。返回是否通过（不抛异常，问题记在 stats 里）。"""
        if len(got) != length:
            self._record(f"{path} 偏移 {offset} 请求 {length} 字节，只读到 {len(got)} 字节")
            return False

        want = expected_bytes(seed, offset, length)
        if got == want:
            self.checked += 1
            return True

        i = first_mismatch(got, want)
        absolute = offset + i
        self._record(
            f"{path} 偏移 {offset}+{i}（绝对 {absolute}，块 {absolute // BLOCK}）内容不符："
            f"期望 0x{want[i]:02X} 实际 0x{got[i]:02X}（本次读 {length} 字节，仅报首处）")
        return False

    def summary(self):
        if self.checked == 0 and self.bad == 0:
            return "未启用"
        if self.bad == 0:
            return f"通过 {self.checked}/{self.checked}"
        return f"**不符 {self.bad} 次（通过 {self.checked} 次）**"


# ======================= 生成与读取 =======================

def generate_file(path, size, seed, chunk_blocks=1024):
    """生成 size 字节：逐块写入由 (seed, 块号) 决定的内容，按 chunk_blocks 攒批写。

    攒批是为了减少系统调用；块内容必须逐块独立生成（不能用一个连续随机流），
    否则校验时无法按偏移定位重算。

    **返回值是两份耗时**：(总耗时, 其中真正花在写盘上的耗时)。
    拆开是因为这一步同时也是一次"顺序写"测量，而按块拼内容在 Python 里是纯 CPU：
    512 MiB 的生成有十几秒量级，若不拆开，40 MiB/s 这种数字分不清是盘慢还是解释器慢。
    注意：写路径是写透传、不经缓存，所以这个"写盘耗时"就是**源设备自己的顺序写速度**。
    """
    if size % BLOCK != 0:
        raise ValueError(f"文件大小必须是 {BLOCK} 的整数倍（当前 {size}）")

    bar = make_bar(size, "生成可校验内容", unit="B", unit_scale=True)
    t0 = time.perf_counter()
    io_secs = 0.0
    try:
        with open(path, 'wb', buffering=0) as f:
            block_index = 0
            written = 0
            while written < size:
                n = min(chunk_blocks, (size - written) // BLOCK)
                buf = b''.join(block_content(seed, block_index + k) for k in range(n))
                t_write = time.perf_counter()
                f.write(buf)
                io_secs += time.perf_counter() - t_write
                block_index += n
                written += len(buf)
                bar.update(len(buf))
            t_flush = time.perf_counter()
            f.flush()
            os.fsync(f.fileno())
            io_secs += time.perf_counter() - t_flush
    finally:
        bar.close()
    return time.perf_counter() - t0, io_secs


def parse_size(text):
    """'4096' / '64k' / '1m' / '1g' → 字节数（k/m/g 按 1024 进制）。"""
    t = text.strip().lower()
    mult = 1
    if t and t[-1] in "kmg":
        mult = {"k": 1024, "m": 1024 ** 2, "g": 1024 ** 3}[t[-1]]
        t = t[:-1]
    return int(t) * mult


def make_sequential_plan(size, op, align):
    """顺序计划：以 op（长度档里的最大值）为步长，从头把整个文件读完。

    读多少次由**文件大小**决定，`--reads` 在这个模式下不起作用；
    步长与文件大小不成整数倍时，最后补一次"剩下的那一段"，保证每个字节都被走到。
    """
    if align:
        op = max(SECTOR, op // SECTOR * SECTOR)
    if op >= size:
        return [(0, size // SECTOR * SECTOR if align else size)]

    plan = []
    off = 0
    while off < size:
        length = min(op, size - off)
        if align:
            length = length // SECTOR * SECTOR
            if length < SECTOR:
                break            # 尾部不足一个对齐单位：跳过（那几个 KiB 不影响结论）
        plan.append((off, length))
        off += op
    return plan


def make_read_plan(size, reads, seed, align, lengths, sequential):
    """生成 (offset, length) 序列。align=True 时两者都向下对齐到 SECTOR（NO_BUFFERING 要求）。

    sequential=True ⇒ 顺序读（见 make_sequential_plan）；否则随机读：
    每次从 lengths 里随机取一档长度，再在文件里随机取偏移。
    """
    if sequential:
        return make_sequential_plan(size, max(lengths), align)

    rnd = random.Random(seed)
    plan = []
    for _ in range(reads):
        length = rnd.choice(lengths)
        if length > size:
            length = size
        max_off = size - length
        off = rnd.randrange(0, max_off + 1)
        if align:
            off = off // SECTOR * SECTOR
            length = length // SECTOR * SECTOR
            if length < SECTOR or off + length > size:
                continue
        plan.append((off, length))
    return plan


def percentiles(lat_ms):
    """返回 (min, p50, p95, max)。**顺序必须与调用处的解包一致**——
    此前这里返回 min/p50/p95/max 却被解包成 p50/p95/p99/max，于是打印出来的
    "p50" 其实是 min、"p95" 其实是 p50，会把"长尾有多重"看反。"""
    s = sorted(lat_ms)
    n = len(s)
    return s[0], s[n // 2], s[min(n - 1, int(n * 0.95))], s[-1]


def read_buffered(path, plan, seed, stats, bar=None):
    """常规 read（Windows Cache Manager 生效）。返回 (字节数, 秒, 单次延迟 ms 列表)。

    注意计时窗口**只含 IO**：内容确认放在 append 之后，不污染性能数字。
    """
    lat, total = [], 0
    with open(path, 'rb', buffering=0) as f:
        for off, length in plan:
            t0 = time.perf_counter()
            f.seek(off)
            buf = bytearray()
            while len(buf) < length:
                chunk = f.read(length - len(buf))
                if not chunk:
                    break
                buf += chunk
            lat.append((time.perf_counter() - t0) * 1000.0)
            total += len(buf)
            if stats is not None:
                stats.verify_read(path, bytes(buf), off, length, seed)
            if bar is not None:
                bar.update(1)
    return total, sum(lat) / 1000.0, lat


def read_direct(path, plan, max_len, seed, stats, bar=None):
    """CreateFileW(FILE_FLAG_NO_BUFFERING) + ReadFile，缓冲区用 VirtualAlloc（页对齐）。

    只绕过 Windows 文件缓存；本层（winfsp + 我们自己的内存池）照常参与。
    """
    h = kernel32.CreateFileW(path, GENERIC_READ, FILE_SHARE_RWD, None,
                             OPEN_EXISTING, FILE_FLAG_NO_BUFFERING, None)
    if h == INVALID_HANDLE:
        err = ctypes.get_last_error()
        raise OSError(f"CreateFileW(FILE_FLAG_NO_BUFFERING) 失败 Win32={err}"
                      f"（若为 87，说明该卷/该层不支持无缓冲打开）")
    buf = kernel32.VirtualAlloc(None, max_len, MEM_COMMIT, PAGE_READWRITE)
    if not buf:
        kernel32.CloseHandle(h)
        raise OSError(f"VirtualAlloc({max_len}) 失败 Win32={ctypes.get_last_error()}")
    lat, total = [], 0
    try:
        for off, length in plan:
            t0 = time.perf_counter()
            if not kernel32.SetFilePointerEx(h, off, None, FILE_BEGIN):
                raise OSError(f"SetFilePointerEx(off={off}) 失败 Win32={ctypes.get_last_error()}")
            got = DWORD(0)
            if not kernel32.ReadFile(h, buf, length, byref(got), None):
                raise OSError(f"ReadFile(off={off}, len={length}) 失败 Win32={ctypes.get_last_error()}")
            lat.append((time.perf_counter() - t0) * 1000.0)
            total += got.value
            if stats is not None:
                stats.verify_read(path, ctypes.string_at(buf, got.value), off, length, seed)
            if bar is not None:
                bar.update(1)
    finally:
        kernel32.VirtualFree(buf, 0, MEM_RELEASE)
        kernel32.CloseHandle(h)
    return total, sum(lat) / 1000.0, lat


def run_once(label, path, plan, direct, max_len, seed, stats):
    bar = make_bar(len(plan), f"{label} 随机读")
    try:
        if direct:
            total, secs, lat = read_direct(path, plan, max_len, seed, stats, bar)
        else:
            total, secs, lat = read_buffered(path, plan, seed, stats, bar)
    finally:
        bar.close()
    pmin, p50, p95, pmax = percentiles(lat)
    mibs = (total / MiB) / secs if secs > 0 else float('inf')
    print(f"  {label:<16} {human(total):>11} / {secs:>6.2f}s = {mibs:>9.1f} MiB/s"
          f"   | 单次读 min={pmin:>6.2f}ms p50={p50:>6.2f}ms p95={p95:>7.2f}ms max={pmax:>8.2f}ms"
          f"   ({len(lat)} 次)")
    return mibs


def usage():
    print(__doc__)
    print("参数：")
    print("  文件路径                必填，**被测盘**的视图，例如 X:\\bigread\\test.bin")
    print("                          （文件在这里生成；默认跑完**保留**，要删见 --delete）")
    print("  --compare 路径          选填，**对照盘**的视图，例如 D:\\bigread\\test.bin")
    print("                          给了才做两盘并排对照与比值判读；不给就只测被测盘")
    print("  --size-mb N             文件大小，默认 1024（1 GiB）")
    print("  --reads N               每遍随机读次数，默认 1000（× 平均 72KiB ≈ 71MiB/遍）")
    print("                          （--seq 顺序模式下不起作用：读多少次由文件大小决定）")
    print("  --seq                   顺序读：按步长从头把整个文件读完一遍")
    print("                          步长取长度档里的最大值（默认 256 KiB）")
    print("  --lengths 列表          长度档，逗号分隔、支持 k/m/g（默认 4k,8k,16k,32k,64k,128k,256k）")
    print("                          例：--lengths 1m --seq 就是「1 MiB 一读、读完整个文件」")
    print("  --passes N              遍数，默认 3")
    print("  --direct                用 FILE_FLAG_NO_BUFFERING 绕过 Windows 文件缓存（**推荐**）")
    print("  --no-verify             关掉内容确认（只测性能；默认开启）")
    print("  --delete                跑完删除测试文件（**默认保留**，留着才能验证 L2 的跨重启持久性）")
    print("  --reuse                 复用已存在且大小相符的文件（不生成、不删除；隐含保留）；")
    print("                          用来把\"刚写完 1GiB\"这个变量摘出去——新写完的盘上还有")
    print("                          SSD 的 GC 与 Windows 的收尾写，会拖慢紧随其后的第一遍读取")
    print("  --seed N                随机序列种子，默认 20260926（各视图共用同一序列，保证可比）")
    print()
    print("L2 持久性验证（三步）：")
    print("  1) repro_bigread.py X:\\bigread\\test.bin --direct        # 读一遍，把块灌进 L2（文件默认保留）")
    print("  2) 正常停服 → 再启服务                                  # 账本只在停服时落盘（见设计文档 §7.6）")
    print("  3) repro_bigread.py X:\\bigread\\test.bin --reuse --direct")
    print("     检查器应显示\"已载入上次正常关服留下的账本\"，且第 1 遍就有明显 L2 命中。")
    print("     若改成**强杀**服务再启，L2 应整层作废（脏即弃）——那是设计预期，不是 bug。")


def main():
    argv = sys.argv[1:]
    if not argv or argv[0] in ("-h", "--help"):
        usage()
        return 1

    target_path = None    # 被测盘视图（必填；生成与清理都在它上面）
    compare_path = None   # 对照盘视图（可选；给了才做并排对照与比值判读）
    size_mb, reads, passes, seed = 1024, 1000, 3, 20260926
    direct, verify, reuse = False, True, False
    delete_after = False  # **默认保留**测试文件：留着才能验证 L2 的跨重启持久性（要删用 --delete）
    lengths = list(LENGTHS)
    sequential = False    # --seq：顺序读（步长取长度档里的最大值，一次读完整个文件）

    i = 0
    while i < len(argv):
        a = argv[i]
        if a == "--direct":
            direct = True
        elif a == "--delete":
            delete_after = True
        elif a == "--reuse":
            reuse = True
        elif a == "--no-verify":
            verify = False
        elif a == "--seq":
            sequential = True
        elif a == "--lengths":
            if i + 1 >= len(argv):
                print("错误：--lengths 需要一个逗号分隔的列表，如 64k,256k,1m")
                return 2
            try:
                lengths = [parse_size(x) for x in argv[i + 1].split(",") if x.strip()]
            except ValueError:
                print(f"错误：无法解析长度档“{argv[i + 1]}”（形如 4096 / 64k / 1m / 1g）")
                return 2
            if not lengths or any(l <= 0 for l in lengths):
                print("错误：长度档必须都是正整数")
                return 2
            i += 1
        elif a == "--compare":
            if i + 1 >= len(argv):
                print("错误：--compare 需要一个路径参数（对照盘视图）")
                return 2
            compare_path = argv[i + 1]
            i += 1
        elif a in ("--size-mb", "--reads", "--passes", "--seed"):
            if i + 1 >= len(argv):
                print(f"错误：{a} 需要一个整数参数")
                return 2
            val = int(argv[i + 1])
            i += 1
            if a == "--size-mb":
                size_mb = val
            elif a == "--reads":
                reads = val
            elif a == "--passes":
                passes = val
            else:
                seed = val
        elif a.startswith("--"):
            print(f"错误：未知参数 {a}")
            if a == "--keep":
                print("      提示：现在**默认就保留**测试文件；要跑完删除请改用 --delete")
            return 2
        elif target_path is None:
            target_path = a
        else:
            print(f"错误：多余的位置参数 {a}")
            print("      要顺带与原盘并排对照，请写成：--compare D:\\bigread\\test.bin")
            return 2
        i += 1

    if target_path is None:
        usage()
        return 2

    size = size_mb * 1024 * 1024
    if size % BLOCK != 0:
        print(f"错误：文件大小必须是 {BLOCK} 的整数倍（当前 {size} 字节）；"
              f"换一个 --size-mb（1 MiB 的整数倍即可）")
        return 2
    if compare_path is not None:
        cparent = os.path.dirname(os.path.abspath(compare_path))
        if not os.path.isdir(cparent):
            print(f"错误：对照盘目录不存在：{cparent}")
            return 2

    mode = "direct（FILE_FLAG_NO_BUFFERING，绕过 Windows 文件缓存）" if direct else \
           "buffered（Windows 文件缓存生效 —— 第 2 遍起两边都只是内存到内存）"

    print(f"### repro_bigread  python={sys.version.split()[0]}"
          f"  进度条={'tqdm' if tqdm is not None else '内置简易条（pip install tqdm 可升级）'}")
    print(f"文件大小 {human(size)} | 模式：{mode}")
    plan_desc = "顺序读（按步长读完整个文件，--reads 不起作用）" if sequential else f"每遍 {reads} 次随机读"
    print(f"{plan_desc} | {passes} 遍 | 长度档 {[l // 1024 for l in lengths]} KiB | seed={seed}")
    print(f"内容确认：{'开启（按 4KiB 块逐字节比对）' if verify else '关闭（--no-verify）'}")
    print(f"被测盘视图：{target_path}（文件在这里生成、最后在这里删除）")
    print(f"对照盘视图：{compare_path if compare_path else '（未指定；本次只测被测盘，不做两盘对照）'}")

    try:
        created_target_dir = ensure_parent_dir(target_path)
    except OSError as ex:
        print(f"错误：无法创建目录 {os.path.dirname(os.path.abspath(target_path))}：{ex}")
        print("      （若这是虚拟盘路径，可能是虚拟盘未挂载，或路径写错）")
        return 2

    if reuse:
        # 复用已有文件：不生成、不删除。用来把"刚写完 1GiB"这个变量摘出去。
        # 注意：文件必须是用**同一个 seed** 生成的，否则内容确认会报不符——那是预期行为，不是 bug。
        if not os.path.exists(target_path) or os.path.getsize(target_path) != size:
            now = "不存在" if not os.path.exists(target_path) else f"大小为 {os.path.getsize(target_path)} 字节"
            print(f"错误：--reuse 要求文件已存在且大小正好 {size} 字节（当前{now}）")
            return 2
        print(f"\n[生成] 跳过：--reuse 复用已有文件（不生成、不删除）")
    else:
        # 先删掉同名旧文件：一是避免"这一轮读到的是上一轮生成的内容"，
        # 二是这一删会经 Cleanup(DELETE) 让本层把该文件的缓存整体失效（起跑线一致）
        if os.path.exists(target_path):
            try:
                os.remove(target_path)
                print(f"\n[准备] 已删除同名旧文件 {target_path}")
            except OSError as ex:
                print(f"错误：无法删除已存在的 {target_path}: {ex}")
                return 3

        print(f"\n[生成] {human(size)} 可校验内容 → {target_path}")
        gen_secs, gen_io = generate_file(target_path, size, seed)
        print(f"[生成] {human(size)} 用时 {gen_secs:.2f}s（{size / MiB / gen_secs:.1f} MiB/s 含拼内容）")
        if gen_io > 0:
            # 写路径是写透传、不经缓存 ⇒ 这个数就是源设备自己的顺序写速度（拼内容那段 CPU 已剔除）
            print(f"[顺序写] 其中写盘 {gen_io:.2f}s = {size / MiB / gen_io:.1f} MiB/s"
                  f"（写透传、不过缓存；另 {gen_secs - gen_io:.2f}s 花在按块拼内容上，属 CPU 不含在 IO 里）")
        else:
            print(f"[顺序写] 写盘耗时读数为 0（异常），本次不给顺序写速度")

    # 对齐策略：direct 必须对齐；buffered 保留真正的"任意偏移任意长度"
    plan = make_read_plan(size, reads, seed, align=direct, lengths=lengths, sequential=sequential)
    if not sequential and len(plan) != reads:
        print(f"[注意] 对齐后有 {reads - len(plan)} 次被丢弃，实际读 {len(plan)} 次")
    if not plan:
        print("错误：随机读计划为空")
        return 3
    max_len = max(l for _, l in plan)
    if sequential:
        print(f"[计划] 顺序读 {len(plan)} 次（整个文件 {human(size)} 走完一遍，"
              f"步长 {max(l for _, l in plan) // 1024} KiB）")
    else:
        print(f"[计划] {len(plan)} 次随机读，平均 {sum(l for _, l in plan) / len(plan) / 1024:.1f} KiB/次，"
              f"合计 {human(sum(l for _, l in plan))}/遍")

    # 视图顺序：**被测盘在前**（汇总表第一行就是被测盘，判读也以它为主）
    views = [("被测盘", target_path)]
    if compare_path:
        views.append(("对照盘", compare_path))

    # 逐遍交替跑两个视图：让两边看到同样的缓存状态，才算公平对照
    results = {}
    verifies = {}
    for label, path in views:
        verifies[label] = VerifyStats() if verify else None

    for p in range(1, passes + 1):
        print(f"\n=== 第 {p}/{passes} 遍{'（冷）' if p == 1 else '（热）'} ===")
        for label, path in views:
            tag = f"{label}({path[:2]})"
            try:
                results.setdefault(tag, []).append(
                    run_once(tag, path, plan, direct, max_len, seed, verifies[label]))
            except OSError as ex:
                print(f"  {tag:<16} 失败：{ex}")
                results.setdefault(tag, []).append(float('nan'))

    print("\n=== 汇总（MiB/s） ===")
    header = f"{'通道':<16}" + "".join(f"{'第' + str(p) + '遍':>12}" for p in range(1, passes + 1))
    print(header)
    for tag, vals in results.items():
        print(f"{tag:<16}" + "".join(
            ("        n/a" if v != v else f"{v:>12.1f}") for v in vals))

    if verify:
        print("\n=== 内容确认 ===")
        for label, path in views:
            st = verifies[label]
            print(f"  {label}({path[:2]})  {st.summary()}")
        for label, path in views:
            for text in verifies[label].problems:
                print(f"    ! {text}")

    # ===== 判读 =====
    # 被测盘永远是判读主体；两盘对照只在给了 --compare 时才出现
    print("\n=== 判读 ===")
    tag_list = list(results.keys())
    target_vals = results[tag_list[0]]
    if len(target_vals) > 1 and target_vals[0] == target_vals[0] and target_vals[0] > 0 \
            and target_vals[1] == target_vals[1]:
        print(f"  被测盘 冷→热   {target_vals[0]:.1f} → {target_vals[1]:.1f} MiB/s"
              f"  → 提速 {target_vals[1] / target_vals[0]:.2f} 倍")

    if len(tag_list) == 2:
        cmp_vals = results[tag_list[1]]
        if cmp_vals[0] == cmp_vals[0] and cmp_vals[0] > 0 and target_vals[0] == target_vals[0]:
            print(f"  第1遍（冷）  对照盘 {cmp_vals[0]:.1f} vs 被测盘 {target_vals[0]:.1f} MiB/s"
                  f"  → 比值 {target_vals[0] / cmp_vals[0]:.2f}")
        if len(cmp_vals) > 1 and cmp_vals[1] == cmp_vals[1] and target_vals[1] == target_vals[1] \
                and cmp_vals[1] > 0:
            print(f"  第2遍（热）  对照盘 {cmp_vals[1]:.1f} vs 被测盘 {target_vals[1]:.1f} MiB/s"
                  f"  → 比值 {target_vals[1] / cmp_vals[1]:.2f}")

    if not direct:
        print("  **当前是 buffered 模式：数字都被 Windows 文件缓存盖住了，")
        print("    上表不能用来判断本层缓存有没有用。请加 --direct 重跑。**")
    else:
        print("  说明：--direct 只绕过了 Windows 内核缓存，本层缓存（L1 内存池 / L2 SSD）照常生效。")
        if sequential:
            print("    顺序模式下每遍都把整个文件走一遍 ⇒ 第 1 遍（冷）应≈源设备速度，")
            print("    第 2 遍起应明显更快（接近全命中）；三遍都差不多 ⇒ 缓存没生效（或压根没回填）。")
        elif len(tag_list) == 2:
            print("    被测盘热态显著快于冷态、且快过对照盘 ⇒ 本层缓存生效；")
            print("    若两边三遍都差不多 ⇒ 缓存没起作用（或缓存压根没回填）——都是有价值的结论。")
        else:
            print("    被测盘热态显著快于冷态 ⇒ 本层缓存生效；若三遍都差不多，")
            print("    说明随机窗口没命中已缓存的块（或缓存压根没回填）——都是有价值的结论。")

    print("\n=== 判读（内容确认） ===")
    if not verify:
        print("  本次未开启内容确认（--no-verify）。")
    else:
        bad_total = sum(verifies[label].bad for label, _ in views)
        if bad_total == 0:
            print("  所有读回的数据都与期望值逐一相符：缓存层没有返回脏数据。")
        else:
            print(f"  **共 {bad_total} 次读取的内容与期望不符 —— 这是真问题，不是性能噪声：**")
            print("    对照盘侧不符 ⇒ 生成或读回路径有问题；")
            print("    被测盘侧不符 ⇒ 缓存层在返回脏数据，优先怀疑淘汰（槽位归还/复用）、")
            print("                  失效（写后未失效）、块索引错位，并把上面的偏移与块号拿去对日志。")

    if delete_after and not reuse:
        print(f"\n[清理] 删除 {target_path} …")
        try:
            os.remove(target_path)
            print("[清理] 完成")
        except OSError as ex:
            print(f"[清理] 失败：{ex}")
        if created_target_dir:
            try:
                os.rmdir(os.path.dirname(os.path.abspath(target_path)))
                print("[清理] 已移除本脚本新建的目录")
            except OSError:
                pass
    else:
        # 默认保留（--reuse 也隐含保留：复用的文件不该被这一轮悄悄删掉）
        print(f"\n[保留] 文件未删除：{target_path}")
        if not reuse:
            print("       （要做 L2 持久性验证：正常停服 → 再启服务 → 用 --reuse 复跑同一路径）")

    if verify and any(verifies[label].bad for label, _ in views):
        return 4
    return 0


if __name__ == "__main__":
    sys.exit(main())
