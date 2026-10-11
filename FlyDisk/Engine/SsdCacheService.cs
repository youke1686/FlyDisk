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
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DiskAccessLibrary;
using FlyDisk.Localization;
using FlyDisk.Models;
using Microsoft.Win32.SafeHandles;

namespace FlyDisk.Engine
{
    /// <summary>
    /// SSD 二级缓存（L2）——跨重启存活的块缓存。**淘汰结构 = 单条 M 环（FIFO-reinsertion）+ ghost**
    /// （2026-09-30 改造：原 S3-FIFO 的三段结构里 **S 队列已删除**，理由见 `后续待办.md` 第五节）。
    /// 一句话理由：S 队列的"一次性访问过滤"职责 **L1 已经在做**（L1 的准入就是 S 的准入、L1 的 LRU 淘汰
    /// 就是从 S 出队），L2 再放一个 S 只是把同一件事做第二遍，而且它拿到的输入是"每次访问"而不是
    /// "L1 装不下"——真正的信号淹掉之后，S 长期满溢、M 饿死。
    ///
    /// 设计见 `关于内存缓存的进一步讨论.md` §7（该章已按上述改造更新）。与阶段一的差别只有一个：
    /// **键从"路径 + 文件内块号"换成"全局块号"**（`LBA / SectorsPerBlock`），于是整张文件表消失、
    /// 失效退化成块区间失效。容器、身份戳、空洞机制、账本原子替换**逐行保留**。
    ///
    /// 读本文件前必须知道七条前提：
    /// 1. **只服务读**：L2 里的数据是"源盘读取时的写穿副本"，永不对源盘写回；源盘写入走"透传 + 失效"。
    /// 2. **准入的唯一入口是"回源读到整块"**（<see cref="Insert"/>）：L1 命中对 L2 **完全静默**——
    ///    那个块 L1 服务得了，L2 既不必留它、也不必记它的频次。旧版在这里调 `Touch` 把"L1 命中的块"
    ///    也灌进 L2，正是 S 长期满溢、M 只剩千余块的原因（实测数字见 `后续待办.md` 第五节）。
    /// 3. **跑在 target 工作线程内**（库里每个 target 一个后台线程串行执行 SCSI 命令）：块读写与淘汰都在
    ///    同一线程上完成，因此本类的表**不需要加锁**；统计只读 Interlocked 计数器。
    ///    索引刷盘只在停服时做一次（那时 target 已停），所以它也不参与并发。
    /// 4. **持久化只认"干净关服"**：容器头部第 0 页存一个身份戳，**每次开机都换一个新的并落盘**；
    ///    索引只在停服那一次写出（内含当前身份戳）。于是「索引里的戳 == 容器里的戳」
    ///    ⟺ 上次走到了停服那一步 ⟺ 上次是正常关闭。掉电/强杀/崩溃 ⇒ 戳对不上 ⇒ **整层作废**
    ///    （脏即弃，绝不尝试修复）。因此运行期完全不碰盘，没有"周期性全量快照"带来的停顿；
    ///    代价是异常终止会白白丢掉整层缓存（缓存的代价，不是数据的代价）。
    /// 5. **空洞机制**：失效的槽位不能从 FIFO 环中间删除，只能标记为空洞、等弹到它时顺手回收；
    ///    空洞的槽位要等环上条目被弹出才回到空闲栈——这条不变量保证"同一个槽同时坐两个环条目"不可能发生。
    /// 6. ★ **账本是否可信（阶段二新增，也是本层最要紧的判据）**：
    ///    阶段一靠"源文件签名（大小 + 最后写入时间）"判断账本里的每个文件有没有变过；块设备下没有文件，
    ///    这个判据整段消失。取而代之的是**设备级判据**：打开这块盘时它**原本就是脱机**的。
    ///
    ///    为什么这条能成立：本程序把盘"占为己有"——启动时整盘脱机（属性**持久化**）、停止时不联机
    ///    （见 <see cref="PhysicalDiskHandle"/> 类注释）。于是"盘是脱机的"就等价于"从上次运行到现在，
    ///    没有任何程序能写它"，账本自然不会过期；反过来，只要盘是联机的（用户手动联机了，或它被拿到
    ///    别的机器 / 双系统里用过），账本一律**不采信**——**宁可丢缓存，不可返回陈旧块**（那会让 NTFS 读到
    ///    别人的数据，属于卷损坏级事故）。
    ///
    ///    **本层防不住的两类"离线修改"（如实登记，与 PrimoCache 文档所述同类问题）**：
    ///    ① 用户手动把盘联机 → 改了数据 → 自己再脱机：全程没有"联机状态可被我们观测到"的时机；
    ///    ② 把盘接到另一台电脑或另一个操作系统上改写：本机 Windows 里那条"脱机属性"记录根本不会变。
    ///    这两类只能靠用户自觉规避（与 PrimoCache"离线修改"的免责口径一致）。
    ///
    /// 7. ★ **容器不是预分配的**（2026-10-11 起）：它在磁盘上只占"真正用到的部分"——启动时最多一个头部块，
    ///    随写入**按 Slab（64 MB = 16384 槽）整块增长**；扩展时用 `SetFileValidData` 免掉文件系统的零填充
    ///    （该卷不支持 VDL 语义 / 特权不可用 ⇒ 本会话门闩回退纯 `SetLength`；扩展失败只关"扩容"、**不停用 L2**）。
    ///    容量变化（上次容器槽数 ≠ 本次配置）**缩放保留**、由 UI 在启动前确认。容器长度不再等于整容量，
    ///    因此**只保证"覆盖已用到的槽"**。详见 `docs/L2容器按需增长与容量缩放_设计.md`。
    ///
    /// 8. ★ **账本只有"装载"与"报错"两种结局，绝不静默重建**（2026-10-11 起，约定见 AGENTS.md §2.3）：
    ///    凡是"要不要清掉 / 要不要校验 / 要不要缩放"的判定与询问**全部在 UI 的启动前预检里**
    ///    （`Form1.PrepareL2BeforeStart`）——引擎只做加速。所以本层：**确实没有账本**（容器与索引都不存在）
    ///    时新建空缓存；**其余任何不自洽**（文件不成对 / 不属于本盘 / 盘原本联机 / 身份戳不符）
    ///    一律抛 <see cref="L2LedgerUnusableException"/> 终止启动，让用户看见、由预检去问怎么办。
    /// </summary>
    public sealed class SsdCacheService : IDisposable
    {
        #region 常量

        private const int BLOCK_SIZE = ServiceConstants.BlockSize;

        /// <summary>整块大小（64 MB）。**L2 容器按 Slab 整块增长 / 截断**（见 docs/L2容器按需增长与容量缩放_设计.md §4）。</summary>
        private const int SLAB_SIZE = ServiceConstants.SlabSize;

        /// <summary>一个 Slab 的槽数（64 MB / 4 KB = 16384）。</summary>
        private const int SLOTS_PER_SLAB = SLAB_SIZE / BLOCK_SIZE;

        // 容器头部：占一个块，存"身份戳 + 设备身份"。用来识别"用户删掉了 cache.dat 但 index.bin 还在"
        // 与"换了另一块盘却沿用同一个目录"这两类情况——若不做这个校验，重建出来的容器（全零）会被旧索引
        // 当成有效缓存，把全零块当数据返回（静默错数据）。
        private const int CONTAINER_HEADER_SIZE = BLOCK_SIZE;
        private const uint CONTAINER_MAGIC = 0x44434453;   // "SDCD"
        private const int CONTAINER_VERSION = 2;           // 2：头部加入设备身份（阶段一为 1，只有身份戳）
        private const int CONTAINER_HASH_LEN = 32;         // 头部自身校验和覆盖的字节数（含设备身份）

        private const uint INDEX_MAGIC = 0x31434453;       // "SDC1"
        private const int INDEX_VERSION = 3;               // 3：删掉 S 环，正文只写 M 环（头部布局不变，sLimit/sCount 恒 0）
        private const int INDEX_HEADER_SIZE = 96;
        private const int INDEX_HEADER_HASH_OFFSET = 88;   // 头部校验和所在的偏移（= 它覆盖的字节数）

        // M 的"激进/保守"分界：可用槽位（_freeCount）占槽位总数低于保留量时，只收 ghost 命中的块。
        // 阈值本身是配置项（DiskConfig.SsdConservativeThreshold，占用率形态），这里只说明**为什么用 _freeCount 判**：
        // **必须用 _freeCount 而不是 _usedSlots**：作废产生的空洞在 _usedSlots 上已经不计数，但那些槽
        // 要等 FIFO 环弹到它才回空闲栈，是拿不回来的（见 后续待办.md 第五节）。

        private const byte FREQ_MASK = 0x03;               // 每块 2 bit 访问计数（0..3）
        private const int MAX_FREQ = 3;

        // 容量下限 = 1 个 Slab（64 MiB）。容量必须**规整为整 Slab**，否则"整块增长"的批次边界不干净；
        // 设置界面本来就是整 GiB（1 GiB = 16 个 Slab），所以这条对用户可选值无影响（见设计文档 §4 D1）。
        private const int MIN_SLOTS = SLOTS_PER_SLAB;
        // 容量上限 64 GiB。**上限的实质是内存**：索引/槽位结构常驻内存，约 MetadataBytesPerBlock 字节/块，
        // 每 1 GiB 容量约 15 MiB，所以 64 GiB 大约要 930 MiB 常驻内存（设置界面会把这份估算摊给用户看）。
        // 想再往上放就得改块大小（见 后续待办.md）或把索引也落盘，不能只改这个数。
        private const long MAX_CAPACITY_BYTES = 64L * 1024 * 1024 * 1024;
        private const int MAX_SLOTS = (int)(MAX_CAPACITY_BYTES / BLOCK_SIZE);
        // 为**缓存盘**保留的最小空闲空间（1 GiB）。SSD 快满时读写速度**可能明显变慢**，留一段空闲是保证
        // 加速运行流畅的必要条件，也顺带避免卷被缓存容器填满。UI 在"夹取警告"里会说明这一点。
        private const long MIN_KEEP_FREE_BYTES = 1L * 1024 * 1024 * 1024;

        private const int RING_SLACK = 4;                  // 环形缓冲余量，避免在边界上反复淘汰

        /// <summary>为缓存盘保留的最小空闲空间（字节）——UI 在"夹取警告"里要说明这个数</summary>
        public static long KeepFreeBytes => MIN_KEEP_FREE_BYTES;

        /// <summary>
        /// 每块（<see cref="ServiceConstants.BlockSize"/>）索引的**常驻内存开销估算**（字节）。
        /// 用于设置界面把"容量预算"折算成内存代价（每 1 GiB ≈ 15 MiB）。
        /// 与 <see cref="MAX_SLOTS"/> 的注释同源；口径若有变动，两处一起改。
        /// </summary>
        public const int MetadataBytesPerBlock = 58;

        private const string ContainerFileName = "cache.dat";
        private const string IndexFileName = "index.bin";
        private const string IndexTempFileName = "index.tmp";
        private const string LockFileName = "cache.lock";

        #endregion

        #region 字段

        private readonly string _dir;              // 缓存目录（容器与索引都在这里）
        private readonly long _deviceIdentity;     // 被加速设备的身份（型号 + 容量 + 扇区大小 的散列）

