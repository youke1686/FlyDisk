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

namespace FlyDisk.Models
{
    /// <summary>
    /// 缓存与设备统计快照：由窗体定时器直接调用引擎的 <c>CreateSnapshot()</c> 取得（单进程，不再走 IPC）。
    /// 只承载"事实"，不含任何计算——展示口径由 UI 决定。
    /// </summary>
    public class CacheStats
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // ===== 目标与设备 =====

        /// <summary>target 是否正在提供块设备（= 盘已脱机 + 已独占 + 已监听）</summary>
        public bool IsRunning { get; set; }

        /// <summary>被加速的物理盘号（<c>\\.\PhysicalDriveN</c> 的 N）</summary>
        public int PhysicalDiskNumber { get; set; }

        /// <summary>底层盘型号（INQUIRY 上报值的来源，如 "ST1000DM010-2EP102"）</summary>
        public string DeviceModel { get; set; } = string.Empty;

        /// <summary>底层盘序列号（VPD 0x80 上报值的来源）</summary>
        public string DeviceSerial { get; set; } = string.Empty;

        /// <summary>盘容量（字节）</summary>
        public long DeviceSizeBytes { get; set; }

        /// <summary>逻辑扇区大小（跟随源盘：512e 盘为 512，4Kn 盘为 4096）</summary>
        public int BytesPerSector { get; set; }

        /// <summary>一个缓存块包含几个逻辑扇区（BytesPerSector × 它 == 4096）</summary>
        public int SectorsPerBlock { get; set; }

        /// <summary>启动时该盘是否**已经是脱机**状态——这是"上次的 L2 账本是否可信"的判据（见 SsdCacheService 类注释）</summary>
        public bool DeviceWasOffline { get; set; }

        /// <summary>当前仍连着的 iSCSI 连接数（0 = 没有发起程序挂着；-1 = 查询失败；目标未运行时为 0）。有连接时停止会被拒绝</summary>
        public int ActiveConnections { get; set; }

        /// <summary>目标 IQN</summary>
        public string TargetIqn { get; set; } = string.Empty;

        /// <summary>监听端点（如 127.0.0.1:3260），供 UI 提示用户到哪里连接</summary>
        public string ListenEndpoint { get; set; } = string.Empty;

        // ===== 块 IO 计数（SCSI 命令级，来自 CachedPhysicalDisk）=====

        public long ReadCommandsTotal { get; set; }
        public long ReadSectorsTotal { get; set; }

        /// <summary>整条 READ 命令**全部命中缓存**（一个扇区都没回源）的次数</summary>
        public long ReadFullHitCommandsTotal { get; set; }

        public long WriteCommandsTotal { get; set; }
        public long WriteSectorsTotal { get; set; }

        // ===== 读块去向的滑动窗口（见 后续待办.md 第七节）=====
        // 口径：**按读取量滑动，不是按时间**——"近 1MB" = 最近读过的 1MB 数据量。
        // 累计计数器只能加不能减、回答不了"最近 N"，所以引擎另留了一份有界历史（环形缓冲）来算这两个窗口。

        /// <summary>最近 1MB 读取量内的读块去向</summary>
        public ReadOutcomeWindow Recent1MB { get; set; }

        /// <summary>最近 1GB 读取量内的读块去向</summary>
        public ReadOutcomeWindow Recent1GB { get; set; }

        // ===== L1（内存缓存）=====

        /// <summary>已分配的非托管内存总量（字节）</summary>
        public long TotalCacheSize { get; set; }

        /// <summary>已占用 4KB 插槽数</summary>
        public long UsedSlots { get; set; }

        /// <summary>空闲 4KB 插槽数</summary>
        public long FreeSlots { get; set; }

        /// <summary>已缓存的块数（原来叫"缓存文件总数"——块设备下没有文件，只有块）</summary>
        public long CachedBlocks { get; set; }

        /// <summary>L1 命中的块数</summary>
        public long L1HitBlocksTotal { get; set; }

        /// <summary>L1 未命中的块数（这些块会继续查 L2，再谈回源）</summary>
        public long L1MissBlocksTotal { get; set; }

        /// <summary>写命中刷新（W1）在 L1 里就地覆盖的块数——整块被写覆盖、且该块当时在 L1 中</summary>
        public long L1WriteUpdateBlocksTotal { get; set; }

        /// <summary>系统物理内存负载 (0.0 - 1.0)</summary>
        public double SystemMemoryLoad { get; set; }

        public double EvictionThreshold { get; set; }
        public double StopCachingThreshold { get; set; }

