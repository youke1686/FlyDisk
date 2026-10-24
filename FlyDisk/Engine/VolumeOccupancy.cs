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

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FlyDisk.Engine.HandleScan;
using FlyDisk.Localization;
using Microsoft.Win32.SafeHandles;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 「占用查询」的引擎入口：**盘号 → 该盘各卷的设备路径前缀 → 全系统句柄扫描**。
    ///
    /// 为什么不用盘符 / 挂载点：卷可能没有盘符，甚至没有挂载点；而句柄名里始终是
    /// `\Device\HarddiskVolumeN\…`，按它匹配既完整又与盘符无关。
    /// </summary>
    internal static class VolumeOccupancy
    {
        private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;

        /// <summary>一次扫描的完整入口。取消粒度：每 256 个句柄 / 每个进程。</summary>
        public static OccupancyScanResult Scan(int diskNumber, Action<int, int>? onProgress, CancellationToken ct)
        {
            List<VolumePrefix> volumes = GetVolumePrefixes(diskNumber, out string? locateError, out string trace);
            if (volumes.Count == 0)
            {
                // 定位失败时把中间事实一并落盘并显示：枚举到几个卷、每个卷打开/IOCTL/反查的结果。
                // 这句诊断只在定位失败时出现，正常路径看不到。
                LogService.DebugFile($"占用查询：定位磁盘 {diskNumber} 的卷失败。{Environment.NewLine}{trace}");

                return new OccupancyScanResult
                {
                    Error = (locateError ?? Locale.T("occupy.err.noVolume"))
                            + Environment.NewLine + Environment.NewLine + trace,
                };
            }

            var prefixes = new List<string>(volumes.Count);
            var displayRoots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (VolumePrefix volume in volumes)
            {
                prefixes.Add(volume.DevicePrefix);
                displayRoots[volume.DevicePrefix] = volume.DisplayRoot;
            }

            return HandleInfoRetriever.FindOccupants(prefixes, displayRoots, onProgress, ct);
        }

        /// <summary>一个卷：句柄里出现的前缀 + 给人看的根路径</summary>
        private readonly record struct VolumePrefix(string DevicePrefix, string DisplayRoot);

        /// <summary>
        /// 找出属于 <paramref name="diskNumber"/> 的所有卷，返回 `\Device\HarddiskVolumeN\` 形式的前缀
        /// 以及它对应的可读根路径（`E:\`，没有挂载点时用卷 GUID 路径）。
        /// 定位不到时返回空表，并通过 <paramref name="trace"/> 给出逐卷的中间事实（便于排障）。
        /// </summary>
        private static List<VolumePrefix> GetVolumePrefixes(int diskNumber, out string? error, out string trace)
        {
            error = null;
            var prefixes = new List<VolumePrefix>();
            var notes = new List<string>();
            var volumeName = new StringBuilder(260);

            using SafeFindVolumeHandle find = FindFirstVolumeW(volumeName, volumeName.Capacity);
            if (find.IsInvalid)
            {
                error = Locale.T("occupy.err.enumerateVolumes", Marshal.GetLastWin32Error());
                trace = "FindFirstVolume 失败，错误码 " + Marshal.GetLastWin32Error();
                return prefixes;
            }

            notes.Add($"目标盘号：{diskNumber}");

            do
            {
                string volumePath = volumeName.ToString();
                volumeName.Clear();

                // 1) 这个卷里有没有落在目标盘上的 extent
                bool belongs = VolumeBelongsToDisk(volumePath, diskNumber, notes);
                if (!belongs) continue;

                // 2) \?\Volume{GUID}\ → Volume{GUID} → \Device\HarddiskVolumeN
                string dosName = volumePath.TrimEnd('\\');
                if (dosName.StartsWith(@"\\?\", StringComparison.Ordinal)) dosName = dosName.Substring(4);

                var target = new StringBuilder(260);
                uint rc = QueryDosDeviceW(dosName, target, (uint)target.Capacity);
                if (rc == 0)
                {
                    notes.Add($"  反查设备名失败：{dosName}，错误码 {Marshal.GetLastWin32Error()}");
                    continue;
                }

                string device = target.ToString();
                if (device.Length == 0)
                {
                    notes.Add($"  反查设备名为空：{dosName}");
                    continue;
                }

                notes.Add($"  命中：{volumePath} → {device}");
                prefixes.Add(new VolumePrefix(device.TrimEnd('\\') + "\\", GetDisplayRoot(volumePath)));
            }
            while (FindNextVolumeW(find, volumeName, volumeName.Capacity));

            if (prefixes.Count == 0) error = Locale.T("occupy.err.noVolume");
            if (notes.Count > 60)
            {
                notes.RemoveRange(60, notes.Count - 60);
                notes.Add("…（其余卷省略）");
            }
            trace = string.Join(Environment.NewLine, notes);
            return prefixes;
        }

        private static bool VolumeBelongsToDisk(string volumePath, int diskNumber, List<string> notes)
        {
            using SafeFileHandle volume = OpenVolume(volumePath, out int openError);
            if (volume.IsInvalid)
            {
                notes.Add($"{volumePath}：打开卷失败，错误码 {openError}");
                return false;
            }

            // 一块盘可能有多个 extent；给 8 个的余量（每个 extent 24 字节）
            var buffer = new byte[8 + 24 * 8];
            if (!DeviceIoControl(volume, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
                    IntPtr.Zero, 0, buffer, buffer.Length, out int returned, IntPtr.Zero))
            {
                notes.Add($"{volumePath}：IOCTL 失败，错误码 {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (returned < 8)
            {
                notes.Add($"{volumePath}：IOCTL 返回长度 {returned}，不足 8 字节");
                return false;
            }

            uint count = BitConverter.ToUInt32(buffer, 0);
            var found = new List<uint>();
            for (int i = 0; i < count; i++)
            {
                // DISK_EXTENT 的字段顺序是 **DiskNumber 在最前**，之后才是 StartingOffset / ExtentLength：
                //   ULONG DiskNumber (0) | 4 字节对齐填充 | LARGE_INTEGER StartingOffset (8) | LARGE_INTEGER ExtentLength (16)
                // 每个 extent 占 24 字节。之前误按"偏移/长度/盘号"解析，读到的是 ExtentLength 的低 32 位，永远匹配不上。
                int offset = 8 + i * 24;
                if (offset + 24 > returned) break;
                found.Add(BitConverter.ToUInt32(buffer, offset));
            }

            bool matches = found.Contains((uint)diskNumber);
            notes.Add($"{volumePath}：IOCTL 返回 {returned} 字节，extent 数 {count}，盘号 [{string.Join(",", found)}]"
                      + (matches ? " ← 命中" : string.Empty));
            return matches;
        }

        /// <summary>
        /// 卷 → 给人看的根路径（`E:\`）。没有挂载点时退回卷 GUID 路径 `\\?\Volume{…}\`，
        /// 这样结果里仍然能看出"打开的是哪个卷"。
        /// </summary>
        private static string GetDisplayRoot(string volumePath)
        {
            var buffer = new char[512];
            if (GetVolumePathNamesForVolumeNameW(volumePath, buffer, (uint)buffer.Length, out uint length) && length > 0)
            {
                int end = Array.IndexOf(buffer, '\0');
                if (end > 0) return new string(buffer, 0, end);
            }
            return volumePath;
        }

        /// <summary>
        /// 打开卷句柄（只需要"能发 IOCTL"，所以访问权限给 0）。
        ///
        /// **必须去掉 `FindFirstVolume` 返回名字的尾反斜杠**：在 `\\?\` 前缀下，带尾反斜杠的
        /// `\\?\Volume{GUID}\` 会返回 `ERROR_PATH_NOT_FOUND (3)`，去掉后可正常打开。
        /// </summary>
        private static SafeFileHandle OpenVolume(string volumePath, out int error)
        {
            SafeFileHandle handle = CreateFileW(volumePath.TrimEnd('\\'), 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero,
                OPEN_EXISTING, 0, IntPtr.Zero);
            error = Marshal.GetLastWin32Error();
            return handle;
        }

        #region Win32

        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;

        private sealed class SafeFindVolumeHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            public SafeFindVolumeHandle() : base(true) { }
            protected override bool ReleaseHandle() => FindVolumeClose(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFindVolumeHandle FindFirstVolumeW(StringBuilder lpszVolumeName, int cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextVolumeW(SafeFindVolumeHandle hFindVolume,
            StringBuilder lpszVolumeName, int cchBufferLength);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindVolumeClose(IntPtr hFindVolume);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess,
            uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize,
            out int lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumePathNamesForVolumeNameW(string lpszVolumeName,
            [Out] char[] lpszVolumePathNames, uint cchBufferLength, out uint lpcchReturnLength);

        #endregion
    }
}
