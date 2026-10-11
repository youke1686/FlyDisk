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
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 校验/修复的阶段。**语言无关**：引擎只报枚举，具体文字由 UI 按当前语言取词
    /// （见 后续待办.md 第八节：引擎不产出成品文案）。
    /// </summary>
    public enum L2VerifyPhase
    {
        /// <summary>逐块比对源盘与 L2 容器</summary>
        Compare,
        /// <summary>把不一致的槽用源盘数据回写</summary>
        Writeback
    }

    /// <summary>校验/修复进度（由校验器节流，最多每 200 ms 报一次）</summary>
    public sealed class L2VerifyProgress
    {
        public L2VerifyPhase Phase { get; set; }
        public long Processed { get; set; }
        public long Total { get; set; }
        public long Mismatched { get; set; }
        public long Repaired { get; set; }
    }

    /// <summary>一次「校验」的结论</summary>
    public sealed class L2VerifyResult
    {
        public long SlotCount { get; set; }
        public long OccupiedSlots { get; set; }
        /// <summary>本次计划扫描的块数（越界/重复的条目已剔除）</summary>
        public long PlannedBlocks { get; set; }
        public long ScannedBlocks { get; set; }
        public long MatchedBlocks { get; set; }
        public long MismatchedBlocks { get; set; }
        public long ReadFailedBlocks { get; set; }
        /// <summary>账本里块号超出源盘容量的条目数（没有真值可比）</summary>
        public long OutOfRangeBlocks { get; set; }
        /// <summary>账本里同一块号被两个槽登记的条目数</summary>
        public long DuplicateBlocks { get; set; }
        public long SourceTotalBlocks { get; set; }
        public long Generation { get; set; }
        public long ContainerId { get; set; }
        public double ElapsedMs { get; set; }
        public bool Cancelled { get; set; }
        public bool Aborted { get; set; }
        public string AbortReason { get; set; } = string.Empty;
    }

    /// <summary>一次「修复」的结论</summary>
    public sealed class L2RepairResult
    {
        public long RepairedBlocks { get; set; }
        public long ReadFailedBlocks { get; set; }
        public long WriteFailedBlocks { get; set; }
        public double ElapsedMs { get; set; }
        public bool Cancelled { get; set; }
    }

    /// <summary>
    /// L2 校验/修复的引擎（**只读装载 + 逐字节比对 + 可选回写**）。设计定稿见 `后续待办.md` 第二节
    /// 「L2 校验 / 修复：施工级定稿」。
    ///
    /// 它回答的问题只有一个：**账本里每个占用槽的容器内容，是否等于源盘同块号处的内容**——
    /// 不回答"该缓存的有没有缓存"（那是缓存效果，不是一致性）。
    ///
    /// 六条前提：
    /// 1. **绝不构造 <see cref="SsdCacheService"/>**：它的构造器会 `RotateContainerId()`，等于当场把账本作废；
    ///    本类只读装载，全过程不动身份戳、不动 index.bin。
    /// 2. **槽位数以盘上记录为准**，不用 `ComputeSlotCount` 重算（它按缓存盘当前可用空间夹取，两次运行之间会变）。
    /// 3. **遍历用方案 α**：源盘按块号升序顺序读（连续块合并成一次读），容器按块随机 4KB 读。
    ///    源盘（机械盘）的寻道是这里唯一昂贵的资源，升序读就是它的最优序；容器在 SSD 上，随机读可接受。
    ///    **不用散列**：哈希只保证"不等 ⇒ 一定不等"，"相等"存在碰撞 ⇒ 那是**漏报**方向（陈旧块继续被服务），
    ///    恰是要拦的那一边；逐字节比对本就没有这个方向，也就没有概率成分。
    /// 4. **修复只写 L2 容器**：源盘始终是只读句柄（OS 层面只读）；不一致的槽用源盘数据覆写，账本条目保留。
    ///    因此修复**幂等、无原子性要求**：中途崩溃/取消只是"有些块还没修"，下次校验会再抓到。
    /// 5. **源盘读失败的块绝不覆写**（没有真值），只计"读失败"。
    /// 6. **盘保持脱机**：会话打开时确保整盘脱机（内容静止才谈得上比对），关闭时也不联机。
    /// </summary>
    public sealed class L2Verifier : IDisposable
    {
        #region 常量（账本格式的只读镜像；与 SsdCacheService 一一对应）

        private const int BLOCK_SIZE = ServiceConstants.BlockSize;

        private const uint CONTAINER_MAGIC = 0x44434453;   // "SDCD"
        private const int CONTAINER_VERSION = 2;
        private const int CONTAINER_HASH_LEN = 32;         // 头部校验和覆盖的字节数

        private const uint INDEX_MAGIC = 0x31434453;       // "SDC1"
        private const int INDEX_VERSION = 3;               // 3：L2 删掉 S 环（正文只写 M 环，头部 sLimit/sCount 恒 0）
        private const int INDEX_HEADER_SIZE = 96;
        private const int INDEX_HEADER_HASH_OFFSET = 88;
        private const int RING_SLACK = 4;                  // 与 SsdCacheService 的环形缓冲余量一致

        private const string ContainerFileName = "cache.dat";
        private const string IndexFileName = "index.bin";
        private const string LockFileName = "cache.lock";

        /// <summary>源盘单次连续读的最大块数（= 1 MB，与 PhysicalDiskHandle 的单次搬运上限一致）</summary>
        private const int MAX_RUN_BLOCKS = 256;
        /// <summary>错误明细最多写多少行（其余只在摘要里计数）</summary>
        private const int MAX_DETAIL_LINES = 200;
        /// <summary>连续多少块读失败就判定为介质故障并中止（避免坏盘上白跑几小时）</summary>
        private const int MAX_CONSECUTIVE_READ_FAILURES = 64;
        /// <summary>进度上报的最小间隔（毫秒）</summary>
        private const int PROGRESS_INTERVAL_MS = 200;

        #endregion

        #region 字段

        private readonly DiskConfig _config;
        private string _dir = string.Empty;

        private FileStream? _lockFile;      // 与引擎同一把单实例锁
        private FileStream? _container;     // cache.dat（读写打开：修复要回写）
        private PhysicalDiskHandle? _device; // 源盘（**只读**句柄）

        private long _containerId;          // 容器头部当前的戳（进会话时会被轮换成新的，见 RotateStamp）
        private long _indexContainerId;     // 索引里记的戳（= 上次正常关服时的戳）：确认一致时要把它写回容器头
        private long _deviceIdentity;       // 容器头里的设备身份（重写头部时要原样写回）
        private bool _stampRotated;         // 本次会话已把容器头戳轮换过（"进过校验窗口"的痕迹）
        private int _slotCount;
        private long _occupied;
        private long _generation;
        private long _totalBlocks;          // 源盘块总数
        private long _outOfRangeBlocks;
        private long _duplicateBlocks;

        /// <summary>占用块号（升序）</summary>
        private long[] _blocks = Array.Empty<long>();
        /// <summary>与 <see cref="_blocks"/> 同下标的槽号</summary>
        private int[] _slots = Array.Empty<int>();

        /// <summary>最近一次校验认定的不一致块（下标指向上面两个数组，按块号升序）</summary>
        private readonly List<int> _mismatch = new();
        private L2VerifyResult? _lastVerify;

        private readonly byte[] _srcBuf = new byte[BLOCK_SIZE * MAX_RUN_BLOCKS];
        private readonly bool[] _srcOk = new bool[MAX_RUN_BLOCKS];
        private readonly byte[] _slotBuf = new byte[BLOCK_SIZE];

        #endregion

        /// <summary>本次要校验的目标盘号（由 UI 在打开校验窗口前选定，**不再从配置读**）</summary>
        private readonly int _diskNumber;

        public L2Verifier(DiskConfig config, int diskNumber)
        {
            _config = config;
            _diskNumber = diskNumber;
        }

        #region 会话

        /// <summary>
        /// 打开会话：抢锁 → 只读装载账本 → 校验设备身份与块号范围 → 确保源盘脱机并只读打开。
        /// 任何前置不通过都抛 <see cref="InvalidOperationException"/>，且**不会**动源盘状态
        /// （账本检查全在脱机之前完成）。
        /// </summary>
        public void Open()
        {
            if (_container != null) return;

            if (_diskNumber < 0)
                throw new InvalidOperationException(Locale.T("l2v.err.noDiskSelected"));
            if (string.IsNullOrWhiteSpace(_config.SsdCachePath))
                throw new InvalidOperationException(Locale.T("l2v.err.noCacheDir"));

            _dir = Path.GetFullPath(_config.SsdCachePath).TrimEnd('\\');

            // ① 单实例锁：与引擎同一把锁 ⇒ 目标运行中必然抢不到
            string lockPath = Path.Combine(_dir, LockFileName);
            if (!File.Exists(lockPath))
            {
                throw new InvalidOperationException(
                    Locale.T("l2v.err.noLockFile", LockFileName, _dir));
            }
            try
            {
                _lockFile = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                throw new InvalidOperationException(Locale.T("l2v.err.lockBusy"));
            }

            try
            {
                OpenLedger();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>只读装载账本（容器头部 + 索引），并校验设备身份与块号范围</summary>
        private void OpenLedger()
        {
            // ① 先审盘，而且**全部在脱机之前**：任何一条不过，系统状态一点都没动
            PhysicalDiskInfo info = PhysicalDiskHandle.Probe(_diskNumber);
            ValidateTargetDisk(info);

            string containerPath = Path.Combine(_dir, ContainerFileName);
            if (!File.Exists(containerPath))
                throw new InvalidOperationException(Locale.T("l2v.err.noContainer", containerPath));

            _container = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None,
                bufferSize: 1, FileOptions.RandomAccess);

            // 容器**按需、按 Slab 增长** ⇒ 长度不再等于整容量；这里只要求"至少含一个头部块"，
            // "是否覆盖账本用到的每个槽"由 LoadIndex 拿到实际槽位后再校验（见设计文档 §5.5）。
            if (_container.Length < BLOCK_SIZE)
                throw new InvalidOperationException(Locale.T("l2v.err.containerLength", _container.Length, BLOCK_SIZE));

            byte[] header = new byte[BLOCK_SIZE];
            _container.ReadExactly(header, 0, BLOCK_SIZE);

            if (Hash(header, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(header, CONTAINER_HASH_LEN) ||
                BitConverter.ToUInt32(header, 0) != CONTAINER_MAGIC ||
                BitConverter.ToInt32(header, 4) != CONTAINER_VERSION)
            {
                throw new InvalidOperationException(Locale.T("l2v.err.containerHeaderBad"));
            }

            _containerId = BitConverter.ToInt64(header, 8);
            _slotCount = BitConverter.ToInt32(header, 16);
            int blockSize = BitConverter.ToInt32(header, 20);
            long deviceIdentity = BitConverter.ToInt64(header, 24);
            _deviceIdentity = deviceIdentity;

            if (blockSize != BLOCK_SIZE || _slotCount <= 0)
                throw new InvalidOperationException(Locale.T("l2v.err.containerHeaderParams", blockSize, _slotCount));

            // 槽位数以盘上记录为准：不重算（见类注释第 2 条）
            _totalBlocks = info.SizeBytes / BLOCK_SIZE;
            if (deviceIdentity != ComputeDeviceIdentity(info))
            {
                throw new InvalidOperationException(
                    Locale.T("l2v.err.containerOwner", info.DiskNumber, info.Model));
            }

            LoadIndex();
            FilterAndSortBlocks();

            // 账本全部通过 ⇒ 留下"进过校验窗口"的痕迹：把容器头的身份戳换成新的随机值。
            // 于是盘上那份索引的戳立刻与容器不符 ⇒ 除非本次校验跑干净（届时调 ConfirmLedger 把戳写回去），
            // 下次启动一定不会误信这份没验完的账本。
            RotateStamp();

            // 前置全过才动盘的状态：确保整盘脱机（内容静止），并以**只读**句柄打开
            _device = PhysicalDiskHandle.OpenReadOnly(_diskNumber);
        }

        /// <summary>重写容器头部的身份戳（其余字段原样保留）并强制落盘</summary>
        private void WriteContainerHeader(long containerId)
        {
            byte[] header = new byte[BLOCK_SIZE];
            using (var ms = new MemoryStream(header))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(CONTAINER_MAGIC);
                w.Write(CONTAINER_VERSION);
                w.Write(containerId);
                w.Write(_slotCount);
                w.Write(BLOCK_SIZE);
                w.Write(_deviceIdentity);
                w.Write(Hash(header, 0, CONTAINER_HASH_LEN));
            }

            _container!.Position = 0;
            _container.Write(header, 0, BLOCK_SIZE);
            _container.Flush(true);
            _containerId = containerId;
        }

        /// <summary>进会话时的"留痕"：容器头的身份戳换成新的随机值（见调用点的说明）</summary>
        private void RotateStamp()
        {
            WriteContainerHeader(Random.Shared.NextInt64());
            _stampRotated = true;
        }

        /// <summary>
        /// 本次校验认定账本已与源盘一致（不一致 = 0，或已全部修复干净）⇒ 把容器头的身份戳**写回索引里的
        /// 那个值**，让引擎下次启动能正常载入这份账本（引擎的判据是"索引的戳 == 容器头的戳"）。
        ///
        /// 校验**没跑干净**（取消/中止/有没修完的不一致）时**不要调用**：容器头会停在轮换后的新戳上，
        /// 与索引对不上 ⇒ 下次启动仍会要求校验（或按"上次未正常关服"处理）。
        /// </summary>
        public void ConfirmLedger()
        {
            if (_container == null || !_stampRotated) return;

            WriteContainerHeader(_indexContainerId);
            _stampRotated = false;
            LogService.DebugFile($"L2 校验：账本已确认与源盘一致，容器身份戳已写回 0x{_indexContainerId:X16}");
        }

        /// <summary>
        /// 目标盘的**硬拦**审查——全部在"打开任何文件、脱机任何盘"之前完成。三条都必须有：
        ///
        /// 1. **块大小**：块号是按 `BytesPerSector × SectorsPerBlock` 算出来的，尺寸不对，
        ///    "块号 b ↔ 字节偏移 b×4096"这条换算就是错的 ⇒ 拿错偏移去比对，还会把错的比对照写回容器。
        /// 2. **系统盘 / 本程序所在盘 / 分页文件所在盘**：本流程要**整盘脱机**，脱错盘是灾难
        ///    （Windows 或本程序当场失去那块盘；分页文件所在盘还会让内核换页失败而蓝屏）。
        ///    判据与引擎启动校验、选择硬盘对话框共用 `PhysicalDiskHandle.DescribeTargetDiskBlockReason`。
        /// 3. **L2 缓存目录不得与被校验的盘同盘**：脱机后那个卷会消失，而容器/索引/lock 正被读着。
        /// </summary>
        private void ValidateTargetDisk(PhysicalDiskInfo info)
        {
            int blockBytes = info.BytesPerSector * Math.Max(0, _config.SectorsPerBlock);
            if (info.BytesPerSector <= 0 || _config.SectorsPerBlock <= 0 || blockBytes != ServiceConstants.BlockSize)
            {
                throw new InvalidOperationException(Locale.T("l2v.err.blockSize",
                    info.BytesPerSector, _config.SectorsPerBlock, blockBytes, ServiceConstants.BlockSize));
            }

            string blocked = PhysicalDiskHandle.DescribeTargetDiskBlockReason(info.DiskNumber);
            if (blocked.Length > 0)
            {
                throw new InvalidOperationException(
                    Locale.T("l2v.err.targetBlocked", info.DiskNumber, blocked));
            }

            int cacheDisk = PhysicalDiskHandle.GetDiskNumberOfVolume(Path.GetPathRoot(_dir) ?? string.Empty);
            if (cacheDisk >= 0 && cacheDisk == info.DiskNumber)
            {
                throw new InvalidOperationException(
                    Locale.T("l2v.err.cacheOnTarget", _dir, info.DiskNumber));
            }

            if (info.IsReadOnly)
                throw new InvalidOperationException(Locale.T("l2v.err.targetReadOnly", info.DiskNumber));
        }

        /// <summary>
        /// 只读装载 index.bin：头部（magic/version/slotCount/身份戳/校验和） + 正文（M 环、ghost）
        /// + 尾部正文校验和。任何不自洽一律抛异常——缓存可以丢，但绝不能拿着半截账本去比对。
        /// </summary>
        private void LoadIndex()
        {
            string indexPath = Path.Combine(_dir, IndexFileName);
            if (!File.Exists(indexPath))
                throw new InvalidOperationException(Locale.T("l2v.err.noIndex", indexPath));

            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            if (fs.Length < INDEX_HEADER_SIZE + 8)
                throw new InvalidOperationException(Locale.T("l2v.err.indexTooShort"));

            byte[] header = new byte[INDEX_HEADER_SIZE];
            fs.ReadExactly(header, 0, INDEX_HEADER_SIZE);

            uint magic = BitConverter.ToUInt32(header, 0);
            int version = BitConverter.ToInt32(header, 4);
            int blockSize = BitConverter.ToInt32(header, 8);
            int slotCount = BitConverter.ToInt32(header, 12);
            int sLimit = BitConverter.ToInt32(header, 16);
            int mLimit = BitConverter.ToInt32(header, 20);
            int ghostLimit = BitConverter.ToInt32(header, 24);
            int usedSlots = BitConverter.ToInt32(header, 28);
            int sCount = BitConverter.ToInt32(header, 32);
            int mCount = BitConverter.ToInt32(header, 36);
            int ghostCount = BitConverter.ToInt32(header, 40);
            long generation = BitConverter.ToInt64(header, 44);
            long containerId = BitConverter.ToInt64(header, 52);

            if (magic != INDEX_MAGIC || version != INDEX_VERSION || blockSize != BLOCK_SIZE)
                throw new InvalidOperationException(Locale.T("l2v.err.indexHeaderBad"));
            if (Hash(header, 0, INDEX_HEADER_HASH_OFFSET) != BitConverter.ToUInt64(header, INDEX_HEADER_HASH_OFFSET))
                throw new InvalidOperationException(Locale.T("l2v.err.indexHeaderHash"));
            if (slotCount != _slotCount)
                throw new InvalidOperationException(Locale.T("l2v.err.indexSlotCount", slotCount, _slotCount));

            // 身份戳：索引里记的是"上次正常关服那一刻"的戳，容器头是"上次开机时轮换的"戳。
            // 两者不符 = **上次没有正常关服**（掉电/强杀/崩溃）：索引还是上上次的快照，而容器已被上次运行改过。
            // 这**不代表账本没救**——索引本身结构自洽（上面那几条都过了），逐块比对会把所有错乱的槽
            // （包括"索引指向的槽已被复用给别的块"）用源盘数据修回正确内容，映射随之重新正确。
            // 所以这里只记录、不拒绝；本次校验干净时由 ConfirmLedger 把戳写回来自洽。
            _indexContainerId = containerId;
            if (containerId != _containerId)
            {
                LogService.DebugFile(
                    $"L2 校验：索引的身份戳 0x{containerId:X16} 与容器头的 0x{_containerId:X16} 不符（上次未正常关服）" +
                    "⇒ 按“可校验恢复”处理：逐块比对修复内容，校验干净则把戳写回索引里的值。");
            }
            // v3 起 S 环已删除：头部 sLimit/sCount 恒 0
            if (sLimit != 0 || sCount != 0 || mLimit < 0 || mCount < 0 || mCount > mLimit + RING_SLACK)
                throw new InvalidOperationException(Locale.T("l2v.err.indexRing", sLimit, sCount, mCount, mLimit));
            if (ghostLimit < 0 || ghostCount < 0 || ghostCount > Math.Max(1, ghostLimit))
                throw new InvalidOperationException(Locale.T("l2v.err.indexGhost", ghostCount, ghostLimit));
            if (usedSlots != sCount + mCount)
                throw new InvalidOperationException(Locale.T("l2v.err.indexUsedSlots", usedSlots, sCount + mCount));
            if (usedSlots < 0 || usedSlots > _slotCount)
                throw new InvalidOperationException(Locale.T("l2v.err.indexUsedSlotsRange", usedSlots, _slotCount));

            _occupied = usedSlots;
            _generation = generation;

            bool[] slotSeen = new bool[_slotCount];
            long[] blocks = new long[usedSlots];
            int[] slots = new int[usedSlots];
            int n = 0;
            int maxSlot = -1;     // 账本里用到的最大槽号（用于校验容器是否覆盖到位）
            ulong bodyHash;

            using (var body = new HashingStream(fs))
            {
                using (var br = new BinaryReader(body, Encoding.UTF8, leaveOpen: true))
                {
                    // M 环的条目依次登记即可：这里只需要"块号 ↔ 槽号"这张表
                    for (int i = 0; i < sCount + mCount; i++)
                    {
                        int slot = br.ReadInt32();
                        long block = br.ReadInt64();
                        br.ReadByte();   // 标志位（freq / 保留位）：与校验无关

                        if (slot < 0 || slot >= _slotCount)
                            throw new InvalidOperationException(Locale.T("l2v.err.indexSlotOutOfRange", slot));
                        if (block < 0)
                            throw new InvalidOperationException(Locale.T("l2v.err.indexBlockInvalid", block));
                        if (slotSeen[slot])
                            throw new InvalidOperationException(Locale.T("l2v.err.indexSlotDup", slot));

                        slotSeen[slot] = true;
                        if (slot > maxSlot) maxSlot = slot;
                        blocks[n] = block;
                        slots[n] = slot;
                        n++;
                    }

                    // ghost 只有指纹、没有数据 ⇒ 与"数据是否一致"无关，读掉即可（但它参与正文校验和）
                    for (int i = 0; i < ghostCount; i++) br.ReadInt64();

                    bodyHash = body.Hash;
                }

                byte[] trailer = new byte[8];
                fs.ReadExactly(trailer, 0, 8);
                if (BitConverter.ToUInt64(trailer, 0) != bodyHash)
                    throw new InvalidOperationException(Locale.T("l2v.err.indexBodyHash"));
            }

            // 容器必须**覆盖账本用到的每一个槽**（容器按需增长 ⇒ 长度不固定，只校验"够不够用"，见设计文档 §5.5）
            long needed = (long)(maxSlot + 2) * BLOCK_SIZE;
            if (_container.Length < needed)
            {
                throw new InvalidOperationException(
                    Locale.T("l2v.err.containerLength", _container.Length, needed));
            }

            _blocks = blocks;
            _slots = slots;
        }

        /// <summary>
        /// 按块号升序排序，并剔除两类没有真值可比的条目：越界块号（盘容量变过）、重复块号（账本损坏）。
        /// </summary>
        private void FilterAndSortBlocks()
        {
            if (_blocks.Length == 0) return;

            Array.Sort(_blocks, _slots);   // 升序 = 源盘顺序读

            int w = 0;
            long prev = -1;
            for (int i = 0; i < _blocks.Length; i++)
            {
                long block = _blocks[i];
                if (block >= _totalBlocks) { _outOfRangeBlocks++; continue; }
                if (block == prev) { _duplicateBlocks++; continue; }
                prev = block;
                _blocks[w] = block;
                _slots[w] = _slots[i];
                w++;
            }

            if (w != _blocks.Length)
            {
                Array.Resize(ref _blocks, w);
                Array.Resize(ref _slots, w);
            }
        }

        #endregion

        #region 校验（只读）

        /// <summary>
        /// 逐块比对：容器里的每个占用槽 vs 源盘同块号。**全程不写任何东西**（源盘是只读句柄，
        /// 容器只做随机读）。结果同时也是「修复」的输入。
        /// </summary>
        public L2VerifyResult Verify(Action<L2VerifyProgress>? progress, CancellationToken ct)
        {
            EnsureOpen();

            var result = new L2VerifyResult
            {
                SlotCount = _slotCount,
                OccupiedSlots = _occupied,
                PlannedBlocks = _blocks.Length,
                OutOfRangeBlocks = _outOfRangeBlocks,
                DuplicateBlocks = _duplicateBlocks,
                SourceTotalBlocks = _totalBlocks,
                Generation = _generation,
                ContainerId = _containerId
            };

            _mismatch.Clear();
            _lastVerify = null;

            int total = _blocks.Length;
            long startTicks = Environment.TickCount64;
            long lastReport = startTicks;
            int consecutiveFailures = 0;

            LogService.DebugFile(
                $"L2校验：账本第 {_generation} 代，容器 id 0x{_containerId:X16}，占用槽 {_occupied}/{_slotCount}，" +
                $"待扫描 {total} 块（越界 {_outOfRangeBlocks}、重复 {_duplicateBlocks}），源盘共 {_totalBlocks} 块；" +
                $"源盘以只读句柄打开，本次不回写任何数据");

            Report(progress, L2VerifyPhase.Compare, 0, total, 0, 0);

            int i = 0;
            while (i < total)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

                // 连续块合并成一次读：机械盘的顺序读就是它的最优序
                int run = 1;
                while (i + run < total && run < MAX_RUN_BLOCKS && _blocks[i + run] == _blocks[i + run - 1] + 1) run++;

                ReadSourceRun(_blocks[i], run);
                bool abortedHere = false;

                for (int k = 0; k < run; k++)
                {
                    int idx = i + k;
                    if (!_srcOk[k])
                    {
                        result.ReadFailedBlocks++;
                        consecutiveFailures++;
                        if (consecutiveFailures >= MAX_CONSECUTIVE_READ_FAILURES)
                        {
                            result.Aborted = true;
                            result.AbortReason = Locale.T("l2v.abortReason.consecutiveReadFailures", consecutiveFailures);
                            abortedHere = true;
                            break;
                        }
                        continue;
                    }
                    consecutiveFailures = 0;

                    ReadContainerSlot(_slots[idx]);
                    if (_srcBuf.AsSpan(k * BLOCK_SIZE, BLOCK_SIZE).SequenceEqual(_slotBuf.AsSpan(0, BLOCK_SIZE)))
                    {
                        result.MatchedBlocks++;
                    }
                    else
                    {
                        result.MismatchedBlocks++;
                        _mismatch.Add(idx);
                        if (result.MismatchedBlocks <= MAX_DETAIL_LINES)
                        {
                            LogService.DebugFile($"L2校验：块 {_blocks[idx]}（槽 {_slots[idx]}）与源盘不一致");
                        }
                    }
                }

                i += run;
                if (abortedHere) break;

                long now = Environment.TickCount64;
                if (now - lastReport >= PROGRESS_INTERVAL_MS)
                {
                    lastReport = now;
                    Report(progress, L2VerifyPhase.Compare, i, total, result.MismatchedBlocks, 0);
                }
            }

            result.ScannedBlocks = result.MatchedBlocks + result.MismatchedBlocks;
            result.ElapsedMs = Environment.TickCount64 - startTicks;

            if (result.MismatchedBlocks > MAX_DETAIL_LINES)
            {
                LogService.DebugFile(
                    $"L2校验：错误明细后续 {result.MismatchedBlocks - MAX_DETAIL_LINES} 条已省略（共 {result.MismatchedBlocks} 块不一致）");
            }

            Report(progress, L2VerifyPhase.Compare, i, total, result.MismatchedBlocks, 0);
            _lastVerify = result;
            return result;
        }

        #endregion

        #region 修复（回写）

        /// <summary>
        /// 用源盘数据回写「刚才那次校验」认定的不一致块。
        /// **只写 L2 容器**：不动 index.bin、不换身份戳、不动 M 环的 freq/环位（这是后台维护，不是用户访问，
        /// 计进去会污染频次信号）。源盘只读 ⇒ 这个动作不可能弄坏源盘。
        /// </summary>
        public L2RepairResult Repair(Action<L2VerifyProgress>? progress, CancellationToken ct)
        {
            EnsureOpen();
            if (_lastVerify == null)
                throw new InvalidOperationException(Locale.T("l2v.err.verifyFirst"));

            var result = new L2RepairResult();
            int total = _mismatch.Count;
            long mismatchedTotal = _lastVerify.MismatchedBlocks;
            long startTicks = Environment.TickCount64;
            long lastReport = startTicks;

            Report(progress, L2VerifyPhase.Writeback, 0, total, mismatchedTotal, 0);

            int i = 0;
            while (i < total)
            {
                if (ct.IsCancellationRequested) { result.Cancelled = true; break; }

                int run = 1;
                while (i + run < total && run < MAX_RUN_BLOCKS &&
                       _blocks[_mismatch[i + run]] == _blocks[_mismatch[i + run - 1]] + 1) run++;

                ReadSourceRun(_blocks[_mismatch[i]], run);

                for (int k = 0; k < run; k++)
                {
                    int idx = _mismatch[i + k];
                    if (!_srcOk[k]) { result.ReadFailedBlocks++; continue; }   // 没有真值 ⇒ 不回写

                    try
                    {
                        WriteContainerSlot(_slots[idx], k * BLOCK_SIZE);
                        result.RepairedBlocks++;
                    }
                    catch (IOException ex)
                    {
                        result.WriteFailedBlocks++;
                        LogService.DebugFile($"L2修复：回写槽 {_slots[idx]}（块 {_blocks[idx]}）失败：{ex.Message}");
                    }
                }

                i += run;
                long now = Environment.TickCount64;
                if (now - lastReport >= PROGRESS_INTERVAL_MS)
                {
                    lastReport = now;
                    Report(progress, L2VerifyPhase.Writeback, i, total, mismatchedTotal, result.RepairedBlocks);
                }
            }

            if (result.RepairedBlocks > 0)
            {
                // 回写的数据必须真落盘（账本本身不动 ⇒ 修复是幂等的，不需要任何原子性）
                _container!.Flush(true);
            }

            result.ElapsedMs = Environment.TickCount64 - startTicks;

            // 全部回写成功才清空"待修"清单；有失败就留着，用户可再点一次修复重试
            if (!result.Cancelled && result.WriteFailedBlocks == 0 && result.ReadFailedBlocks == 0)
            {
                _mismatch.Clear();
            }

            Report(progress, L2VerifyPhase.Writeback, i, total, mismatchedTotal, result.RepairedBlocks);
            return result;
        }

        #endregion

        #region 扫描原语

        private void EnsureOpen()
        {
            if (_container == null || _device == null)
                throw new InvalidOperationException(Locale.T("l2v.err.sessionNotOpen"));
        }

        /// <summary>
        /// 读源盘从 <paramref name="firstBlock"/> 起的连续 <paramref name="count"/> 块到 <see cref="_srcBuf"/>，
        /// 每块的成功与否写进 <see cref="_srcOk"/>。整段读失败（典型是坏道）时**降级成逐块读**，
        /// 这样只有真正坏的那一块会被记为"读失败"，而不会连坐同段的邻居。
        /// </summary>
        private void ReadSourceRun(long firstBlock, int count)
        {
            try
            {
                _device!.Read(firstBlock * BLOCK_SIZE, _srcBuf, 0, count * BLOCK_SIZE);
                for (int k = 0; k < count; k++) _srcOk[k] = true;
                return;
            }
            catch (IOException)
            {
                // 落到下面的逐块降级
            }

            for (int k = 0; k < count; k++)
            {
                try
                {
                    _device!.Read((firstBlock + k) * BLOCK_SIZE, _srcBuf, k * BLOCK_SIZE, BLOCK_SIZE);
                    _srcOk[k] = true;
                }
                catch (IOException ex)
                {
                    _srcOk[k] = false;
                    LogService.DebugFile($"L2校验：读源盘失败（块 {firstBlock + k}）：{ex.Message}");
                }
            }
        }

        private void ReadContainerSlot(int slot)
        {
            _container!.Position = (long)(slot + 1) * BLOCK_SIZE;
            _container.ReadExactly(_slotBuf, 0, BLOCK_SIZE);
        }

        private void WriteContainerSlot(int slot, int bufferOffset)
        {
            _container!.Position = (long)(slot + 1) * BLOCK_SIZE;
            _container.Write(_srcBuf, bufferOffset, BLOCK_SIZE);
        }

        private static void Report(Action<L2VerifyProgress>? progress, L2VerifyPhase phase,
            long processed, long total, long mismatched, long repaired)
        {
            progress?.Invoke(new L2VerifyProgress
            {
                Phase = phase,
                Processed = processed,
                Total = total,
                Mismatched = mismatched,
                Repaired = repaired
            });
        }

        #endregion

        #region 身份与散列（与 SsdCacheService 同算法）

        /// <summary>
        /// **只读 peek**：读出 L2 容器头里记录的**归属设备身份**（偏移 24，算法与写入端逐字一致）。
        ///
        /// 用途：「校验 L2」据此把候选盘收窄到"容器真正属于的那块盘"。标记是**散列、不可逆**，
        /// 所以只能由调用方枚举本地盘、逐个调 <see cref="Identify"/> 重算比对。
        ///
        /// **刻意只读**：不开单实例锁、不轮换身份戳——那两件事是"进校验窗口"才做的（见 OpenLedger 末尾的
        /// RotateStamp），只是看一眼不该在盘上留下任何痕迹。
        ///
        /// 返回 <c>null</c> = 拿不到归属身份（没启用 L2 / 目录或容器不存在 / 头部不合法 / 读失败），
        /// 调用方据此回退到完整清单，把精确报错留给校验器去说。
        /// </summary>
        public static long? TryReadOwnerIdentity(DiskConfig config)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config.SsdCachePath)) return null;

                string containerPath = Path.Combine(
                    Path.GetFullPath(config.SsdCachePath).TrimEnd('\\'), ContainerFileName);
                if (!File.Exists(containerPath)) return null;

                byte[] header = new byte[BLOCK_SIZE];
                using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite, bufferSize: 1, FileOptions.RandomAccess))
                {
                    fs.ReadExactly(header, 0, BLOCK_SIZE);
                }

                if (Hash(header, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(header, CONTAINER_HASH_LEN) ||
                    BitConverter.ToUInt32(header, 0) != CONTAINER_MAGIC ||
                    BitConverter.ToInt32(header, 4) != CONTAINER_VERSION)
                {
                    return null;
                }

                return BitConverter.ToInt64(header, 24);
            }
            catch
            {
                return null;   // 看一眼失败一律当作"没有可用的 L2"，不打断入口
            }
        }

        /// <summary>某块盘的设备身份，供入口与 <see cref="TryReadOwnerIdentity"/> 的结果比对</summary>
        public static long Identify(PhysicalDiskInfo info) => ComputeDeviceIdentity(info);

        /// <summary>设备身份：型号 + 序列号 + 容量 + 扇区大小 的 FNV-1a 散列（与 SsdCacheService 逐字一致）</summary>
        private static long ComputeDeviceIdentity(PhysicalDiskInfo info)
        {
            string source = $"{info.Model}|{info.SerialNumber}|{info.SizeBytes}|{info.BytesPerSector}";
            ulong h = 14695981039346656037UL;
            foreach (char c in source)
            {
                h = (h ^ c) * 1099511628211UL;
            }
            return unchecked((long)h);
        }

        private static ulong Hash(byte[] data, int offset, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < count; i++)
            {
                h ^= data[offset + i];
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>把读出的字节流增量喂给 FNV-1a（账本正文校验和的算法，与 SsdCacheService 一致）</summary>
        private sealed class HashingStream : Stream
        {
            private readonly Stream _inner;
            public ulong Hash { get; private set; } = 14695981039346656037UL;

            public HashingStream(Stream inner) => _inner = inner;

            private void Accumulate(ReadOnlySpan<byte> buffer)
            {
                ulong h = Hash;
                for (int i = 0; i < buffer.Length; i++)
                {
                    h ^= buffer[i];
                    h *= 1099511628211UL;
                }
                Hash = h;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => false;
            public override bool CanWrite => _inner.CanWrite;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

            public override void Flush() => _inner.Flush();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

            public override int Read(Span<byte> buffer)
            {
                int n = _inner.Read(buffer);
                Accumulate(buffer.Slice(0, n));
                return n;
            }

            public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

            public override void Write(ReadOnlySpan<byte> buffer)
            {
                Accumulate(buffer);
                _inner.Write(buffer);
            }
        }

        #endregion

        public void Dispose()
        {
            try { _container?.Dispose(); } catch { /* 关句柄失败没有补救手段 */ }
            _container = null;

            // **保持脱机**（盘归本程序管，与引擎同口径）
            try { _device?.Dispose(); } catch { }
            _device = null;

            try { _lockFile?.Dispose(); } catch { }
            _lockFile = null;
        }
    }
}

