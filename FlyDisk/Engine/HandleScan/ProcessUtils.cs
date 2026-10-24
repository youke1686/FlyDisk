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
// Copyright (c) 2022 PolarGoose。删掉了查模块、查属主等本项目用不到的部分。
// 许可证见 THIRD-PARTY-NOTICES.md。

using Microsoft.Win32.SafeHandles;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace FlyDisk.Engine.HandleScan
{
    /// <summary>进程级小工具（只保留"打开别人进程"与"取可执行文件路径"）</summary>
    internal static class ProcessUtils
    {
        /// <summary>
        /// 以 `PROCESS_DUP_HANDLE | QUERY_INFORMATION | VM_READ` 打开目标进程。
        /// **失败是常态**：受保护进程、系统进程、权限不足都会打不开，调用方把它计为"未知占用者"。
        /// </summary>
        internal static SafeProcessHandle? OpenProcessToDuplicateHandle(UIntPtr pid)
        {
            SafeProcessHandle handle = WinApi.OpenProcess(
                WinApi.ProcessAccessRights.PROCESS_DUP_HANDLE |
                WinApi.ProcessAccessRights.PROCESS_QUERY_INFORMATION |
                WinApi.ProcessAccessRights.PROCESS_VM_READ,
                false, pid);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                return null;
            }
            return handle;
        }

        /// <summary>可执行文件全路径；取不到返回 null（受保护进程很常见）</summary>
        internal static string? GetProcessExeFullName(SafeProcessHandle processHandle)
        {
            for (int capacity = 2048; capacity <= 65536; capacity *= 2)
            {
                var buffer = new StringBuilder(capacity);
                int size = capacity;

                if (!WinApi.QueryFullProcessImageName(processHandle, 0, buffer, ref size))
                {
                    if (Marshal.GetLastWin32Error() != WinApi.ERROR_INSUFFICIENT_BUFFER) return null;
                    continue;
                }

                return buffer.ToString(0, size);
            }

            return null;
        }

        /// <summary>进程名（不含路径与扩展名）；进程已退出或打不开时返回 null</summary>
        internal static string? GetProcessName(int pid)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(pid);
                return process.ProcessName;
            }
            catch
            {
                return null;
            }
        }
    }
}
