// Copyright (C) 2026 youke1686 (https://github.com/youke1686)
//
// This file is part of FlyDisk.
//
// FlyDisk is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// FlyDisk is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with FlyDisk.  If not, see <https://www.gnu.org/licenses/>.
//
// ---------------------------------------------------------------------------
// 本文件改编自 Handle2（https://github.com/PolarGoose/Handle2），MIT License，
// Copyright (c) 2022 PolarGoose。只保留本项目需要的几个入口。
// 许可证见 THIRD-PARTY-NOTICES.md。

using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyDisk.Engine.HandleScan
{
    /// <summary>枚举句柄所需的 Win32 原语</summary>
    internal static class WinApi
    {
        [Flags]
        internal enum ProcessAccessRights : uint
        {
            PROCESS_DUP_HANDLE = 0x0040,
            PROCESS_QUERY_INFORMATION = 0x0400,
            PROCESS_VM_READ = 0x0010,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            ProcessAccessRights dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            UIntPtr dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(
            SafeProcessHandle hSourceProcessHandle,
            IntPtr hSourceHandle,
            SafeProcessHandle hTargetProcessHandle,
            out SafeFileHandle lpTargetHandle,
            uint dwDesiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            uint dwOptions);

        /// <summary>把别的进程里的句柄复制到本进程；失败时返回一个"无效但可 Dispose"的句柄</summary>
        internal static SafeFileHandle DuplicateHandle(
            SafeProcessHandle currentProcess,
            SafeProcessHandle handleOwnerProcess,
            NtDll.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX handle)
        {
            bool ok = DuplicateHandle(
                handleOwnerProcess, handle.HandleValue, currentProcess, out SafeFileHandle duplicated,
                0, false, 0);
            return ok ? duplicated : new SafeFileHandle(IntPtr.Zero, true);
        }

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetCurrentProcess")]
        private static extern IntPtr GetCurrentProcessPrivate();

        internal static SafeProcessHandle GetCurrentProcess() =>
            new(GetCurrentProcessPrivate(), ownsHandle: false);

        internal const int ERROR_INSUFFICIENT_BUFFER = 122;

        /// <summary>`GetFileType` 的返回类型。**只有 <see cref="Disk"/> 才允许去查对象名**。</summary>
        internal enum FileType : uint
        {
            Unknown = 0,
            Disk = 0x0001,
            Char = 0x0002,
            Pipe = 0x0003,
            Remote = 0x8000,
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern FileType GetFileType(SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryFullProcessImageName(
            SafeProcessHandle hProcess, int dwFlags, StringBuilder? lpExeName, ref int lpdwSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryDosDeviceW(string lpDeviceName, [Out] StringBuilder lpTargetPath, uint ucchMax);
    }
}