namespace FlyDisk
{
    /// <summary>
    /// 「校验 L2」对话框：先只读逐块比对（绝不写），看到结果后再由用户决定是否「修复」
    /// （把不一致的容器槽用源盘数据覆写）。
    ///
    /// 控件全部用代码构建（不配 designer/resx）——整个校验/修复功能只占这一个文件，`Form1` 只挂一个菜单入口。
    /// 设计定稿见 `后续待办.md` 第二节「L2 校验 / 修复：施工级定稿」。
    /// </summary>
    public sealed class L2VerifyForm : ThemedForm, ILocalizable
    {
        private readonly DiskConfig _config;
        private readonly Label _lblInfo = new();
        private readonly ProgressBar _bar = new();
        private readonly Label _lblCount = new();
        private readonly Button _btnPrimary = new();
        private readonly Button _btnClose = new();
        private readonly Action<Engine.L2VerifyProgress> _onProgress;

        private Engine.L2Verifier? _verifier;
        private CancellationTokenSource? _cts;
        private Engine.L2VerifyResult? _verify;
        private bool _busy;
        private bool _closeWhenDone;
        private bool _repairedCleanly;

        /// <summary>当前说明行对应的动作键（`l2v.action.verify` / `l2v.action.repair`），供切语言时重刷</summary>
        private string _actionKey = "l2v.action.verify";