        private readonly int _slotCount;           // 总槽位数
        private readonly int _mLimit;              // M 队列目标长度（= 全部槽位：S 删掉后整条环都归 M）
        private readonly int _ghostLimit;          // ghost 条目数上限
        private readonly int _fillingReserve;      // 激进期门槛：可用槽位 ≥ 它 ⇒ 回源块一律写 M
        private readonly double _conservativeOccupancy;  // 生效的保守线（占用率，已夹取）——供检查器画水位线

        private FileStream? _container;
        private FileStream? _lockFile;             // 单实例锁（两个实例同时写容器会把索引写坏）
        private long _containerId;                 // 容器身份戳（写在容器头部，也写进索引）
        private long _containerBytes;              // 容器**当前长度**（按需增长后不再等于"整容量"）
        private volatile bool _sfvdUnavailable;    // 门闩：该卷不支持 VDL 语义 / 特权不可用 ⇒ 本会话扩展不再免零填充
        private volatile bool _growthBlocked;      // 门闩：容器扩展失败（卷满）⇒ 本会话只停"扩容"，L2 继续服务已覆盖范围

        // ===== 每槽元数据（下标 = 槽号）=====
        // 阶段一这里是 (fileId, 文件内块号)；块设备下没有文件，只剩一个**全局块号**：
        // -1 = 该槽没有有效数据（空闲或空洞）
        private readonly long[] _slotBlockIndex;
        private readonly byte[] _slotFlags;        // freq(2bit) + 保留位

        // ===== 块索引：全局块号 → 槽号 =====
        private readonly Dictionary<long, int> _blocksByIndex = new();

        // ===== M 的 FIFO 环（环形缓冲，不需要链表指针）=====
        private readonly int[] _mRing;
        private int _mHead, _mCount;

        // ===== ghost 队列（只存块号，无数据）=====
        private readonly long[] _ghostRing;
        private readonly HashSet<long> _ghostSet = new();
        private int _ghostHead, _ghostCount;

        // ===== 空闲槽栈 =====
        private readonly int[] _freeStack;
        private int _freeCount;

        /// <summary>停服序列一旦置位，本层不再准入 / 不再失效（保证账本是一致快照，见 <see cref="Freeze"/>）</summary>
        private volatile bool _frozen;
        private volatile bool _failed;            // 运行期 IO 故障后停用整层，避免读取部分写入的数据
        private bool _persistLedger = true;
        public bool IsEnabled => _container != null && !_failed;

        // ===== 计数（供每秒统计快照跨线程读取：只读计数器，不遍历集合）=====
        private long _usedSlots;
        private long _holeSlots;
        private long _hitBlocks;
        private long _missBlocks;
        private long _writeBlocks;
        private long _fillBlocks;          // 其中来自"激进期"（可用槽位充足，回源块来者不拒）的部分
        private long _ghostPushBlocks;     // 新键被记进 ghost 的次数（"见过一个新块"）
        private long _evictMBlocks;
        private long _resurrectBlocks;     // 其中来自"ghost 命中（复活）"的部分
        private long _invalidatedBlocks;
        private long _writeUpdateBlocks;   // 写命中刷新（W1）：整块被写覆盖、且该块当时在 L2 中，就地刷成新数据
        private long _generation;
        private long _loadMs;
        private long _lastFlushMs;
        private readonly bool _loadedFromDisk;

        #endregion

        #region 构造与统计

