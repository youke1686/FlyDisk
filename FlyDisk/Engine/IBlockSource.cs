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

namespace FlyDisk.Engine
{
    /// <summary>
    /// 块读取源：**缓存层与 LUN 后端只认这个接口**，不认底层是本地物理盘还是远端盘。
    ///
    /// 本地实现 = <see cref="PhysicalDiskHandle"/>（整盘脱机 + 独占 + 原始扇区）；
    /// 远端实现 = 协议客户端（把读写请求送到对端）。
    /// 引入它是为了三职责共用同一套 L1 / L2 / 账本 / iSCSI target：**只换块源，其余一行不动**。
    ///
    /// 线程前提：调用方是库的 target 工作线程（每个 target 一个后台线程串行执行 SCSI 命令），
    /// 所以实现不必考虑并发读写。
    /// </summary>
    public interface IBlockSource : IDisposable
    {
        /// <summary>逻辑扇区大小（跟随源设备：512e 为 512，4Kn 为 4096）</summary>
        int BytesPerSector { get; }

        /// <summary>容量（字节）</summary>
        long SizeBytes { get; }

        /// <summary>按字节偏移读一段（调用方保证 [offset, offset+count) 落在源内且 count &gt; 0）</summary>
        void Read(long byteOffset, byte[] buffer, int bufferOffset, int count);

        /// <summary>按字节偏移写一段（调用方保证 [offset, offset+count) 落在源内且 count &gt; 0）</summary>
        void Write(long byteOffset, byte[] data, int dataOffset, int count);
    }

    /// <summary>
    /// 块源的"身份与形状"——**缓存层需要的全部信息，不含读写能力**。
    ///
    /// 本地从 <see cref="PhysicalDiskInfo"/> 构造；远端从协议握手结果构造（对端把这同样的几项报过来）。
    /// 有了它，<c>CacheService</c> / <c>SsdCacheService</c> 就不必感知块源在本地还是远端——
    /// 而 L2 的"设备身份"与"账本是否可信"两条判据也随之对两端一致。
    /// </summary>
    public sealed class BlockSourceInfo
    {
        /// <summary>设备型号（厂商 + 产品；参与身份计算，也是 INQUIRY 的上报来源）</summary>
        public string Model { get; init; } = string.Empty;

        /// <summary>序列号（VPD 页 0x80 的上报来源）</summary>
        public string SerialNumber { get; init; } = string.Empty;

        /// <summary>厂商串（仅用于 INQUIRY 上报）</summary>
        public string VendorId { get; init; } = string.Empty;

        /// <summary>产品串（仅用于 INQUIRY 上报）</summary>
        public string ProductId { get; init; } = string.Empty;

        /// <summary>固件版本串（仅用于 INQUIRY 上报）</summary>
        public string FirmwareRevision { get; init; } = string.Empty;

        /// <summary>容量（字节）</summary>
        public long SizeBytes { get; init; }

        /// <summary>逻辑扇区大小</summary>
        public int BytesPerSector { get; init; }

        /// <summary>
        /// **我们打开它之前**，它是否处于"对别的程序可见/可写"的状态。
        /// 这是"上次留下的 L2 账本是否可信"的判据：**为假才敢采信账本**（见 SsdCacheService 类注释）。
        /// 本地 = 打开该物理盘之前它是否联机；远端 = **服务端打开它那块盘时**是否联机（由服务端捎过来）。
        /// </summary>
        public bool WasOnline { get; init; }

        /// <summary>由本地物理盘的只读信息构造</summary>
        public static BlockSourceInfo FromDisk(PhysicalDiskInfo info, bool wasOnline) => new()
        {
            Model = info.Model,
            SerialNumber = info.SerialNumber,
            VendorId = info.VendorId,
            ProductId = info.ProductId,
            FirmwareRevision = info.ProductRevision,
            SizeBytes = info.SizeBytes,
            BytesPerSector = info.BytesPerSector,
            WasOnline = wasOnline
        };
    }
}