        /// <summary>
        /// 本次会话结束时，L2 容器与源盘是否**已经一致**（= 这份账本可以安全采信）。
        /// 只有「不一致 = 0 且无读失败」或「不一致已全部修复成功」才算 true；
        /// 取消、中止、修复有失败、或压根没跑完，一律 false。
        /// 启动流程据此决定：干净 ⇒ 带着这份 L2 继续启动（并把容器头的身份戳写回索引里的值）；
        /// 否则 ⇒ 中止启动、不做补救动作（容器头的戳停在进窗口时轮换的新值上，下次启动仍会要求校验）。
        /// </summary>
        public bool LedgerIsConsistent =>
            _repairedCleanly ||
            (_verify is { Cancelled: false, Aborted: false, MismatchedBlocks: 0, ReadFailedBlocks: 0 });

        /// <summary>本次要校验的目标盘号（由调用方在打开本窗口前选定）</summary>
        private readonly int _diskNumber;

        /// <summary>控件登记表（见 <see cref="ILocalizable"/>）：静态文本键 → 控件</summary>
        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        /// <summary>登记一条键（一条键可绑多个控件）</summary>
        private void Bind(string key, params Control[] controls)
        {
            if (!TextBindings.TryGetValue(key, out List<Control>? list))
            {
                list = new List<Control>();
                TextBindings[key] = list;
            }
            list.AddRange(controls);
        }