        /// <param name="config">配置（用 SsdCachePath / SsdCacheMaxBytes / ghost 倍率）</param>
        /// <param name="source">
        /// 块源的"身份与形状"（<see cref="BlockSourceInfo"/>）：本层从它取**设备身份**，
        /// 并用 <c>WasOnline</c> 判定账本可信度。**它不感知块源在本地还是远端**——
        /// 远端形态下这两项都由服务端捎过来（见 后续待办.md 第一节）。
        /// </param>
        /// <remarks>
        /// **账本在这里"要么装载、要么报错"，绝不静默重建**：所有"要不要清掉 / 要不要校验"的判定都在
        /// UI 的启动前预检里做完（见 AGENTS.md §2.3 的职责边界），引擎只在**确实没有账本**时新建空缓存。
        /// </remarks>
        /// <exception cref="L2LedgerUnusableException">
        /// 账本存在但不可用：不完整 / 不属于本盘 / 盘原本联机 / 上次未正常关服
        /// </exception>
        public SsdCacheService(DiskConfig config, BlockSourceInfo source)
        {
            _dir = Path.GetFullPath(config.SsdCachePath).TrimEnd('\\');
            _deviceIdentity = ComputeDeviceIdentity(source.Model, source.SerialNumber, source.SizeBytes, source.BytesPerSector);

            Directory.CreateDirectory(_dir);

            // 单实例锁：缓存丢了可以接受，但"被自己两个实例写坏"不可接受
            _lockFile = new FileStream(Path.Combine(_dir, LockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);

            try
            {
                _slotCount = ComputeSlotCount(_dir, config.SsdCacheMaxBytes);

                double ghostFraction = Clamp(config.SsdCacheGhostFraction, 0.0, 4.0);
                if (Math.Abs(config.SsdCacheGhostFraction - ghostFraction) > 1e-9)
                {
                    LogService.DebugFile(
                        $"SSD 二级缓存：ghost 倍率越界已夹取 {config.SsdCacheGhostFraction} → {ghostFraction}");
                }
                _mLimit = Math.Max(1, _slotCount);          // S 删掉后整条环都归 M
                _ghostLimit = (int)(_mLimit * ghostFraction);

                // 保守线（占用率）→ 保留量：占用 80% 与可用 20% 是同一个约束的两种说法。
                // 夹到 [0.50, 0.99]：低于 50% 会让 L2 几乎一直保守（学不动），等于 1.0 则退化成"永不保守"。
                double occupancy = Clamp(config.SsdConservativeThreshold, 0.50, 0.99);
                if (Math.Abs(config.SsdConservativeThreshold - occupancy) > 1e-9)
                {
                    LogService.DebugFile(
                        $"SSD 二级缓存：保守线越界已夹取 {config.SsdConservativeThreshold} → {occupancy}");
                }
                _conservativeOccupancy = occupancy;
                _fillingReserve = Math.Max(1, (int)(_slotCount * (1.0 - occupancy)));

                _slotBlockIndex = new long[_slotCount];
                _slotFlags = new byte[_slotCount];
                Array.Fill(_slotBlockIndex, -1);
                _freeStack = new int[_slotCount];
                _mRing = new int[_mLimit + RING_SLACK];
                _ghostRing = new long[Math.Max(1, _ghostLimit)];

                long t0 = Environment.TickCount64;

                // **先看盘上到底有没有账本**：FileMode.OpenOrCreate 会凭空建出空文件，所以必须在打开之前看
                string containerPath = Path.Combine(_dir, ContainerFileName);
                string indexPath = Path.Combine(_dir, IndexFileName);
                bool hadContainer = File.Exists(containerPath) && new FileInfo(containerPath).Length > 0;
                bool hadIndex = File.Exists(indexPath);

                _container = new FileStream(containerPath, FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
                _containerBytes = _container.Length;

                // 容器**按需、按 Slab 整块增长**（见 docs/L2容器按需增长与容量缩放_设计.md §4 D2）：
                // 启动时只要求"至少有一个头部块、且头部可信"，**不再要求长度等于整容量**——这正是
                // "不再一次性初始化整个文件"、从而修掉"大 L2 启动卡住"的关键。
                long previousId = 0;
                int storedSlots = 0;
                bool containerReady = _containerBytes >= BLOCK_SIZE && TryReadContainerHeader(out previousId, out storedSlots);

                bool loaded;
                if (!hadContainer && !hadIndex)
                {
                    // **确实没有账本**：新建空缓存。这是引擎唯一允许"自动做"的动作——它不是回退，
                    // 只是"首次使用"的正常路径。
                    ResetToEmpty("缓存目录是空的（首次使用）");
                    loaded = false;
                }
                else
                {
                    // **有账本就必须装得起来**：任何不自我一致一律**报错终止**，绝不静默重建。
                    // 这些情形本该由 UI 的启动前预检先问清楚（清空 / 校验后保留）；走到这里说明
                    // UI 与引擎看到的状态不一致（或有人绕过了 UI）——此时静默重建会让人不知不觉丢缓存，
                    // 所以要报出来让人看见。
                    if (!containerReady || !hadIndex)
                        throw new L2LedgerUnusableException(Locale.T("engine.l2.ledgerPartial"));

                    // 账本可信判据（见类注释第 6 条）：只有当"打开这块盘时它原本就是脱机"时，
                    // 才可能有人打包票说"上次运行之后没人写过它"。盘若是联机的（用户手动联机过、
                    // 或它去过别的机器），一律不采信——绝不能把陈旧块当数据返回。
                    if (source.WasOnline)
                        throw new L2LedgerUnusableException(Locale.T("engine.l2.ledgerUntrustedOnline"));

                    // 身份戳必须对得上：每次开机都换戳 ⇒ 对不上就是"上次没正常关服"
                    if (!TryLoadIndex(previousId))
                        throw new L2LedgerUnusableException(Locale.T("engine.l2.ledgerInconsistent"));

                    // **容量缩放**：缩容时把文件截到"覆盖最大已登记槽"的 Slab 边界（回收物理空间）；
                    // 扩容**不预扩**——交给按需增长（见设计文档 §4 D6）。缩容按"位置截断"，与热度无关。
                    TruncateToUsedSlabs();
                    loaded = true;
                }

                // **开机留痕**（整个方案里唯一一处运行期落盘）：把身份戳换成新的并 FlushFileBuffers。
                // 从此盘上那份旧索引的戳永远对不上 ⇒ 本次运行无论怎么改容器、无论怎么死，下次开机都不会误信它。
                RotateContainerId();

                _loadMs = Environment.TickCount64 - t0;
                _loadedFromDisk = loaded;

                LogService.DebugFile(
                    $"SSD 二级缓存 {(loaded ? "已载入" : "已新建")}：{_slotCount} 槽位（{(long)_slotCount * BLOCK_SIZE / 1024 / 1024} MB / " +
                    $"{(long)_slotCount / SLOTS_PER_SLAB} Slab）" +
                    (loaded && storedSlots != _slotCount ? $"，由上次 {storedSlots} 槽缩放而来" : "") +
                    $" / M={_mLimit} G={_ghostLimit}（保守门槛 {_fillingReserve} 空闲槽 / 保守线 {_conservativeOccupancy:P0}）/ 占用 {UsedSlots} 槽、空洞 {HoleSlots}" +
                    $" / 容器 {_containerBytes / 1024 / 1024} MB / 用时 {_loadMs} ms / 身份戳 0x{_containerId:X16}" +
                    (loaded ? "（异常终止会使整层缓存作废）" : "（本次新建空缓存）"));
            }
            catch
            {
                // 构造失败时对象不会交给 CacheService，必须当场释放容器和锁，允许用户立即重试。
                Close(persistLedger: false);
                throw;
            }
        }

        private static double Clamp(double value, double min, double max) => value < min ? min : (value > max ? max : value);

        /// <summary>
        /// 设备身份：型号 + 序列号 + 容量 + 扇区大小 的 FNV-1a 散列。
        /// 换盘（哪怕是同一型号的另一块盘，只要容量或序列号不同）就会对不上，容器随之重建——
        /// 这是"块号只在同一块盘上才有意义"这条事实的唯一守卫。
        ///
        /// **远端形态下同样成立**：服务端把这四项原样报过来（见 后续待办.md 第一节），
        /// 所以本地与远端的身份算法一字不差，L2 容器天然不认错盘。
        /// </summary>
        private static long ComputeDeviceIdentity(string model, string serialNumber, long sizeBytes, int bytesPerSector)
        {
            string source = $"{model}|{serialNumber}|{sizeBytes}|{bytesPerSector}";
            ulong h = 14695981039346656037UL;
            foreach (char c in source)
            {
                h = (h ^ c) * 1099511628211UL;
            }
            return unchecked((long)h);
        }

        /// <summary>容量（字节）→ 槽位数：下限 1 Slab（64 MiB）、上限 64 GiB，为**缓存盘**留 1 GiB，**并规整为整 Slab**</summary>
        public static int ComputeSlotCount(string dir, long maxBytes)
        {
            int slots = (int)Math.Clamp(maxBytes / BLOCK_SIZE, MIN_SLOTS, MAX_SLOTS);
            try
            {
                string? root = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(root))
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    // `free` 是**扣除现有 cache.dat / index.bin 之后**的余量 ⇒ 必须把"L2 已占用的空间"加回来，
                    // 否则同一份空间被扣两次、容量会被白白夹小（例：现有 4 GiB 容器 + 另有 7.1 GiB 空闲，
                    // 明明够放 8 GiB，却会被夹到 5.1 GiB）。
                    long l2Used = ContainerFootprint(dir);
                    long budget = free - MIN_KEEP_FREE_BYTES + l2Used;
                    long wanted = (long)slots * BLOCK_SIZE;
                    if (wanted > budget)
                    {
                        long allowed = Math.Max((long)MIN_SLOTS * BLOCK_SIZE, budget / BLOCK_SIZE * BLOCK_SIZE);
                        int clamped = (int)Math.Clamp(allowed / BLOCK_SIZE, MIN_SLOTS, MAX_SLOTS);
                        LogService.DebugFile(
                            $"SSD 二级缓存：容量超过该卷可用空间，已夹取 {wanted / 1024 / 1024} MiB → {clamped * (long)BLOCK_SIZE / 1024 / 1024} MiB" +
                            $"（可用 {free / 1024 / 1024} MiB，L2 已占用 {l2Used / 1024 / 1024} MiB，为缓存盘保留 {MIN_KEEP_FREE_BYTES / 1024 / 1024} MiB）");
                        slots = clamped;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存：查询 {dir} 可用空间失败（按配置值继续）：{ex.Message}");
            }

            // 容量必须规整为整 Slab（见设计文档 §4 D1）
            return RoundDownToSlab(slots);
        }

        /// <summary>L2 该目录下已占用的空间（容器 + 索引）——用于把"已被 available-free 扣掉的部分"加回预算</summary>
        private static long ContainerFootprint(string dir)
        {
            long used = 0;
            try
            {
                var container = new FileInfo(Path.Combine(dir, ContainerFileName));
                if (container.Exists) used += container.Length;
                var index = new FileInfo(Path.Combine(dir, IndexFileName));
                if (index.Exists) used += index.Length;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存：读取 L2 已占用空间失败（按 0 处理）：{ex.Message}");
            }
            return used;
        }

        /// <summary>
        /// 按配置算出本次将采用的槽数。**口径与引擎完全一致**，供 UI 在启动前预检"容量是否变化"复用。
        /// 路径非法 / 查询失败返回 0（调用方按"读不到"处理）。
        /// </summary>
        public static int ComputeSlotCountFor(DiskConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.SsdCachePath)) return 0;
            try
            {
                return ComputeSlotCount(Path.GetFullPath(config.SsdCachePath).TrimEnd('\\'), config.SsdCacheMaxBytes);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 按配置算出的**标称**槽数（只做 MIN/MAX 与整 Slab 规整，**不做可用空间夹取**）。
        /// 与 <see cref="ComputeSlotCountFor"/>（夹取后的实际值）配合，供 UI 判断"本次是否发生了夹取"。
        /// </summary>
        public static int ConfiguredSlotCountFor(DiskConfig config)
        {
            if (string.IsNullOrWhiteSpace(config.SsdCachePath)) return 0;
            int slots = (int)Math.Clamp(config.SsdCacheMaxBytes / BLOCK_SIZE, (long)MIN_SLOTS, (long)MAX_SLOTS);
            return RoundDownToSlab(slots);
        }

        /// <summary>把槽数向下规整到一个整 Slab（下限 1 Slab、上限 MAX_SLOTS）</summary>
        private static int RoundDownToSlab(int slots)
            => Math.Clamp(slots / SLOTS_PER_SLAB * SLOTS_PER_SLAB, SLOTS_PER_SLAB, MAX_SLOTS);

        /// <summary>
        /// **只读探测**：上次留下的 L2 容器是否属于**当前这块盘**（设备身份）。
        ///
        /// 返回"为什么不能复用"（空串 = 可以复用、或根本没有容器可谈）。**只输出事实、不做任何判定**——
        /// 判定与"要不要问用户"全部在 UI 的启动前预检里（见 AGENTS.md §2.3 的职责边界）。
        ///
        /// **刻意不管的两类**：
        /// ① "上次没正常关服"（身份戳对不上、索引缺失）——那由 <see cref="LedgerStampMismatch"/> 报；
        /// ② **容量 / ghost 参数与本次配置不一致**——改为**缩放保留**，其"缩/扩确认"由 UI 预检负责
        ///    （见 docs/L2容器按需增长与容量缩放_设计.md §4 D6/D7），所以这里不再报。
        /// 探测自身出意外时**不再"当作无冲突"放过**（约定：不静默）——原样上抛，由 UI 报错中止。
        /// </summary>
        public static string DetectLedgerMismatch(DiskConfig config, BlockSourceInfo info)
        {
            try
            {
                string dir = Path.GetFullPath(config.SsdCachePath).TrimEnd('\\');
                string containerPath = Path.Combine(dir, ContainerFileName);
                if (!File.Exists(containerPath)) return string.Empty;   // 首次运行：没有账本可谈

                long identity = ComputeDeviceIdentity(info.Model, info.SerialNumber, info.SizeBytes, info.BytesPerSector);

                byte[] header = new byte[BLOCK_SIZE];
                using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < BLOCK_SIZE)
                        return Locale.T("engine.l2.containerTooShort", containerPath);

                    fs.ReadExactly(header, 0, BLOCK_SIZE);
                }

                if (Hash(header, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(header, CONTAINER_HASH_LEN) ||
                    BitConverter.ToUInt32(header, 0) != CONTAINER_MAGIC ||
                    BitConverter.ToInt32(header, 4) != CONTAINER_VERSION)
                {
                    return Locale.T("engine.l2.containerNotOurs", containerPath);
                }

                if (BitConverter.ToInt64(header, 24) != identity)
                {
                    return Locale.T("engine.l2.wrongDisk", info.Model, info.SerialNumber,
                        ((double)info.SizeBytes / 1024 / 1024 / 1024).ToString("F1"));
                }

                // **容量不一致不再在这里拦**（见设计文档 §4 D6）：引擎按"已由 UI 确认"执行缩放保留；
                // "缩 / 扩确认"一并挪到 UI 预检（Form1.PrepareL2BeforeStart）。ghost 参数差异同理静默适配。
                return string.Empty;
            }
            catch (Exception ex)
            {
                // 约定：不静默。探测本身出意外时不再"当作无冲突"放过——上抛，由 UI 报错中止。
                throw new InvalidOperationException(
                    Locale.T("engine.l2.probeFailed", config.SsdCachePath, ex.Message), ex);
            }
        }

        /// <summary>
        /// 缓存目录里是否存在一份**看起来可供校验**的账本（容器与索引都在）。只做存在性判断、不读内容——
        /// 启动流程用它决定"要不要先让用户校验一遍"（盘原本联机时）。
        /// </summary>
        public static bool LedgerExists(DiskConfig config)
        {
            try
            {
                string dir = Path.GetFullPath(config.SsdCachePath).TrimEnd('\\');
                return File.Exists(Path.Combine(dir, ContainerFileName)) &&
                       File.Exists(Path.Combine(dir, IndexFileName));
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"L2 账本存在性探测失败（按“没有账本”处理）：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// **把这份 L2 账本标记为失效**：只改容器头里的"身份戳"，索引原样不动 ⇒ 两者的戳不再一致，
        /// 下次启用 L2 时 <see cref="LedgerStampMismatch"/> 会判"上次没有正常关服"，UI 从而要求**校验**
        /// （而不是静默清空重来，也不是把它当正常账本直接采信）。
        ///
        /// **为什么不改头部的 VERSION**：版本不符会被判成"上一版格式"从而静默重建，那就丢掉了"让用户校验"的机会。
        ///
        /// 用于"本次不用 L2 加速这块盘、但缓存目录里还留着它的账本"：加速期间盘会被写透传改写，
        /// 那份账本随即与盘不一致；若不让它失效，下次启用时它可能因为"盘是脱机打开的"而被直接采信 ⇒ 返回陈旧块。
        /// </summary>
        /// <returns>成功标记返回 true；没有可标记的账本（不存在 / 头部不合法 / 打不开 / 写失败）返回 false</returns>
        public static bool InvalidateLedger(DiskConfig config)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(config.SsdCachePath)) return false;

                string containerPath = Path.Combine(
                    Path.GetFullPath(config.SsdCachePath).TrimEnd('\\'), ContainerFileName);
                if (!File.Exists(containerPath)) return false;

                using var fs = new FileStream(containerPath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.None, bufferSize: 1, FileOptions.None);

                byte[] header = new byte[BLOCK_SIZE];
                fs.ReadExactly(header, 0, BLOCK_SIZE);

                if (Hash(header, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(header, CONTAINER_HASH_LEN) ||
                    BitConverter.ToUInt32(header, 0) != CONTAINER_MAGIC ||
                    BitConverter.ToInt32(header, 4) != CONTAINER_VERSION)
                {
                    return false;   // 头部本来就不合法：它下次会被静默重建，没什么可标记的
                }

                // 换掉身份戳（与 RotateContainerId 改的是同一个位置），再重算头部校验和写回。
                BitConverter.GetBytes(Random.Shared.NextInt64()).CopyTo(header, 8);
                BitConverter.GetBytes(Hash(header, 0, CONTAINER_HASH_LEN)).CopyTo(header, CONTAINER_HASH_LEN);

                fs.Position = 0;
                fs.Write(header, 0, BLOCK_SIZE);
                fs.Flush(true);
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"标记 L2 账本失效失败（按“未能标记”处理）：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// **只读探测**：容器头部记的**槽数**（供 UI 在启动前判断"容量是否变化"，见设计文档 §4 D7）。
        /// 与 <see cref="DetectLedgerMismatch"/> 同一套头部校验；任何情况下都不写盘。
        /// </summary>
        /// <returns>读到返回 true；没有容器 / 头部不合法 / 打不开 / 槽数非正 返回 false</returns>
        public static bool TryPeekContainerSlotCount(DiskConfig config, out int slotCount)
        {
            slotCount = 0;
            try
            {
                if (string.IsNullOrWhiteSpace(config.SsdCachePath)) return false;

                string containerPath = Path.Combine(
                    Path.GetFullPath(config.SsdCachePath).TrimEnd('\\'), ContainerFileName);
                if (!File.Exists(containerPath)) return false;

                byte[] header = new byte[BLOCK_SIZE];
                using var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < BLOCK_SIZE) return false;
                fs.ReadExactly(header, 0, BLOCK_SIZE);

                if (Hash(header, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(header, CONTAINER_HASH_LEN) ||
                    BitConverter.ToUInt32(header, 0) != CONTAINER_MAGIC ||
                    BitConverter.ToInt32(header, 4) != CONTAINER_VERSION)
                {
                    return false;
                }

                slotCount = BitConverter.ToInt32(header, 16);
                return slotCount > 0;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"L2 容器槽数探测失败（按“读不到”处理）：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 【管理 L2】扫出**整机上所有的 L2 缓存目录**（只读，不改动任何东西）。
        ///
        /// 为什么"扫"是靠得住的：`SsdCachePath` 永远由「缓存盘根 + 固定目录名
        /// <see cref="ServiceConstants.SsdCacheDirectoryName"/>」拼出来（见 <c>Settings</c>），
        /// 所以每个固定卷只需查一个位置；配置里那份额外补进来（手工改过 <c>config.json</c> 也能看到）。
        ///
        /// 用途：用户**忘了**某块盘上还留着 L2 时，能自己找出来删掉（见 TODO「缓存的手动管理」）。
        /// </summary>
        public static List<L2CacheInfo> DescribeAllL2Caches(DiskConfig config)
        {
            var list = new List<L2CacheInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void TryAdd(string dir)
            {
                try
                {
                    string full = Path.GetFullPath(dir).TrimEnd('\\');
                    if (!seen.Add(full) || !Directory.Exists(full)) return;
                    list.Add(DescribeL2Cache(full, config));
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"管理 L2：跳过目录 {dir}：{ex.Message}");
                }
            }

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    TryAdd(Path.Combine(drive.RootDirectory.FullName, ServiceConstants.SsdCacheDirectoryName));
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"管理 L2：枚举卷 {drive.Name} 失败：{ex.Message}");
                }
            }

            if (!string.IsNullOrWhiteSpace(config.SsdCachePath)) TryAdd(config.SsdCachePath);
            return list;
        }

        /// <summary>【管理 L2】只读描述**一个** L2 缓存目录（不打开、不改动、不加锁）</summary>
        public static L2CacheInfo DescribeL2Cache(string dir, DiskConfig config)
        {
            string full = Path.GetFullPath(dir).TrimEnd('\\');
            string containerPath = Path.Combine(full, ContainerFileName);
            string indexPath = Path.Combine(full, IndexFileName);

            bool containerExists = File.Exists(containerPath);
            bool indexExists = File.Exists(indexPath);
            long containerBytes = containerExists ? new FileInfo(containerPath).Length : 0;
            long indexBytes = indexExists ? new FileInfo(indexPath).Length : 0;

            bool recognized = false;
            int storedSlots = 0;
            long ownerIdentity = 0;
            long containerStamp = 0;
            if (containerBytes >= BLOCK_SIZE)
            {
                try
                {
                    byte[] header = new byte[BLOCK_SIZE];
                    using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.ReadExactly(header, 0, BLOCK_SIZE);
                    }
                    if (BitConverter.ToUInt32(header, 0) == CONTAINER_MAGIC &&
                        BitConverter.ToInt32(header, 4) == CONTAINER_VERSION &&
                        Hash(header, 0, CONTAINER_HASH_LEN) == BitConverter.ToUInt64(header, CONTAINER_HASH_LEN))
                    {
                        recognized = true;
                        storedSlots = BitConverter.ToInt32(header, 16);
                        ownerIdentity = BitConverter.ToInt64(header, 24);
                        containerStamp = BitConverter.ToInt64(header, 8);
                    }
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"管理 L2：读取 {containerPath} 头部失败：{ex.Message}");
                }
            }

