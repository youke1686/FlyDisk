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

r"""smoke_stress.py — FlyDisk 冒烟压测（跨进程黑盒）

定位：不是性能对照，而是"压完之后数据还对、程序还没垮、落盘内容没问题"。
所有 IO 都对着**已经加速后的盘符**做；脚本不控制 FlyDisk.exe，也不改任何配置。

三个阶段（每次运行都完整执行，跑单个阶段也一样）：
  起步 记录环境 → 提示打开加速 → 生成测试文件并建校验表
  测试 按顺序执行选中的阶段 → 最后要求人工停止加速 → 未加速回读校验
  收尾 静默：关句柄、全部通过才删测试文件、写报告；操作记录永不删除

阶段（默认全跑，顺序固定）：
  cold        整文件顺序读一遍，逐块校验并测速
  hot         整文件顺序再读一遍，逐块校验并测速
  write       单线程随机写（整块 / 块内部分 / 跨块边界），写后立即读回
  concurrent  多线程分区随机读写 + 共享热点区（多版本判定）
  soak        按时长持续并发随机读写

起步与参数：
  · 加速前先采内存基线与 EvictionThreshold，并打印 L1 估算（只作参考）
  · 打开加速后脚本列出卷让你选，再让你输入测试文件大小（GiB，可写 512m / 1.5g）
  · 选盘后会查该卷所在磁盘的总线类型：不是 iSCSI 就提示"这次测的是原盘"（只警告、不中止）
  · 块大小与对齐取自该卷的扇区大小（max(4096, 扇区)），不依赖 L2 配置
  L1 估算 = max(0, EvictionThreshold − 加速前内存占用率) × 物理内存；
  系统内存已在阈值之上时估算为 0（L1 从一开始就在淘汰）。

用法（管理员 PowerShell / cmd）：
  python smoke_stress.py                       # 先打开加速，脚本随后列出卷让你选
  python smoke_stress.py --phase cold,hot
  python smoke_stress.py --phase write
  python smoke_stress.py --phase soak --seconds 120
  python smoke_stress.py --threads 16 --write-ops 5000

人工步骤（测试跑完脚本会停下来等）：
  停止加速 → 退出软件 → 重新联机该盘 → 回车
  脚本随后确认"FlyDisk.exe 已退出、卷可访问、文件大小未变"，再用 NO_BUFFERING
  整文件回读一遍——这一步才真正证明"落盘内容没问题"。

退出码：0 通过 / 2 前置不满足 / 3 内容不符 / 4 IO 异常 / 5 中断或未预期异常。
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import hashlib
import json
import os
import random
import struct
import subprocess
import sys
import threading
import time
from ctypes import POINTER, WinDLL, byref, c_longlong, c_void_p, c_wchar_p

try:
    from tqdm import tqdm
except ImportError:
    tqdm = None


# ======================= 可调默认值 =======================

THREADS = 8               # 并发阶段与 soak 的线程数
WRITE_OPS = 2000          # write / concurrent 阶段的写次数（concurrent 是总次数）
SOAK_SECONDS = 60         # soak 阶段时长（秒）


# ======================= 内部常量 =======================

PHASES = ("cold", "hot", "write", "concurrent", "soak")
ALIGN = 4096              # NO_BUFFERING 的对齐粒度（512e / 4Kn 都安全）
CHUNK = 1 << 20           # 顺序读写分块（会再对齐到块大小）
RING_SLOTS = 1 << 16      # 操作记录环形槽位数（约 4 MiB）
REC_FMT = "<QQIBBQI8s8s8s6x"
REC_SIZE = struct.calcsize(REC_FMT)
PATTERN_COUNT = 16        # 共享热点区的"版本"数
HOT_REGION_BYTES = 64 << 20
READ_RATIO = 0.7          # 并发阶段里读操作的比例

assert REC_SIZE == 64, "操作记录必须是 64 字节"


class PreconditionError(Exception):
    """前置条件不满足（配置、空间、盘符）"""


class VerifyError(Exception):
    """读回内容与期望不符"""

    def __init__(self, phase, block_index, block, detail):
        self.phase = phase
        self.block_index = block_index
        super().__init__(
            f"{phase}：块 {block_index}（偏移 {block_index * block}）{detail}")


# ======================= Win32 =======================

kernel32 = WinDLL("kernel32", use_last_error=True)

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_ALL = 0x1 | 0x2 | 0x4
CREATE_ALWAYS = 2
OPEN_EXISTING = 3
FILE_FLAG_NO_BUFFERING = 0x20000000
FILE_FLAG_WRITE_THROUGH = 0x80000000
FILE_BEGIN = 0
MEM_COMMIT = 0x1000
MEM_RESERVE = 0x2000
MEM_RELEASE = 0x8000
PAGE_READWRITE = 0x04
INVALID_HANDLE = ctypes.c_void_p(-1).value

kernel32.CreateFileW.argtypes = [c_wchar_p, wt.DWORD, wt.DWORD, c_void_p,
                                 wt.DWORD, wt.DWORD, c_void_p]
kernel32.CreateFileW.restype = c_void_p
kernel32.ReadFile.argtypes = [c_void_p, c_void_p, wt.DWORD, POINTER(wt.DWORD), c_void_p]
kernel32.ReadFile.restype = wt.BOOL
kernel32.WriteFile.argtypes = [c_void_p, c_void_p, wt.DWORD, POINTER(wt.DWORD), c_void_p]
kernel32.WriteFile.restype = wt.BOOL
kernel32.SetFilePointerEx.argtypes = [c_void_p, c_longlong, POINTER(c_longlong), wt.DWORD]
kernel32.SetFilePointerEx.restype = wt.BOOL
kernel32.CloseHandle.argtypes = [c_void_p]
kernel32.CloseHandle.restype = wt.BOOL
kernel32.VirtualAlloc.argtypes = [c_void_p, ctypes.c_size_t, wt.DWORD, wt.DWORD]
kernel32.VirtualAlloc.restype = c_void_p
kernel32.VirtualFree.argtypes = [c_void_p, ctypes.c_size_t, wt.DWORD]
kernel32.VirtualFree.restype = wt.BOOL
kernel32.GetDiskFreeSpaceExW.argtypes = [c_wchar_p, POINTER(ctypes.c_ulonglong),
                                         POINTER(ctypes.c_ulonglong),
                                         POINTER(ctypes.c_ulonglong)]
kernel32.GetDiskFreeSpaceExW.restype = wt.BOOL
kernel32.GetDiskFreeSpaceW.argtypes = [c_wchar_p, POINTER(wt.DWORD), POINTER(wt.DWORD),
                                       POINTER(wt.DWORD), POINTER(wt.DWORD)]
kernel32.GetDiskFreeSpaceW.restype = wt.BOOL
kernel32.DeviceIoControl.argtypes = [c_void_p, wt.DWORD, c_void_p, wt.DWORD,
                                     c_void_p, wt.DWORD, POINTER(wt.DWORD), c_void_p]
kernel32.DeviceIoControl.restype = wt.BOOL
kernel32.GetLogicalDrives.argtypes = []
kernel32.GetLogicalDrives.restype = wt.DWORD
kernel32.GetDriveTypeW.argtypes = [c_wchar_p]
kernel32.GetDriveTypeW.restype = wt.UINT
kernel32.GetVolumeInformationW.argtypes = [c_wchar_p, c_wchar_p, wt.DWORD,
                                           POINTER(wt.DWORD), POINTER(wt.DWORD),
                                           POINTER(wt.DWORD), c_wchar_p, wt.DWORD]
kernel32.GetVolumeInformationW.restype = wt.BOOL

DRIVE_REMOVABLE = 2
DRIVE_FIXED = 3

IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000
IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400
STORAGE_DEVICE_PROPERTY = 0
BUS_ISCSI = 9
BUS_NAMES = {
    0: "Unknown", 1: "SCSI", 2: "ATAPI", 3: "ATA", 4: "1394", 5: "SSA",
    6: "Fibre", 7: "USB", 8: "RAID", 9: "iSCSI", 10: "SAS", 11: "SATA",
    12: "SD", 13: "MMC", 14: "Virtual", 15: "FileBackedVirtual",
    16: "Spaces", 17: "NVMe", 18: "SCM", 19: "UFS",
}


class RawFile:
    """NO_BUFFERING 的原始文件句柄。

    每个线程各开一个句柄（SetFilePointerEx 会改文件指针，共享句柄没法并发）；
    写句柄再加 FILE_FLAG_WRITE_THROUGH：写透传本来就不过系统缓存，测试这边也要
    保证"写下去就是写下去"。
    """

    def __init__(self, path, writable, create=False):
        access = GENERIC_READ | (GENERIC_WRITE if writable else 0)
        flags = FILE_FLAG_NO_BUFFERING | (FILE_FLAG_WRITE_THROUGH if writable else 0)
        handle = kernel32.CreateFileW(path, access, FILE_SHARE_ALL, None,
                                      CREATE_ALWAYS if create else OPEN_EXISTING,
                                      flags, None)
        if not handle or handle == INVALID_HANDLE:
            raise OSError(ctypes.get_last_error(), f"CreateFileW 失败：{path}")
        self.h = handle

    def _seek(self, off):
        if not kernel32.SetFilePointerEx(self.h, off, None, FILE_BEGIN):
            raise OSError(ctypes.get_last_error(), f"SetFilePointerEx({off}) 失败")

    def read_at(self, off, buf, n):
        self._seek(off)
        got = wt.DWORD(0)
        if not kernel32.ReadFile(self.h, buf, n, byref(got), None):
            raise OSError(ctypes.get_last_error(), f"ReadFile(off={off}, len={n}) 失败")
        if got.value != n:
            raise OSError(0, f"ReadFile 短读：请求 {n}，得到 {got.value}")

    def write_at(self, off, buf, n):
        self._seek(off)
        put = wt.DWORD(0)
        if not kernel32.WriteFile(self.h, buf, n, byref(put), None):
            raise OSError(ctypes.get_last_error(), f"WriteFile(off={off}, len={n}) 失败")
        if put.value != n:
            raise OSError(0, f"WriteFile 短写：期望 {n}，写入 {put.value}")

    def close(self):
        if self.h:
            kernel32.CloseHandle(self.h)
            self.h = None

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.close()


def alloc(size):
    p = kernel32.VirtualAlloc(None, size, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE)
    if not p:
        raise MemoryError(f"VirtualAlloc({size}) 失败")
    return p


def free(p):
    if p:
        kernel32.VirtualFree(p, 0, MEM_RELEASE)


class MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [
        ("dwLength", wt.DWORD),
        ("dwMemoryLoad", wt.DWORD),
        ("ullTotalPhys", ctypes.c_ulonglong),
        ("ullAvailPhys", ctypes.c_ulonglong),
        ("ullTotalPageFile", ctypes.c_ulonglong),
        ("ullAvailPageFile", ctypes.c_ulonglong),
        ("ullTotalVirtual", ctypes.c_ulonglong),
        ("ullAvailVirtual", ctypes.c_ulonglong),
        ("ullAvailExtendedVirtual", ctypes.c_ulonglong),
    ]


kernel32.GlobalMemoryStatusEx.argtypes = [POINTER(MEMORYSTATUSEX)]
kernel32.GlobalMemoryStatusEx.restype = wt.BOOL


def memory_status():
    m = MEMORYSTATUSEX()
    m.dwLength = ctypes.sizeof(m)
    if not kernel32.GlobalMemoryStatusEx(byref(m)):
        raise OSError(ctypes.get_last_error(), "GlobalMemoryStatusEx 失败")
    return m


def disk_space(root):
    avail = ctypes.c_ulonglong(0)
    total = ctypes.c_ulonglong(0)
    free_bytes = ctypes.c_ulonglong(0)
    if not kernel32.GetDiskFreeSpaceExW(root, byref(avail), byref(total), byref(free_bytes)):
        raise OSError(ctypes.get_last_error(), f"GetDiskFreeSpaceExW 失败：{root}")
    return avail.value, total.value


def free_space(root):
    return disk_space(root)[0]


def human(n):
    if n >= (1 << 30):
        return f"{n / float(1 << 30):.2f} GiB"
    return f"{n / float(1 << 20):.1f} MiB"


def percentiles(lat_ms):
    s = sorted(lat_ms)
    n = len(s)
    if n == 0:
        return 0.0, 0.0
    return s[n // 2], s[min(n - 1, int(n * 0.95))]


def choose_volume():
    """加速打开之后让用户在 CLI 里选被测盘（不通过命令行参数指定）。"""
    volumes = list_candidate_volumes()
    if not volumes:
        raise PreconditionError(
            "没有找到可选的卷（固定盘 / 可移动盘）；请确认加速已经打开、盘符已经回来")
    print("请选择被测盘：")
    for i, (root, label, avail, total) in enumerate(volumes, 1):
        name = label or "(无卷标)"
        print(f"  {i}) {root}  {name}  可用 {human(avail)} / 总 {human(total)}")
    while True:
        try:
            text = input("序号：").strip()
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
            return volumes[index - 1][0]
        print("序号超出范围。")


# ======================= 进度条 =======================

class _SimpleBar:
    """tqdm 缺席时的兜底：一行百分比，接口只要求 update / close / n。"""

    def __init__(self, total, desc, unit):
        self.total = total
        self.desc = desc
        self.n = 0.0

    def update(self, n=1):
        self.n += n
        pct = (self.n * 100.0 / self.total) if self.total else 100.0
        sys.stdout.write(f"\r  {self.desc} {pct:5.1f}%  {self.n:.0f}/{self.total:.0f}")
        sys.stdout.flush()

    def close(self):
        sys.stdout.write("\n")
        sys.stdout.flush()


class Bar:
    """线程安全的进度条包装（并发阶段多个 worker 都会 update）。"""

    def __init__(self, total, desc, unit="", unit_scale=False):
        self._lock = threading.Lock()
        if tqdm is not None:
            self._bar = tqdm(total=total, desc=desc, unit=unit,
                             unit_scale=unit_scale, ncols=100, leave=False)
        else:
            self._bar = _SimpleBar(total, desc, unit)

    @property
    def n(self):
        return self._bar.n

    def update(self, n=1):
        with self._lock:
            self._bar.update(n)

    def close(self):
        self._bar.close()


# ======================= 操作记录（环形，直接落盘） =======================

class OpsLog:
    """最近若干条操作的环形记录。

    每条操作**先写 start、再发 IO，完成后回写同一槽位改成结果**：进程被杀时，
    文件里最后一条就是"有 start 没 end"，正好定位卡在哪。写的是页缓存里的
    64 字节、不 fsync（进程死亡不丢，断电才丢）。
    """

    def __init__(self, path, enabled):
        self.enabled = enabled
        self.fd = None
        self.lock = threading.Lock()
        self.seq = 0
        if not enabled:
            return
        self.fd = os.open(path, os.O_RDWR | os.O_CREAT | os.O_TRUNC | os.O_BINARY)
        left = RING_SLOTS * REC_SIZE
        zeros = b"\0" * 65536
        while left > 0:
            n = min(left, len(zeros))
            os.write(self.fd, zeros[:n])
            left -= n

    def _put(self, seq, tid, op, state, off, length, exp, act, old):
        rec = struct.pack(REC_FMT, seq, time.perf_counter_ns(), tid, op, state,
                          off, length, exp, act, old)
        pos = (seq % RING_SLOTS) * REC_SIZE
        if hasattr(os, "pwrite"):
            os.pwrite(self.fd, rec, pos)
        else:
            self.lock.acquire()
            try:
                os.lseek(self.fd, pos, os.SEEK_SET)
                os.write(self.fd, rec)
            finally:
                self.lock.release()

    def start(self, tid, op, off, length, old=b"\0" * 8):
        if not self.enabled:
            return None
        with self.lock:
            self.seq += 1
            seq = self.seq
        self._put(seq, tid, op, 0, off, length, b"\0" * 8, b"\0" * 8, old)
        return seq

    def end(self, seq, tid, op, off, length, exp=b"\0" * 8, act=b"\0" * 8,
            old=b"\0" * 8, state=1):
        if seq is None or not self.enabled:
            return
        self._put(seq, tid, op, state, off, length, exp, act, old)

    def close(self):
        if self.fd is not None:
            os.close(self.fd)
            self.fd = None


def dump_ops(path, limit=100):
    try:
        with open(path, "rb") as f:
            raw = f.read()
    except OSError:
        return []
    recs = []
    for off in range(0, len(raw) - REC_SIZE + 1, REC_SIZE):
        fields = struct.unpack(REC_FMT, raw[off:off + REC_SIZE])
        if fields[0] == 0:
            continue
        recs.append(fields)
    recs.sort(key=lambda r: r[0])
    return recs[-limit:]


def format_op(rec):
    seq, ts, tid, op, state, off, length, exp, act, old = rec
    op_name = "W" if op == 1 else "R"
    state_name = {0: "start", 1: "ok", 2: "mismatch", 3: "error"}.get(state, str(state))
    return (f"seq={seq} t={ts} tid={tid} {op_name} {state_name} off={off} len={length} "
            f"exp={exp.hex()} act={act.hex()} old={old.hex()}")


# ======================= 环境与配置 =======================

def config_path():
    program_data = os.environ.get("ProgramData") or r"C:\ProgramData"
    return os.path.join(program_data, "FlyDisk", "config.json")


def list_candidate_volumes():
    """列出可选的卷：固定盘与可移动盘，排除系统盘（FlyDisk 本身也不允许加速系统盘）。"""
    mask = kernel32.GetLogicalDrives()
    system_drive = (os.environ.get("SystemDrive") or "C:").upper()
    out = []
    for i in range(26):
        if not (mask & (1 << i)):
            continue
        root = f"{chr(ord('A') + i)}:\\"
        if root[:2].upper() == system_drive:
            continue
        if kernel32.GetDriveTypeW(root) not in (DRIVE_FIXED, DRIVE_REMOVABLE):
            continue
        label = ctypes.create_unicode_buffer(261)
        fs = ctypes.create_unicode_buffer(261)
        serial = wt.DWORD(0)
        maxlen = wt.DWORD(0)
        flags = wt.DWORD(0)
        if not kernel32.GetVolumeInformationW(root, label, 261, byref(serial),
                                              byref(maxlen), byref(flags), fs, 261):
            continue
        try:
            avail, total = disk_space(root)
        except OSError:
            continue
        out.append((root, label.value, avail, total))
    return out


def load_config():
    path = config_path()
    if not os.path.exists(path):
        raise PreconditionError(f"找不到配置文件 {path}（先以管理员运行一次 FlyDisk）")
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            return json.load(f)
    except (OSError, ValueError) as ex:
        raise PreconditionError(f"读不了配置文件 {path}：{ex}")


def pick_threshold():
    """读 EvictionThreshold / StopCachingThreshold；读不到或无效就按默认 0.8 估算并给出说明。"""
    try:
        cfg = load_config()
    except PreconditionError as ex:
        return 0.8, 0.9, f"{ex}；按默认阈值 0.8 估算 L1"
    threshold = float(cfg.get("EvictionThreshold", 0.8))
    stop = float(cfg.get("StopCachingThreshold", 0.9))
    if not (0.0 < threshold < stop <= 1.0):
        return 0.8, 0.9, f"config.json 的阈值无效（{threshold} / {stop}）；按默认 0.8 估算 L1"
    return threshold, stop, ""


def volume_sector_size(root):
    """卷的逻辑扇区大小（4Kn 为 4096，512e 为 512）。"""
    spc = wt.DWORD(0)
    bps = wt.DWORD(0)
    free_clusters = wt.DWORD(0)
    total_clusters = wt.DWORD(0)
    if not kernel32.GetDiskFreeSpaceW(root, byref(spc), byref(bps),
                                      byref(free_clusters), byref(total_clusters)):
        raise OSError(ctypes.get_last_error(), f"GetDiskFreeSpaceW 失败：{root}")
    return max(512, bps.value)


def _device_io_control(handle, code, in_buf, in_size, out_buf, out_size):
    returned = wt.DWORD(0)
    if not kernel32.DeviceIoControl(handle, code, in_buf, in_size, out_buf, out_size,
                                    byref(returned), None):
        raise OSError(ctypes.get_last_error(), f"DeviceIoControl(0x{code:08X}) 失败")
    return returned.value


def _open_device(path):
    """用 0 访问权限打开设备（查属性足够）；不行再退回 GENERIC_READ。"""
    handle = kernel32.CreateFileW(path, 0, FILE_SHARE_ALL, None, OPEN_EXISTING, 0, None)
    if not handle or handle == INVALID_HANDLE:
        handle = kernel32.CreateFileW(path, GENERIC_READ, FILE_SHARE_ALL, None,
                                      OPEN_EXISTING, 0, None)
    if not handle or handle == INVALID_HANDLE:
        return None
    return handle


def _disk_bus_type(disk_number):
    handle = _open_device(f"\\\\.\\PhysicalDrive{disk_number}")
    if handle is None:
        return None
    try:
        query = ctypes.create_string_buffer(12)
        struct.pack_into("<II", query, 0, STORAGE_DEVICE_PROPERTY, 0)
        out = ctypes.create_string_buffer(1024)
        size = _device_io_control(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                                  ctypes.cast(query, c_void_p), 12,
                                  ctypes.cast(out, c_void_p), 1024)
        if size < 32:
            return None
        return struct.unpack_from("<I", out.raw, 28)[0]
    except OSError:
        return None
    finally:
        kernel32.CloseHandle(handle)


def volume_bus_types(root):
    """该卷所在所有物理盘的 BusType（iSCSI 是 9）；查不到返回空列表。"""
    handle = _open_device("\\\\.\\" + root[:2])
    if handle is None:
        return []
    disks = []
    try:
        out = ctypes.create_string_buffer(1024)
        size = _device_io_control(handle, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, None, 0,
                                  ctypes.cast(out, c_void_p), 1024)
        count = struct.unpack_from("<I", out.raw, 0)[0]
        count = min(count, max(0, (size - 8) // 24))
        for i in range(count):
            disks.append(struct.unpack_from("<I", out.raw, 8 + i * 24)[0])
    except OSError:
        return []
    finally:
        kernel32.CloseHandle(handle)

    buses = []
    for disk in disks:
        bus = _disk_bus_type(disk)
        if bus is not None:
            buses.append(bus)
    return buses


def parse_size_gib(text):
    """'2' / '1.5g' / '512m' / '64k' → 字节数（无后缀按 GiB）。非法输入返回 0。"""
    t = text.strip().lower().replace(" ", "")
    if not t:
        return 0
    mult = 1.0
    if t.endswith("gib") or t.endswith("gb"):
        t = t[:-3] if t.endswith("gib") else t[:-2]
    elif t.endswith("mib") or t.endswith("mb"):
        mult = 1.0 / 1024
        t = t[:-3] if t.endswith("mib") else t[:-2]
    elif t.endswith("kib") or t.endswith("kb"):
        mult = 1.0 / (1024 * 1024)
        t = t[:-3] if t.endswith("kib") else t[:-2]
    elif t.endswith("g"):
        t = t[:-1]
    elif t.endswith("m"):
        mult = 1.0 / 1024
        t = t[:-1]
    elif t.endswith("k"):
        mult = 1.0 / (1024 * 1024)
        t = t[:-1]
    try:
        value = float(t)
    except ValueError:
        return 0
    if value <= 0:
        return 0
    return int(value * mult * (1 << 30))


def prompt_size(block):
    """让用户输入测试文件大小，返回向上对齐到块大小的字节数。"""
    while True:
        try:
            text = input("测试文件大小（GiB，可写 512m / 1.5g）：")
        except EOFError:
            raise KeyboardInterrupt
        size = parse_size_gib(text)
        if size < (64 << 20):
            print("至少要 64 MiB，请重新输入。")
            continue
        return size + (-size) % block


# ======================= 校验表与内容 =======================

def digest_block(data, start, block):
    return hashlib.blake2b(memoryview(data)[start:start + block], digest_size=8).digest()


def check_chunk(hashes, data, off, block):
    """逐块比对哈希；返回 (块号, 期望, 实际) 或 None。"""
    base = off // block
    for i in range(len(data) // block):
        idx = base + i
        want = bytes(hashes[idx * 8:idx * 8 + 8])
        got = digest_block(data, i * block, block)
        if got != want:
            return idx, want, got
    return None


def generate(path, size, block, bar):
    """顺序写满文件，同时逐块建哈希表（表只在内存里，不落盘）。"""
    hashes = bytearray((size // block) * 8)
    chunk = block * max(1, CHUNK // block)
    buf = alloc(chunk)
    try:
        with RawFile(path, True, create=True) as f:
            off = 0
            blk = 0
            while off < size:
                n = min(chunk, size - off)
                data = os.urandom(n)
                ctypes.memmove(buf, data, n)
                f.write_at(off, buf, n)
                for i in range(0, n, block):
                    pos = (blk + i // block) * 8
                    hashes[pos:pos + 8] = hashlib.blake2b(
                        data[i:i + block], digest_size=8).digest()
                blk += n // block
                off += n
                bar.update(n)
    finally:
        free(buf)
    return hashes


def sequential_read(path, size, block, hashes, desc):
    """整文件顺序读一遍并逐块校验，返回 (字节数, 秒, 每次 IO 的毫秒延迟)。"""
    chunk = block * max(1, CHUNK // block)
    buf = alloc(chunk)
    lat = []
    bar = Bar(size, desc, unit="B", unit_scale=True)
    try:
        with RawFile(path, False) as f:
            off = 0
            while off < size:
                n = min(chunk, size - off)
                t0 = time.perf_counter()
                f.read_at(off, buf, n)
                lat.append((time.perf_counter() - t0) * 1000.0)
                data = ctypes.string_at(buf, n)
                bad = check_chunk(hashes, data, off, block)
                if bad is not None:
                    idx, want, got = bad
                    raise VerifyError(desc, idx, block,
                                      f"哈希不符 期望 {want.hex()} 实际 {got.hex()}")
                off += n
                bar.update(n)
    finally:
        bar.close()
        free(buf)
    return size, sum(lat) / 1000.0, lat


def rebuild_hashes(ctx, blk_lo, blk_hi):
    """共享热点区被写成"版本块"之后，重新读一遍把哈希表恢复成权威值。"""
    block = ctx["block"]
    chunk = block * max(1, CHUNK // block)
    off = blk_lo * block
    end = blk_hi * block
    buf = alloc(chunk)
    try:
        with RawFile(ctx["path"], False) as f:
            while off < end:
                n = min(chunk, end - off)
                f.read_at(off, buf, n)
                data = ctypes.string_at(buf, n)
                base = off // block
                for i in range(n // block):
                    pos = (base + i) * 8
                    ctx["hashes"][pos:pos + 8] = digest_block(data, i * block, block)
                off += n
    finally:
        free(buf)


# ======================= 阶段实现 =======================

def phase_sequential(ctx, tag):
    size, secs, lat = sequential_read(ctx["path"], ctx["size"], ctx["block"],
                                      ctx["hashes"], tag)
    mibs = size / float(1 << 20) / secs if secs > 0 else 0.0
    p50, p95 = percentiles(lat)
    blocks = ctx["nblocks"]
    line = (f"[{tag}] {human(size)} / {secs:.1f}s = {mibs:.1f} MiB/s  "
            f"p50 {p50:.1f}ms p95 {p95:.1f}ms  校验 {blocks}/{blocks} 通过")
    print(line)
    ctx["phases"].append({"name": tag, "line": line, "mibs": mibs, "p50": p50, "p95": p95})


def phase_write(ctx, args):
    block = ctx["block"]
    align = ctx["align"]
    size = ctx["size"]
    nblocks = ctx["nblocks"]
    hashes = ctx["hashes"]
    log = ctx["opslog"]
    rnd = random.Random(ctx["seed"])

    piece_buf = alloc(block * 2)
    enc_buf = alloc(block * 2)
    lat = []
    bar = Bar(args.write_ops, "write", unit="次")
    t_start = time.perf_counter()
    ops = 0
    try:
        with RawFile(ctx["path"], True) as f:
            while ops < args.write_ops:
                kind = rnd.randrange(3)
                blk = rnd.randrange(0, nblocks - 1)
                if kind == 0:          # 整块
                    start, length = blk * block, block
                elif kind == 1:        # 块内部分覆盖
                    start = blk * block + rnd.randrange(0, block // align) * align
                    length = align
                else:                  # 骑块边界
                    start = (blk + 1) * block - align
                    length = 2 * align
                if start + length > size:
                    continue

                lo = start // block
                hi = (start + length - 1) // block
                enc_off = lo * block
                enc_len = (hi - lo + 1) * block

                t0 = time.perf_counter()
                f.read_at(enc_off, enc_buf, enc_len)
                lat.append((time.perf_counter() - t0) * 1000.0)
                old = ctypes.string_at(enc_buf, enc_len)
                for b in range(lo, hi + 1):
                    got = digest_block(old, (b - lo) * block, block)
                    want = bytes(hashes[b * 8:b * 8 + 8])
                    if got != want:
                        raise VerifyError("write", b, block,
                                          f"写前哈希不符 期望 {want.hex()} 实际 {got.hex()}")

                rel = start - enc_off
                new = bytearray(old)
                new[rel:rel + length] = os.urandom(length)
                ctypes.memmove(piece_buf, bytes(new[rel:rel + length]), length)
                old_hash = bytes(hashes[lo * 8:lo * 8 + 8])

                seq = log.start(0, 1, start, length, old_hash)
                t0 = time.perf_counter()
                f.write_at(start, piece_buf, length)
                lat.append((time.perf_counter() - t0) * 1000.0)

                t0 = time.perf_counter()
                f.read_at(enc_off, enc_buf, enc_len)
                lat.append((time.perf_counter() - t0) * 1000.0)
                back = ctypes.string_at(enc_buf, enc_len)

                if back != bytes(new):
                    diff = 0
                    while diff < len(back) and back[diff] == new[diff]:
                        diff += 1
                    bad_blk = lo + diff // block
                    log.end(seq, 0, 1, start, length, old_hash, b"\0" * 8,
                            old_hash, 2)
                    raise VerifyError("write", bad_blk, block,
                                      f"写后回读不符（绝对偏移 {enc_off + diff}）")

                for b in range(lo, hi + 1):
                    pos = b * 8
                    hashes[pos:pos + 8] = digest_block(back, (b - lo) * block, block)
                log.end(seq, 0, 1, start, length,
                        bytes(hashes[lo * 8:lo * 8 + 8]), b"\0" * 8, old_hash, 1)
                ops += 1
                bar.update(1)
    finally:
        bar.close()
        free(piece_buf)
        free(enc_buf)

    secs = time.perf_counter() - t_start
    p50, p95 = percentiles(lat)
    line = (f"[write] {args.write_ops} 次 通过 {ops}/{args.write_ops}  不符 0  "
            f"{secs:.1f}s  IO p50 {p50:.1f}ms p95 {p95:.1f}ms")
    print(line)
    ctx["phases"].append({"name": "write", "line": line, "p50": p50, "p95": p95})


def _read_verify(f, ctx, blk, buf, log, tid, phase):
    block = ctx["block"]
    off = blk * block
    seq = log.start(tid, 0, off, block)
    t0 = time.perf_counter()
    f.read_at(off, buf, block)
    dt = (time.perf_counter() - t0) * 1000.0
    data = ctypes.string_at(buf, block)
    want = bytes(ctx["hashes"][blk * 8:blk * 8 + 8])
    got = digest_block(data, 0, block)
    if got != want:
        log.end(seq, tid, 0, off, block, want, got, b"\0" * 8, 2)
        raise VerifyError(phase, blk, block, f"哈希不符 期望 {want.hex()} 实际 {got.hex()}")
    log.end(seq, tid, 0, off, block, want, got, b"\0" * 8, 1)
    return dt


def _write_full_block(f, ctx, blk, buf, piece_buf, log, tid, phase):
    block = ctx["block"]
    off = blk * block
    old_hash = bytes(ctx["hashes"][blk * 8:blk * 8 + 8])
    seq = log.start(tid, 1, off, block, old_hash)
    data = os.urandom(block)
    ctypes.memmove(piece_buf, data, block)
    f.write_at(off, piece_buf, block)
    f.read_at(off, buf, block)
    back = ctypes.string_at(buf, block)
    if back != data:
        diff = 0
        while diff < block and back[diff] == data[diff]:
            diff += 1
        log.end(seq, tid, 1, off, block, old_hash, b"\0" * 8, old_hash, 2)
        raise VerifyError(phase, blk, block, f"写后回读不符（块内偏移 {diff}）")
    new_hash = digest_block(back, 0, block)
    ctx["hashes"][blk * 8:blk * 8 + 8] = new_hash
    log.end(seq, tid, 1, off, block, new_hash, b"\0" * 8, old_hash, 1)


def _region_worker(ctx, blk_lo, blk_hi, ops, deadline, seed, tid, log, bar, out, stop,
                   phase):
    """分区并发：每个线程只碰自己那段，哈希表更新无需加锁。"""
    block = ctx["block"]
    rnd = random.Random(seed)
    buf = alloc(block)
    piece_buf = alloc(block)
    lat = []
    reads = writes = 0
    try:
        with RawFile(ctx["path"], True) as f:
            while blk_hi > blk_lo and not stop.is_set():
                if deadline is not None and time.perf_counter() >= deadline:
                    break
                if ops is not None and reads + writes >= ops:
                    break
                blk = rnd.randrange(blk_lo, blk_hi)
                if rnd.random() < READ_RATIO:
                    lat.append(_read_verify(f, ctx, blk, buf, log, tid, phase))
                    reads += 1
                else:
                    _write_full_block(f, ctx, blk, buf, piece_buf, log, tid, phase)
                    writes += 1
                if bar is not None:
                    bar.update(1)
    finally:
        free(buf)
        free(piece_buf)
    out.append((reads, writes, lat))


def _is_pattern_block(data, pattern_sectors):
    """按扇区判断：每个扇区内部的字节必须一致，且值在 pattern 范围内。

    放宽到扇区级是为了一次 4 KiB 写可能被拆成多个扇区落盘，读线程恰好读到一半时
    会看到"前半个旧值、后半个新值"——那是写入的正常现象，不该算错误。
    """
    step = len(pattern_sectors[0])
    for i in range(0, len(data), step):
        first = data[i]
        if first >= len(pattern_sectors):
            return False
        seg = data[i:i + step]
        if seg != pattern_sectors[first][:len(seg)]:
            return False
    return True


def prefill_hot(ctx, blk_lo, blk_hi):
    """把热点区顺序写成全 0（pattern 0），保证任何读到的块都已经有合法版本。"""
    block = ctx["block"]
    chunk = block * max(1, (1 << 20) // block)
    buf = alloc(chunk)
    try:
        ctypes.memset(buf, 0, chunk)
        with RawFile(ctx["path"], True) as f:
            off = blk_lo * block
            end = blk_hi * block
            while off < end:
                n = min(chunk, end - off)
                f.write_at(off, buf, n)
                off += n
    finally:
        free(buf)


def _hot_worker(ctx, blk_lo, blk_hi, ops, seed, tid, log, bar, out, stop):
    """共享热点区：写的是 16 种"版本块"，读到的每个扇区必须是其中一种。"""
    block = ctx["block"]
    patterns = ctx["patterns"]
    pattern_sectors = ctx["pattern_sectors"]
    rnd = random.Random(seed)
    buf = alloc(block)
    piece_buf = alloc(block)
    reads = writes = 0
    try:
        with RawFile(ctx["path"], True) as f:
            while reads + writes < ops and not stop.is_set():
                blk = rnd.randrange(blk_lo, blk_hi)
                off = blk * block
                if rnd.random() < READ_RATIO:
                    seq = log.start(tid, 0, off, block)
                    f.read_at(off, buf, block)
                    data = ctypes.string_at(buf, block)
                    ok = _is_pattern_block(data, pattern_sectors)
                    log.end(seq, tid, 0, off, block, b"\0" * 8,
                            digest_block(data, 0, block), b"\0" * 8, 1 if ok else 2)
                    if not ok:
                        raise VerifyError("concurrent", blk, block,
                                          "热点区读到的不是任何已写入的版本")
                    reads += 1
                else:
                    p = rnd.randrange(PATTERN_COUNT)
                    seq = log.start(tid, 1, off, block)
                    ctypes.memmove(piece_buf, patterns[p], block)
                    f.write_at(off, piece_buf, block)
                    log.end(seq, tid, 1, off, block, b"\0" * 8, b"\0" * 8,
                            b"\0" * 8, 1)
                    writes += 1
                if bar is not None:
                    bar.update(1)
    finally:
        free(buf)
        free(piece_buf)
    out.append((reads, writes))


def run_guarded(jobs, stop):
    """启动一组线程；任一抛出即置 stop 并把异常带回主线程。"""
    errors = []

    def runner(job):
        try:
            job()
        except BaseException as ex:
            errors.append(ex)
            stop.set()

    threads = [threading.Thread(target=runner, args=(job,), daemon=True) for job in jobs]
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    if errors:
        raise errors[0]


def phase_concurrent(ctx, args):
    block = ctx["block"]
    nblocks = ctx["nblocks"]
    threads = max(1, min(args.threads, 64))
    total = max(1, args.write_ops)
    ops_a = max(1, total * 2 // 3)
    ops_b = max(1, total - ops_a)

    hot_blocks = max(1, min(HOT_REGION_BYTES // block, nblocks // 5 or 1))
    region_end = nblocks - hot_blocks
    if region_end < threads * 2:
        raise PreconditionError("测试文件太小，无法分区并发（把 L2 上限或阈值调大一点）")

    ctx["patterns"] = [bytes([i]) * block for i in range(PATTERN_COUNT)]
    ctx["pattern_sectors"] = [bytes([i]) * ctx["sector"] for i in range(PATTERN_COUNT)]
    prefill_hot(ctx, region_end, nblocks)
    stop = threading.Event()
    log = ctx["opslog"]
    per_worker = max(1, ops_a // threads)
    hot_per_worker = max(1, ops_b // threads)
    bar = Bar(ops_a + ops_b, "concurrent", unit="次")
    out_region = []
    out_hot = []

    jobs = []
    for i in range(threads):
        lo = region_end * i // threads
        hi = region_end * (i + 1) // threads
        jobs.append(lambda lo=lo, hi=hi, i=i: _region_worker(
            ctx, lo, hi, per_worker, None, ctx["seed"] + i + 1, i + 1,
            log, bar, out_region, stop, "concurrent"))
    for i in range(threads):
        jobs.append(lambda i=i: _hot_worker(
            ctx, region_end, nblocks, hot_per_worker, ctx["seed"] + 100 + i,
            i + 1, log, bar, out_hot, stop))

    try:
        run_guarded(jobs, stop)
    finally:
        bar.close()

    rebuild_hashes(ctx, region_end, nblocks)

    reads = sum(item[0] for item in out_region)
    writes = sum(item[1] for item in out_region)
    hot_reads = sum(item[0] for item in out_hot)
    hot_writes = sum(item[1] for item in out_hot)
    lat = [x for item in out_region for x in item[2]]
    p50, p95 = percentiles(lat)
    line = (f"[concurrent] 分区 读 {reads} / 写 {writes}，热点 读 {hot_reads} / 写 {hot_writes}"
            f"  不符 0  IO p50 {p50:.1f}ms p95 {p95:.1f}ms")
    print(line)
    ctx["phases"].append({"name": "concurrent", "line": line, "p50": p50, "p95": p95})


def phase_soak(ctx, args):
    block = ctx["block"]
    nblocks = ctx["nblocks"]
    threads = max(1, min(args.threads, 64))
    hot_blocks = max(1, min(HOT_REGION_BYTES // block, nblocks // 5 or 1))
    region_end = nblocks - hot_blocks
    if region_end < threads * 2:
        raise PreconditionError("测试文件太小，无法分区并发（把 L2 上限或阈值调大一点）")

    stop = threading.Event()
    errors = []
    log = ctx["opslog"]
    deadline = time.perf_counter() + args.seconds
    out = []

    def runner(job):
        try:
            job()
        except BaseException as ex:
            errors.append(ex)
            stop.set()

    jobs = []
    for i in range(threads):
        lo = region_end * i // threads
        hi = region_end * (i + 1) // threads
        jobs.append(lambda lo=lo, hi=hi, i=i: _region_worker(
            ctx, lo, hi, None, deadline, ctx["seed"] + 200 + i, i + 1,
            log, None, out, stop, "soak"))

    workers = [threading.Thread(target=runner, args=(job,), daemon=True) for job in jobs]
    bar = Bar(args.seconds, "soak", unit="s")
    t0 = time.perf_counter()
    for t in workers:
        t.start()
    try:
        while any(t.is_alive() for t in workers):
            elapsed = min(time.perf_counter() - t0, float(args.seconds))
            if elapsed > bar.n:
                bar.update(elapsed - bar.n)
            time.sleep(0.2)
        if bar.n < args.seconds:
            bar.update(args.seconds - bar.n)
    finally:
        bar.close()
    for t in workers:
        t.join()
    if errors:
        raise errors[0]

    reads = sum(item[0] for item in out)
    writes = sum(item[1] for item in out)
    lat = [x for item in out for x in item[2]]
    p50, p95 = percentiles(lat)
    line = (f"[soak] {args.seconds}s 读 {reads} / 写 {writes}  不符 0  "
            f"IO p50 {p50:.1f}ms p95 {p95:.1f}ms")
    print(line)
    ctx["phases"].append({"name": "soak", "line": line, "p50": p50, "p95": p95})


def flydisk_running():
    try:
        out = subprocess.run(
            ["tasklist", "/FI", "IMAGENAME eq FlyDisk.exe", "/NH"],
            capture_output=True, text=True, timeout=10,
            creationflags=0x08000000).stdout
        return "FlyDisk.exe" in out
    except Exception:
        return False


def phase_disk(ctx):
    print("请停止加速 → 退出软件 → 重新联机该盘，完成后按回车继续 …")
    while True:
        try:
            input()
        except EOFError:
            raise KeyboardInterrupt
        if flydisk_running():
            print("FlyDisk.exe 仍在运行，请先退出软件后再回车。")
            continue
        if not os.path.exists(ctx["path"]):
            print("测试文件不可访问：请确认该盘已重新联机、盘符未变。")
            continue
        if os.path.getsize(ctx["path"]) != ctx["size"]:
            print("文件大小与记录不符，请确认没有换盘或改名。")
            continue
        break

    size, secs, lat = sequential_read(ctx["path"], ctx["size"], ctx["block"],
                                      ctx["hashes"], "disk")
    mibs = size / float(1 << 20) / secs if secs > 0 else 0.0
    p50, p95 = percentiles(lat)
    blocks = ctx["nblocks"]
    line = (f"[disk] 未加速回读 {human(size)} / {secs:.1f}s = {mibs:.1f} MiB/s  "
            f"校验 {blocks}/{blocks} 通过 | FlyDisk 已退出 | 卷已联机  "
            f"p50 {p50:.1f}ms p95 {p95:.1f}ms")
    print(line)
    ctx["phases"].append({"name": "disk", "line": line, "mibs": mibs, "p50": p50, "p95": p95})


# ======================= 报告 =======================

def build_report_body(ctx):
    lines = []
    lines.append("## 结论")
    lines.append("")
    lines.append(ctx.get("conclusion") or "通过")
    lines.append("")

    lines.append("## 环境")
    lines.append("")
    lines.append(f"- 开始时间：{ctx.get('started', '')}")
    lines.append(f"- 盘符：{ctx.get('root', '')}")
    lines.append(f"- 测试文件：{ctx.get('path', '')}（{human(ctx.get('size', 0))}，用户指定）")
    lines.append(f"- 配置文件：{ctx.get('cfg_path', '')}{ctx.get('cfg_note', '')}")
    lines.append(f"- 操作记录：{ctx.get('ops_path', '')}（收尾不删除）")
    lines.append(f"- EvictionThreshold：{ctx.get('threshold', 0):.2f}，"
                 f"StopCachingThreshold：{ctx.get('stop_threshold', 0):.2f}")
    lines.append(f"- 内存：总计 {human(ctx.get('total_phys', 0))}，"
                 f"加速前占用 {ctx.get('baseline', 0):.1%}，"
                 f"L1 估算（参考）{human(ctx.get('l1_bytes', 0))}")
    lines.append(f"- 卷扇区：{ctx.get('sector', 0)} 字节，块大小 / 对齐：{ctx.get('block', 0)} 字节，"
                 f"块数 {ctx.get('nblocks', 0)}")
    if ctx.get("bus_note"):
        lines.append(f"- 提示：{ctx['bus_note']}")
    lines.append(f"- 并发线程：{ctx.get('threads', 0)}，写次数：{ctx.get('write_ops', 0)}，"
                 f"soak 时长：{ctx.get('seconds', 0)}s，随机种子：{ctx.get('seed', 0)}")
    lines.append(f"- Python：{sys.version.split()[0]}，"
                 f"tqdm：{'可用' if tqdm is not None else '未安装（用内置进度条）'}")
    lines.append("")

    lines.append("## 阶段结果")
    lines.append("")
    for item in ctx.get("phases", []):
        lines.append(f"- {item['line']}")
    lines.append("")

    if not ctx.get("all_ok", False) and ctx.get("ops_path"):
        recs = dump_ops(ctx["ops_path"], 100)
        lines.append("## 最近的操作记录")
        lines.append("")
        if recs:
            for rec in recs:
                lines.append(f"- {format_op(rec)}")
        else:
            lines.append("- 本次没有记录到操作（失败发生在写阶段之前，或未启用记录）")
        lines.append("")

    if ctx.get("cleanup_error"):
        lines.append("## 收尾")
        lines.append("")
        lines.append(f"- 删除测试文件失败：{ctx['cleanup_error']}")
        lines.append("")

    lines.append("## 说明")
    lines.append("")
    lines.append("- 所有 IO 均带 FILE_FLAG_NO_BUFFERING；写句柄另加 FILE_FLAG_WRITE_THROUGH。")
    lines.append("- 冷读/热读的吞吐只含 IO，校验哈希不计入计时。")
    lines.append("- [disk] 一项是在未加速、FlyDisk 已退出的状态下整文件回读的结果。")
    lines.append("")
    return "\n".join(lines)


def write_report(ctx):
    try:
        body = build_report_body(ctx)
        data = body.encode("utf-8")
        digest = hashlib.sha256(data).hexdigest()
        header = (
            "# FlyDisk 冒烟压测报告\n\n"
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


# ======================= 起步 / 收尾 / 主流程 =======================

def startup(ctx):
    threshold, stop, note = pick_threshold()
    mem = memory_status()
    baseline = mem.dwMemoryLoad / 100.0
    total = mem.ullTotalPhys
    ctx.update({
        "threshold": threshold,
        "stop_threshold": stop,
        "baseline": baseline,
        "total_phys": total,
        "cfg_path": config_path(),
        "cfg_note": note,
    })

    print(f"已记录环境：内存基线 {baseline:.0%}、物理内存 {human(total)}、"
          f"EvictionThreshold {threshold:.2f}")
    if note:
        print(f"注意：{note}")
    print(f"报告将写入：{ctx['report_path']}")
    print("请现在打开加速，确认盘符可用后按回车继续 …")
    try:
        input()
    except EOFError:
        raise KeyboardInterrupt

    ctx["root"] = choose_volume()
    ctx["dir"] = os.path.join(ctx["root"], "FlyDisk.Smoke")
    ctx["path"] = os.path.join(ctx["dir"], "test.bin")

    buses = volume_bus_types(ctx["root"])
    if buses and BUS_ISCSI not in buses:
        names = "/".join(BUS_NAMES.get(b, str(b)) for b in buses)
        ctx["bus_note"] = (f"所选卷所在磁盘的总线类型是 {names}，"
                           "看起来加速没有生效，这次测的是原盘。")
        print(f"注意：{ctx['bus_note']}")

    # 块大小 / 对齐只取该卷的扇区大小，不再依赖 L2 配置
    sector = volume_sector_size(ctx["root"])
    block = ALIGN if sector <= ALIGN else sector
    l1_bytes = int(max(0.0, threshold - baseline) * total)
    print(f"L1 估算（参考）：{human(l1_bytes)}  "
          f"= max(0, {threshold:.2f} − {baseline:.0%}) × {human(total)}")
    size = prompt_size(block)
    ctx.update({
        "sector": sector,
        "block": block,
        "align": block,
        "l1_bytes": l1_bytes,
        "size": size,
        "nblocks": size // block,
    })

    try:
        os.makedirs(ctx["dir"], exist_ok=True)
        os.makedirs(ctx["report_dir"], exist_ok=True)
    except OSError as ex:
        raise PreconditionError(f"盘符不可用（{ctx['root']}）：{ex}")

    need = int(size * 1.2) + (64 << 20)
    avail = free_space(ctx["root"])
    if avail < need:
        raise PreconditionError(
            f"目标卷剩余空间不足：需要约 {human(need)}，当前 {human(avail)}；"
            "请调小测试文件大小")

    ctx["phases"] = []
    ctx["opslog"] = OpsLog(ctx["ops_path"], ctx["need_log"])

    bar = Bar(size, "gen", unit="B", unit_scale=True)
    try:
        ctx["hashes"] = generate(ctx["path"], size, block, bar)
    finally:
        bar.close()


def teardown(ctx):
    """静默收尾：关句柄、全部通过才删测试文件、写报告；操作记录永不删除。"""
    log = ctx.get("opslog")
    if log is not None:
        try:
            log.close()
        except OSError:
            pass
    if ctx.get("all_ok"):
        try:
            if ctx.get("path") and os.path.exists(ctx["path"]):
                os.remove(ctx["path"])
            if ctx.get("dir") and os.path.isdir(ctx["dir"]) and not os.listdir(ctx["dir"]):
                os.rmdir(ctx["dir"])
        except OSError as ex:
            ctx["cleanup_error"] = str(ex)
    write_report(ctx)


def parse_phase(text):
    names = [p.strip().lower() for p in text.split(",") if p.strip()]
    if not names:
        raise argparse.ArgumentTypeError("至少要给一个阶段")
    for name in names:
        if name not in PHASES:
            raise argparse.ArgumentTypeError(
                f"未知阶段 {name}；可选：{', '.join(PHASES)}")
    picked = set(names)
    return [p for p in PHASES if p in picked]


def parse_args():
    parser = argparse.ArgumentParser(
        description="FlyDisk 冒烟压测：先打开加速，脚本再让你选被测盘",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="阶段：cold / hot / write / concurrent / soak（默认全跑）")
    parser.add_argument("--phase", type=parse_phase, default=list(PHASES),
                        help="逗号分隔的阶段名，默认全部")
    parser.add_argument("--threads", type=int, default=THREADS,
                        help=f"并发线程数（默认 {THREADS}）")
    parser.add_argument("--write-ops", type=int, default=WRITE_OPS,
                        help=f"write / concurrent 的写次数（默认 {WRITE_OPS}）")
    parser.add_argument("--seconds", type=int, default=SOAK_SECONDS,
                        help=f"soak 时长秒数（默认 {SOAK_SECONDS}）")
    return parser.parse_args()


def main():
    args = parse_args()
    if args.threads < 1 or args.write_ops < 1 or args.seconds < 1:
        print("前置条件不满足：--threads / --write-ops / --seconds 必须 ≥ 1")
        return 2

    script_dir = os.path.dirname(os.path.abspath(__file__))
    report_dir = os.path.join(script_dir, "docs", "测试报告")
    stamp = time.strftime("%Y%m%d-%H%M%S")
    ctx = {
        "started": time.strftime("%Y-%m-%d %H:%M:%S"),
        "root": "",
        "dir": "",
        "path": "",
        "report_dir": report_dir,
        "report_path": os.path.join(report_dir, f"smoke-{stamp}.md"),
        "ops_path": os.path.join(report_dir, f"ops-{stamp}.bin"),
        "threads": args.threads,
        "write_ops": args.write_ops,
        "seconds": args.seconds,
        "seed": int(time.time()),
        "phases": [],
        "need_log": any(p in args.phase for p in ("write", "concurrent", "soak")),
        "all_ok": False,
        "opslog": None,
        "conclusion": "",
        "cleanup_error": "",
        "size": 0,
        "block": 0,
        "align": 0,
        "nblocks": 0,
        "sector": 0,
        "cfg_note": "",
        "bus_note": "",
        "hashes": bytearray(),
    }

    code = 0
    try:
        startup(ctx)

        if "cold" in args.phase:
            phase_sequential(ctx, "cold")
        if "hot" in args.phase:
            phase_sequential(ctx, "hot")
        if "write" in args.phase:
            phase_write(ctx, args)
        if "concurrent" in args.phase:
            phase_concurrent(ctx, args)
        if "soak" in args.phase:
            phase_soak(ctx, args)
        phase_disk(ctx)

        ctx["conclusion"] = "通过：所有阶段内容校验一致，未加速回读一致。"
    except PreconditionError as ex:
        ctx["conclusion"] = f"前置条件不满足：{ex}"
        print(f"前置条件不满足：{ex}")
        code = 2
    except VerifyError as ex:
        ctx["conclusion"] = f"内容不符：{ex}"
        print(f"内容不符：{ex}")
        code = 3
    except KeyboardInterrupt:
        ctx["conclusion"] = "用户中断"
        print("已中断。")
        code = 5
    except OSError as ex:
        ctx["conclusion"] = f"IO 异常：{ex}"
        print(f"IO 异常：{ex}")
        code = 4
    except Exception as ex:
        ctx["conclusion"] = f"未预期异常：{type(ex).__name__}: {ex}"
        print(f"未预期异常：{type(ex).__name__}: {ex}")
        code = 5
    finally:
        ctx["all_ok"] = (code == 0)
        teardown(ctx)

    return code


if __name__ == "__main__":
    sys.exit(main())
