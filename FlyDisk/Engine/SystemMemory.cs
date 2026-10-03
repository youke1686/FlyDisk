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

using System.Runtime.InteropServices;

namespace FlyDisk.Engine
{
    /// <summary>一次系统内存读数（<c>GlobalMemoryStatusEx</c> 的裁剪版）</summary>
    internal readonly struct MemoryStatus
    {
        /// <summary>物理内存总量（字节）</summary>
        public readonly ulong TotalPhysicalBytes;

        /// <summary>系统内存使用率（0–100）</summary>
        public readonly uint MemoryLoadPercent;

        public MemoryStatus(ulong totalPhysicalBytes, uint memoryLoadPercent)
        {
            TotalPhysicalBytes = totalPhysicalBytes;
            MemoryLoadPercent = memoryLoadPercent;
        }
    }

    /// <summary>
    /// 系统内存读数的**唯一定义点**：缓存淘汰的水位节拍（<c>CacheService.WaterLevelTick</c>）与设置界面的
    /// 「预计额外内存」都取自这里。
    ///
    /// **两边必须同源**：界面上那条 L1 估算（"当前内存到开始淘汰水位的差距"）用的正是引擎真正据以触发淘汰的
    /// 那个 <c>dwMemoryLoad</c>；若各读各的，提示就会与实际行为对不上。
    /// 沿用 未解决的疑点.md TD-20 的"同一事实只定义一处"。
    /// </summary>
    internal static class SystemMemory
    {
        /// <summary>读一次系统内存状态；失败返回 <c>false</c>（调用方各自决定兜底值）</summary>
        public static bool TryRead(out MemoryStatus status)
        {
            var raw = new MEMORYSTATUSEX();
            if (!GlobalMemoryStatusEx(raw))
            {
                status = default;
                return false;
            }

            status = new MemoryStatus(raw.ullTotalPhys, raw.dwMemoryLoad);
            return true;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad; // 系统内存使用百分比
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;

            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);
    }
}