            // 上次是否正常关服：索引里记的戳 == 容器头当前的戳（与 LedgerStampMismatch 同判据）。
            // 两者戳一致也可能来自"校验后回写"，那种情况同样可安全装载，所以统一表述为"正常关服"。
            bool? lastClean = null;
            if (recognized && indexBytes >= INDEX_HEADER_SIZE)
            {
                try
                {
                    byte[] ih = new byte[INDEX_HEADER_SIZE];
                    using (var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        fs.ReadExactly(ih, 0, INDEX_HEADER_SIZE);
                    }
                    if (BitConverter.ToUInt32(ih, 0) == INDEX_MAGIC &&
                        BitConverter.ToInt32(ih, 4) == INDEX_VERSION &&
                        Hash(ih, 0, INDEX_HEADER_HASH_OFFSET) == BitConverter.ToUInt64(ih, INDEX_HEADER_HASH_OFFSET))
                    {
                        lastClean = BitConverter.ToInt64(ih, 52) == containerStamp;
                    }
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"管理 L2：读取 {indexPath} 头部失败：{ex.Message}");
                }
            }

            // 属主盘：容器里只存了身份的**散列**（不可逆），所以枚举本机盘重算比对
            string ownerText = recognized ? Locale.T("l2m.ownerUnknown") : string.Empty;
            if (recognized)
            {
                try
                {
                    foreach (PhysicalDiskInfo disk in PhysicalDiskHandle.Enumerate())
                    {
                        if (L2Verifier.Identify(disk) != ownerIdentity) continue;
                        ownerText = Locale.T("l2m.ownerDisk", disk.DiskNumber,
                            disk.Model.Length > 0 ? disk.Model : Locale.T("selectDisk.modelUnknown"),
                            disk.SizeText);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"管理 L2：比对属主盘失败：{ex.Message}");
                }
            }

            bool isConfigured = !string.IsNullOrWhiteSpace(config.SsdCachePath) &&
                string.Equals(Path.GetFullPath(config.SsdCachePath).TrimEnd('\\'), full, StringComparison.OrdinalIgnoreCase);