        /// <summary>
        /// 动态文本：窗体标题不是控件、说明行随动作（校验/修复）变、主按钮随运行状态变，都要重刷（见 <see cref="ILocalizable"/>）。
        /// 本窗口是模态的（主窗菜单在它打开期间点不到），实践中不会在开着时切语言；这里仍按契约实现以求自洽。
        /// </summary>
        public void ApplyDynamicText()
        {
            Text = Locale.T("l2v.title");
            _lblInfo.Text = BuildInfoText(_actionKey);
            UpdateButtons();
        }

        public L2VerifyForm(DiskConfig config, int diskNumber)
        {
            _config = config;
            _diskNumber = diskNumber;
            _onProgress = OnProgress;

            Text = Locale.T("l2v.title");
            ClientSize = new Size(470, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = _btnClose;

            _lblInfo.AutoSize = false;
            _lblInfo.Location = new Point(12, 10);
            _lblInfo.Size = new Size(446, 34);
            _lblInfo.Text = BuildInfoText(_actionKey);

            _bar.Location = new Point(12, 48);
            _bar.Size = new Size(446, 18);
            _bar.Maximum = 1000;
            _bar.Style = ProgressBarStyle.Marquee;

            _lblCount.AutoSize = false;
            _lblCount.Location = new Point(12, 74);
            _lblCount.Size = new Size(446, 48);
            _lblCount.Text = Locale.T("l2v.preparing");

            _btnPrimary.Location = new Point(236, 130);
            _btnPrimary.Size = new Size(110, 28);
            _btnPrimary.Text = Locale.T("common.cancel");
            _btnPrimary.UseVisualStyleBackColor = true;
            _btnPrimary.Click += BtnPrimary_Click;

            _btnClose.Location = new Point(348, 130);
            _btnClose.Size = new Size(110, 28);
            _btnClose.Text = Locale.T("common.close");
            _btnClose.UseVisualStyleBackColor = true;
            _btnClose.Visible = false;
            _btnClose.Click += (s, e) => Close();

            Controls.Add(_lblInfo);
            Controls.Add(_bar);
            Controls.Add(_lblCount);
            Controls.Add(_btnPrimary);
            Controls.Add(_btnClose);

            // 静态文本登记（关掉「关闭」不随状态变）；说明行与主按钮随状态走 ApplyDynamicText
            Bind("common.close", _btnClose);
        }

        /// <summary>说明行：把"这次在干什么、盘什么状态、会写哪里"一次讲清（不弹任何确认框）</summary>
        private string BuildInfoText(string actionKey)
        {
            string disk;
            if (_diskNumber < 0)
            {
                disk = Locale.T("l2v.diskNone");
            }
            else
            {
                string model = string.Empty;
                try
                {
                    Engine.PhysicalDiskInfo info = Engine.PhysicalDiskHandle.Probe(_diskNumber);
                    model = info.Model;
                }
                catch
                {
                    // 探测失败就只显示盘号：真正的错误会在打开会话时如实报出
                }
                disk = model.Length > 0
                    ? Locale.T("l2v.diskWithModel", _diskNumber, model)
                    : Locale.T("l2v.disk", _diskNumber);
            }
            return Locale.T("l2v.info", Locale.T(actionKey), disk);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            StartVerify();
        }

        #region 两个动作

        private void StartVerify()
        {
            _busy = true;
            _repairedCleanly = false;
            _actionKey = "l2v.action.verify";
            _lblInfo.Text = BuildInfoText(_actionKey);
            _lblCount.Text = Locale.T("l2v.loadingLedger");
            _bar.Style = ProgressBarStyle.Marquee;
            UpdateButtons();

            CancellationTokenSource cts = new();
            _cts = cts;
            Engine.L2Verifier verifier = _verifier ??= new Engine.L2Verifier(_config, _diskNumber);

            Task.Run(() =>
            {
                try
                {
                    verifier.Open();
                    Engine.L2VerifyResult result = verifier.Verify(_onProgress, cts.Token);
                    Post(() => OnVerified(result));
                }
                catch (Exception ex)
                {
                    Post(() => OnFailed(ex.Message));
                }
            });
        }

        private void StartRepair()
        {
            if (_verifier == null) return;

            _busy = true;
            _actionKey = "l2v.action.repair";
            _lblInfo.Text = BuildInfoText(_actionKey);
            _lblCount.Text = Locale.T("l2v.writingBack");
            _bar.Style = ProgressBarStyle.Marquee;
            UpdateButtons();

            CancellationTokenSource cts = new();
            _cts = cts;
            Engine.L2Verifier verifier = _verifier;

            Task.Run(() =>
            {
                try
                {
                    Engine.L2RepairResult result = verifier.Repair(_onProgress, cts.Token);
                    Post(() => OnRepaired(result));
                }
                catch (Exception ex)
                {
                    Post(() => OnFailed(ex.Message));
                }
            });
        }

        #endregion

        #region 结果落地

        private void OnVerified(Engine.L2VerifyResult result)
        {
            _busy = false;
            _verify = result;

            string text = DescribeVerify(result);
            if (result.MismatchedBlocks > 0 && !result.Cancelled && !result.Aborted)
            {
                text += Locale.T("l2v.detailHint");
            }
            _lblCount.Text = text;

            string summary = $"L2 校验：{text}";
            Engine.LogService.DebugFile(summary);

            UpdateButtons();
            if (_closeWhenDone) Close();
        }

        private void OnRepaired(Engine.L2RepairResult result)
        {
            _busy = false;
            _repairedCleanly = !result.Cancelled && result.WriteFailedBlocks == 0 && result.ReadFailedBlocks == 0;

            string text = _verify != null ? DescribeVerify(_verify) : Locale.T("l2v.repair");
            text += result.Cancelled
                ? Locale.T("l2v.repairCancelled", result.RepairedBlocks.ToString("N0"))
                : Locale.T("l2v.repaired", result.RepairedBlocks.ToString("N0"));
            if (result.ReadFailedBlocks > 0) text += Locale.T("l2v.readFailed", result.ReadFailedBlocks.ToString("N0"));
            if (result.WriteFailedBlocks > 0) text += Locale.T("l2v.writeFailed", result.WriteFailedBlocks.ToString("N0"));
            _lblCount.Text = text;

            string summary = $"L2 修复：已回写 {result.RepairedBlocks:N0} 块（读失败 {result.ReadFailedBlocks:N0} / " +
                             $"写入失败 {result.WriteFailedBlocks:N0}）· 用时 {FormatDuration(result.ElapsedMs)}";
            Engine.LogService.DebugFile(summary);

            UpdateButtons();
            if (_closeWhenDone) Close();
        }

        private void OnFailed(string message)
        {
            _busy = false;
            _verify = null;
            _repairedCleanly = false;
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Value = 0;
            _lblCount.Text = Locale.T("l2v.failed", message);

            Engine.LogService.DebugFile($"L2校验/修复失败：{message}");

            // 打开/扫描失败：把会话收干净（释放锁与句柄，盘保持脱机）
            _verifier?.Dispose();
            _verifier = null;

            UpdateButtons();
            if (_closeWhenDone) Close();
        }

        private void UpdateButtons()
        {
            if (_busy)
            {
                _btnPrimary.Text = Locale.T("common.cancel");
                _btnPrimary.Enabled = true;
                _btnClose.Visible = false;
                return;
            }

            bool canRepair = _verify is { MismatchedBlocks: > 0, Cancelled: false, Aborted: false } && !_repairedCleanly;
            _btnPrimary.Text = Locale.T("l2v.repair");
            _btnPrimary.Enabled = canRepair;
            _btnClose.Visible = true;
        }

        #endregion

        #region 进度与按钮

        /// <summary>进度回调来自后台线程：一律切回 UI 线程再碰控件</summary>
        private void OnProgress(Engine.L2VerifyProgress progress)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action<Engine.L2VerifyProgress>(RenderProgress), progress);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void RenderProgress(Engine.L2VerifyProgress progress)
        {
            if (IsDisposed) return;
            if (progress.Total <= 0) return;

            if (_bar.Style != ProgressBarStyle.Continuous)
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = 1000;
            }
            _bar.Value = (int)Math.Clamp(progress.Processed * 1000 / progress.Total, 0, 1000);

