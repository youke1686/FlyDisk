#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
卷锁定实验：实测“哪些权限的句柄会挡住 FSCTL_LOCK_VOLUME”。

用途
----
确定占用查询里“哪些句柄真的会影响卷被卸下”。结论只对**本次实验的操作系统版本**成立。

流程（与需求一一对应）
----------------------
  选盘
  建一个文件，不拿句柄
  尝试落锁                      ← 基线，失败就说明盘不干净，直接退出
  循环：
      拿一种句柄
      测试落锁                  ← 锁定 + 解锁，只报告成功与否，不终止
      放掉句柄
      尝试落锁                  ← 锁定 + 解锁，失败即终止整个测试
  删掉文件

术语
----
* 测试落锁：锁定 + 解锁，输出成败；无论成败都继续。
* 尝试落锁：锁定 + 解锁，失败一次（默认重试 3 次）就终止整个测试。

用法
----
  以管理员身份运行：
      python volume_lock_probe.py E:
      python volume_lock_probe.py E: --strict
      python volume_lock_probe.py E: --path E:\\some\\existing.file

注意
----
* 只对**可移动/测试用卷**使用，脚本会拒绝系统盘。
* FSCTL_LOCK_VOLUME 成功时，NTFS 会把卷当作“已卸载”；脚本会立刻解锁，
  并在每轮开始前读一次目录，让文件系统重新挂载。