            return new L2CacheInfo
            {
                Directory = full,
                DriveRoot = Path.GetPathRoot(full) ?? string.Empty,
                ContainerPath = containerPath,
                IndexPath = indexPath,
                LockPath = Path.Combine(full, LockFileName),
                ContainerExists = containerExists,
                IndexExists = indexExists,
                ContainerBytes = containerBytes,
                IndexBytes = indexBytes,
                Recognized = recognized,
                StoredSlots = storedSlots,
                OwnerText = ownerText,
                LastShutdownClean = lastClean,
                IsConfigured = isConfigured,
            };
        }

        /// <summary>
        /// 只读探测："容器头部的身份戳"与"索引里记的戳"是否**不符**——不符 = **上次没有正常关服**
        /// （掉电/强杀/崩溃）：索引还是上一次正常关服时的快照，而容器已被之后的运行改过。
        ///
        /// 不一致**不代表账本没救**：索引结构仍自洽（校验器会逐条查），`L2Verifier` 逐块比对能把所有
        /// 错乱的槽（含"索引指向的槽已被复用给别的块"）用源盘数据修回正确内容，修完由
        /// `L2Verifier.ConfirmLedger` 把戳写回即重新自洽。所以这条归"需要校验"，而不是"静默作废"。
        ///
        /// 返回空串 = **正常**（含"校验后回写"：校验器把索引里的戳写回容器头，两者戳一致、槽数可能因缩放而不同），
        /// 或"没有可校验的账本"（容器/索引不存在、头部不可信）。
        /// 探测自身出意外时**原样上抛**（约定：不静默），由 UI 报错中止。
        /// </summary>
        public static string LedgerStampMismatch(DiskConfig config)
        {
            try
            {
                string dir = Path.GetFullPath(config.SsdCachePath).TrimEnd('\\');
                string containerPath = Path.Combine(dir, ContainerFileName);
                string indexPath = Path.Combine(dir, IndexFileName);
                if (!File.Exists(containerPath) || !File.Exists(indexPath)) return string.Empty;

                byte[] ch = new byte[BLOCK_SIZE];
                using (var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < BLOCK_SIZE) return string.Empty;
                    fs.ReadExactly(ch, 0, BLOCK_SIZE);
                }
                if (BitConverter.ToUInt32(ch, 0) != CONTAINER_MAGIC ||
                    BitConverter.ToInt32(ch, 4) != CONTAINER_VERSION ||
                    Hash(ch, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(ch, CONTAINER_HASH_LEN))
                {
                    return string.Empty;   // 容器头部本身不可信：归"容器已重建"那条
                }

                byte[] ih = new byte[INDEX_HEADER_SIZE];
                using (var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < INDEX_HEADER_SIZE + 8) return string.Empty;
                    fs.ReadExactly(ih, 0, INDEX_HEADER_SIZE);
                }
                if (BitConverter.ToUInt32(ih, 0) != INDEX_MAGIC ||
                    BitConverter.ToInt32(ih, 4) != INDEX_VERSION ||
                    Hash(ih, 0, INDEX_HEADER_HASH_OFFSET) != BitConverter.ToUInt64(ih, INDEX_HEADER_HASH_OFFSET))
                {
                    return string.Empty;   // 索引头部不可信（上一版格式 / 没写完）：没有可校验的账本
                }
                // **戳一致 ⇒ 上次正常关服**。这当然也包括"校验后回写"的状态：校验器会把索引里的戳
                // 写回容器头，此时两者的戳一致、而槽数可能因容量缩放而不同——那是正常的，不能报。
                long containerStamp = BitConverter.ToInt64(ch, 8);
                if (BitConverter.ToInt64(ih, 52) == containerStamp) return string.Empty;

                // 戳不一致 = **上次没有正常关服**。**绝不能因为"槽数不同"就把它放过**：容器头每次开机
                // 都被重写（含槽数），所以槽数不同这件事本身恰恰说明"索引落盘之后容器头又被写过"——
                // 同样是没正常关服，必须一并报出来，交给用户"校验后保留 / 清空重建"。
                return Locale.T("engine.l2.stampMismatch");
            }
            catch (Exception ex)
            {
                // 约定：不静默。探测本身出意外时不再"当作无需校验"放过——上抛，由 UI 报错中止。
                throw new InvalidOperationException(
                    Locale.T("engine.l2.probeFailed", config.SsdCachePath, ex.Message), ex);
            }
        }

        public long TotalSlots => _slotCount;
        public long UsedSlots => Interlocked.Read(ref _usedSlots);
        public long FreeSlots => _freeCount;
        public long HoleSlots => Interlocked.Read(ref _holeSlots);

        /// <summary>
        /// 生效的保守线（占用率，已夹取）。**必须是夹取后的值**——UI 拿它画水位线，
        /// 用原始配置值会与引擎实际判据对不上。
        /// </summary>
        public double ConservativeOccupancy => _conservativeOccupancy;

        /// <summary>L2 独有命中块数（L1 未命中且 L2 命中）</summary>
        public long HitBlocksTotal => Interlocked.Read(ref _hitBlocks);
        /// <summary>两层都未命中、回源读取的块数</summary>
        public long MissBlocksTotal => Interlocked.Read(ref _missBlocks);
        /// <summary>写进 M 的块数（回源准入的总数）</summary>
        public long WriteBlocksTotal => Interlocked.Read(ref _writeBlocks);
        /// <summary>其中来自"激进期"（可用槽位充足 ⇒ 回源块来者不拒）的部分</summary>
        public long FillBlocksTotal => Interlocked.Read(ref _fillBlocks);
        /// <summary>新键被记进 ghost 的次数——它比 <see cref="WriteBlocksTotal"/> 多出来的部分 = "见过但 M 装不下"的块</summary>
        public long GhostPushBlocksTotal => Interlocked.Read(ref _ghostPushBlocks);
        /// <summary>M 的淘汰块数</summary>
        public long EvictBlocksTotal => Interlocked.Read(ref _evictMBlocks);
        /// <summary>ghost 命中而写进 M 的块数（"复活"= 有第二次访问证据的块）</summary>
        public long ResurrectBlocksTotal => Interlocked.Read(ref _resurrectBlocks);
        /// <summary>被写透传作废的块数（W1 之后只剩"块没被写请求完整覆盖"的首尾残缺块）</summary>
        public long InvalidatedBlocksTotal => Interlocked.Read(ref _invalidatedBlocks);
        /// <summary>写命中刷新（W1）：整块被写覆盖、且该块当时在 L2 中，就地刷成新数据（不占新槽、不入队、不加计数）</summary>
        public long WriteUpdateBlocksTotal => Interlocked.Read(ref _writeUpdateBlocks);
        public long Generation => Interlocked.Read(ref _generation);
        public long LoadMs => _loadMs;
        /// <summary>本次启动是否成功载入了上次留下的账本（否则是新建，或判据不成立而整层作废）</summary>
        public bool LoadedFromDisk => _loadedFromDisk;

        #endregion

        #region 读

        /// <summary>
        /// 读一个块到 <paramref name="buffer"/> 的 <paramref name="offset"/> 处（4KB）。
        /// 命中时给该块计数 +1（M 的"第二次机会"计数：只加计数、不搬运队列元素）。
        /// </summary>
        public bool TryRead(long blockIndex, byte[] buffer, int offset)
        {
            if (!TryFindInL2(blockIndex, out int slot))
            {
                Interlocked.Increment(ref _missBlocks);
                return false;
            }

            try
            {
                ReadSlot(slot, buffer, offset);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DisableAfterIoFailure(ex);
                Interlocked.Increment(ref _missBlocks);
                return false;   // L2 是旁路：读取失败让调用方回源
            }
            NoteAccessSlot(slot);
            Interlocked.Increment(ref _hitBlocks);
            return true;
        }

        /// <summary>
        /// 该块是否已在 L2？是则给出槽号（**不**加计数，由调用方决定语义）。
        /// 所有入口（读命中、命中准入、回源写穿）都先走这里查一次，避免三处各写一套查找。
        /// </summary>
        private bool TryFindInL2(long blockIndex, out int slot)
        {
            slot = -1;
            if (!IsEnabled) return false;
            if (blockIndex < 0) return false;

            if (!_blocksByIndex.TryGetValue(blockIndex, out slot)) return false;

            // 防御：块索引指向的槽已被回收（正常情况下不会出现）
            if (_slotBlockIndex[slot] != blockIndex)
            {
                _blocksByIndex.Remove(blockIndex);
                slot = -1;
                return false;
            }
            return true;
        }

        #endregion

        #region 准入（唯一入口：回源读到整块）

        /// <summary>
        /// **准入的唯一入口：回源读到整块时调用**（数据在托管缓冲里）。
        /// 不受 L1 内存水位影响——L2 是磁盘缓存，内存吃紧时它仍应继续学习，
        /// 否则"刚开机/低内存"的加速场景会失去意义。
        ///
        /// **L1 命中不再进这里**（2026-09-30）：那个块 L1 服务得了，L2 既不必留它、也不必记它的频次。
        /// 旧版为此单开了一个 `Touch` 入口，把"L1 命中的块"也持续灌进 L2，正是 S 长期满溢、M 饿死的成因。
        ///
        /// **准入规则**：可用槽位还在保留量之上（**激进期**）⇒ 回源块来者不拒，先把闲着的容量用起来；
        /// 可用槽位不足之后（**保守期**）⇒ 只收 ghost 命中的块（= 有第二次访问证据的块）。
        /// 分界值见 <see cref="DiskConfig.SsdConservativeThreshold"/>（占用率形态，与这里的可用槽位是补数关系）。
        /// 前者让第 1 轮跑完时 M 就是满的（第 2 轮启动即有命中），后者让写放大收敛到"被重复访问的块量"。
        /// </summary>
        public void Insert(long blockIndex, byte[] buffer, int offset)
        {
            if (_frozen || !IsEnabled) return;
            if (TryFindInL2(blockIndex, out int slot))
            {
                // 已在 M：刷新数据（从盘上读到的才是最新的）并加计数，**不重复入队**（否则队列顺序会被打乱）
                if (!WriteSlotFrom(slot, buffer, offset))
                {
                    InvalidateRange(blockIndex, 1);
                    return;
                }
                NoteAccessSlot(slot);
                Interlocked.Increment(ref _writeBlocks);
                return;
            }

            // 不在 M：先问 ghost "见没见过它"
            bool seen = GhostRemove(blockIndex);
            if (!seen)
            {
                GhostPush(blockIndex);                                  // 没见过 ⇒ 只留一个"见过"标记
                Interlocked.Increment(ref _ghostPushBlocks);
            }

            // 分界用 _freeCount 而非 _usedSlots：作废产生的空洞在 _usedSlots 上已不计数，但那些槽拿不回来
            if (!seen && _freeCount < _fillingReserve) return;

            AdmitToM(blockIndex, buffer, offset, resurrect: seen);
        }

        /// <summary>
        /// 落位到 M：腾一个槽、写数据、登记索引、挂到 M 队尾。
        /// 任何一步失败都**放弃这一块**（L2 是旁路，绝不能因为缓存写不进去而影响上层读取）。
        /// </summary>
        /// <param name="resurrect">true = 这次准入靠 ghost 命中（有第二次访问证据）；false = 激进期"来者不拒"</param>
        private void AdmitToM(long blockIndex, byte[] buffer, int offset, bool resurrect)
        {
            if (_container == null) return;
            if (blockIndex < 0) return;

            // 竞态兜底：查一次发现已在 M（正常路径不会走到，因为入口先查过）
            if (_blocksByIndex.TryGetValue(blockIndex, out int existing))
            {
                if (_slotBlockIndex[existing] == blockIndex)
                {
                    NoteAccessSlot(existing);
                    return;
                }
                _blocksByIndex.Remove(blockIndex);   // 陈旧引用，丢掉后按新块处理
            }

            if (!EnsureSpace())
            {
                if (resurrect) GhostPush(blockIndex);   // 腾不出空间 ⇒ 把"见过"的标记退回去，下次还有机会
                return;
            }

            int slot = PopFree();
            if (slot < 0)
            {
                if (resurrect) GhostPush(blockIndex);
                return;
            }

            // 定序：**先写数据块，再登记索引**
            if (!WriteSlotFrom(slot, buffer, offset))
            {
                PushFree(slot);   // 尚未登记索引/入环，失败时直接退回空闲槽
                return;
            }
            _slotBlockIndex[slot] = blockIndex;
            _slotFlags[slot] = 0;
            _blocksByIndex[blockIndex] = slot;
            Interlocked.Increment(ref _usedSlots);
            Interlocked.Increment(ref _writeBlocks);
            if (resurrect) Interlocked.Increment(ref _resurrectBlocks);
            else Interlocked.Increment(ref _fillBlocks);

            if (!PushRingM(slot))
            {
                DiscardSlot(slot);   // 极端情况下的兜底：宁可放弃这一块，也不能留下"不在任何环上"的槽
            }
        }

        /// <summary>写一个槽的数据（数据源只有托管缓冲：准入的唯一来源就是回源读到的整块）</summary>
        private bool WriteSlotFrom(int slot, byte[] buffer, int offset)
        {
            // **按需、按 Slab 整块增长**：需要新 Slab 时先扩（扩容失败只关"扩容"，不停 L2，见 GrowToCoverSlot）
            if (!GrowToCoverSlot(slot)) return false;

            try
            {
                _container!.Position = (long)(slot + 1) * BLOCK_SIZE;
                _container.Write(buffer, offset, BLOCK_SIZE);
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                DisableAfterIoFailure(ex);
                return false;
            }
        }

        private void DisableAfterIoFailure(Exception ex)
        {
            _failed = true;
            _persistLedger = false;
            // 不提交本次身份戳对应的账本；下次启动仍要求校验或清空。
            LogService.DebugFile($"SSD 二级缓存运行期 IO 失败，已停用 L2 并跳过账本提交：{ex.Message}");
        }

        #endregion

        #region 写命中刷新（W1）

        /// <summary>
        /// **写命中刷新**：写透传落盘之后，把 L2 里已有的那一份就地刷成新数据。
        /// 与 <see cref="Insert"/> 的区别是**绝不准入**——只刷已存在的槽，不占新槽、不入队、不动环、不加频次计数。
        /// 它与 <see cref="InvalidateRange"/> 是同一位置的两个选择：被写覆盖的块**要么刷成新值、要么作废**，
        /// 两者都保证"缓存里不留与源盘不一致的副本"，区别只在下次读要不要回源。
        /// **命中就必须刷**：漏刷等于留下写前的旧数据，下次读会拿到旧值（比作废更糟）。
        /// 冻结期（停服序列）与 <see cref="InvalidateRange"/> 一样直接返回——那段时间不该再有写命令（见 <see cref="Freeze"/>）。
        /// </summary>
        /// <param name="blockIndex">全局块号（= LBA / SectorsPerBlock）</param>
        /// <param name="buffer">写请求的数据（源盘此时已是这份内容）</param>
        /// <param name="offset">该块在 <paramref name="buffer"/> 中的起始偏移（调用方保证整块覆盖，即 4 KB）</param>
        public void UpdateExisting(long blockIndex, byte[] buffer, int offset)
        {
            if (_frozen) return;
            if (!TryFindInL2(blockIndex, out int slot)) return;

            if (!WriteSlotFrom(slot, buffer, offset))
            {
                InvalidateRange(blockIndex, 1);
                return;
            }
            Interlocked.Increment(ref _writeUpdateBlocks);
        }

        #endregion

        #region 失效

        /// <summary>
        /// 停服序列：冻结本层——<see cref="Insert"/> / <see cref="InvalidateRange"/>
        /// 全部变成空操作。目的是让随后写出的账本成为一个**一致快照**：
        /// ① 并发的读回填不会再改表（否则序列化期间表还在变）；
        /// ② 不会再发生新的失效（否则账本里可能留下"已被写覆盖"的陈旧块映射 ⇒ 下次载入返回旧数据）。
        /// 冻结期间读照旧命中（<see cref="TryRead"/> 不受影响），只是不再回填。
        /// </summary>
        public void Freeze() => _frozen = true;

        /// <summary>
        /// 使一段块区间在 L2 里的全部块失效（槽位标空洞，等 FIFO 环弹到它时回收）。
        /// 由写透传调用：被写覆盖的块必须立刻从缓存里消失，否则后续读会拿到写前的旧数据。
        /// </summary>
        public void InvalidateRange(long firstBlock, long blockCount)
        {
            if (_frozen) return;
            for (long offset = 0; offset < blockCount; offset++)
            {
                long blockIndex = firstBlock + offset;
                if (!_blocksByIndex.TryGetValue(blockIndex, out int slot)) continue;
                if (_slotBlockIndex[slot] != blockIndex) continue;

                _blocksByIndex.Remove(blockIndex);
                // 标空洞：槽位仍在 FIFO 环上（环不支持中段删除），占用计数先减、空闲栈等弹到再加
                _slotBlockIndex[slot] = -1;
                _slotFlags[slot] = 0;
                Interlocked.Decrement(ref _usedSlots);
                Interlocked.Increment(ref _holeSlots);
                Interlocked.Increment(ref _invalidatedBlocks);
            }
        }

        #endregion

        #region M 环淘汰与槽位元数据

        /// <summary>
        /// 腾出空间：至少 1 个空闲槽。**O(K)**，在需要槽位时按需完成，
        /// 因此不需要后台线程（与 §6 的 L1 淘汰同一哲学）。
        /// 现在只有 M 一条环，"有没有环位"这件事已被"有没有空闲槽"蕴含：
        /// `_usedSlots + _holeSlots + _freeCount == _slotCount`，而环上条目数正是前两项之和。
        /// </summary>
        private bool EnsureSpace()
        {
            int guard = _slotCount + 64;
            while (_freeCount <= 0 && _mCount > 0 && guard-- > 0)
            {
                EvictMain();
            }

            bool ok = _freeCount > 0;
            if (!ok)
            {
                LogService.DebugFile(
                    $"SSD 二级缓存：腾不出空闲槽位（M={_mCount} 空闲={_freeCount} 空洞={_holeSlots}），本轮放弃写入");
            }
            return ok;
        }

        /// <summary>M 队列淘汰：FIFO-Reinsertion（第二次机会，计数 −1 后重排队尾）；计数为 0 才真淘汰</summary>
        private void EvictMain()
        {
            int secondChances = 4;   // 上限：避免队尾全是"有计数"的块时长时间打转
            while (_mCount > 0 && secondChances-- > 0)
            {
                int slot = PopRingM();
                if (!SlotOccupied(slot))
                {
                    RecycleHole(slot);
                    return;
                }

                byte freq = FreqOf(slot);
                if (freq > 0)
                {
                    SetFreq(slot, (byte)(freq - 1));
                    if (PushRingM(slot))
                    {
                        continue;   // 第二次机会
                    }
                    DiscardSlot(slot);   // 环满（理论不可达）：只能真淘汰，保证不留下"不在环上"的槽
                    Interlocked.Increment(ref _evictMBlocks);
                    return;
                }

                DiscardSlot(slot);   // M 的淘汰**不进 ghost**（与论文一致）
                Interlocked.Increment(ref _evictMBlocks);
                return;
            }

            // 兜底：保证每次调用至少让 M 长度减一，否则上层循环可能空转
            if (_mCount > 0)
            {
                int slot = PopRingM();
                if (SlotOccupied(slot)) DiscardSlot(slot);
                else RecycleHole(slot);
                Interlocked.Increment(ref _evictMBlocks);
            }
        }

        /// <summary>
        /// 弹到空洞：失效时它是"已不算占用、但还占着环位"的状态，此刻才算真正回到空闲栈。
        /// 这条不变量（占用 + 空闲 + 空洞 = 总槽数）是槽位不泄漏的唯一保证。
        /// </summary>
        private void RecycleHole(int slot)
        {
            _slotBlockIndex[slot] = -1;
            _slotFlags[slot] = 0;
            Interlocked.Decrement(ref _holeSlots);
            PushFree(slot);
        }

        /// <summary>真正回收一个槽：从块索引里摘掉、槽位回空闲栈</summary>
        private void DiscardSlot(int slot)
        {
            long blockIndex = _slotBlockIndex[slot];
            if (blockIndex >= 0 && _blocksByIndex.TryGetValue(blockIndex, out int mapped) && mapped == slot)
            {
                _blocksByIndex.Remove(blockIndex);
            }

            _slotBlockIndex[slot] = -1;
            _slotFlags[slot] = 0;
            PushFree(slot);
            Interlocked.Decrement(ref _usedSlots);
        }

        private void GhostPush(long blockIndex)
        {
            if (_ghostLimit <= 0) return;
            if (!_ghostSet.Add(blockIndex)) return;   // 已在队列里，不重复入队

            if (_ghostCount >= _ghostLimit)
            {
                // 队列满：弹最老的（被弹出的条目留在哈希表里，靠"下次碰撞"惰性清理，与论文实现一致）
                _ghostSet.Remove(_ghostRing[_ghostHead]);
                _ghostRing[_ghostHead] = blockIndex;
                _ghostHead = (_ghostHead + 1) % _ghostRing.Length;
            }
            else
            {
                _ghostRing[(_ghostHead + _ghostCount) % _ghostRing.Length] = blockIndex;
                _ghostCount++;
            }
        }

        private bool GhostRemove(long blockIndex)
        {
            // 队列里的条目随弹出自然过期，这里只摘哈希（可能摘到已过期的那条，后果仅是少一次复活）
            return _ghostSet.Remove(blockIndex);
        }

        /// <summary>该槽是否坐着有效数据（-1 = 空闲或空洞）</summary>
        private bool SlotOccupied(int slot) => _slotBlockIndex[slot] >= 0;
        private byte FreqOf(int slot) => (byte)(_slotFlags[slot] & FREQ_MASK);
        private void SetFreq(int slot, byte value) => _slotFlags[slot] = (byte)((_slotFlags[slot] & ~FREQ_MASK) | (value & FREQ_MASK));

        private void NoteAccessSlot(int slot)
        {
            byte freq = FreqOf(slot);
            if (freq < MAX_FREQ)
            {
                SetFreq(slot, (byte)(freq + 1));
            }
        }

        #endregion

        #region 环形缓冲与空闲栈

        /// <summary>入 M 队尾。**返回值必须检查**：环溢出（理论不可达）时调用方负责回退，否则会留下"不在任何环上"的槽。</summary>
        private bool PushRingM(int slot)
        {
            if (_mCount >= _mRing.Length)
            {
                LogService.DebugFile($"SSD 二级缓存：M 环溢出（长度 {_mCount}），该块未入队");
                return false;
            }
            _mRing[(_mHead + _mCount) % _mRing.Length] = slot;
            _mCount++;
            return true;
        }

        private int PopRingM()
        {
            int slot = _mRing[_mHead];
            _mHead = (_mHead + 1) % _mRing.Length;
            _mCount--;
            return slot;
        }

        private int PopFree()
        {
            if (_freeCount <= 0) return -1;
            return _freeStack[--_freeCount];
        }

        private void PushFree(int slot)
        {
            if (_freeCount >= _freeStack.Length)
            {
                LogService.DebugFile($"SSD 二级缓存结构自检：空闲栈溢出（{_freeCount}），槽 {slot} 未回收");
                return;
            }
            _freeStack[_freeCount++] = slot;
        }

        #endregion

        #region 容器读写

        private void ReadSlot(int slot, byte[] buffer, int offset)
        {
            _container!.Position = (long)(slot + 1) * BLOCK_SIZE;   // +1：跳过容器头部
            _container.ReadExactly(buffer, offset, BLOCK_SIZE);
        }

        /// <summary>
        /// 把容器调整到指定长度并同步 <see cref="_containerBytes"/>。
        /// **只用于两处**：重置到"只剩头部块"、以及缩容截断——**不再一次性扩到整容量**（那是老实现卡住的根因）。
        /// 增长走 <see cref="GrowToCoverSlot"/>（它还要顺带做免零填充）。
        /// </summary>
        private void EnsureContainerLength(long length)
        {
            if (_container!.Length != length)
            {
                _container.SetLength(length);
                LogService.DebugFile($"Ssd 容器尺寸已调整为 {length} 字节");
            }
            _containerBytes = length;
        }

        /// <summary>
        /// **按需、按 Slab 整块增长**：确保容器覆盖到 <paramref name="slot"/> 所在 Slab 的末尾。
        /// 扩展用 <see cref="SetFileValidData"/> 免掉文件系统的零填充（省 SSD 写放大，见设计文档 §4 D4）；
        /// 该卷不支持 VDL 语义 / 特权不可用 ⇒ 本会话门闩 <see cref="_sfvdUnavailable"/>，回退纯 SetLength。
        /// 扩展失败（多半卷满）⇒ 只关掉"扩容"能力（<see cref="_growthBlocked"/>），**不停用 L2**（§4 D5）：
        /// 已覆盖范围内的缓存照常命中，账本照样提交。
        /// </summary>
        /// <returns>true = 已覆盖该槽（或本来就在范围内）；false = 本块放弃准入</returns>
        private bool GrowToCoverSlot(int slot)
        {
            long needed = BLOCK_SIZE + ((long)slot / SLOTS_PER_SLAB + 1) * SLAB_SIZE;
            if (needed <= _containerBytes) return true;
            if (_growthBlocked) return false;

            try
            {
                _container!.SetLength(needed);
                _containerBytes = needed;

                if (!_sfvdUnavailable && !TrySetFileValidData(_container!, needed))
                {
                    _sfvdUnavailable = true;
                    LogService.DebugFile(
                        "SSD 二级缓存：SetFileValidData 不可用（该卷不支持 VDL 语义，或特权不可用）" +
                        "⇒ 本会话容器扩展不再免零填充（功能不受影响，只是会多写一遍零）");
                }
                return true;
            }
            catch (Exception ex)
            {
                // L2 是旁路：这里是"尽力而为"的扩容，**任何失败都不许冒到上层读写路径**。
                // 绝大多数是卷满（IOException）/权限（UnauthorizedAccessException），一律按"本会话不再扩容"处理。
                _growthBlocked = true;
                LogService.DebugFile(
                    $"SSD 二级缓存：容器扩展失败（{ex.GetType().Name}: {ex.Message}）⇒ 本会话不再扩容；" +
                    "已覆盖范围继续服务，账本照常提交");
                return false;
            }
        }

        /// <summary>
        /// **缩容**：把容器截到"覆盖最大已登记槽"的 Slab 边界（回收物理空间）；无占用则只留头部块。
        /// 只在装载成功之后调用（此时所有已登记槽都在新槽数内）。**扩容不在这里预扩**——交给 <see cref="GrowToCoverSlot"/>。
        /// </summary>
        private void TruncateToUsedSlabs()
        {
            int maxUsedSlot = -1;
            for (int slot = _slotCount - 1; slot >= 0; slot--)
            {
                if (_slotBlockIndex[slot] >= 0) { maxUsedSlot = slot; break; }
            }

            long needed = maxUsedSlot < 0
                ? BLOCK_SIZE
                : BLOCK_SIZE + ((long)maxUsedSlot / SLOTS_PER_SLAB + 1) * SLAB_SIZE;

            if (_containerBytes > needed)
            {
                EnsureContainerLength(needed);
                LogService.DebugFile(
                    $"SSD 二级缓存缩容：容器截到 {needed / 1024 / 1024} MB（最大已登记槽 {maxUsedSlot}）");
            }
        }

        /// <summary>
        /// 开机留痕：换一个全新的容器身份戳，写进容器头部第 0 页并**强制落盘**。
        ///
        /// `Flush(true)` 这一步不能省：若只写在系统写缓存里，进程被强杀时"本次运行已开始"这个事实没落到盘上，
        /// 盘上仍留着上一次的戳 ⇒ 下次开机会误信一份可能已被本次运行改脏的旧账本（最坏形态：读到别人的数据）。
        /// </summary>
        private void RotateContainerId()
        {
            _containerId = Random.Shared.NextInt64();

            byte[] buf = new byte[BLOCK_SIZE];
            using (var ms = new MemoryStream(buf))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(CONTAINER_MAGIC);
                w.Write(CONTAINER_VERSION);
                w.Write(_containerId);
                w.Write(_slotCount);
                w.Write(BLOCK_SIZE);
                w.Write(_deviceIdentity);
                w.Write(Hash(buf, 0, CONTAINER_HASH_LEN));
            }
            _container!.Position = 0;
            _container.Write(buf, 0, BLOCK_SIZE);
            _container.Flush(true);
        }

        /// <summary>
        /// 读容器头部并校验"是不是我们的容器"。**不再校验槽数**——允许与本次配置不同（容量缩放，见设计文档 §4 D6），
        /// 容器里记的旧槽数经 <paramref name="storedSlotCount"/> 返回，仅供日志。
        /// </summary>
        private bool TryReadContainerHeader(out long containerId, out int storedSlotCount)
        {
            containerId = 0;
            storedSlotCount = 0;
            byte[] buf = new byte[BLOCK_SIZE];
            _container!.Position = 0;
            _container.ReadExactly(buf, 0, BLOCK_SIZE);

            if (Hash(buf, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(buf, CONTAINER_HASH_LEN)) return false;
            if (BitConverter.ToUInt32(buf, 0) != CONTAINER_MAGIC) return false;
            if (BitConverter.ToInt32(buf, 4) != CONTAINER_VERSION) return false;
            if (BitConverter.ToInt32(buf, 20) != BLOCK_SIZE) return false;
            if (BitConverter.ToInt64(buf, 24) != _deviceIdentity) return false;   // 换了另一块盘 ⇒ 容器作废

            storedSlotCount = BitConverter.ToInt32(buf, 16);   // 允许与本次配置不同（缩放）
            containerId = BitConverter.ToInt64(buf, 8);
            return true;
        }

        private void ResetToEmpty(string reason)
        {
            _blocksByIndex.Clear();
            _ghostSet.Clear();
            _mHead = _mCount = 0;
            _ghostHead = _ghostCount = 0;
            Array.Fill(_slotBlockIndex, -1L);
            Array.Fill(_slotFlags, (byte)0);
            _freeCount = 0;
            // **降序压栈** ⇒ PopFree（LIFO，取栈尾）**升序出槽** ⇒ 文件从头连续增长、不留稀疏空洞（见设计文档 §4 D3）
            for (int i = _slotCount - 1; i >= 0; i--)
            {
                _freeStack[_freeCount++] = i;
            }
            Interlocked.Exchange(ref _usedSlots, 0);
            Interlocked.Exchange(ref _holeSlots, 0);

            try
            {
                // 清空 = 只留头部块；其余空间按需再长（顺带把旧容器占的物理空间还回卷）
                if (_container!.Length != BLOCK_SIZE) _container.SetLength(BLOCK_SIZE);
                _containerBytes = BLOCK_SIZE;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存：容器重置为空失败：{ex.Message}");
            }

            LogService.DebugFile($"SSD 二级缓存：已重建为空缓存（{reason}）");
        }

        #endregion

        #region 索引快照

        /// <summary>
        /// 落盘索引快照——**只在停服时调用一次**，运行期一次都不写（见 关于内存缓存的进一步讨论.md §7.6）。
        /// 之所以敢只在停服写：本次运行若没能走到这里，开机时身份戳对不上，整层缓存自动作废，
        /// 不会有任何人去读一份过期的账本。写之前会先把数据文件 FlushFileBuffers（见 <see cref="WriteSnapshot"/>）。
        /// </summary>
        public void Flush()
        {
            if (_container == null || !_persistLedger) return;

            try
            {
                WriteSnapshot();
            }
            catch (Exception ex)
            {
                LogService.DebugFile(
                    $"SSD 二级缓存索引刷盘失败（本次运行不会被下次开机认可，缓存数据本身不受影响）：{ex.Message}");
            }
        }

        private void WriteSnapshot()
        {
            long t0 = Environment.TickCount64;

            // 1) 数据必须先真的落盘。"正常关服"不等于"数据已在盘上"——数据还在系统写缓存里，
            //    若之后机器掉电，账本就会指向盘上其实没有的块。这一次 FlushFileBuffers 就是为此存在。
            _container!.Flush(true);

            // 2) 环上跳过空洞：写出去的顺序 = 内存顺序去掉空洞，**相对顺序不变**
            int mCountWrite = 0;
            for (int i = 0; i < _mCount; i++)
            {
                if (SlotOccupied(_mRing[(_mHead + i) % _mRing.Length])) mCountWrite++;
            }
            int usedSlotsWrite = (int)Interlocked.Read(ref _usedSlots);
            long generation = Interlocked.Increment(ref _generation);

            string tempPath = Path.Combine(_dir, IndexTempFileName);
            string indexPath = Path.Combine(_dir, IndexFileName);

            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                byte[] header = new byte[INDEX_HEADER_SIZE];
                using (var ms = new MemoryStream(header))
                using (var hw = new BinaryWriter(ms))
                {
                    hw.Write(INDEX_MAGIC);
                    hw.Write(INDEX_VERSION);
                    hw.Write(BLOCK_SIZE);
                    hw.Write(_slotCount);
                    hw.Write(0);            // 原 S 队列目标长度：S 已删（保留字段只为不动头部布局）
                    hw.Write(_mLimit);
                    hw.Write(_ghostLimit);
                    hw.Write(usedSlotsWrite);
                    hw.Write(0);            // 原 S 环条目数：恒 0
                    hw.Write(mCountWrite);
                    hw.Write(_ghostCount);
                    hw.Write(generation);
                    hw.Write(_containerId);
                    hw.Write(new byte[INDEX_HEADER_HASH_OFFSET - 60]);   // 保留区
                    hw.Write(Hash(header, 0, INDEX_HEADER_HASH_OFFSET));
                }
                fs.Write(header, 0, INDEX_HEADER_SIZE);

                ulong bodyHash;
                using (var body = new HashingStream(fs))
                {
                    using (var bw = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
                    {
                        WriteRing(bw, _mRing, _mHead, _mCount);
                        for (int i = 0; i < _ghostCount; i++)
                        {
                            bw.Write(_ghostRing[(_ghostHead + i) % _ghostRing.Length]);
                        }
                    }
                    bodyHash = body.Hash;
                }

                byte[] trailer = new byte[8];
                BitConverter.TryWriteBytes(trailer, bodyHash);
                fs.Write(trailer, 0, trailer.Length);
            }

            // 3) 原子替换：写到 index.tmp 再换名 ⇒ 任何时刻 index.bin 都是完整的一份
            if (File.Exists(indexPath))
            {
                File.Replace(tempPath, indexPath, null);
            }
            else
            {
                File.Move(tempPath, indexPath);
            }

            _lastFlushMs = Environment.TickCount64 - t0;
            LogService.DebugFile($"Ssd 索引已刷盘（仅正常关服会走到这里）：M {mCountWrite}、" +
                $"G {_ghostCount}、占用 {usedSlotsWrite} 槽、代 {generation}、用时 {_lastFlushMs} ms");
        }

        /// <summary>
        /// 写一个 FIFO 环：每个有效条目 = (槽号, 全局块号, 标志位)。**空洞压缩掉**，相对顺序不变。
        /// 阶段一这里还要写"文件 id + 文件内块号 + 全部文件表"，块设备下整个文件表都消失了。
        /// </summary>
        private void WriteRing(BinaryWriter bw, int[] ring, int head, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int slot = ring[(head + i) % ring.Length];
                if (!SlotOccupied(slot)) continue;
                bw.Write(slot);
                bw.Write(_slotBlockIndex[slot]);
                bw.Write(_slotFlags[slot]);
            }
        }

        /// <summary>
        /// 加载索引。**任何不自洽一律返回 false**（调用方会重建空缓存）——缓存可以丢，但绝不能
        /// 拿着半截索引去服务读请求。
        /// </summary>
        private bool TryLoadIndex(long containerId)
        {
            string indexPath = Path.Combine(_dir, IndexFileName);
            if (!File.Exists(indexPath)) return false;

            try
            {
                using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                if (fs.Length < INDEX_HEADER_SIZE + 8) return false;

                byte[] header = new byte[INDEX_HEADER_SIZE];
                fs.ReadExactly(header, 0, INDEX_HEADER_SIZE);

                uint magic;
                int version, blockSize, oldSlotCount, sLimit, oldGhostLimit;
                int usedSlots, sCount, mCount, ghostCount;
                long generation, storedContainerId;
                ulong headerHash;
                using (var ms = new MemoryStream(header))
                using (var hr = new BinaryReader(ms))
                {
                    magic = hr.ReadUInt32();
                    version = hr.ReadInt32();
                    blockSize = hr.ReadInt32();
                    oldSlotCount = hr.ReadInt32();
                    sLimit = hr.ReadInt32();
                    _ = hr.ReadInt32();        // 旧 M 上限：不再参与校验（mLimit/ghostLimit 一律由新配置推导）
                    oldGhostLimit = hr.ReadInt32();
                    usedSlots = hr.ReadInt32();
                    sCount = hr.ReadInt32();
                    mCount = hr.ReadInt32();
                    ghostCount = hr.ReadInt32();
                    generation = hr.ReadInt64();
                    storedContainerId = hr.ReadInt64();
                    hr.ReadBytes(INDEX_HEADER_HASH_OFFSET - 60);   // 保留区
                    headerHash = hr.ReadUInt64();
                }

                if (magic != INDEX_MAGIC || version != INDEX_VERSION || blockSize != BLOCK_SIZE) return false;
                // sLimit 在 v3 里恒为 0 —— 非 0 说明这是删掉 S 之前的账本（版本号本该先拦住，这里再兜一道）
                if (oldSlotCount <= 0 || sLimit != 0) return false;
                if (storedContainerId != containerId) return false;   // 容器被重建过（例如只删了 cache.dat）
                if (Hash(header, 0, INDEX_HEADER_HASH_OFFSET) != headerHash) return false;
                if (usedSlots < 0 || sCount != 0) return false;       // S 已删：正文里不该再有 S 环条目
                // **计数用"旧头部值"做 sanity 上界**（头部有 hash 校验、可信）：容量缩放后不能再拿当前数组长度当界，
                // 否则损坏文件里一个巨大的计数会变成长时间循环 / _ghostSet 膨胀（见设计文档 §5 D6）。
                if (mCount < 0 || mCount > oldSlotCount) return false;
                if (oldGhostLimit < 0 || ghostCount < 0 || ghostCount > oldGhostLimit) return false;

                // 每个槽（下标）只能出现一次，且必须与环上的块号对得上。
                // 最危险的形态是"块索引指向的槽不在环上"——那个槽会被当成空闲复用，旧条目就会读到别人的数据。
                bool[] ringed = new bool[_slotCount];
                int registered = 0;
                int dropped = 0;   // 超出本次容量的旧槽条目（缩容时丢弃）

                using (var body = new HashingStream(fs))
                {
                    using (var br = new BinaryReader(body, Encoding.UTF8, leaveOpen: true))
                    {
                        if (!ReadRing(br, _mRing, ref _mHead, ref _mCount, mCount, ringed, ref registered, ref dropped))
                            return false;

                        // ghost：超出新上限的部分丢弃，**保留较新的那批**（跳过最老的，与 FIFO 语义一致）；
                        // 新上限为 0 时一条都不留（否则会写下 ghostCount>ghostLimit 的账本，下次装载被 sanity 拦掉）
                        int ghostKeep = Math.Max(0, Math.Min(ghostCount, _ghostLimit));
                        int ghostSkip = ghostCount - ghostKeep;
                        for (int i = 0; i < ghostCount; i++)
                        {
                            long blockIndex = br.ReadInt64();
                            if (i < ghostSkip) continue;
                            _ghostRing[(_ghostHead + _ghostCount) % _ghostRing.Length] = blockIndex;
                            _ghostCount++;
                            _ghostSet.Add(blockIndex);
                        }
                    }

                    ulong bodyHash = body.Hash;      // 先取哈希，再读尾部（尾部本身不参与计算）
                    byte[] trailer = new byte[8];
                    fs.ReadExactly(trailer, 0, 8);
                    if (BitConverter.ToUInt64(trailer, 0) != bodyHash) return false;
                }

                // 交叉校验：① "登记 + 丢弃"必须等于头部记的"占用槽数"（缩容丢掉的也计入）；
                //            ② 块索引的条目数必须等于登记数（两者都从同一批环条目重建，不等说明正文被改过）
                if (registered + dropped != usedSlots) return false;
                if (_blocksByIndex.Count != registered) return false;

                // 未出现在任何环上的槽 ⇒ 空闲。**降序压栈 ⇒ PopFree 升序出槽**（文件从头连续增长，见 §4 D3）
                _freeCount = 0;
                for (int slot = _slotCount - 1; slot >= 0; slot--)
                {
                    if (!ringed[slot]) _freeStack[_freeCount++] = slot;
                }
                Interlocked.Exchange(ref _usedSlots, registered);

                if (dropped > 0)
                {
                    LogService.DebugFile(
                        $"Ssd 索引载入：容量缩放丢弃 {dropped} 个超出新容量的槽条目（保留 {registered} 个）");
                }

                LogService.DebugFile($"Ssd 索引载入成功：代 {generation}、容器 id=0x{containerId:X16}");
                _generation = generation;   // 代次跨运行延续（纯诊断，用于确认"第 N 次正常关服留下了账本"）
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"Ssd 索引载入失败（将重建）：{ex.GetType().Name} {ex.Message}");
                return false;
            }
        }

        /// <summary>读一个 FIFO 环：按 (槽号, 全局块号, 标志位) 逐条重建块索引；空洞（块号 &lt; 0）压缩掉</summary>
        private bool ReadRing(BinaryReader br, int[] ring, ref int head, ref int count, int entryCount,
            bool[] ringed, ref int registered, ref int dropped)
        {
            int read = 0;
            for (int i = 0; i < entryCount; i++)
            {
                int slot = br.ReadInt32();
                long blockIndex = br.ReadInt64();
                byte flags = br.ReadByte();

                if (slot < 0) return false;
                if (blockIndex < 0) continue;               // 空洞：不入环，槽位保持空闲

                // **容量缩放**：旧账本里超出本次容量的槽一律丢弃（其余原槽位不动，相对顺序不变）
                if (slot >= _slotCount) { dropped++; continue; }

                if (ringed[slot]) return false;             // 同一个槽不能同时出现在两个环上
                ringed[slot] = true;
                if (_slotBlockIndex[slot] >= 0) return false;

                _slotBlockIndex[slot] = blockIndex;
                _slotFlags[slot] = flags;
                if (!_blocksByIndex.TryAdd(blockIndex, slot)) return false;   // 同一个块号不能被两个槽登记

                registered++;
                ring[read++] = slot;
            }

            head = 0;
            count = read;
            return true;
        }

        private static ulong Hash(byte[] data, int offset, int count)
        {
            // FNV-1a 64：够用的完整性校验（不引入新依赖），配合"原子替换"足以挡住撕裂与错配
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < count; i++)
            {
                h ^= data[offset + i];
                h *= 1099511628211UL;
            }
            return h;
        }

        /// <summary>把写入/读出的字节流增量喂给 FNV-1a 的包装流（写与读各用一次）</summary>
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

        #region 释放

        public void Dispose() => Close(persistLedger: true);

        /// <summary>仅有序且无故障的停止提交账本；异常停止保留不匹配的身份戳</summary>
        public void Close(bool persistLedger)
        {
            try
            {
                if (persistLedger) Flush();
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存关闭时刷盘/关闭失败：{ex.Message}");
            }
            finally
            {
                try { _container?.Dispose(); }
                catch (Exception ex)
                {
                    LogService.DebugFile($"SSD 二级缓存：关闭容器失败：{ex.Message}");
                }
                finally
                {
                    _container = null;
                    try { _lockFile?.Dispose(); }
                    catch (Exception ex)
                    {
                        LogService.DebugFile($"SSD 二级缓存：关闭目录锁失败：{ex.Message}");
                    }
                    finally { _lockFile = null; }
                }
            }
        }

        #endregion

        #region Win32 API

        // ===== 免零填充：把容器的"有效数据长度（VDL）"直接推到新长度 =====
        // 文件系统扩展文件时默认要保证新区域"读出来是 0"，代价是把这批簇写一遍零（大 L2 启动卡住的根因）。
        // SetFileValidData 声明"这段已是有效数据"，从而免掉零填充。前提是**该卷实现并保留 VDL 语义**
        // （NTFS / ReFS 可以，FAT32 / exFAT 不行），且调用方持有 SE_MANAGE_VOLUME_NAME 特权。
        // 我们**不预判文件系统**：直接调，失败就置 _sfvdUnavailable 门闩回退纯 SetLength。

        private static readonly object _privilegeLock = new();
        private static bool _privilegeReady;

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        private const uint TOKEN_QUERY = 0x0008;
        private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        private const string SE_MANAGE_VOLUME_NAME = "SeManageVolumePrivilege";

        /// <summary>
        /// 启用 SE_MANAGE_VOLUME_NAME（管理员默认持有但**被禁用**，需显式启用）。进程级、只需一次。
        /// 加锁是因为"首次触发"可能来自构造线程与 target 工作线程两处（改的是进程 token）。
        /// </summary>
        private static bool TryEnableVolumePrivilege()
        {
            if (_privilegeReady) return true;
            lock (_privilegeLock)
            {
                if (_privilegeReady) return true;

                IntPtr token = IntPtr.Zero;
                try
                {
                    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                        return false;
                    if (!LookupPrivilegeValue(null, SE_MANAGE_VOLUME_NAME, out LUID luid))
                        return false;

                    var tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
                    };
                    if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                        return false;
                    // AdjustTokenPrivileges "成功"也可能什么都没改（ERROR_NOT_ALL_ASSIGNED）
                    if (Marshal.GetLastWin32Error() != 0) return false;

                    _privilegeReady = true;
                    return true;
                }
                catch
                {
                    return false;
                }
                finally
                {
                    if (token != IntPtr.Zero) CloseHandle(token);
                }
            }
        }

        /// <summary>把容器的有效数据长度推到 <paramref name="length"/>（免零填充）。任一前提不满足返回 false。</summary>
        private static bool TrySetFileValidData(FileStream container, long length)
        {
            try
            {
                if (!TryEnableVolumePrivilege()) return false;
                return SetFileValidData(container.SafeFileHandle, length);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileValidData(SafeFileHandle hFile, long validDataLength);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
            ref TOKEN_PRIVILEGES newState, int bufferLength, IntPtr previousState, IntPtr returnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID_AND_ATTRIBUTES
        {
            public LUID Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public LUID_AND_ATTRIBUTES Privileges;
        }

        #endregion
    }

    /// <summary>
    /// "**L2 账本存在但不可用**"——在 <see cref="SsdCacheService"/> 构造时抛出（构造函数没法返回结果），
    /// 由 <see cref="TargetService"/> 转成"启动失败"报给 UI。
    ///
    /// 四种情形：**不完整**（只有容器或只有索引）、**不属于这块盘**、**盘原本联机**（上次运行之后可能被写过）、
    /// **身份戳不符**（上次没正常关服）。
    ///
    /// 为什么是异常而不是"静默重建"：约定的职责边界是**拦截一律在 UI 的启动前预检**（清空 / 校验后保留都
    /// 在那里问），引擎只做加速；引擎这边再遇到不自洽，说明 UI 与引擎看到的状态不一致（或有人绕过 UI），
    /// 此时悄悄丢掉整层缓存会让人不知不觉——所以要报出来。
    /// </summary>
    public sealed class L2LedgerUnusableException : Exception
    {
        public L2LedgerUnusableException(string message) : base(message) { }
    }

    /// <summary>
    /// 【管理 L2】一份 L2 缓存目录的**只读**描述（见 <see cref="SsdCacheService.DescribeAllL2Caches"/>）。
    /// 只列事实：文件在不在、多大、是不是我们的容器、认不认得属主盘、是不是配置里当前那份。
    /// </summary>
    public sealed class L2CacheInfo
    {
        /// <summary>缓存目录（绝对路径、无尾斜杠）</summary>
        public string Directory { get; init; } = string.Empty;

        /// <summary>所在卷根（如 <c>D:\</c>）</summary>
        public string DriveRoot { get; init; } = string.Empty;

        public string ContainerPath { get; init; } = string.Empty;
        public string IndexPath { get; init; } = string.Empty;

        /// <summary>单实例锁文件路径——**删除前要用它做闸**（引擎与校验器都以 <c>FileShare.None</c> 持有它）</summary>
        public string LockPath { get; init; } = string.Empty;

        public bool ContainerExists { get; init; }
        public bool IndexExists { get; init; }
        public long ContainerBytes { get; init; }
        public long IndexBytes { get; init; }

        /// <summary>容器头的 magic / version / 校验和都通过 = 确实是本程序的容器（否则是无法识别的残留）</summary>
        public bool Recognized { get; init; }

        /// <summary>容器头记的槽位数（0 = 未知；仅 <see cref="Recognized"/> 为真时有意义）</summary>
        public int StoredSlots { get; init; }

        /// <summary>属主盘的描述（"磁盘 N：型号，容量"）；认不出时是"未知盘"，<see cref="Recognized"/> 为假时为空串</summary>
        public string OwnerText { get; init; } = string.Empty;

        /// <summary>
        /// 上次是否**正常关服**（索引里的身份戳 == 容器头当前的戳）。
        /// <c>null</c> = 判断不了（容器无法识别 / 索引缺失或头部不合法）。
        /// </summary>
        public bool? LastShutdownClean { get; init; }

        /// <summary>是否是配置里**当前**使用的那个缓存目录</summary>
        public bool IsConfigured { get; init; }

        public long TotalBytes => ContainerBytes + IndexBytes;
    }
}
