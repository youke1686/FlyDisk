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
    /// 块几何（block geometry）：一次加速会话里**块、Slab、槽三者的尺寸与对齐关系**，
    /// 也是全程序对「块大小到底是多少」的**唯一推导点**（见 docs/可变缓存块大小与扇区对齐_设计与决策.md）。
    ///
    /// 推导链：
    /// <code>
    /// 扇区（源盘事实）
    ///   → 每块几个扇区（SectorsPerBlock = 用户配置；无 L2 时 = ceil(4096 / 扇区)）
    ///     → 块字节数（BlockSize = SectorsPerBlock × 扇区）
    ///       → 每个 Slab 装几块、Slab 实际多少字节
    /// </code>
    ///
    /// 两条必须守住的约束：
    /// 1. **整扇区对齐**：块字节数永远是扇区大小的整数倍——源盘上已有的 NTFS/GPT 按 LBA 解释，
    ///    块只是缓存内部的聚合单位，不能反过来改变扇区语义；
    /// 2. **Slab 永远装整数个块**：64 MiB 除不尽块大小时（4160 / 4224 等），
    ///    Slab 的实际字节数就向下取整到整数个块（每个 Slab 浪费不足一个块），
    ///    这样「槽偏移 = (槽号 + 1) × BlockSize」这条既有公式一个字都不用改。
    /// </summary>
    public readonly struct BlockGeometry
    {
        /// <summary>
        /// 块的**推荐**下限（4 KiB）：自动建议与"无 L2 时的最小块"都从这里起步。
        /// 用户手动配置时可以低到 1 个扇区（元数据开销由「L2 管理」的索引内存列呈现）。
        /// </summary>
        public const int MinBlockBytes = 4096;

        /// <summary>块字节数上限：再大只会放大回源读取与失效范围</summary>
        public const int MaxBlockBytes = 1024 * 1024;

        /// <summary>
        /// L2 索引的**每块**常驻内存开销估算（字节）。与 <c>SsdCacheService.MetadataBytesPerBlock</c> 同源；
        /// 放在这里是为了让 UI（L2 管理的「索引内存」与 5% 自动规则）不必依赖引擎层。
        /// </summary>
        public const int L2MetadataBytesPerBlock = 58;

        /// <summary>源盘逻辑扇区大小（跟随源盘，不能由用户改）</summary>
        public int BytesPerSector { get; }

        /// <summary>一个缓存块包含几个逻辑扇区</summary>
        public int SectorsPerBlock { get; }

        /// <summary>实际块字节数 = <see cref="SectorsPerBlock"/> × <see cref="BytesPerSector"/></summary>
        public int BlockSize { get; }

        /// <summary>一个 Slab 装多少个块（= floor(64 MiB / BlockSize)）</summary>
        public int SlotsPerSlab { get; }

        /// <summary>Slab 的实际字节数（= SlotsPerSlab × BlockSize；非 2 幂块时略小于 64 MiB）</summary>
        public int SlabBytes { get; }

        /// <summary>源盘上**完整块**的个数（盘尾残块不计入）</summary>
        public long WholeBlocks { get; }

        private BlockGeometry(int bytesPerSector, int sectorsPerBlock, int blockSize,
                              int slotsPerSlab, int slabBytes, long wholeBlocks)
        {
            BytesPerSector = bytesPerSector;
            SectorsPerBlock = sectorsPerBlock;
            BlockSize = blockSize;
            SlotsPerSlab = slotsPerSlab;
            SlabBytes = slabBytes;
            WholeBlocks = wholeBlocks;
        }

        /// <summary>扇区大小是否可用（必须为正，且不超过块上限）</summary>
        public static bool IsValidSector(int bytesPerSector)
            => bytesPerSector > 0 && bytesPerSector <= MaxBlockBytes;

        /// <summary>最小每块扇区数：**1 扇区**（块 = 一个扇区；512→512 B、4096→4 KiB）。下限只为给用户自由度，自动配置不会取到这里。</summary>
        public static int MinSectorsFor(int bytesPerSector)
            => bytesPerSector <= 0 ? 0 : 1;

        /// <summary>
        /// 推荐下限：块 ≥ 4 KiB（512→8、4096→1、520→8、528→8）。
        /// **自动建议与"无 L2 时的最小块"都用它**，避免把块压到一个扇区（元数据开销会暴涨）。
        /// </summary>
        public static int PreferredMinSectorsFor(int bytesPerSector)
            => bytesPerSector <= 0 ? 0 : Math.Max(1, (MinBlockBytes + bytesPerSector - 1) / bytesPerSector);

        /// <summary>最大每块扇区数：保证块 ≤ 1 MiB（512→2048、4096→256、520→2016、528→1985）</summary>
        public static int MaxSectorsFor(int bytesPerSector)
            => bytesPerSector <= 0 ? 0 : Math.Max(1, MaxBlockBytes / bytesPerSector);

        /// <summary>
        /// 构造几何并把扇区数夹到合法区间（0 / 负数 = 用最小值）。
        /// **UI 的静默夹取与引擎的防御性规整共用这一处**，不允许各写一份。
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">扇区大小非法（&lt;= 0 或 &gt; 1 MiB）</exception>
        public static BlockGeometry Clamped(int bytesPerSector, int sectorsPerBlock, long sizeBytes)
        {
            if (!IsValidSector(bytesPerSector))
            {
                throw new ArgumentOutOfRangeException(nameof(bytesPerSector),
                    $"扇区大小 {bytesPerSector} 非法（必须为 1..{MaxBlockBytes} 字节）");
            }

            int min = MinSectorsFor(bytesPerSector);
            int max = MaxSectorsFor(bytesPerSector);
            int preferred = PreferredMinSectorsFor(bytesPerSector);
            int sectors = sectorsPerBlock <= 0 ? preferred : Math.Clamp(sectorsPerBlock, min, max);

            int blockSize = checked(sectors * bytesPerSector);
            int slotsPerSlab = Math.Max(1, ServiceConstants.TargetSlabSize / blockSize);
            int slabBytes = slotsPerSlab * blockSize;
            long wholeBlocks = sizeBytes > 0 ? sizeBytes / blockSize : 0;

            return new BlockGeometry(bytesPerSector, sectors, blockSize, slotsPerSlab, slabBytes, wholeBlocks);
        }

        /// <summary>无 L2（或 L2 停用）时的几何：块取「最接近 4 KiB 的整扇区数」</summary>
        public static BlockGeometry Minimal(int bytesPerSector, long sizeBytes)
            => Clamped(bytesPerSector, PreferredMinSectorsFor(bytesPerSector), sizeBytes);

        /// <summary>
        /// 按「该盘配置的 L2 条目」推导几何：
        /// L2 启用且条目里写了扇区数 ⇒ 用它；否则（无条目 / 停用 / 未设置）⇒ 最小块。
        /// 身份串为空（远程形态）时一律按最小块。
        /// </summary>
        public static BlockGeometry ForDisk(DiskConfig config, string? diskIdentity, int bytesPerSector, long sizeBytes)
        {
            int sectors = 0;
            if (config != null && !string.IsNullOrEmpty(diskIdentity))
            {
                L2DiskConfig? entry = L2DiskConfig.Find(config.L2Disks, diskIdentity);
                if (entry != null && entry.Enabled && entry.SectorsPerBlock > 0) sectors = entry.SectorsPerBlock;
            }
            return Clamped(bytesPerSector, sectors, sizeBytes);
        }

        /// <summary>
        /// 5% 自动规则：假设 L2 占满时索引内存不超过物理内存的 5%，返回建议的**每块扇区数**。
        /// 结果落在 [MinSectorsFor, MaxSectorsFor] 内，且优先取 2 的幂（避免用户面对一个不断变化的复杂数字）。
        /// 内存或容量未知时返回最小值（等于不放大）。
        /// </summary>
        public static int SuggestSectors(long maxBytes, int bytesPerSector, long totalMemoryBytes)
        {
            if (!IsValidSector(bytesPerSector)) return 0;

            int min = PreferredMinSectorsFor(bytesPerSector);
            int max = MaxSectorsFor(bytesPerSector);
            if (maxBytes <= 0 || totalMemoryBytes <= 0) return min;

            long budget = (long)(totalMemoryBytes * 0.05);
            if (budget <= 0) return min;

            // 所需块字节 ≥ maxBytes × 58 B / 预算
            double requiredBytes = (double)maxBytes * L2MetadataBytesPerBlock / budget;
            long requiredSectors = (long)Math.Ceiling(requiredBytes / bytesPerSector);

            int value = min;
            while (value < requiredSectors && value < max) value <<= 1;
            return Math.Clamp(value, min, max);
        }

        /// <summary>该容量上限下，L2 索引的常驻内存估算（字节）</summary>
        public long IndexMemoryBytes(long maxBytes)
            => maxBytes <= 0 || BlockSize <= 0 ? 0 : maxBytes / BlockSize * L2MetadataBytesPerBlock;
    }
}
