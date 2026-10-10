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
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DiskAccessLibrary.Win32;
using FlyDisk.Localization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 物理盘信息快照（在**脱机之前**采集一次）。
    /// 脱机后卷被卸载、设备栈只剩裸设备，型号/序列号/搜寻惩罚这些"设备身份"就查不到了，
    /// 而它们既是 INQUIRY 的上报来源，也是启动校验（可移动介质 / 是否机械盘）的依据。
    /// </summary>
    public sealed class PhysicalDiskInfo
    {
        public int DiskNumber { get; set; }
        public int BytesPerSector { get; set; }
        public long SizeBytes { get; set; }
        public bool IsOnline { get; set; }
        public bool IsReadOnly { get; set; }

        /// <summary>可移动介质（U 盘/读卡器等）——不做加速目标</summary>
        public bool RemovableMedia { get; set; }

        /// <summary>设备描述符里的厂商串（如 "ATA"）</summary>
        public string VendorId { get; set; } = string.Empty;

        /// <summary>设备描述符里的产品串（如 "ST1000DM010-2EP102"）</summary>
        public string ProductId { get; set; } = string.Empty;

        /// <summary>固件版本串</summary>
        public string ProductRevision { get; set; } = string.Empty;

        /// <summary>序列号（VPD 页 0x80 的上报来源）</summary>
        public string SerialNumber { get; set; } = string.Empty;

        /// <summary>是否报告"有搜寻惩罚"（机械盘特征；由 IOCTL_STORAGE_QUERY_PROPERTY 的 SeekPenalty 属性给出）</summary>
        public bool IncursSeekPenalty { get; set; }

        /// <summary>搜寻惩罚属性是否查询成功（查不到时按"未知"处理，不做机械盘判断，见 TargetService 的校验）</summary>
        public bool SeekPenaltyKnown { get; set; }

        /// <summary>
        /// 该盘当前挂载的卷盘符（如 <c>"C:"</c>、<c>"D:"</c>），按字母升序；无卷（未格式化 / 已脱机）时为空。
        /// **只在"盘还能被系统看到"时采集得到**（整盘脱机后卷消失），故由 <see cref="PhysicalDiskHandle.Enumerate"/>
        /// 在逐块 <see cref="PhysicalDiskHandle.Probe"/> 之后统一填一次（见 <see cref="PhysicalDiskHandle.FillDriveLetters"/>）。
        /// </summary>
        public List<string> DriveLetters { get; } = new();

        /// <summary>展示用的型号串（厂商 + 产品）</summary>
        public string Model
        {
            get
            {
                string vendor = VendorId.Trim();
                string product = ProductId.Trim();
                if (vendor.Length == 0) return product;
                if (product.Length == 0) return vendor;
                return vendor + " " + product;
            }
        }

        /// <summary>展示用的容量串</summary>
        public string SizeText => FormatSize(SizeBytes);

        private static string FormatSize(long bytes)
        {
            // 用 GiB 口径（1024 进制）；厂商标称的 GB 是 1000 进制，这里只在展示与日志里对照用，故标注单位
            double gib = (double)bytes / 1024 / 1024 / 1024;
            return gib >= 1 ? $"{gib:F1} GiB" : $"{(double)bytes / 1024 / 1024:F1} MiB";
        }
    }

    /// <summary>
    /// 物理盘的**原始扇区**访问：整盘脱机 → 独占句柄 → 按字节偏移读写。
    ///
    /// 独占是两层（见 阶段二-iSCSI块设备形态.md §3.6），缺一不可：
    /// 1. **整盘脱机**：<c>IOCTL_DISK_SET_DISK_ATTRIBUTES</c> 置 <c>DISK_ATTRIBUTE_OFFLINE</c>
    ///    （`diskpart offline disk` 的底层），让宿主 NTFS/卷管理器退场——否则两个 NTFS 实例同管一批扇区必然损坏；
    /// 2. **独占句柄**：<c>dwShareMode = 0</c> 打开 <c>\\.\PhysicalDriveN</c>，别人连原始扇区也拿不到。
    /// 两者都程序化做，不调 diskpart（外部进程、慢、错误难判）。
    ///
    /// **关闭时不自动联机**（2026-09-27 定下的口径）：盘归本程序管 ⇒ 程序没运行期间没人能写它
    /// ⇒ L2 账本（跨重启缓存）才敢直接采信。想看数据就再启动本程序并连接 iSCSI；
    /// 要把盘还给系统则用「磁盘管理 → 联机」（那之后跨重启缓存会被判为过期而作废）。
    /// 异常路径用 <see cref="RestoreAndClose"/> 把盘恢复原状，见其注释。
    ///
    /// 线程前提：读写的调用方是库的 target 工作线程（<c>SCSITarget</c> 每个 target 一个后台线程串行执行命令），
    /// 本类因此共用一个文件指针、不做加锁——**若将来把 IO 并发化，这里必须改成按偏移读写**。
    /// </summary>
    public sealed class PhysicalDiskHandle : IDisposable, IBlockSource
    {
        /// <summary>单次 ReadFile/WriteFile 的字节上限（1MB）。与大库 PhysicalDisk 取同一口径，避开设备/驱动对单次搬运量的隐式上限</summary>
        private const int MaxTransferBytes = 1024 * 1024;

        private SafeFileHandle? _handle;

        private PhysicalDiskHandle(int diskNumber, PhysicalDiskInfo info, SafeFileHandle handle, bool wasOnline)
        {
            DiskNumber = diskNumber;
            Info = info;
            _handle = handle;
            WasOnline = wasOnline;
        }

        /// <summary>物理盘号</summary>
        public int DiskNumber { get; }

        /// <summary>打开前采集的设备信息</summary>
        public PhysicalDiskInfo Info { get; }

        /// <summary>
        /// 我们打开它之前该盘是否处于**联机**状态。
        /// 这是"上次留下的 L2 账本是否可信"的判据之一：盘若曾是联机的，说明上次运行之后
        /// 它对别的程序可见过，期间可能被写过，账本一律作废（见 SsdCacheService 类注释）。
        /// </summary>
        public bool WasOnline { get; }

        /// <summary>盘容量（字节）</summary>
        public long SizeBytes => Info.SizeBytes;

        /// <summary>逻辑扇区大小（跟随源盘）</summary>
        public int BytesPerSector => Info.BytesPerSector;

        /// <summary>本块源的"身份与形状"（缓存层需要的那几项，见 <see cref="BlockSourceInfo"/>）</summary>
        public BlockSourceInfo ToBlockSourceInfo() => BlockSourceInfo.FromDisk(Info, WasOnline);

        #region 打开与关闭

        /// <summary>
        /// 只查询信息，不改变磁盘状态（设置对话框用它列出各盘，启动校验也先走它）。
        /// </summary>
        /// <exception cref="IOException">盘不存在 / 打不开 / 查询失败</exception>
        public static PhysicalDiskInfo Probe(int diskNumber)
        {
            if (diskNumber < 0)
                throw new ArgumentOutOfRangeException(nameof(diskNumber), Locale.T("engine.disk.badDiskNumber"));

            using SafeFileHandle probe = CreateDiskHandle(diskNumber, FileAccess.Read, FileShare.ReadWrite);
            if (probe.IsInvalid)
                throw new IOException(Locale.T("engine.disk.probeFailed", diskNumber, LastErrorText()));

            var info = new PhysicalDiskInfo { DiskNumber = diskNumber };

            DISK_GEOMETRY geometry = PhysicalDiskControl.GetDiskGeometryAndSize(probe, out long size);
            info.BytesPerSector = (int)geometry.BytesPerSector;
            info.SizeBytes = size;

            info.IsOnline = PhysicalDiskControl.GetOnlineStatus(probe, out bool isReadOnly);
            info.IsReadOnly = isReadOnly;

            FillDeviceIdentity(probe, info);
            TryQuerySeekPenalty(probe, info);
            return info;
        }

        /// <summary>
        /// 把该盘**整盘脱机**并**独占打开**，返回可直接读写原始扇区的句柄。
        /// 盘已经脱机时直接接管（不"先联机再脱机"）——这正是崩溃/强杀/断电后重新启动本程序的路径。
        /// </summary>
        /// <exception cref="IOException">脱机失败或无法独占打开</exception>
        public static PhysicalDiskHandle Open(int diskNumber)
        {
            PhysicalDiskInfo info = Probe(diskNumber);
            bool wasOnline = info.IsOnline;

            if (wasOnline)
            {
                using SafeFileHandle probe = CreateDiskHandle(diskNumber, FileAccess.ReadWrite, FileShare.Read);
                if (probe.IsInvalid)
                    throw new IOException(Locale.T("engine.disk.offlineNoWriteAccess", diskNumber, LastErrorText()));

                // persist = true：脱机属性必须**持久化**。否则重启后盘就自己回来了，
                // "程序没运行时没人写过它"这条判据不成立，跨重启的 L2 缓存也就无从谈起（见类注释）。
                if (!PhysicalDiskControl.SetOnlineStatus(probe, online: false, persist: true))
                    throw new IOException(Locale.T("engine.disk.offlineFailed", diskNumber));
            }

            SafeFileHandle handle = CreateDiskHandle(diskNumber, FileAccess.ReadWrite, FileShare.None, writeThrough: true);
            if (handle.IsInvalid)
            {
                // 退一步：只阻止别人写入。脱机已经让卷退场、数据本就拿不到，这里只是再上一道保险。
                int exclusiveError = Marshal.GetLastWin32Error();
                handle = CreateDiskHandle(diskNumber, FileAccess.ReadWrite, FileShare.Read, writeThrough: true);
                if (handle.IsInvalid)
                {
                    // 独占失败就不要把盘留在"看不见"的状态里：恢复成我们打开它之前的样子
                    if (wasOnline) SetOnlineBestEffort(diskNumber);
                    throw new IOException(
                        Locale.T("engine.disk.exclusiveOpenFailed", diskNumber, exclusiveError));
                }
                LogService.DebugFile($"磁盘 {diskNumber} 未能取得独占句柄（错误 {exclusiveError}），已退化为共享读打开；" +
                                     "该盘已脱机，数据仍不会被其他程序访问");
            }

            LogService.DebugFile($"磁盘 {diskNumber} 已脱机并打开（原状态：{(wasOnline ? "联机" : "脱机（接管）")}，" +
                                 $"{info.BytesPerSector} B/扇区，{info.SizeBytes} 字节，写透传）");
            return new PhysicalDiskHandle(diskNumber, info, handle, wasOnline);
        }

        /// <summary>
        /// **只读**打开（L2 校验用）：同样先确保整盘脱机（内容静止才谈得上比对），但句柄是
        /// <c>GENERIC_READ</c> —— 这是**操作系统层面**的只读，即使调用方代码有 bug 也写不进去，
        /// 于是"只读扫描"这条路径不可能弄坏源盘。关闭时与 <see cref="Dispose"/> 一样**保持脱机**。
        ///
        /// 与 <see cref="Open"/> 的差别：不要求独占（<c>FILE_SHARE_READ</c>）、不加写透传标志、不返回可写句柄。
        /// 校验/修复只回写 L2 容器（另一块盘上的 cache.dat），源盘一个字节都不写。
        /// </summary>
        /// <exception cref="IOException">脱机失败，或只读打开失败</exception>
        public static PhysicalDiskHandle OpenReadOnly(int diskNumber)
        {
            PhysicalDiskInfo info = Probe(diskNumber);
            bool wasOnline = info.IsOnline;

            if (wasOnline)
            {
                using SafeFileHandle probe = CreateDiskHandle(diskNumber, FileAccess.ReadWrite, FileShare.Read);
                if (probe.IsInvalid)
                    throw new IOException(Locale.T("engine.disk.offlineNoWriteAccess", diskNumber, LastErrorText()));

                // 与 Open 同口径：脱机属性必须持久化，否则重启后盘自己回来，"没人写过它"这条判据不成立
                if (!PhysicalDiskControl.SetOnlineStatus(probe, online: false, persist: true))
                    throw new IOException(Locale.T("engine.disk.offlineFailed", diskNumber));
            }

            SafeFileHandle handle = CreateDiskHandle(diskNumber, FileAccess.Read, FileShare.Read);
            if (handle.IsInvalid)
            {
                // 打不开就不要把盘留在"看不见"的状态里：恢复成我们打开它之前的样子
                if (wasOnline) SetOnlineBestEffort(diskNumber);
                throw new IOException(Locale.T("engine.disk.readOnlyOpenFailed", diskNumber, LastErrorText()));
            }

            LogService.DebugFile($"磁盘 {diskNumber} 已脱机并**只读**打开（原状态：{(wasOnline ? "联机" : "脱机（接管）")}）");
            return new PhysicalDiskHandle(diskNumber, info, handle, wasOnline);
        }

        /// <summary>
        /// 正常停止：关掉独占句柄，**盘保持脱机**（盘归本程序管，见类注释）。
        /// </summary>
        public void Dispose() => CloseHandle(online: false);

        /// <summary>
        /// 异常路径（启动中途失败）：关掉句柄并把盘**恢复到我们打开它之前的状态**——
        /// 原本联机就联机回来，原本就是脱机（上次异常终止）就仍然脱机。
        /// 启动失败不应该把用户的盘留在"看不见"的状态里。
        /// </summary>
        public void RestoreAndClose() => CloseHandle(online: WasOnline);

        private void CloseHandle(bool online)
        {
            SafeFileHandle? handle = _handle;
            _handle = null;
            if (handle != null)
            {
                try { handle.Dispose(); } catch { /* 关句柄失败没有补救手段，忽略 */ }
            }

            if (online)
            {
                SetOnlineBestEffort(DiskNumber);
            }
        }

        /// <summary>
        /// 把被本程序脱机的物理盘**重新联机**（等价于「磁盘管理 → 联机」/ `diskpart online disk`）。
        ///
        /// **由用户显式触发**，不挂在 <see cref="Dispose"/> 上——"停止加速后仍保持脱机"是刻意口径（见类注释）。
        /// <c>persist: true</c>：脱机属性当初就是持久化的，联机也必须持久化，否则重启后盘又变回脱机。
        /// 联机成功后顺带触发一次重新枚举（失败不算失败，见下）。
        ///
        /// 调用前提（由调用方保证，见 <c>TargetService.TryReonlineSourceDisk</c>）：本程序**没有**独占该盘，
        /// 且**没有** iSCSI 连接挂着它的克隆盘——否则系统视野里会同时出现两块同签名的盘。
        /// </summary>
        /// <returns>成功 <c>true</c>；失败 <c>false</c> 且 <paramref name="error"/> 给出可读原因（不抛）</returns>
        public static bool TryOnline(int diskNumber, out string error)
        {
            try
            {
                using SafeFileHandle probe = CreateDiskHandle(diskNumber, FileAccess.ReadWrite, FileShare.Read);
                if (probe.IsInvalid)
                {
                    error = Locale.T("engine.online.openFailed", diskNumber, LastErrorText());
                    return false;
                }

                // 幂等：本就在线时直接成功（SetOnlineStatus 内部先查当前状态）
                if (!PhysicalDiskControl.SetOnlineStatus(probe, online: true, persist: true))
                {
                    error = Locale.T("engine.online.failed", diskNumber);
                    return false;
                }

                // 让 partmgr 丢掉缓存的磁盘属性并重新枚举设备，卷/盘符回来得更快。它只是"加速器"，
                // 失败不影响联机本身（盘已经在线，卷通常仍会被自动认回），故只落诊断日志
                if (!PhysicalDiskControl.UpdateDiskProperties(probe))
                {
                    LogService.DebugFile($"磁盘 {diskNumber} 已联机，但重新枚举（IOCTL_DISK_UPDATE_PROPERTIES）失败");
                }

                error = string.Empty;
                LogService.DebugFile($"磁盘 {diskNumber} 已重新联机（卷与源盘符交回系统；" +
                                     "该盘从此对别的程序可见，跨重启的 L2 账本会因此被判过期）");
                return true;
            }
            catch (Exception ex)
            {
                error = Locale.T("engine.online.failedWithError", diskNumber, ex.Message);
                return false;
            }
        }

        /// <summary>尽力把盘联机（失败只落诊断日志：这种情况用户可以用磁盘管理手动联机）</summary>
        private static void SetOnlineBestEffort(int diskNumber)
        {
            if (!TryOnline(diskNumber, out string error))
            {
                LogService.DebugFile($"磁盘 {diskNumber} 联机失败（可用磁盘管理手动联机）：{error}");
            }
        }

        /// <summary>
        /// 复读**本句柄对应的磁盘**此刻的属性（脱机 / 只读）。
        ///
        /// 为什么必须走"已持有的句柄"：源盘被本程序独占打开（<c>FILE_SHARE_NONE</c>）后，谁再开探针句柄
        /// 都会被挡，只能拿这个句柄查——它是观察"我们脱机之后，系统有没有把盘改回联机"的唯一窗口
        /// （调用方见 TargetService.TraceSourceDiskAttributes）。
        /// </summary>
        /// <returns>读到返回 <c>true</c> 并填出两项；失败返回 <c>false</c>（只记诊断日志，不抛）</returns>
        public bool TryGetCurrentAttributes(out bool isOffline, out bool isReadOnly)
        {
            isOffline = false;
            isReadOnly = false;

            SafeFileHandle? handle = _handle;
            if (handle == null || handle.IsInvalid) return false;

            try
            {
                bool isOnline = PhysicalDiskControl.GetOnlineStatus(handle, out isReadOnly);
                isOffline = !isOnline;
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"磁盘 {DiskNumber} 属性复读失败：{ex.Message}");
                return false;
            }
        }

        #endregion

        #region 原始扇区读写

        /// <summary>按字节偏移读一段（调用方保证 [offset, offset+count) 落在盘内且 count &gt; 0）</summary>
        /// <exception cref="IOException">设备报错或读到的字节数不足</exception>
        public unsafe void Read(long byteOffset, byte[] buffer, int bufferOffset, int count)
        {
            SafeFileHandle handle = _handle ?? throw new ObjectDisposedException(nameof(PhysicalDiskHandle));
            int done = 0;
            while (done < count)
            {
                int chunk = Math.Min(count - done, MaxTransferBytes);
                long at = byteOffset + done;
                if (!SetFilePointerEx(handle, at, IntPtr.Zero, FILE_BEGIN))
                    throw new IOException($"设置读位置失败（偏移 {at}）：{LastErrorText()}");

                // fixed 必须**包住** P/Invoke：出了这个块数组就不再被钉住，GC 可以搬它
                fixed (byte* p = &buffer[bufferOffset + done])
                {
                    if (!ReadFile(handle, (IntPtr)p, (uint)chunk, out uint read, IntPtr.Zero) || read != (uint)chunk)
                        throw new IOException($"读扇区失败（偏移 {at}，{chunk} 字节）：{LastErrorText()}");
                }
                done += chunk;
            }
        }

        /// <summary>按字节偏移写一段（调用方保证 [offset, offset+data.Length) 落在盘内且长度 &gt; 0）</summary>
        /// <exception cref="IOException">设备报错或写入的字节数不足</exception>
        public unsafe void Write(long byteOffset, byte[] data, int dataOffset, int count)
        {
            SafeFileHandle handle = _handle ?? throw new ObjectDisposedException(nameof(PhysicalDiskHandle));
            int done = 0;
            while (done < count)
            {
                int chunk = Math.Min(count - done, MaxTransferBytes);
                long at = byteOffset + done;
                if (!SetFilePointerEx(handle, at, IntPtr.Zero, FILE_BEGIN))
                    throw new IOException($"设置写位置失败（偏移 {at}）：{LastErrorText()}");

                fixed (byte* p = &data[dataOffset + done])
                {
                    if (!WriteFile(handle, (IntPtr)p, (uint)chunk, out uint written, IntPtr.Zero) || written != (uint)chunk)
                        throw new IOException($"写扇区失败（偏移 {at}，{chunk} 字节）：{LastErrorText()}");
                }
                done += chunk;
            }
        }

        #endregion

        #region 盘与卷的对照（启动校验用）

        /// <summary>
        /// 某个卷根（如 "C:\"）落在哪块物理盘上。用于两条启动校验：
        /// ① 不允许把系统盘当加速目标；② 不允许把 L2 缓存目录放在被加速的那块盘上。
        /// </summary>
        public static bool TryGetDiskNumberOfVolume(string volumeRoot, out int diskNumber)
        {
            diskNumber = -1;
            string root = (volumeRoot ?? string.Empty).TrimEnd('\\', '/');
            if (root.Length == 0) return false;

            try
            {
                using SafeFileHandle handle = CreateHandle(@"\\.\" + root, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE);
                if (handle.IsInvalid) return false;
                diskNumber = (int)PhysicalDiskControl.GetDeviceNumber(handle).DeviceNumber;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>卷根所在的盘号；查不到时返回 -1</summary>
        public static int GetDiskNumberOfVolume(string volumeRoot)
            => TryGetDiskNumberOfVolume(volumeRoot, out int number) ? number : -1;

        /// <summary>承载系统盘的物理盘号；查不到时返回 -1</summary>
        public static int GetSystemDiskNumber()
            => GetDiskNumberOfVolume(Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\");

        /// <summary>承载本程序自己（`AppContext.BaseDirectory`）的物理盘号；查不到时返回 -1</summary>
        public static int GetProgramDiskNumber()
            => GetDiskNumberOfVolume(Path.GetPathRoot(AppContext.BaseDirectory) ?? @"C:\");

        /// <summary>
        /// 配置里的 L2 缓存目录落在哪块物理盘上（-1 = L2 未启用 / 没配路径 / 查不到盘号）。
        /// 「选择硬盘」用它把"缓存所在的那块盘"标成不能加速（自己给自己加速是纯写放大）。
        ///
        /// 判据与引擎启动校验 ⑥ 同源（<c>Path.GetPathRoot</c> → <see cref="GetDiskNumberOfVolume"/>），
        /// 避免两处各写一遍；路径非法时返回 -1，交给引擎启动校验去兜底报错。
        /// </summary>
        public static int GetL2CacheDiskNumber(bool enabled, string ssdCachePath)
        {
            if (!enabled || string.IsNullOrWhiteSpace(ssdCachePath)) return -1;
            try
            {
                return GetDiskNumberOfVolume(Path.GetPathRoot(Path.GetFullPath(ssdCachePath)) ?? string.Empty);
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// 承载分页文件（<c>pagefile.sys</c>）的物理盘号集合。
        ///
        /// **这些盘绝不能作为加速目标**：本程序对目标盘做的是**整盘脱机**，而分页文件是内核在运行中
        /// 随时读写的内存交换区——盘一脱机，内核换页失败，当场蓝屏
        /// <c>KERNEL_DATA_INPAGE_ERROR (0x7A)</c>。系统盘虽已被 <see cref="DescribeTargetDiskBlockReason"/>
        /// 拦下，但分页文件**可以配在别的数据盘上**（"虚拟内存"里给 D:、E: 各设一段是常见做法），
        /// 那种盘此前是放行的，正是这份探测要堵的洞。
        ///
        /// 两个来源取并集，互为兜底：
        /// ① 注册表里配置的分页文件位置（<c>PagingFiles</c>）：权威，且**不受目标盘当前联机/脱机影响**；
        /// ② 各已挂载固定卷根上**实际存在**的 <c>pagefile.sys</c>：覆盖 <c>?:\pagefile.sys</c> 这类
        ///    由系统自动放置、配置里查不到盘符的情形，也兜住注册表读取失败。
        ///
        /// 任何一步探测失败都**只跳过、不抛出**（与 <see cref="FillDriveLetters"/> 同一口径）：
        /// 不能因为这项防护本身出错而让整个选盘/启动流程挂掉。
        /// </summary>
        public static HashSet<int> GetPageFileDiskNumbers()
        {
            var disks = new HashSet<int>();

            // ① 注册表：HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management → PagingFiles
            try
            {
                using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
                if (key?.GetValue("PagingFiles") is { } raw)
                {
                    // REG_MULTI_SZ 正常给 string[]；个别环境给单个 string，一并兼容
                    string[] entries = raw switch
                    {
                        string[] multi => multi,
                        string single => new[] { single },
                        _ => Array.Empty<string>(),
                    };
                    foreach (string entry in entries)
                    {
                        string root = PageFileEntryRoot(entry);
                        if (root.Length == 0) continue;   // "?:\..."（系统自动放置）/ 空 / 其它：交给 ②
                        int disk = GetDiskNumberOfVolume(root);
                        if (disk >= 0) disks.Add(disk);
                    }
                }
            }
            catch
            {
                // 读不到就算了：② 的存在性扫描继续兜底
            }

            // ② 存在性扫描：逐个已挂载的固定卷根找 pagefile.sys
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        // 分页文件只可能落在固定盘上：跳过光驱 / 可移动介质 / 未就绪卷
                        if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                        if (!File.Exists(Path.Combine(drive.RootDirectory.FullName, "pagefile.sys"))) continue;
                        if (TryGetDiskNumberOfVolume(drive.RootDirectory.FullName, out int disk) && disk >= 0)
                            disks.Add(disk);
                    }
                    catch
                    {
                        // 单个卷探测失败：跳过它，别的卷照常
                    }
                }
            }
            catch
            {
                // DriveInfo.GetDrives() 本身失败：本次拿不到，不拦
            }

            return disks;
        }

        /// <summary>
        /// 从 <c>PagingFiles</c> 的一条配置里取出盘符根（如 <c>"D:\"</c>）；取不到时返回空串。
        ///
        /// 条目形如 <c>"D:\pagefile.sys 2048 4096"</c>（路径 + 初始大小 + 最大值），只有第一段是路径。
        /// 由系统自动管理时条目是 <c>"?:\pagefile.sys"</c>：`?` 不是盘符，按空串处理，交给存在性扫描去认。
        /// </summary>
        private static string PageFileEntryRoot(string entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return string.Empty;

            string path = entry.Trim().Split(' ', '\t')[0];
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\') return string.Empty;   // 含 "?:\..."

            char letter = path[0];
            bool isLetter = (letter >= 'A' && letter <= 'Z') || (letter >= 'a' && letter <= 'z');
            return isLetter ? path.Substring(0, 3) : string.Empty;
        }

        /// <summary>
        /// 把各盘当前挂载的盘符填进 <see cref="PhysicalDiskInfo.DriveLetters"/>：**一次全盘扫描 + 按盘号归组**
        /// （不做"每块盘各扫一遍 A–Z"的 O(N×26) 重复打开）。
        ///
        /// 判据与 <see cref="TryGetDiskNumberOfVolume"/> 同源（卷根 → 设备号），故只认有卷、有盘符、能打开的卷；
        /// 未格式化分区、盘符未分配、CD/网络盘都会自然落空，不影响其余盘。
        /// 盘符只是"锦上添花"：个别卷读属性会抛、`DriveInfo.GetDrives()` 本身在极少数环境下也会抛，
        /// 这里一律吞掉——**不能因为查盘符失败就让整份磁盘清单挂掉**。
        /// </summary>
        public static void FillDriveLetters(IEnumerable<PhysicalDiskInfo> disks)
        {
            var byDisk = new Dictionary<int, List<string>>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        if (TryGetDiskNumberOfVolume(drive.Name, out int disk) && disk >= 0)
                        {
                            if (!byDisk.TryGetValue(disk, out List<string>? letters))
                                byDisk[disk] = letters = new List<string>();
                            letters.Add(drive.Name.TrimEnd('\\', '/'));
                        }
                    }
                    catch
                    {
                        // 单个卷读属性失败：跳过它，别的卷照常
                    }
                }
            }
            catch
            {
                // DriveInfo.GetDrives() 失败：本次拿不到盘符，清单照常返回（盘符本来就是可选信息）
            }

            foreach (PhysicalDiskInfo info in disks)
            {
                if (!byDisk.TryGetValue(info.DiskNumber, out List<string>? letters)) continue;
                letters.Sort(StringComparer.OrdinalIgnoreCase);   // C: 在 D: 前
                info.DriveLetters.AddRange(letters);
            }
        }

        /// <summary>
        /// 该盘为什么**不能**被本程序当作目标盘（空串 = 可以）。三条硬拦：承载系统盘、承载本程序自己、
        /// 承载分页文件。
        ///
        /// 为什么必须是硬拦：本程序对目标盘做的是**整盘脱机**（所有卷立刻消失）——
        /// 系统盘脱机等于把 Windows 送走；程序所在盘脱机等于把本程序（以及盘上的配置、日志、L2 容器）一起送走；
        /// 分页文件所在盘脱机则会让内核换页失败、**当场蓝屏**（<c>KERNEL_DATA_INPAGE_ERROR 0x7A</c>，
        /// 见 <see cref="GetPageFileDiskNumbers"/>）。
        ///
        /// **三条独立判定、全部列出**（以 ", " 连接）：一块盘常常同时踩中多条——本程序通常就装在系统盘上，
        /// 而系统盘几乎必然承载分页文件；只报第一条会让用户"修一条、重试、再撞下一条"。
        ///
        /// 调用方是引擎启动校验（<c>TargetService.Validate</c>）与 L2 校验/修复（<c>L2Verify</c>），
        /// 它们把返回值原样拼进"是{1}"的句子里。以前这条判据在这两处各写了一遍，L2 校验那次就漏掉了
        /// （而它比启动更危险——启动失败什么都不会发生，校验漏了则可能真的把盘脱机），现已收敛到这一份。
        /// 「选择硬盘」对话框是唯一例外：它要带 ❌ 前缀逐条列，用的是同判据的另一套文案
        /// （<c>SelectDiskForm.BlockReasons</c>）。
        /// </summary>
        public static string DescribeTargetDiskBlockReason(int diskNumber)
        {
            if (diskNumber < 0) return string.Empty;

            var reasons = new List<string>();
            if (GetSystemDiskNumber() == diskNumber)
                reasons.Add(Locale.T("selectDisk.block.systemDisk", Environment.SystemDirectory));
            if (GetProgramDiskNumber() == diskNumber)
                reasons.Add(Locale.T("selectDisk.block.programDisk", AppContext.BaseDirectory));
            if (GetPageFileDiskNumbers().Contains(diskNumber))
                reasons.Add(Locale.T("selectDisk.block.pageFile"));

            return string.Join(", ", reasons);
        }

        #endregion

        #region 枚举与盘身份（配置持久化用）

        /// <summary>
        /// 枚举本机所有可访问的物理盘（**只读探测，不改任何状态**）。
        /// 设置对话框与启动时的选盘对话框共用这一份实现，保证两处列出的清单一致。
        /// 单块盘探测失败只跳过它并落一条诊断日志，不影响其余盘。
        /// </summary>
        /// <exception cref="IOException">设备枚举本身失败</exception>
        public static List<PhysicalDiskInfo> Enumerate()
        {
            var list = new List<PhysicalDiskInfo>();

            // 刻意**不用** PhysicalDiskHelper.GetPhysicalDisks()：它为了拿到盘号，会对每块盘构造一个完整的
            // PhysicalDisk（跑 geometry + 设备描述符 + 序列号解码一串 IOCTL），而它只捕获 4 种异常
            // （DriveNotFound / DeviceNotReady / SharingViolation / InvalidData），
            // 别的异常——例如个别设备上抛出的 ArgumentOutOfRangeException——会一路冒到调用方，
            // **一块怪盘就能让整份清单为空**。这里只要盘号，故直接取号再逐块 Probe：
            // Probe 的异常在下面逐块吞掉，坏盘只跳过（顺带省掉每盘一次多余的 PhysicalDisk 构造）。
            foreach (int diskNumber in PhysicalDiskControl.GetPhysicalDiskIndexList())
            {
                try
                {
                    list.Add(Probe(diskNumber));
                }
                catch (Exception ex)
                {
                    // 带上异常类型名：光看 Message 分不清是设备不认某条 IOCTL 还是别的怪问题
                    LogService.DebugFile($"枚举磁盘 {diskNumber} 失败（{ex.GetType().Name}，已跳过）：{ex.Message}");
                }
            }

            // 盘符要在"盘还联机、卷还在"的时候采（脱机后 \\.\X: 就开不出来了），故在此统一补一次
            FillDriveLetters(list);
            return list;
        }

        /// <summary>
        /// 配置里持久化的**盘身份**——用于启动时反查到当前盘号。
        ///
        /// 有序列号时用 `型号|序列号`（跨插拔、跨重启都稳）；序列号为空（部分 USB 桥 / 虚拟盘 / RAM 盘）
        /// 时回落到 `型号|容量|扇区`，此时**同型号多块会分不开**——UI 必须如实提示该设备没有稳定身份。
        /// </summary>
        public static string DescribeIdentity(PhysicalDiskInfo info)
            => info.SerialNumber.Length > 0
                ? $"{info.Model}|{info.SerialNumber}"
                : $"{info.Model}|{info.SizeBytes}|{info.BytesPerSector}";

        /// <summary>该设备是否有稳定身份（即有没有序列号）</summary>
        public static bool HasStableIdentity(PhysicalDiskInfo info) => info.SerialNumber.Length > 0;

        /// <summary>
        /// 按身份串反查当前盘号；**查不到返回 -1**。
        /// 调用方必须据此**拒绝启动**，而不是随便挑一块凑合——盘号只是枚举顺序，
        /// 选错盘会把它整盘脱机（这是本程序最严重的误操作）。
        /// </summary>
        public static int ResolveDiskNumber(IReadOnlyList<PhysicalDiskInfo> disks, string identity)
        {
            if (string.IsNullOrEmpty(identity)) return -1;
            for (int i = 0; i < disks.Count; i++)
            {
                if (DescribeIdentity(disks[i]) == identity) return disks[i].DiskNumber;
            }
            return -1;
        }

        #endregion

        #region 设备身份与特性查询

        /// <summary>从 STORAGE_DEVICE_DESCRIPTOR 里取厂商/产品/固件/序列号（取不到就留空，不阻塞流程）</summary>
        private static void FillDeviceIdentity(SafeFileHandle handle, PhysicalDiskInfo info)
        {
            try
            {
                STORAGE_DEVICE_DESCRIPTOR descriptor = PhysicalDiskControl.GetDeviceDescriptor(handle);
                info.RemovableMedia = descriptor.RemovableMedia != 0;
                info.VendorId = ReadDescriptorString(descriptor, descriptor.VendorIdOffset);
                info.ProductId = ReadDescriptorString(descriptor, descriptor.ProductIdOffset);
                info.ProductRevision = ReadDescriptorString(descriptor, descriptor.ProductRevisionOffset);
                info.SerialNumber = ReadDescriptorString(descriptor, descriptor.SerialNumberOffset);
            }
            catch (Exception ex)
            {
                // 个别设备驱动不支持这条 IOCTL（大库注释里也记录过 Dataram RAMDisk 的同类情况）：
                // 身份串缺失只影响 INQUIRY 的展示，不影响正确性
                LogService.DebugFile($"磁盘 {info.DiskNumber} 读取设备描述符失败（型号/序列号将留空）：{ex.Message}");
            }
        }

        /// <summary>
        /// 取描述符里的一个字符串字段。字段偏移由驱动给出、基准是描述符结构体自身的长度
        /// （与大库 PhysicalDiskControl.GetDeviceDescription 的算法一致）。
        /// </summary>
        private static string ReadDescriptorString(STORAGE_DEVICE_DESCRIPTOR descriptor, uint fieldOffset)
        {
            if (fieldOffset == 0 || fieldOffset == 0xFFFFFFFF) return string.Empty;
            byte[]? raw = descriptor.RawDeviceProperties;
            if (raw == null) return string.Empty;

            int offset = (int)fieldOffset - Marshal.SizeOf(typeof(STORAGE_DEVICE_DESCRIPTOR));
            if (offset < 0 || offset >= raw.Length) return string.Empty;

            int end = offset;
            while (end < raw.Length && raw[end] != 0) end++;
            if (end == offset) return string.Empty;
            return Encoding.Latin1.GetString(raw, offset, end - offset).Trim();
        }

        /// <summary>
        /// 查询"搜寻惩罚"属性（机械盘为 true）。查不到时只标记为未知，不做判断——
        /// 由 TargetService 决定未知时的口径（目前是放行 + 警告，不拦）。
        /// </summary>
        private static void TryQuerySeekPenalty(SafeFileHandle handle, PhysicalDiskInfo info)
        {
            const int StorageDeviceSeekPenaltyProperty = 7;
            const int PropertyStandardQuery = 0;

            IntPtr inBuffer = IntPtr.Zero;
            IntPtr outBuffer = IntPtr.Zero;
            try
            {
                var query = new STORAGE_PROPERTY_QUERY
                {
                    PropertyId = StorageDeviceSeekPenaltyProperty,
                    QueryType = PropertyStandardQuery,
                    AdditionalParameters = new byte[1]
                };

                int inSize = Marshal.SizeOf(typeof(STORAGE_PROPERTY_QUERY));
                int outSize = Marshal.SizeOf(typeof(DEVICE_SEEK_PENALTY_DESCRIPTOR));
                inBuffer = Marshal.AllocHGlobal(inSize);
                outBuffer = Marshal.AllocHGlobal(outSize);
                Marshal.StructureToPtr(query, inBuffer, false);

                bool ok = PhysicalDiskControl.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                    inBuffer, (uint)inSize, outBuffer, (uint)outSize, out _, IntPtr.Zero);
                if (!ok) return;

                var penalty = Marshal.PtrToStructure<DEVICE_SEEK_PENALTY_DESCRIPTOR>(outBuffer);
                info.IncursSeekPenalty = penalty.IncursSeekPenalty;
                info.SeekPenaltyKnown = true;
            }
            catch
            {
                // 属性不可用：保持"未知"
            }
            finally
            {
                if (inBuffer != IntPtr.Zero) Marshal.FreeHGlobal(inBuffer);
                if (outBuffer != IntPtr.Zero) Marshal.FreeHGlobal(outBuffer);
            }
        }

        #endregion

        #region Win32 API

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_BEGIN = 0;
        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

        /// <summary>
        /// 写不经过系统写缓存。这是"写已落盘"在本层能做到的最大程度——
        /// 磁盘自身可能还有易失写缓存，那要靠设备侧的 FUA/刷缓存命令，本层管不到（见 §3.4 的口径说明）。
        /// </summary>
        private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;

            [MarshalAs(UnmanagedType.I1)]
            public bool IncursSeekPenalty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFilePointerEx(SafeFileHandle hFile, long liDistanceToMove, IntPtr lpNewFilePointer, uint dwMoveMethod);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(SafeFileHandle hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

        private static SafeFileHandle CreateDiskHandle(int diskNumber, FileAccess access, FileShare share, bool writeThrough = false)
            => CreateHandle($@"\\.\PhysicalDrive{diskNumber}", GetDesiredAccess(access), (uint)share, writeThrough);

        private static SafeFileHandle CreateHandle(string devicePath, uint desiredAccess, uint shareMode, bool writeThrough = false)
        {
            uint flags = FILE_ATTRIBUTE_NORMAL | (writeThrough ? FILE_FLAG_WRITE_THROUGH : 0);
            return CreateFile(devicePath, desiredAccess, shareMode, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        }

        private static uint GetDesiredAccess(FileAccess access) => access switch
        {
            FileAccess.Read => GENERIC_READ,
            FileAccess.Write => GENERIC_WRITE,
            _ => GENERIC_READ | GENERIC_WRITE
        };

        private static string LastErrorText()
        {
            int code = Marshal.GetLastWin32Error();
            try
            {
                return $"{code}（{new Win32Exception(code).Message}）";
            }
            catch
            {
                return code.ToString();
            }
        }

        #endregion
    }
}