        public bool EnableMemoryCache { get; set; }

        /// <summary>自动淘汰是否已启用（缓存开启且淘汰阈值配置有效）</summary>
        public bool EvictionEnabled { get; set; }

        /// <summary>累计淘汰的块数</summary>
        public long EvictedBlocksTotal { get; set; }

        /// <summary>最近一轮淘汰摘除的块数</summary>
        public int LastEvictionBlocks { get; set; }

        /// <summary>最近一轮淘汰的耗时（毫秒）</summary>
        public double LastEvictionMs { get; set; }

        /// <summary>当前持有的 Slab 数（每个 64MB；= 已分配内存 ÷ Slab 大小）</summary>
        public long SlabCount { get; set; }

        /// <summary>
        /// 累计**归还给系统**的 Slab 数。这是"越线后内存真被交回去"的证据：
        /// 注意"淘汰块"本身不会降低系统内存占用（页仍属本进程），只有整 Slab 归还才会（见 §6.9/§6.10）。
        /// </summary>
        public long ReleasedSlabsTotal { get; set; }

        /// <summary>水位节拍（主动淘汰）触发的次数——"开始淘汰"这道线是否真的被跨过去过</summary>
        public long WatermarkEvictionsTotal { get; set; }

        // ===== SSD 二级缓存（L2）统计（见 关于内存缓存的进一步讨论.md §7）=====

        /// <summary>L2 是否已启用并成功加载</summary>
        public bool SsdCacheEnabled { get; set; }

        /// <summary>L2 总槽位数（4KB/槽）</summary>
        public long SsdTotalSlots { get; set; }

        public long SsdUsedSlots { get; set; }

        /// <summary>空闲槽位（已回到空闲栈、可直接复用）</summary>
        public long SsdFreeSlots { get; set; }

        /// <summary>空洞槽位（已失效但还占着 FIFO 环位，等弹到它时回收）</summary>
        public long SsdHoleSlots { get; set; }

        /// <summary>L2 独有命中块数（L1 未命中且 L2 命中）——L2 的真实贡献</summary>
        public long SsdHitBlocksTotal { get; set; }

        /// <summary>两层都未命中、回源读取的块数</summary>
        public long SsdMissBlocksTotal { get; set; }

        /// <summary>写进 L2 的 M 环的块数（回源准入的总数）</summary>
        public long SsdWriteBlocksTotal { get; set; }

        /// <summary>其中来自"激进期"（可用槽位充足 ⇒ 回源块来者不拒）的部分</summary>
        public long SsdFillBlocksTotal { get; set; }

        /// <summary>新键被记进 ghost 的次数（"见过一个新块"）</summary>
        public long SsdGhostPushBlocksTotal { get; set; }

        /// <summary>M 的淘汰块数</summary>
        public long SsdEvictBlocksTotal { get; set; }

        /// <summary>ghost 命中的块数（有第二次访问证据 ⇒ 被写进 M，即"复活"）</summary>
        public long SsdResurrectBlocksTotal { get; set; }

        /// <summary>被写透传作废的 L2 块数（W1 之后只剩"块没被写请求完整覆盖"的首尾残缺块）</summary>
        public long SsdInvalidatedBlocksTotal { get; set; }

        /// <summary>写命中刷新（W1）在 L2 里就地覆盖的块数——整块被写覆盖、且该块当时在 L2 中（不占新槽、不入队）</summary>
        public long SsdWriteUpdateBlocksTotal { get; set; }

        /// <summary>本次启动是否成功载入了上次留下的账本（false = 新建，或判据不成立/不自洽而整层作废）</summary>
        public bool SsdCacheLoadedFromDisk { get; set; }

        /// <summary>账本代次（每次落盘 +1；仅作诊断，不承担正确性）</summary>
        public long SsdGeneration { get; set; }

        /// <summary>加载账本耗时（毫秒）</summary>
        public double SsdLoadMs { get; set; }

        /// <summary>L2 转入保守准入的占用率（引擎**夹取后**的生效值，供检查器画水位线；L2 未启用时为 0）</summary>
        public double SsdConservativeThreshold { get; set; }
    }

    /// <summary>
    /// 一个读块去向窗口里，三类归宿各自的块数。
    /// 三者之和 = 该窗口的读块总数，也就是各类占比的分母。
    /// </summary>
    public readonly record struct ReadOutcomeWindow(long L1Hit, long L2Hit, long Source)
    {
        /// <summary>该窗口的读块总数（占比的分母）</summary>
        public long Total => L1Hit + L2Hit + Source;
    }
}