            string phase = Locale.T(progress.Phase == Engine.L2VerifyPhase.Writeback
                ? "l2v.phase.writeback"
                : "l2v.phase.compare");
            string text = Locale.T("l2v.progress", phase,
                progress.Processed.ToString("N0"), progress.Total.ToString("N0"), progress.Mismatched.ToString("N0"));
            if (progress.Repaired > 0) text += Locale.T("l2v.repaired", progress.Repaired.ToString("N0"));
            _lblCount.Text = text;
        }

        private void BtnPrimary_Click(object? sender, EventArgs e)
        {
            if (_busy)
            {
                _cts?.Cancel();
                _btnPrimary.Enabled = false;
                _lblCount.Text = Locale.T("l2v.cancelling");
                return;
            }
            StartRepair();
        }

        private void Post(Action action)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        #endregion

        #region 文案

        private static string DescribeVerify(Engine.L2VerifyResult r)
        {
            string duration = FormatDuration(r.ElapsedMs);
            string speed = r.ScannedBlocks > 0
                ? FormatSpeed(r.ScannedBlocks * (long)ServiceConstants.BlockSize, r.ElapsedMs)
                : "—";

            if (r.Aborted)
                return Locale.T("l2v.result.aborted", r.AbortReason,
                    r.ScannedBlocks.ToString("N0"), r.PlannedBlocks.ToString("N0"), r.MismatchedBlocks.ToString("N0"));
            if (r.Cancelled)
                return Locale.T("l2v.result.cancelled",
                    r.ScannedBlocks.ToString("N0"), r.PlannedBlocks.ToString("N0"), r.MismatchedBlocks.ToString("N0"));
            if (r.PlannedBlocks == 0)
                return Locale.T("l2v.result.noBlocks",
                    r.OccupiedSlots.ToString("N0"), r.OutOfRangeBlocks.ToString("N0"), r.DuplicateBlocks.ToString("N0"));
            if (r.MismatchedBlocks == 0)
                return Locale.T("l2v.result.consistent", r.ScannedBlocks.ToString("N0"), duration, speed);

            double rate = (double)r.MismatchedBlocks / r.ScannedBlocks;
            string tail = r.ReadFailedBlocks > 0 ? Locale.T("l2v.readFailed", r.ReadFailedBlocks.ToString("N0")) : string.Empty;
            return Locale.T("l2v.result.mismatch",
                r.MismatchedBlocks.ToString("N0"), rate.ToString("P4"), r.ScannedBlocks.ToString("N0"), duration, speed, tail);
        }