* 任何一步失败都会尽量清理（关闭句柄、删除探测文件）。
"""

import argparse
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

# ---------------------------------------------------------------- Win32 常量

GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
GENERIC_EXECUTE = 0x20000000

FILE_SHARE_READ = 0x00000001
FILE_SHARE_WRITE = 0x00000002
FILE_SHARE_DELETE = 0x00000004

OPEN_EXISTING = 3
OPEN_ALWAYS = 4
FILE_ATTRIBUTE_NORMAL = 0x00000080
FILE_FLAG_BACKUP_SEMANTICS = 0x02000000
FILE_FLAG_DELETE_ON_CLOSE = 0x04000000

PAGE_EXECUTE_READ = 0x20
FILE_MAP_READ = 0x0004
FILE_MAP_EXECUTE = 0x0020

FILE_NOTIFY_CHANGE_FILE_NAME = 0x00000001

FSCTL_LOCK_VOLUME = 0x00090018
FSCTL_UNLOCK_VOLUME = 0x0009001C

INVALID_HANDLE_VALUE = ctypes.c_void_p(-1).value
ERROR_SUCCESS = 0

# 访问位（文件对象）
FILE_READ_DATA = 0x00000001        # 目录：FILE_LIST_DIRECTORY
FILE_WRITE_DATA = 0x00000002       # 目录：FILE_ADD_FILE
FILE_APPEND_DATA = 0x00000004      # 目录：FILE_ADD_SUBDIRECTORY
FILE_READ_EA = 0x00000008
FILE_WRITE_EA = 0x00000010
FILE_EXECUTE = 0x00000020          # 目录：FILE_TRAVERSE
FILE_DELETE_CHILD = 0x00000040
FILE_READ_ATTRIBUTES = 0x00000080
FILE_WRITE_ATTRIBUTES = 0x00000100
DELETE = 0x00010000
READ_CONTROL = 0x00020000
WRITE_DAC = 0x00040000
WRITE_OWNER = 0x00080000
SYNCHRONIZE = 0x00100000

# Explorer 在卷根上实际持有的掩码：SYNCHRONIZE | FILE_READ_ATTRIBUTES | FILE_LIST_DIRECTORY
EXPLORER_ROOT_MASK = SYNCHRONIZE | FILE_READ_ATTRIBUTES | FILE_READ_DATA

# ---------------------------------------------------------------- P/Invoke

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)

kernel32.CreateFileW.argtypes = [
    wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
    wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE,
]
kernel32.CreateFileW.restype = wintypes.HANDLE

kernel32.DeviceIoControl.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD,
    wintypes.LPVOID, wintypes.DWORD, ctypes.POINTER(wintypes.DWORD), wintypes.LPVOID,
]
kernel32.DeviceIoControl.restype = wintypes.BOOL

kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
kernel32.CloseHandle.restype = wintypes.BOOL

kernel32.CreateFileMappingW.argtypes = [
    wintypes.HANDLE, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD,
    wintypes.DWORD, wintypes.LPCWSTR,
]
kernel32.CreateFileMappingW.restype = wintypes.HANDLE

kernel32.MapViewOfFile.argtypes = [
    wintypes.HANDLE, wintypes.DWORD, wintypes.DWORD, wintypes.DWORD, ctypes.c_size_t,
]
kernel32.MapViewOfFile.restype = wintypes.LPVOID

kernel32.UnmapViewOfFile.argtypes = [wintypes.LPVOID]
kernel32.UnmapViewOfFile.restype = wintypes.BOOL

kernel32.FindFirstChangeNotificationW.argtypes = [
    wintypes.LPCWSTR, wintypes.BOOL, wintypes.DWORD,
]
kernel32.FindFirstChangeNotificationW.restype = wintypes.HANDLE

kernel32.FindCloseChangeNotification.argtypes = [wintypes.HANDLE]
kernel32.FindCloseChangeNotification.restype = wintypes.BOOL


def last_error_text(code=None):
    code = ctypes.get_last_error() if code is None else code
    return f"{code} ({ctypes.FormatError(code).strip()})"


def is_invalid(handle):
    return handle is None or handle == INVALID_HANDLE_VALUE or handle == 0


def close(handle):
    if handle and not is_invalid(handle):
        kernel32.CloseHandle(handle)


# ---------------------------------------------------------------- 卷操作

def open_volume(letter):
    """打开卷句柄。只用于锁/解锁，不要用它读写数据。"""
    path = f"\\\\.\\{letter.strip().rstrip(':')}:"
    handle = kernel32.CreateFileW(
        path,
        GENERIC_READ | GENERIC_WRITE,
        FILE_SHARE_READ | FILE_SHARE_WRITE,
        None,
        OPEN_EXISTING,
        0,
        None,
    )
    if is_invalid(handle):
        # 退一步：有些环境不允许卷的写访问，读访问也够发 IOCTL
        handle = kernel32.CreateFileW(
            path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            None, OPEN_EXISTING, 0, None,
        )
    if is_invalid(handle):
        raise RuntimeError(f"打开卷 {path} 失败：{last_error_text()}")
    return handle


def lock_volume(volume_handle):
    """锁定。返回 (是否成功, 错误码)。"""
    returned = wintypes.DWORD(0)
    ok = kernel32.DeviceIoControl(
        volume_handle, FSCTL_LOCK_VOLUME, None, 0, None, 0,
        ctypes.byref(returned), None,
    )
    return bool(ok), ctypes.get_last_error()


def unlock_volume(volume_handle):
    returned = wintypes.DWORD(0)
    ok = kernel32.DeviceIoControl(
        volume_handle, FSCTL_UNLOCK_VOLUME, None, 0, None, 0,
        ctypes.byref(returned), None,
    )
    return bool(ok), ctypes.get_last_error()


def poke_volume(letter):
    """读一次目录，唤醒可能被“锁定=卸载”弄睡的文件系统。"""
    try:
        os.listdir(f"{letter.rstrip(':')}:\\")
    except OSError:
        pass


def lock_cycle(letter, timeout=5.0):
    """一次完整的“锁定 + 解锁”，**每次都新开卷句柄**。

    为什么不能复用句柄：`FSCTL_LOCK_VOLUME` 成功后，NTFS 把这个卷当作“已卸载”，
    原先那个卷句柄随之失效——之后再拿它去锁，会一直返回 21 (ERROR_NOT_READY)，
    哪怕文件句柄早就放掉了。每次重开句柄并留出重新挂载的时间，才测得到真实结果。
    """
    deadline = time.time() + timeout
    last_err = 0

    while True:
        try:
            handle = open_volume(letter)
        except RuntimeError:
            last_err = ctypes.get_last_error()
            if time.time() >= deadline:
                return False, last_err
            poke_volume(letter)
            time.sleep(0.05)
            continue

        try:
            ok, err = lock_volume(handle)
            if ok:
                unlock_volume(handle)
                time.sleep(0.05)      # 给 NTFS 一点时间从"已卸载"状态回来
                return True, ERROR_SUCCESS
            last_err = err
        finally:
            close(handle)

        # 21 = 卷还没重新挂载：等一下再来，别把它当成“被句柄挡住”
        if err == 21 and time.time() < deadline:
            poke_volume(letter)
            time.sleep(0.05)
            continue
        return False, last_err


def test_lock(letter, label, retries=5, delay=0.25):
    """测试落锁：锁 + 解锁，报告结果，绝不影响流程。

    **必须重试**：上一轮锁卷成功后 NTFS 把卷当作已卸载，解锁后有一小段重新挂载的窗口，
    此时再锁会返回 5(拒绝访问)，与"被句柄挡住"无法区分。真正的阻挡会一直失败，
    所以重试不会把"挡"误判成"不挡"，只能滤掉这个瞬态。
    """
    last_err = 0
    for attempt in range(1, retries + 1):
        ok, err = lock_cycle(letter)
        if ok:
            note = "" if attempt == 1 else f"（第 {attempt} 次才成功）"
            print(f"    {label:<52} 锁: 成功{note}")
            return True
        last_err = err
        if attempt < retries:
            poke_volume(letter)
            time.sleep(delay)

    print(f"    {label:<52} 锁: 失败，错误码 {last_err} "
          f"({ctypes.FormatError(last_err).strip()})，已重试 {retries} 次")
    return False


def attempt_lock(letter, retries, label):
    """尝试落锁：失败即终止整个测试。"""
    for attempt in range(1, retries + 1):
        ok, err = lock_cycle(letter)
        if ok:
            suffix = "" if attempt == 1 else f"（第 {attempt} 次才成功）"
            print(f"    {label:<52} 锁: 成功{suffix}")
            return True
        if attempt < retries:
            time.sleep(0.2)
        else:
            print(f"    {label:<52} 锁: 失败，错误码 {err} "
                  f"({ctypes.FormatError(err).strip()})")
    return False


# ---------------------------------------------------------------- 句柄用例

def open_file(path, access):
    handle = kernel32.CreateFileW(
        path, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        None, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None,
    )
    if is_invalid(handle):
        raise RuntimeError(f"打开文件失败（access=0x{access:X}）：{last_error_text()}")
    return handle


def open_directory(path, access):
    handle = kernel32.CreateFileW(
        path, access, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        None, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, None,
    )
    if is_invalid(handle):
        raise RuntimeError(f"打开目录失败（access=0x{access:X}）：{last_error_text()}")
    return handle


# 文件句柄用例：(名称, access mask)
FILE_CASES = [
    ("FILE_READ_ATTRIBUTES",                   FILE_READ_ATTRIBUTES),
    ("FILE_READ_ATTRIBUTES | SYNCHRONIZE",     FILE_READ_ATTRIBUTES | SYNCHRONIZE),
    ("READ_CONTROL",                           READ_CONTROL),
    ("FILE_READ_EA",                           FILE_READ_EA),
    ("FILE_WRITE_ATTRIBUTES",                  FILE_WRITE_ATTRIBUTES | SYNCHRONIZE),
    ("FILE_READ_DATA",                         FILE_READ_DATA | SYNCHRONIZE),
    ("GENERIC_READ",                           GENERIC_READ),
    ("FILE_WRITE_DATA",                        FILE_WRITE_DATA | SYNCHRONIZE),
    ("FILE_APPEND_DATA",                       FILE_APPEND_DATA | SYNCHRONIZE),
    ("DELETE",                                 DELETE | SYNCHRONIZE),
    ("WRITE_DAC",                              WRITE_DAC | READ_CONTROL),
    ("FILE_EXECUTE（仅打开）",                  FILE_EXECUTE | SYNCHRONIZE),
]


def run_file_case(letter, path, name, access):
    handle = None
    try:
        handle = open_file(path, access)
        return test_lock(letter, name)
    except RuntimeError as ex:
        print(f"    {name:<52} 打不开：{ex}")
        return None
    finally:
        close(handle)


def run_file_mapping_case(letter, path):
    """打开 + 建立映像节：这一格才是 FILE_EXECUTE 真正会挡的组合。"""
    handle = mapping = view = None
    label = "FILE_EXECUTE + CreateFileMapping"
    try:
        handle = open_file(path, GENERIC_READ | GENERIC_EXECUTE)
        mapping = kernel32.CreateFileMappingW(handle, None, PAGE_EXECUTE_READ, 0, 0, None)
        if is_invalid(mapping):
            print(f"    {label:<52} 建映像失败：{last_error_text()}")
            return None
        view = kernel32.MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_EXECUTE, 0, 0, 0)
        if not view:
            print(f"    {label:<52} 映射失败：{last_error_text()}")
            return None
        return test_lock(letter, label)
    except RuntimeError as ex:
        print(f"    {label:<52} 打不开：{ex}")
        return None
    finally:
        if view:
            kernel32.UnmapViewOfFile(view)
        close(mapping)
        close(handle)


def run_directory_case(letter, dir_path, name, access):
    handle = None
    try:
        handle = open_directory(dir_path, access)
        return test_lock(letter, name)
    except RuntimeError as ex:
        print(f"    {name:<52} 打不开：{ex}")
        return None
    finally:
        close(handle)


def run_notification_case(letter, dir_path):
    label = "目录变更通知（FindFirstChangeNotification）"
    handle = kernel32.FindFirstChangeNotificationW(dir_path, False, FILE_NOTIFY_CHANGE_FILE_NAME)
    if is_invalid(handle):
        print(f"    {label:<52} 创建失败：{last_error_text()}")
        return None
    try:
        return test_lock(letter, label)
    finally:
        kernel32.FindCloseChangeNotification(handle)


def run_foreign_case(letter, path, access, label, is_dir=False, hold_ms=5000):
    """由**另一个进程**持有句柄，本进程去锁卷。

    Explorer 的句柄就是这么来的：不在我们的进程里。如果跨进程能锁上、进程内锁不上，
    说明判据与"谁持有"有关，而不是权限位本身。
    """
    cmd = [sys.executable, os.path.abspath(__file__),
           "--hold", path, "--hold-access", hex(access), "--hold-ms", str(hold_ms)]
    if is_dir:
        cmd.append("--hold-dir")

    proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, text=True)
    try:
        line = proc.stdout.readline().strip()
        if line != "READY":
            print(f"    {label:<52} 子进程未能就绪：{line or '（无输出）'}")
            return None
        return test_lock(letter, label)
    finally:
        try:
            proc.kill()
        except OSError:
            pass
        proc.wait(timeout=5)


# ---------------------------------------------------------------- 主流程

def main():
    parser = argparse.ArgumentParser(description="实测哪些权限的句柄会挡住卷锁定")
    parser.add_argument("volume", nargs="?", help="盘符，例如 E: 或 E")
    parser.add_argument("--path", help="在卷上使用的已有文件（默认自动创建一个探测文件）")
    parser.add_argument("--retries", type=int, default=3, help="“尝试落锁”的重试次数（默认 3）")
    parser.add_argument("--strict", action="store_true", help="不重试，失败一次就终止")
    # 内部用：由子进程持有句柄，供"跨进程"用例使用
    parser.add_argument("--hold", help=argparse.SUPPRESS)
    parser.add_argument("--hold-access", default="0x80", help=argparse.SUPPRESS)
    parser.add_argument("--hold-ms", type=int, default=5000, help=argparse.SUPPRESS)
    parser.add_argument("--hold-dir", action="store_true", help=argparse.SUPPRESS)
    args = parser.parse_args()

    if args.hold:
        access = int(args.hold_access, 0)
        handle = open_directory(args.hold, access) if args.hold_dir else open_file(args.hold, access)
        print("READY", flush=True)
        try:
            time.sleep(max(0, args.hold_ms) / 1000.0)
        finally:
            close(handle)
        return 0

    if not args.volume:
        parser.error("需要盘符，例如 E:")

    letter = args.volume.strip().rstrip(":")
    if len(letter) != 1 or not letter.isalpha():
        print(f"盘符格式不对：{args.volume}")
        return 1

    system_drive = os.environ.get("SystemDrive", "C:").rstrip(":")
    if letter.upper() == system_drive.upper():
        print("拒绝在系统盘上做这个实验。")
        return 1

    retries = 1 if args.strict else max(1, args.retries)
    root = f"{letter}:\\"

    created_file = False
    created_dir = False
    probe_file = args.path or os.path.join(root, "flydisk_lock_probe.bin")
    probe_dir = os.path.join(root, "flydisk_lock_probe_dir")

    results = []

    try:
        print(f"== 选盘 ==\n卷：{letter}:")
        probe = open_volume(letter)      # 只验证能打开；锁操作每次都自己开新句柄
        close(probe)

        print("\n== 准备探测对象 ==")
        if not args.path:
            with open(probe_file, "wb") as f:      # 写完就关，不保留句柄
                f.write(b"flydisk lock probe\n")
            created_file = True
        print(f"文件：{probe_file}")

        try:
            os.makedirs(probe_dir, exist_ok=True)
            created_dir = True
            print(f"目录：{probe_dir}")
        except OSError as ex:
            print(f"目录创建失败（目录类用例将跳过）：{ex}")
            created_dir = False

        print("\n== 基线 ==")
        if not attempt_lock(letter, retries, "无任何句柄（基线）"):
            print("\n基线就锁不上：盘不干净（索引器 / 杀软 / 资源管理器？），先处理环境再测。")
            return 2

        print("\n== 对照：紧接着再锁一次（单次，不重试） ==")
        # 若这里失败、而基线成功，就证明解锁后存在“重新挂载窗口”，
        # 单次尝试会把瞬态误报成“被句柄挡住”——后面的用例因此必须重试。
        test_lock(letter, "无句柄，紧接上一轮锁定", retries=1)

        print("\n== 循环：拿一种句柄 → 测试落锁 → 放掉 → 尝试落锁 ==")
        for name, access in FILE_CASES:
            print(f"\n[{name}]  0x{access:X}")
            locked = run_file_case(letter, probe_file, name, access)
            results.append((name, locked))
            if not attempt_lock(letter, retries, "放掉句柄后"):
                print("释放后仍锁不上：环境已被污染，终止测试。")
                return 2

        print("\n[FILE_EXECUTE + CreateFileMapping]")
        locked = run_file_mapping_case(letter, probe_file)
        results.append(("FILE_EXECUTE + CreateFileMapping", locked))
        if not attempt_lock(letter, retries, "放掉映射后"):
            print("释放后仍锁不上：环境已被污染，终止测试。")
            return 2

        if created_dir:
            print("\n[目录句柄：FILE_LIST_DIRECTORY | SYNCHRONIZE]")
            locked = run_directory_case(letter, probe_dir,
                                        "FILE_LIST_DIRECTORY | SYNCHRONIZE",
                                        FILE_READ_DATA | SYNCHRONIZE)
            results.append(("目录：FILE_LIST_DIRECTORY | SYNCHRONIZE", locked))
            if not attempt_lock(letter, retries, "放掉目录句柄后"):
                print("释放后仍锁不上：环境已被污染，终止测试。")
                return 2

            print("\n[目录变更通知句柄]")
            locked = run_notification_case(letter, probe_dir)
            results.append(("目录变更通知", locked))
            if not attempt_lock(letter, retries, "放掉通知句柄后"):
                print("释放后仍锁不上：环境已被污染，终止测试。")
                return 2

        # ---- 下面两条复刻 Explorer：它的句柄开在**卷根**上，掩码 0x100081 ----
        print("\n[进程内：卷根 0x100081]")
        locked = run_directory_case(letter, root, "进程内：卷根 0x100081", EXPLORER_ROOT_MASK)
        results.append(("进程内：卷根 0x100081", locked))
        if not attempt_lock(letter, retries, "放掉卷根句柄后"):
            print("释放后仍锁不上：环境已被污染，终止测试。")
            return 2

        print("\n[跨进程：卷根 0x100081]")
        locked = run_foreign_case(letter, root, EXPLORER_ROOT_MASK,
                                  "跨进程：卷根 0x100081", is_dir=True)
        results.append(("跨进程：卷根 0x100081", locked))
        if not attempt_lock(letter, retries, "子进程退出后"):
            print("释放后仍锁不上：环境已被污染，终止测试。")
            return 2

        print("\n[跨进程：文件 FILE_READ_ATTRIBUTES]")
        locked = run_foreign_case(letter, probe_file, FILE_READ_ATTRIBUTES,
                                  "跨进程：文件 FILE_READ_ATTRIBUTES")
        results.append(("跨进程：文件 FILE_READ_ATTRIBUTES", locked))
        if not attempt_lock(letter, retries, "子进程退出后"):
            print("释放后仍锁不上：环境已被污染，终止测试。")
            return 2

        print("\n== 结论 ==")
        blocking = [n for n, ok in results if ok is False]
        harmless = [n for n, ok in results if ok is True]
        skipped = [n for n, ok in results if ok is None]

        print("挡住锁卷的：")
        for name in blocking or ["（无）"]:
            print(f"  - {name}")
        print("不挡锁卷的：")
        for name in harmless or ["（无）"]:
            print(f"  - {name}")
        if skipped:
            print("未能测试的：")
            for name in skipped:
                print(f"  - {name}")

        print("\n注意：结论只对本次操作系统版本成立；重启或系统更新后建议复测。")
        return 0
    finally:
        try:
            if created_file and os.path.exists(probe_file):
                os.remove(probe_file)
        except OSError as ex:
            print(f"清理探测文件失败：{ex}")
        try:
            if created_dir and os.path.isdir(probe_dir):
                os.rmdir(probe_dir)
        except OSError as ex:
            print(f"清理探测目录失败：{ex}")


if __name__ == "__main__":
    sys.exit(main())