        private static string FormatDuration(double milliseconds)
        {
            TimeSpan ts = TimeSpan.FromMilliseconds(milliseconds);
            return ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";
        }

        private static string FormatSpeed(long bytes, double milliseconds)
        {
            double mbPerSecond = bytes / 1048576.0 / Math.Max(0.001, milliseconds / 1000.0);
            return mbPerSecond >= 1024 ? $"{mbPerSecond / 1024:F2} GB/s" : $"{mbPerSecond:F1} MB/s";
        }

        #endregion

        #region 生命周期

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                // 后台还在跑：先取消，等它收尾（避免拆掉正在用的句柄），收尾后再关
                _closeWhenDone = true;
                _cts?.Cancel();
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { _cts?.Dispose(); } catch { }
            _cts = null;

            // 校验干净（不一致 = 0 或已全部修复成功）⇒ 把容器头的身份戳**写回索引里的那个值**，
            // 让引擎下次启动能正常载入这份账本。没干净就**不写**：戳会停在进窗口时轮换的新值上，
            // 与索引对不上 ⇒ 下次启动仍会要求校验（这正是"进过校验窗口但没验完"要留下的痕迹）。
            if (LedgerIsConsistent)
            {
                try
                {
                    _verifier?.ConfirmLedger();
                }
                catch (Exception ex)
                {
                    Engine.LogService.DebugFile($"L2 校验：写回账本身份戳失败（下次启动会再次要求校验）：{ex.Message}");
                }
            }

            _verifier?.Dispose();   // 释放单实例锁与句柄（盘保持脱机）
            _verifier = null;

            base.OnFormClosed(e);
        }

        #endregion
    }
}
