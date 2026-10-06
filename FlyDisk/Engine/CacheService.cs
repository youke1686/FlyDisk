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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FlyDisk.Models;
// 本项目是 WinForms，隐式 using 会把 System.Windows.Forms.Timer 也拉进来 ⇒ 必须消歧（引擎层要的是线程池版）
using Timer = System.Threading.Timer;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 缓存管理服务 - 两层门面：
    /// L1 = 非托管内存池（Slab/Slot）+「时间桶 + 自排序队列」的 LRU 淘汰；
    /// L2 = SSD 二级缓存（<see cref="SsdCacheService"/>，M 环 + ghost，跨重启存活）。
    /// 读路径 <see cref="TryGetBlock"/> 内部完成 "L1 → L2 → 源盘" 的回退与晋升，因此块设备层不需要知道有两层；
    /// 失效也在这里统一扇出（写透传与块级失效都走 <see cref="InvalidateRange"/>）。
    ///
    /// **索引键 = 块号**（`LBA / SectorsPerBlock`，全局唯一、不区分文件）：
    /// 阶段一是 `路径 → 该文件的块表`，那是文件系统形态的产物；块设备下盘上的每个块就是一个键，
    /// 于是整张表退化为**单张 `块号 → 槽位` 映射** + 时间轮 LRU 链（见 阶段二-iSCSI块设备形态.md §4）。
    ///
    /// L1 淘汰的完整设计见 `关于内存缓存的进一步讨论.md` §6，L2 见 §7。读本文件前必须知道四条前提：
    /// 1. **淘汰有两个入口**：水位节拍（<see cref="WaterLevelTick"/>，后台 1 秒一次，`EvictionThreshold`
    ///    是"开始淘汰"这道线）与按需补齐（<see cref="AcquireSlot"/> 里空闲链空时顺手摘一批，O(K)）。
    ///    **前者是主路径**——只有它能让"全程命中"时缓存也照样回收（§6.10 的订正）；
    /// 2. 数据面只有一个执行者：库里每个 target 起一个后台线程串行执行 SCSI 命令 ⇒ 缓存读写天然串行。
    ///    但**水位节拍是第二个线程**：它淘汰冷块并释放整块空闲 Slab，
    ///    与数据面共享的池状态全部经 `_poolLock`/`_wheelLock` 串行化（锁序见字段区注释）；UI 线程只读计数器；
    /// 3. L1 查找、提升与数据拷贝共用 `_wheelLock`，避免查到槽位后被后台淘汰或复用；
    /// 4. **内存真还给系统的唯一办法是归还 Slab**（整块空闲才可 `free`；只回收块不改提交量，
    ///    所以 `dwMemoryLoad` 不会因为"淘汰"而下降）——见 §6.9/§6.10。
    /// </summary>
    public unsafe class CacheService
    {
        private readonly DiskConfig _config;
        // 块大小是"缓存侧"与"块设备侧"必须一致的事实，唯一定义在 Models 的 ServiceConstants.BlockSize：
        // 启动时会校验 BytesPerSector × SectorsPerBlock == 它，两边由构造保证一致（见 未解决的疑点.md TD-20）
        private const int BLOCK_SIZE = ServiceConstants.BlockSize;
        private const int SLAB_SIZE = ServiceConstants.SlabSize; // 64MB Slab 大小
        private const int SLOTS_PER_SLAB = SLAB_SIZE / BLOCK_SIZE;

        /// <summary>
        /// 每块（<see cref="ServiceConstants.BlockSize"/>）**管理结构的常驻内存开销估算**（字节）。
        /// L1 的数据本身在非托管池里——那算"缓存"，不算额外；这里只估**缓存以外**、随块数增长的那部分：
        /// 时间轮节点 <see cref="CacheNode"/> 24 B + 块索引 <see cref="_blocks"/> 的项（键/值/链 + 桶数组）≈ 60 B
        /// + Slab 空闲栈（<see cref="Slab.FreeIdx"/>，每 64 MB Slab 一个 int[16384]）≈ 4 B，合计约 88 B。
        /// 设置界面据此把"L1 预期能攒到多少块"折算成内存代价，口径与 L2 的
        /// <see cref="SsdCacheService.MetadataBytesPerBlock"/> 同构；两处若有变动需一起改。
        /// </summary>
        public const int MetadataBytesPerBlock = 88;

        // 淘汰参数的合法区间：越界一律夹取并落日志，不让配置错误把缓存打死
        private const int MIN_WINDOW_SECONDS = 2;
        private const int MAX_WINDOW_SECONDS = 86400;
        private const int MAX_BATCH_BLOCKS = 1 << 20;

        // 扩池失败后的冷却时长（见 AllocateSlabLocked）：期内一律不再向系统重试分配。
        // 目的：一旦分配必然失败（如 32 位进程撞上地址空间上限），不至于退化成"每来一块重试一次注定失败的
        // 64MB 分配 + 每块同步刷一行诊断日志"——那会把整条数据面拖到爬行（实测约 1000 块/秒）。
        private const int ALLOC_FAIL_COOLDOWN_MS = 1000;

        #region 字段

        // ===== 淘汰参数（来自 config.json；当前没有 UI 控件）=====
        private readonly int _windowSeconds;   // 时间轮窗口（环形桶数）
        private readonly int _graceSeconds;    // 宽限桶数：只淘汰至少老了这么多秒的块
        private readonly int _batchBlocks;     // 单次按需淘汰的块数上限
        private readonly bool _evictionEnabled;

        // ===== 非托管内存池（按 Slab 分组的空闲链，见 关于内存缓存的进一步讨论.md §6.9/§6.10）=====
        // 为什么不再用"一张全局空闲栈"：整 Slab 空闲时要能把这个 Slab **从流通中隔出来**并还给系统，
        // 而全局栈无法廉价地摘掉某个 Slab 的 16384 个槽位。分组后：pop 只从 _currentSlab 走
        // ⇒ 非当前 Slab 不可能被并发 pop ⇒ 只要它整块空闲就能安全 free。
        private readonly List<Slab> _slabs = new();
        // 锁序（**只能这一个方向**）：持 _wheelLock 时可以再取 _poolLock（淘汰时归还槽位走这条）；
        // 反过来禁止——_poolLock 内绝不取 _wheelLock（否则与"先轮锁再池锁"形成环 ⇒ 死锁）。
        // 落实方式：TakeFreeSlot / ReturnFreeSlot / ReleaseIdleSlabs 内部都不碰时间轮。
        private readonly object _poolLock = new();
        private readonly object _allocationLock = new();  // 只保护"扩池"这条慢路径
        private Slab? _currentSlab;                       // 当前分配 Slab（只从它 pop）
        private long _allocFailCooldownUntilTicks;        // 扩池失败后的冷却截止（Environment.TickCount64）；期内不再重试
        private long _releasedSlabs;                      // 累计归还给系统的 Slab 数
        private long _watermarkEvictions;                 // 水位（主动）淘汰触发的次数
        private Timer? _levelTimer;                       // 水位节拍：越过 EvictionThreshold 就主动淘汰 + 归还空闲 Slab
        private int _shutdown;
        private readonly object _memoryStatusLock = new();
        private long _memoryStatusUntilTicks;
        private double _memoryLoad = 1.0;

        // ===== 块索引：全局块号 → （槽位 + 时间轮节点）=====
        private readonly ConcurrentDictionary<long, SlotRef> _blocks = new();

        // ===== 时间轮（环形桶 + 冷尾链）=====
        // 桶下标 = 绝对秒 % _windowSeconds；桶里的节点只记自己的绝对秒 Epoch，
        // 凡 Epoch <= _coldBaseEpoch 的一律视为"已在冷尾链上"（合并某桶时 O(1) 拼接，不逐节点改标记）。
        private readonly object _wheelLock = new();
        private readonly int[] _bucketHead;
        private readonly int[] _bucketTail;
        private int _coldHead = -1;
        private int _coldTail = -1;
        private int _currentEpoch;    // 时间轮推进到的绝对秒
        private int _coldBaseEpoch;   // 冷尾水位：Epoch <= 它 ⇒ 在冷尾链上

        // ===== 节点池（分块数组，int 索引代替指针；每块一个节点）=====
        private const int NODE_CHUNK_SHIFT = 16;                       // 每块 65536 个节点（≈2MB）
        private const int NODE_CHUNK_SIZE = 1 << NODE_CHUNK_SHIFT;
        private const int NODE_CHUNK_MASK = NODE_CHUNK_SIZE - 1;
        private readonly List<CacheNode[]> _nodeChunks = new();
        private int _nodeHighWater;      // 已启用过的节点数（自检的防环上界）
        private int _freeNodeHead = -1;  // 空闲节点链（复用 Node.Next 串起来）

        // ===== 计数（供每秒统计快照跨线程读取：只读计数器，不遍历集合）=====
        // 注：原先 FreeSlots/UsedSlots 是用 ConcurrentStack.Count 现算的，而它是 O(n) 遍历——
        // 池满时每秒都会把 8M 个空闲槽位数一遍。改用计数器后顺带修掉了这个隐藏开销。
        private long _totalSlots;
        private long _usedSlots;
        private long _chainedNodes;      // 时间轮链上的节点数（自检：应与 _usedSlots 相等）
        // 槽位生命周期的"在途窗口"计数：从"认领槽位"到"挂进时间轮"（+1）、以及从"摘链"到"归还槽位"（+1），
        // 这两处两次计数（_usedSlots / _chainedNodes）不在同一个临界区里变 ⇒ 中间那一瞬两者天然不等。
        // 水位节拍会与 IO 线程并发淘汰，自检必须把这个瞬时差扣掉，否则会误报"结构计数不一致"。
        private long _inFlightSlotOps;
        private long _cachedBlocks;      // 块索引里的条目数
        private long _hitBlocks;
        private long _missBlocks;
        private long _writeUpdateBlocks; // 写命中刷新（W1）：整块被写覆盖、且该块当时在 L1 中，就地覆盖
        private long _evictedBlocksTotal;
        private int _lastEvictionBlocks;
        private double _lastEvictionMs;

        // ===== 读块去向的滑动窗口（见 后续待办.md 第七节）=====
        // 每读一块写一格（0=L1 命中 / 1=L2 命中 / 2=回源）。**近 1GB = 整个环，近 1MB = 环的尾巴**，
        // 一个环同时喂那两根条。写入侧只做"写格 + 自增游标"，**不做增减计数**——要减掉被覆盖的那一格，
        // 就得把减法放到热路径上；计数改成快照时扫一遍（每秒 256KB，≈0.1ms）。
        private const int READ_OUTCOME_RING = (int)(1024L * 1024 * 1024 / BLOCK_SIZE);   // 262144 格 = 1GB
        private const int READ_OUTCOME_1M = (int)(1024L * 1024 / BLOCK_SIZE);            // 256 格 = 1MB
        private const byte OutcomeL1 = 0, OutcomeL2 = 1, OutcomeSource = 2;

        private readonly byte[] _readOutcomeRing = new byte[READ_OUTCOME_RING];
        private long _readOutcomeWritten;   // 累计写入格数（>= 环长时表示环已绕圈）

        // ===== 二级缓存（L2：SSD，M 环 + ghost，跨重启存活）=====
        private readonly SsdCacheService? _ssd;

        #endregion

        #region 构造与统计

        /// <param name="config">配置</param>
        /// <param name="source">
        /// 块源的"身份与形状"（<see cref="BlockSourceInfo"/>）。L2 用它取"设备身份"
        /// （型号 + 序列号 + 容量 + 扇区大小）写进容器头部，防止"换了另一块盘却沿用旧容器"；
        /// 并用 <c>WasOnline</c> 判定盘上那份账本是否可信。**不区分块源在本地还是远端。**
        /// </param>
        /// <param name="allowLedgerReset">允许在"L2 账本与盘/参数不匹配"时清空重建（由 UI 问过用户之后才传 true）</param>
        public CacheService(DiskConfig config, BlockSourceInfo source, bool allowLedgerReset = false)
        {
            _config = config;

            _windowSeconds = Clamp(config.EvictionWindowSeconds, MIN_WINDOW_SECONDS, MAX_WINDOW_SECONDS);
            // 保留当前桶与宽限期的热块；裸指针生命周期另外由 _wheelLock 保护。
            _graceSeconds = Clamp(config.EvictionGraceSeconds, 1, _windowSeconds - 1);
            _batchBlocks = Clamp(config.EvictionBatchBlocks, 1, MAX_BATCH_BLOCKS);

            // 验证阈值设置
            bool thresholdValid = _config.EvictionThreshold < _config.StopCachingThreshold;
            if (!thresholdValid)
            {
                LogService.DebugFile(
                    $"CacheService：阈值设置无效（Eviction {_config.EvictionThreshold} >= Stop {_config.StopCachingThreshold}），缓存功能将受限");
            }

            // 淘汰与缓存是伴生关系：缓存关掉或阈值无效时都不淘汰
            // （阈值无效时 CanCache() 恒为 false，缓存本身就不工作了，淘汰自然没有意义）
            _evictionEnabled = _config.EnableMemoryCache && thresholdValid;

            _bucketHead = new int[_windowSeconds];
            _bucketTail = new int[_windowSeconds];
            Array.Fill(_bucketHead, -1);
            Array.Fill(_bucketTail, -1);
            _currentEpoch = NowSeconds();
            _coldBaseEpoch = _currentEpoch - _windowSeconds;

            if (config.EvictionWindowSeconds != _windowSeconds ||
                config.EvictionGraceSeconds != _graceSeconds ||
                config.EvictionBatchBlocks != _batchBlocks)
            {
                LogService.DebugFile(
                    $"CacheService：淘汰参数越界已夹取 — 窗口 {config.EvictionWindowSeconds}→{_windowSeconds} 秒、" +
                    $"宽限 {config.EvictionGraceSeconds}→{_graceSeconds} 秒、批量 {config.EvictionBatchBlocks}→{_batchBlocks} 块");
            }

            // 这些参数没有 UI 控件，落盘一行留痕，便于事后核对"我改的值到底生效了没有"
            LogService.DebugFile(
                $"CacheService：淘汰参数 时间轮窗口 {_windowSeconds} 秒 / 宽限 {_graceSeconds} 秒 / 单批 {_batchBlocks} 块 / " +
                $"淘汰{(_evictionEnabled ? "已启用" : "未启用")}");

            // SSD 二级缓存（L2）：初始化失败只降级为"仅 L1" + 警告，不让配置问题把缓存整体打死
            if (_config.EnableSsdCache)
            {
                try
                {
                    _ssd = new SsdCacheService(_config, source, allowLedgerReset);
                }
                catch (L2LedgerMismatchException)
                {
                    // "账本与盘/参数不匹配"必须冒到 UI 去问用户，不能被下面那层"降级为仅 L1"的兜底吞掉
                    throw;
                }
                catch (L2NeedsVerifyException)
                {
                    // 同上：这条也必须由 UI 弹窗问用户（校验保留 / 清空重建）
                    throw;
                }
                catch (Exception ex)
                {
                    _ssd = null;
                    LogService.DebugFile($"CacheService：SSD 二级缓存初始化失败，已降级为仅内存缓存：{ex.Message}");
                }
            }

            // 水位节拍（§6.10）：越过 EvictionThreshold 就"开始淘汰"——主动回收最冷的块，
            // 并把整块空闲的 Slab 还给系统；水位回落后自动停手。
            // 用 System.Threading.Timer（线程池回调，不是忙等线程）；回调内必须自己兜住异常，
            // 因为从 Timer 回调里逸出的异常会直接终结进程。
            if (_evictionEnabled)
            {
                _levelTimer = new Timer(_ => WaterLevelTick(), null, WATER_LEVEL_INTERVAL_MS, WATER_LEVEL_INTERVAL_MS);
            }
        }

        private static int Clamp(int value, int min, int max) => value < min ? min : (value > max ? max : value);

        /// <summary>当前绝对秒（相对系统启动；int 值域足够运行期使用）</summary>
        private static int NowSeconds() => (int)(Environment.TickCount64 / 1000);

        /// <summary>统计信息</summary>
        public long TotalCacheSize => Interlocked.Read(ref _totalSlots) * BLOCK_SIZE;
        public long UsedSlots => Interlocked.Read(ref _usedSlots);
        public long FreeSlots => Interlocked.Read(ref _totalSlots) - Interlocked.Read(ref _usedSlots);
        public long CachedBlocks => Interlocked.Read(ref _cachedBlocks);
        public bool EvictionEnabled => _evictionEnabled;

        /// <summary>L2 统计快照（未启用时为 null）</summary>
        public SsdCacheService? SsdCache => _ssd;

        /// <summary>
        /// 生成缓存部分的统计快照（设备与 IO 计数由 <see cref="TargetService.CreateSnapshot"/> 补齐）。
        /// 只读上述计数器，**不遍历任何集合**——本方法运行在 UI 定时器线程上，与 IO 线程并发。
        /// </summary>
        public CacheStats FillSnapshot(CacheStats stats)
        {
            stats.TotalCacheSize = TotalCacheSize;
            stats.UsedSlots = UsedSlots;
            stats.FreeSlots = FreeSlots;
            stats.CachedBlocks = CachedBlocks;
            stats.L1HitBlocksTotal = Interlocked.Read(ref _hitBlocks);
            stats.L1MissBlocksTotal = Interlocked.Read(ref _missBlocks);
            stats.L1WriteUpdateBlocksTotal = Interlocked.Read(ref _writeUpdateBlocks);
            stats.Recent1MB = ScanReadOutcome(READ_OUTCOME_1M);
            stats.Recent1GB = ScanReadOutcome(READ_OUTCOME_RING);
            stats.SystemMemoryLoad = GetSystemMemoryLoad();
            stats.EvictionThreshold = _config.EvictionThreshold;
            stats.StopCachingThreshold = _config.StopCachingThreshold;
            stats.EnableMemoryCache = _config.EnableMemoryCache;
            stats.EvictionEnabled = _evictionEnabled;
            stats.EvictedBlocksTotal = Interlocked.Read(ref _evictedBlocksTotal);
            stats.LastEvictionBlocks = _lastEvictionBlocks;
            stats.LastEvictionMs = _lastEvictionMs;
            stats.SlabCount = Interlocked.Read(ref _totalSlots) / SLOTS_PER_SLAB;
            stats.ReleasedSlabsTotal = Interlocked.Read(ref _releasedSlabs);
            stats.WatermarkEvictionsTotal = Interlocked.Read(ref _watermarkEvictions);

            stats.SsdCacheEnabled = _ssd?.IsEnabled ?? false;
            stats.SsdTotalSlots = _ssd?.TotalSlots ?? 0;
            stats.SsdUsedSlots = _ssd?.UsedSlots ?? 0;
            stats.SsdFreeSlots = _ssd?.FreeSlots ?? 0;
            stats.SsdHoleSlots = _ssd?.HoleSlots ?? 0;
            stats.SsdHitBlocksTotal = _ssd?.HitBlocksTotal ?? 0;
            stats.SsdMissBlocksTotal = _ssd?.MissBlocksTotal ?? 0;
            stats.SsdWriteBlocksTotal = _ssd?.WriteBlocksTotal ?? 0;
            stats.SsdFillBlocksTotal = _ssd?.FillBlocksTotal ?? 0;
            stats.SsdGhostPushBlocksTotal = _ssd?.GhostPushBlocksTotal ?? 0;
            stats.SsdEvictBlocksTotal = _ssd?.EvictBlocksTotal ?? 0;
            stats.SsdResurrectBlocksTotal = _ssd?.ResurrectBlocksTotal ?? 0;
            stats.SsdInvalidatedBlocksTotal = _ssd?.InvalidatedBlocksTotal ?? 0;
            stats.SsdWriteUpdateBlocksTotal = _ssd?.WriteUpdateBlocksTotal ?? 0;
            stats.SsdCacheLoadedFromDisk = _ssd?.LoadedFromDisk ?? false;
            stats.SsdGeneration = _ssd?.Generation ?? 0;
            stats.SsdLoadMs = _ssd?.LoadMs ?? 0;
            stats.SsdConservativeThreshold = _ssd?.ConservativeOccupancy ?? 0;
            return stats;
        }

        /// <summary>
        /// 停止时调用：把 L2 索引落盘并关闭容器——这是**整个运行期唯一一次**账本落盘。
        /// **必须在下线 target 之后调用**（否则还有命令在跑，会在账本序列化的同时改内存表）。
        /// </summary>
        public void Shutdown(bool persistLedger = true)
        {
            if (Interlocked.Exchange(ref _shutdown, 1) != 0) return;
            // 调用方已关闭命令入口并等在途命令结束；节拍也必须完全退出才可释放裸指针。
            Timer? timer = Interlocked.Exchange(ref _levelTimer, null);
            if (timer != null)
            {
                using var done = new ManualResetEvent(false);
                if (timer.Dispose(done)) done.WaitOne();
            }
            try
            {
                _ssd?.Close(persistLedger);
            }
            finally
            {
                lock (_wheelLock)
                {
                    _blocks.Clear();
                    _nodeChunks.Clear();
                    Array.Fill(_bucketHead, -1);
                    Array.Fill(_bucketTail, -1);
                    _coldHead = _coldTail = _freeNodeHead = -1;
                    _nodeHighWater = 0;
                    lock (_poolLock)
                    {
                        foreach (Slab slab in _slabs)
                        {
                            NativeMemory.Free((void*)slab.Base);
                            GC.RemoveMemoryPressure(SLAB_SIZE);
                            Interlocked.Increment(ref _releasedSlabs);
                        }
                        _slabs.Clear();
                        _currentSlab = null;
                        Interlocked.Exchange(ref _totalSlots, 0);
                        Interlocked.Exchange(ref _usedSlots, 0);
                    }
                    Interlocked.Exchange(ref _chainedNodes, 0);
                    Interlocked.Exchange(ref _cachedBlocks, 0);
                }
            }
        }

        /// <summary>
        /// 停服序列的第一步：**冻结 L2**。冻结后 L2 不再准入、不再失效，于是随后写出的账本
        /// 必然是一个"不再变化"的一致快照（冻结期间照旧服务读，只是不再回填）。
        /// 顺序固定为：停 target → <see cref="FreezeForShutdown"/> → <see cref="Shutdown"/>。
        /// </summary>
        public void FreezeForShutdown()
        {
            _ssd?.Freeze();
        }

        #endregion

        #region 非托管内存池

        /// <summary>水位节拍周期（毫秒）</summary>
        private const int WATER_LEVEL_INTERVAL_MS = 1000;
        /// <summary>单次节拍最多淘汰多少块（防长期越线时一口气卡住 IO 线程；65536 块 = 256MB）</summary>
        private const int MAX_EVICT_PER_TICK = 1 << 16;
        /// <summary>归还 Slab 前要求的"整块空闲"持续时长，抑制"刚还回去又立刻要回来"的抖动</summary>
        private const int IDLE_SLAB_GRACE_MS = 1200;
        /// <summary>至少保留几个整块空闲的 Slab 才允许归还（保证下次填充不必立刻再向系统申请）</summary>
        private const int MIN_SPARE_SLABS = 1;

        /// <summary>槽位地址（槽号 = 该地址在 Slab 内的偏移 / BLOCK_SIZE）</summary>
        private static IntPtr SlotAddress(Slab slab, int index) => IntPtr.Add(slab.Base, index * BLOCK_SIZE);

        /// <summary>
        /// 从空闲链取一个槽位（占用计数 +1）。
        /// **只从"当前分配 Slab"取**——这条不变量是"整 Slab 可安全归还"的前提：
        /// 非当前 Slab 不可能被并发 pop，于是只要它整块空闲，就没人持有它的指针。
        /// </summary>
        private bool TakeFreeSlot(out SlotRef slot)
        {
            lock (_poolLock)
            {
                Slab? slab = _currentSlab;
                if (slab is null || slab.FreeTop == 0)
                {
                    slab = null;
                    foreach (Slab s in _slabs)
                    {
                        if (s.FreeTop > 0) { slab = s; break; }
                    }
                    _currentSlab = slab;
                }

                if (slab is null)
                {
                    slot = default;
                    return false;
                }

                if (slab.FreeTop == SLOTS_PER_SLAB) slab.WholeFreeSinceTicks = 0;  // 不再整块空闲 ⇒ 撤销空闲计时
                slot = new SlotRef(slab, slab.FreeIdx[--slab.FreeTop], -1);
                Interlocked.Increment(ref _usedSlots);
                return true;
            }
        }

        /// <summary>把槽位还回它所属 Slab 的空闲链（占用计数 -1）。**每个槽位只能归还一次**——唯一性由调用方保证</summary>
        private void ReturnFreeSlot(SlotRef slot)
        {
            lock (_poolLock) ReturnFreeSlotLocked(slot);
            Interlocked.Decrement(ref _usedSlots);
        }

        /// <summary>归还的公共部分（调用者必须持有 _poolLock）</summary>
        private void ReturnFreeSlotLocked(SlotRef slot)
        {
            // 引用直接带所属 Slab，归还为 O(1)，不再为每个淘汰块遍历全部 Slab。
            Slab slab = slot.Owner;
            slab.FreeIdx[slab.FreeTop++] = slot.SlotIndex;
            if (slab.FreeTop == SLOTS_PER_SLAB)
                slab.WholeFreeSinceTicks = Environment.TickCount64;
        }

        /// <summary>
        /// 获取或分配一个空闲槽位。
        /// 空闲链空时**优先淘汰最冷的旧块**，而不是直接向系统再要 64MB——
        /// 这正是设计文档 §2.2 的原意：系统内存占用达到 EvictionThreshold 就触发 LRU。
        /// 注意这只是"按需"的补充路径；让 `EvictionThreshold` 名实相符的是 WaterLevelTick 的节拍。
        /// </summary>
        private SlotRef? AcquireSlot()
        {
            if (TakeFreeSlot(out SlotRef slot))
            {
                return slot;
            }

            if (_evictionEnabled && Interlocked.Read(ref _usedSlots) > 0 &&
                GetSystemMemoryLoad() >= _config.EvictionThreshold)
            {
                int removed = EvictBatch(_batchBlocks);
                if (removed > 0 && TakeFreeSlot(out slot))
                {
                    return slot;
                }
                // 摘不到（块全都还很热）⇒ 落到下面扩池：宁可多占一点内存，也不能让淘汰把有效缓存砸掉
            }

            lock (_allocationLock)
            {
                // 双重检查，防止并发分配多个 Slab
                if (TakeFreeSlot(out slot)) return slot;
                return AllocateSlabLocked();
            }
        }

        /// <summary>分配一个新 Slab，除第 0 个槽位外的全部槽位入空闲链（调用者必须持有 _allocationLock）</summary>
        private SlotRef? AllocateSlabLocked()
        {
            // 冷却期内不再向系统重试：上次分配失败后的 ALLOC_FAIL_COOLDOWN_MS 里直接返回失败，
            // 调用方按"取不到槽位"处理（本块不进 L1）。这样扩池必然失败时，最多 1 秒才重试/落一次日志。
            if (Volatile.Read(ref _allocFailCooldownUntilTicks) > Environment.TickCount64)
            {
                return null;
            }

            IntPtr basePtr = IntPtr.Zero;
            bool pressureAdded = false;
            try
            {
                basePtr = (IntPtr)NativeMemory.Alloc((nuint)SLAB_SIZE);
                // 少数分配器/平台以"返回空指针"而非抛异常表示失败，一并当作失败处理
                if (basePtr == IntPtr.Zero)
                {
                    throw new OutOfMemoryException();
                }

                var slab = new Slab(basePtr);
                for (int i = SLOTS_PER_SLAB - 1; i >= 1; i--)
                {
                    slab.FreeIdx[slab.FreeTop++] = i;
                }

                GC.AddMemoryPressure(SLAB_SIZE);
                pressureAdded = true;
                lock (_poolLock)
                {
                    _slabs.Add(slab);
                    _currentSlab = slab;
                    Interlocked.Add(ref _totalSlots, SLOTS_PER_SLAB);
                }

                Interlocked.Increment(ref _usedSlots);   // 第 0 块直接返回给调用者，计入占用
                return new SlotRef(slab, 0, -1);
            }
            catch (Exception ex)   // 任何形式的失败都进入冷却（不只 OOM），避免逐块重试 + 逐块刷日志
            {
                if (basePtr != IntPtr.Zero) NativeMemory.Free((void*)basePtr);
                if (pressureAdded) GC.RemoveMemoryPressure(SLAB_SIZE);
                Volatile.Write(ref _allocFailCooldownUntilTicks, Environment.TickCount64 + ALLOC_FAIL_COOLDOWN_MS);
                LogService.DebugFile(
                    $"CacheService：无法分配新的 Slab（{ex.GetType().Name}: {ex.Message}），" +
                    $"进入 {ALLOC_FAIL_COOLDOWN_MS} ms 冷却");
                return null;
            }
        }

        /// <summary>
        /// 水位节拍：`EvictionThreshold` 就是"**开始**淘汰"这道线（§2.1/§2.2）。
        /// 越过它 ⇒ 主动回收最冷的块（缓存从此不再只增不减），顺手把整块空闲的 Slab **还给系统**。
        /// 只有"真把内存还给系统"才能让水位回落、从而自动停手——这就是 §6.10 说的闭环。
        /// </summary>
        private void WaterLevelTick()
        {
            try
            {
                if (!_evictionEnabled || Volatile.Read(ref _shutdown) != 0) return;
                if (!SystemMemory.TryRead(out MemoryStatus mem)) return;

                double load = mem.MemoryLoadPercent / 100.0;
                if (load < _config.EvictionThreshold) return;   // 线下：正常缓存，只增不减地攒热数据

                // 该还多少：按"越线部分 × 物理内存"估算。归还 Slab 后水位会回落，下一拍自然收敛；
                // 若只是被别的进程临时抬高，下一拍也会自动停手（不会一路把缓存清空）。
                long overBytes = (long)((load - _config.EvictionThreshold) * mem.TotalPhysicalBytes);
                long budget = Math.Min(overBytes / BLOCK_SIZE, MAX_EVICT_PER_TICK);
                int removed = 0;
                while (budget > 0)
                {
                    int got = EvictBatch((int)Math.Min(budget, _batchBlocks));
                    if (got <= 0) break;                        // 剩下的块都还在宽限期内（太新），本轮到此
                    removed += got;
                    budget -= got;
                }

                int released = ReleaseIdleSlabs();

                if (removed > 0 || released > 0)
                {
                    Interlocked.Increment(ref _watermarkEvictions);
                    LogService.DebugFile(
                        $"Evict 水位 {load:P0} ≥ 阈值 {_config.EvictionThreshold:P0}：" +
                        $"主动摘除 {removed} 块（{removed * (long)BLOCK_SIZE / (1024 * 1024)} MB），归还整块空闲的 Slab {released} 个；" +
                        $"当前已用 {Interlocked.Read(ref _usedSlots)} 块，累计归还 {Interlocked.Read(ref _releasedSlabs)} 个 Slab");
                }
            }
            catch (Exception ex)
            {
                // Timer 回调里逸出的异常会直接终结进程 ⇒ 必须在这里兜住
                LogService.DebugFile($"CacheService：水位节拍异常：{ex.Message}");
            }
        }

        /// <summary>
        /// 把"整块空闲且空闲够久"的 Slab 还给系统（`NativeMemory.Free` ⇒ 提交量下降 ⇒ 水位才会真回落）。
        /// 安全性依据：pop 只发生在 `_currentSlab`（见 TakeFreeSlot）⇒ 非当前 Slab 整块空闲时无人持有其指针。
        /// 这里**不释放**当前分配 Slab，并至少留 MIN_SPARE_SLABS 个整块空闲的 Slab 备用。
        /// </summary>
        private int ReleaseIdleSlabs()
        {
            int released = 0;
            lock (_poolLock)
            {
                int idle = 0;
                foreach (Slab s in _slabs)
                {
                    if (s.FreeTop == SLOTS_PER_SLAB) idle++;
                }

                for (int i = _slabs.Count - 1; i >= 0 && idle > MIN_SPARE_SLABS; i--)
                {
                    Slab s = _slabs[i];
                    if (ReferenceEquals(s, _currentSlab)) continue;
                    if (s.FreeTop != SLOTS_PER_SLAB) continue;
                    if (Environment.TickCount64 - s.WholeFreeSinceTicks < IDLE_SLAB_GRACE_MS) continue;

                    _slabs.RemoveAt(i);
                    NativeMemory.Free((void*)s.Base);
                    GC.RemoveMemoryPressure(SLAB_SIZE);
                    Interlocked.Add(ref _totalSlots, -SLOTS_PER_SLAB);
                    Interlocked.Increment(ref _releasedSlabs);
                    released++;
                    idle--;
                }
            }
            return released;
        }

        #endregion

        #region 块读写

        /// <summary>
        /// 读取块数据：L1 → L2 → （返回 false 表示要回源）。
        /// 这里已把两层打通，块设备层不需要知道有几层缓存。
        /// </summary>
        /// <param name="blockIndex">全局块号（= LBA / SectorsPerBlock）</param>
        public bool TryGetBlock(long blockIndex, byte[] buffer, int offset)
        {
            // 读路径上没有任何"到点写盘"的检查：L2 的账本只在停服时落一次盘（见 SsdCacheService 类注释）
            if (_config.EnableMemoryCache && InternalTryGetBlock(blockIndex, buffer, offset))
            {
                Interlocked.Increment(ref _hitBlocks);
                RecordReadOutcome(OutcomeL1);
                return true;
            }

            Interlocked.Increment(ref _missBlocks);

            // L1 未命中 **或内存缓存被关闭** ⇒ 查 L2。L2 命中不依赖 L1 槽位（直接从容器读进 buffer），
            // 因此系统内存吃紧、L1 冻结时 L2 依然在干活（见 §7.4 第 2 条）
            if (_ssd != null && _ssd.TryRead(blockIndex, buffer, offset))
            {
                if (CanCache())
                {
                    InternalSetBlock(blockIndex, buffer, offset);   // 晋升回 L1，尽力而为
                }
                RecordReadOutcome(OutcomeL2);
                return true;
            }

            RecordReadOutcome(OutcomeSource);
            return false;
        }

        /// <summary>
        /// 记一次读块去向（三个出口各调一次）。**只写格 + 自增游标，不做任何减法**——
        /// 被覆盖的那一格原来是什么，写入侧不关心；占比由快照时的整段扫描算出来（见 <see cref="ScanReadOutcome"/>）。
        /// </summary>
        private void RecordReadOutcome(byte outcome)
        {
            long n = Interlocked.Increment(ref _readOutcomeWritten);
            _readOutcomeRing[(int)((n - 1) & (READ_OUTCOME_RING - 1))] = outcome;
        }

        /// <summary>
        /// 扫环里最近 <paramref name="take"/> 格的去向。**每秒只调两次**（1MB + 1GB），
        /// 最坏 262144 次数组读 ≈ 0.1ms，可以忽略。
        /// </summary>
        private ReadOutcomeWindow ScanReadOutcome(int take)
        {
            long written = Interlocked.Read(ref _readOutcomeWritten);
            int avail = (int)Math.Min(written, take);
            long l1 = 0, l2 = 0, src = 0;
            for (int k = 1; k <= avail; k++)
            {
                switch (_readOutcomeRing[(int)((written - k) & (READ_OUTCOME_RING - 1))])
                {
                    case OutcomeL1: l1++; break;
                    case OutcomeL2: l2++; break;
                    default: src++; break;
                }
            }
            return new ReadOutcomeWindow(l1, l2, src);
        }

        private bool InternalTryGetBlock(long blockIndex, byte[] buffer, int offset)
        {
            lock (_wheelLock)
            {
                // 查找也在锁内：查到之后才加锁仍可能持有已经归还的旧槽位。
                if (!_blocks.TryGetValue(blockIndex, out SlotRef slotRef)) return false;
                TouchLocked(slotRef.NodeIndex, NowSeconds());
                // **不再通知 L2**（2026-09-30）：L1 服务得了的块，L2 既不必留它、也不必记它的频次。
                // 旧版在这里调 _ssd.Touch，把"L1 命中的块"持续灌进 L2 —— 正是 L2 的 S 长期满溢、
                // M 饿死的成因（见 后续待办.md 第五节）。准入的唯一入口现在是"回源读到整块"。
                Marshal.Copy(slotRef.Slot, buffer, offset, BLOCK_SIZE);
                return true;
            }
        }

        /// <summary>
        /// 写入块数据到缓存：L1 受内存水位约束，L2（写穿）**不受水位影响**——
        /// L2 是磁盘缓存，内存吃紧时它仍应继续学习，否则"刚开机/低内存"的加速场景会失去意义（§7.4）。
        /// </summary>
        public void SetBlock(long blockIndex, byte[] buffer, int offset)
        {
            if (CanCache())
            {
                InternalSetBlock(blockIndex, buffer, offset);
            }
            _ssd?.Insert(blockIndex, buffer, offset);
        }

        private void InternalSetBlock(long blockIndex, byte[] buffer, int offset)
        {
            // 如果该块已经缓存，直接覆盖（回填不是"命中"，故不提升）
            lock (_wheelLock)
            {
                if (_blocks.TryGetValue(blockIndex, out SlotRef existing))
                {
                    Marshal.Copy(buffer, offset, existing.Slot, BLOCK_SIZE);
                    return;
                }
            }

            // 分配新槽位。
            // **注意这个在途窗口**：从"认领槽位"（_usedSlots+1）到"挂进时间轮"（_chainedNodes+1）之间，
            // 两个计数天然差 1；水位节拍线程此时可能正在跑自检，所以要用 _inFlightSlotOps 标出来。
            Interlocked.Increment(ref _inFlightSlotOps);
            SlotRef? acquired = null;
            bool published = false;
            try
            {
                acquired = AcquireSlot();
                if (!acquired.HasValue) return;
                SlotRef slot = acquired.Value;
                Marshal.Copy(buffer, offset, slot.Slot, BLOCK_SIZE);
                int now = NowSeconds();
                lock (_wheelLock)
                {
                    AdvanceTo(now);
                    int nodeIndex = AllocNode(blockIndex, now);
                    try
                    {
                        if (_blocks.TryAdd(blockIndex, new SlotRef(slot.Owner, slot.SlotIndex, nodeIndex)))
                        {
                            LinkToBucket(nodeIndex, NormalizeBucket(now), now);
                            Interlocked.Increment(ref _cachedBlocks);
                            published = true;
                        }
                    }
                    finally
                    {
                        if (!published) FreeNode(nodeIndex);
                    }
                }
            }
            catch (OutOfMemoryException)
            {
                // 缓存元数据分配失败不影响已经从源盘/L2 读到的数据。
                Volatile.Write(ref _allocFailCooldownUntilTicks, Environment.TickCount64 + ALLOC_FAIL_COOLDOWN_MS);
            }
            finally
            {
                if (acquired.HasValue && !published) ReturnFreeSlot(acquired.Value);
                Interlocked.Decrement(ref _inFlightSlotOps);
            }
        }

        /// <summary>
        /// **写命中刷新（W1）**：写透传落盘之后，把已经在缓存里的那一份就地刷成新数据。
        /// 与 <see cref="SetBlock"/> 的区别：**只刷已存在的槽，绝不分配新槽**——SetBlock 走的是"回源准入"
        /// （按激进/保守规则决定要不要把这一块落位到 M），那是读路径的事，写路径不该带那个副作用。
        /// 它与 <see cref="InvalidateRange"/> 是同一位置的两个选择：被写覆盖的块**要么刷成新值、要么作废**，
        /// 两者都保证缓存里不留与源盘不一致的副本，区别只在下次读要不要回源。
        /// **命中即覆盖，不看内存水位**：覆盖已有槽不新增内存，而"因为水位高就跳过刷新"等于留下陈旧副本。
        /// </summary>
        /// <param name="blockIndex">全局块号（= LBA / SectorsPerBlock）</param>
        /// <param name="buffer">写请求的数据（源盘此时已是这份内容）</param>
        /// <param name="offset">该块在 <paramref name="buffer"/> 中的起始偏移（调用方保证整块覆盖，即 4 KB）</param>
        public void UpdateBlock(long blockIndex, byte[] buffer, int offset)
        {
            // 两层各自判断"有没有副本"，互不牵连：块可能同时在两层，L2 那一份同样要刷。
            // 顺序与读路径的 L1 → L2 一致，先内存后磁盘。
            lock (_wheelLock)
            {
                if (_blocks.TryGetValue(blockIndex, out SlotRef slotRef))
                {
                    TouchLocked(slotRef.NodeIndex, NowSeconds());
                    Marshal.Copy(buffer, offset, slotRef.Slot, BLOCK_SIZE);
                    Interlocked.Increment(ref _writeUpdateBlocks);
                }
            }

            _ssd?.UpdateExisting(blockIndex, buffer, offset);
        }

        #endregion

        #region 内存水位

        /// <summary>
        /// 获取当前系统物理内存占用百分比 (0.0 - 1.0)
        /// </summary>
        public double GetSystemMemoryLoad()
        {
            // 同一短周期内复用读数，避免 1 MiB 回填逐块做 256 次 P/Invoke 和对象分配。
            lock (_memoryStatusLock)
            {
                long now = Environment.TickCount64;
                if (now >= _memoryStatusUntilTicks)
                {
                    _memoryLoad = SystemMemory.TryRead(out MemoryStatus mem) ? mem.MemoryLoadPercent / 100.0 : 1.0;
                    _memoryStatusUntilTicks = now + 100;
                }
                return _memoryLoad;
            }
        }

        public bool CanCache()
        {
            if (!_config.EnableMemoryCache) return false;
            if (_config.EvictionThreshold >= _config.StopCachingThreshold) return false;

            double currentLoad = GetSystemMemoryLoad();
            if (currentLoad >= _config.StopCachingThreshold) return false;

            // **L1 已经空了、水位却仍在淘汰线以上** ⇒ 越线不是缓存造成的（是别的进程占着内存）。
            // 这时再收新块只会"刚收进来就被下一拍淘汰"，而且 AcquireSlot 会因空闲链空而**扩池**，
            // 反倒把水位往上推。于是临时把 L1 关上：只要水位还在线上、缓存又是空的就不接纳——
            // 不接纳 ⇒ 缓存持续为空 ⇒ 这条判断自洽地维持到水位回落到淘汰线以下再自动恢复。
            return !(currentLoad >= _config.EvictionThreshold && UsedSlots == 0);
        }

        #endregion

        #region 失效

        /// <summary>
        /// 使一段块区间失效（L1 与 L2 一起）。
        /// 这是阶段二的**架构红利**：阶段一写一个字节就得让整份文件失效（`Invalidate(path)`），
        /// 块级写透传能精确算出被覆盖的块区间，只丢这几个块（见 阶段二-iSCSI块设备形态.md §3.4）。
        /// </summary>
        /// <param name="firstBlock">起始块号（含）</param>
        /// <param name="blockCount">块数（调用方保证 ≥ 1）</param>
        public void InvalidateRange(long firstBlock, long blockCount)
        {
            // 两层在这里统一扇出：调用点只有一处（写透传），但保持"由门面统一扇出"的形状，
            // 将来加调用点时不会有人漏掉某一层 ⇒ 陈旧数据
            _ssd?.InvalidateRange(firstBlock, blockCount);

            for (long offset = 0; offset < blockCount; offset++)
            {
                long blockIndex = firstBlock + offset;
                lock (_wheelLock)
                {
                    if (_blocks.TryRemove(blockIndex, out SlotRef slotRef))
                    {
                        Interlocked.Decrement(ref _cachedBlocks);
                        UnlinkAndReturnSlot(slotRef);
                    }
                }
            }
        }

        /// <summary>
        /// 摘掉一个块并归还它的槽位（失效路径）。
        /// 链上节点数与已占用槽位数分别在各自的临界区里递减，中间那一瞬两者天然差 1 ⇒
        /// 用 <see cref="_inFlightSlotOps"/> 把这个在途窗口标出来（水位节拍的自检会扣掉它）。
        /// </summary>
        private void UnlinkAndReturnSlot(SlotRef slotRef)
        {
            Interlocked.Increment(ref _inFlightSlotOps);
            lock (_wheelLock)
            {
                Unlink(slotRef.NodeIndex);
                FreeNode(slotRef.NodeIndex);
            }
            ReturnFreeSlot(slotRef);
            Interlocked.Decrement(ref _inFlightSlotOps);
        }

        #endregion

        #region 淘汰（时间桶 + 自排序队列）

        /// <summary>桶下标归一化（Epoch 可能为负：启动初期 <em>now - 窗口</em> 的取值）</summary>
        private int NormalizeBucket(int epoch) => (int)(((long)epoch % _windowSeconds + _windowSeconds) % _windowSeconds);

        private ref CacheNode NodeAt(int index) => ref _nodeChunks[index >> NODE_CHUNK_SHIFT][index & NODE_CHUNK_MASK];

        private void EnsureNodeChunk(int index)
        {
            int chunk = index >> NODE_CHUNK_SHIFT;
            while (_nodeChunks.Count <= chunk)
            {
                _nodeChunks.Add(new CacheNode[NODE_CHUNK_SIZE]);
            }
        }

        private int AllocNode(long blockIndex, int epoch)
        {
            int index;
            if (_freeNodeHead >= 0)
            {
                index = _freeNodeHead;
                _freeNodeHead = NodeAt(index).Next;
            }
            else
            {
                index = _nodeHighWater;
                EnsureNodeChunk(index);
                _nodeHighWater++;
            }

            ref CacheNode node = ref NodeAt(index);
            node.BlockIndex = blockIndex;
            node.Epoch = epoch;
            node.Prev = -1;
            node.Next = -1;
            return index;
        }

        private void FreeNode(int index)
        {
            ref CacheNode node = ref NodeAt(index);
            node.BlockIndex = -1;    // 标记"该节点已不在链上"，防止之后被误当活节点处理
            node.Prev = -1;
            node.Next = _freeNodeHead;
            _freeNodeHead = index;
        }

        /// <summary>挂到某个窗口桶的尾部（尾 = 该秒内较早进来的先被淘汰）</summary>
        private void LinkToBucket(int index, int bucket, int epoch)
        {
            ref CacheNode node = ref NodeAt(index);
            node.Epoch = epoch;
            int tail = _bucketTail[bucket];
            node.Prev = tail;
            node.Next = -1;
            if (tail >= 0) NodeAt(tail).Next = index; else _bucketHead[bucket] = index;
            _bucketTail[bucket] = index;
            Interlocked.Increment(ref _chainedNodes);
        }

        /// <summary>
        /// 从它当前所在的链上摘除。归属判定：Epoch &lt;= 冷尾水位 ⇒ 在冷尾链上，否则在 Epoch % 窗口 那个桶里。
        /// </summary>
        private void Unlink(int index)
        {
            ref CacheNode node = ref NodeAt(index);
            if (node.Epoch <= _coldBaseEpoch)
            {
                if (node.Prev >= 0) NodeAt(node.Prev).Next = node.Next; else _coldHead = node.Next;
                if (node.Next >= 0) NodeAt(node.Next).Prev = node.Prev; else _coldTail = node.Prev;
            }
            else
            {
                int bucket = NormalizeBucket(node.Epoch);
                if (node.Prev >= 0) NodeAt(node.Prev).Next = node.Next; else _bucketHead[bucket] = node.Next;
                if (node.Next >= 0) NodeAt(node.Next).Prev = node.Prev; else _bucketTail[bucket] = node.Prev;
            }
            node.Prev = -1;
            node.Next = -1;
            Interlocked.Decrement(ref _chainedNodes);
        }

        /// <summary>
        /// 时间轮推进：把"被覆盖掉的那一圈"的桶整条并入冷尾链。
        /// 逐秒推进时每秒 O(1)（只改 4 个指针）；长时间空闲后的首次访问最多 O(窗口)，仍是微秒级。
        /// </summary>
        private void AdvanceTo(int now)
        {
            if (now <= _currentEpoch) return;

            int steps = now - _currentEpoch;
            if (steps >= _windowSeconds)
            {
                // 跨越了整整一圈以上：所有桶里的块都已过期
                for (int b = 0; b < _windowSeconds; b++)
                {
                    MergeBucketIntoCold(b);
                }
                _coldBaseEpoch = now - _windowSeconds;
            }
            else
            {
                for (int k = 1; k <= steps; k++)
                {
                    int t = _currentEpoch + k;
                    MergeBucketIntoCold(NormalizeBucket(t));
                    _coldBaseEpoch = t - _windowSeconds;
                }
            }

            _currentEpoch = now;
        }

        /// <summary>把某个桶的整条链接到冷尾链尾部（O(1)：不逐节点改标记，归属靠 _coldBaseEpoch 判定）</summary>
        private void MergeBucketIntoCold(int bucket)
        {
            int head = _bucketHead[bucket];
            if (head < 0) return;

            int tail = _bucketTail[bucket];
            NodeAt(head).Prev = _coldTail;
            if (_coldTail >= 0) NodeAt(_coldTail).Next = head; else _coldHead = head;
            _coldTail = tail;

            _bucketHead[bucket] = -1;
            _bucketTail[bucket] = -1;
        }

        /// <summary>命中提升（调用方持有 _wheelLock）：每个块每秒最多一次链表操作</summary>
        private void TouchLocked(int index, int now)
        {
            if (index < 0) return;

            AdvanceTo(now);
            ref CacheNode node = ref NodeAt(index);
            if (node.BlockIndex < 0) return;
            if (node.Epoch == now) return;

            Unlink(index);
            LinkToBucket(index, NormalizeBucket(now), now);
        }

        /// <summary>
        /// 按 LRU 摘除最多 <paramref name="budget"/> 个块，返回实际摘到的块数。
        /// 顺序：冷尾链（最冷）→ 窗口内最老桶 → 较新的桶，**跳过当前桶与宽限桶**。
        /// </summary>
        private int EvictBatch(int budget)
        {
            long startTicks = Environment.TickCount64;
            int removed = 0;

            lock (_wheelLock)
            {
                int now = NowSeconds();
                AdvanceTo(now);

                // 1) 冷尾链：已经是"超过一个窗口没被碰过"的块
                while (removed < budget && _coldHead >= 0)
                {
                    if (EvictNode(_coldHead)) removed++;
                }

                // 2) 窗口内最老 → 较新，跳过当前桶与宽限桶；读取另由同一把轮锁保护。
                int oldest = _coldBaseEpoch + 1;
                int newest = now - _graceSeconds;
                for (int epoch = oldest; epoch <= newest && removed < budget; epoch++)
                {
                    int bucket = NormalizeBucket(epoch);
                    while (removed < budget && _bucketHead[bucket] >= 0)
                    {
                        if (EvictNode(_bucketHead[bucket])) removed++;
                    }
                }

                if (removed > 0)
                {
                    Interlocked.Add(ref _evictedBlocksTotal, removed);
                    _lastEvictionBlocks = removed;
                    _lastEvictionMs = Environment.TickCount64 - startTicks;
                }

                VerifyStructure(removed);
                if (LogService.TraceFileLoggingEnabled) VerifyChainFull();
            }

            if (removed > 0)
            {
                // 逐轮痕迹：默认不落盘（前缀已登记进 LogService.TracePrefixes），开 TraceFileLogging 才记账
                LogService.DebugFile($"Evict 摘除 {removed} 块，用时 {_lastEvictionMs:F0} ms，" +
                                     $"累计 {Interlocked.Read(ref _evictedBlocksTotal)} 块，空闲槽位 {FreeSlots}");
            }

            return removed;
        }

        /// <summary>摘除链上的一个块：原子摘块表（成功者负责归还槽位）→ 摘链 → 还节点</summary>
        private bool EvictNode(int index)
        {
            ref CacheNode node = ref NodeAt(index);
            long blockIndex = node.BlockIndex;

            bool freed = false;
            if (blockIndex >= 0 && _blocks.TryRemove(blockIndex, out SlotRef slotRef))
            {
                // 节点号必须对得上：对不上说明链上这个节点是"上一代"遗留（理论上不该出现），
                // 那就只把它从链上摘掉，绝不去归还别人正在用的槽位（否则两个块共用一块 4KB ⇒ 静默错数据）
                if (slotRef.NodeIndex == index)
                {
                    ReturnFreeSlot(slotRef);
                    Interlocked.Decrement(ref _cachedBlocks);
                    freed = true;
                }
                else
                {
                    LogService.DebugFile(
                        $"CacheService 淘汰自检：块 {blockIndex} 的索引指向节点 {slotRef.NodeIndex}，链上却是 {index}（节点已被顶替）");
                }
            }

            Unlink(index);
            FreeNode(index);
            return freed;
        }

        #endregion

        #region 自检

        /// <summary>
        /// 计数守恒（O(1)）："已占用槽位数"与"时间轮链上的节点数"必须相等——
        /// 这是唯一能廉价发现"槽位泄漏 / 重复归还"的手段，每轮淘汰结束时校验一次。
        /// 两个计数各自在被不同临界区保护的地方变化，所以要把"在途窗口"（<see cref="_inFlightSlotOps"/>）扣掉，
        /// 否则水位节拍与 IO 线程并发时会误报。
        /// </summary>
        private void VerifyStructure(int justRemoved)
        {
            long used = Interlocked.Read(ref _usedSlots) - Interlocked.Read(ref _inFlightSlotOps);
            long chained = Interlocked.Read(ref _chainedNodes);
            if (used != chained)
            {
                LogService.DebugFile(
                    $"CacheService 淘汰自检：结构计数不一致 — 已占用槽位 {used} ≠ 链上节点 {chained}（本轮淘汰 {justRemoved} 块）");
            }
            long blocks = Interlocked.Read(ref _cachedBlocks);
            if (blocks != chained)
            {
                LogService.DebugFile(
                    $"CacheService 淘汰自检：结构计数不一致 — 块索引条目 {blocks} ≠ 链上节点 {chained}（本轮淘汰 {justRemoved} 块）");
            }
        }

        /// <summary>
        /// 全量校验（O(N)，仅在 TraceFileLogging 打开时执行）：逐桶走链，检查前驱指针互指、链尾正确、
        /// 无环、节点总数与计数一致。链表结构一旦损坏是**不可观测**的（不崩溃、无错误码），所以需要这个兜底。
        /// </summary>
        private void VerifyChainFull()
        {
            long counted = 0;
            for (int b = 0; b < _windowSeconds; b++)
            {
                if (!VerifyOneChain(_bucketHead[b], _bucketTail[b], ref counted)) return;
            }
            if (!VerifyOneChain(_coldHead, _coldTail, ref counted)) return;

            long chained = Interlocked.Read(ref _chainedNodes);
            if (counted != chained)
            {
                LogService.DebugFile($"CacheService 淘汰自检：全量校验 — 链上节点 {counted} ≠ 计数 {chained}");
            }
        }

        private bool VerifyOneChain(int head, int tail, ref long counted)
        {
            int prev = -1;
            int current = head;
            while (current >= 0)
            {
                ref CacheNode node = ref NodeAt(current);
                if (node.Prev != prev)
                {
                    LogService.DebugFile($"CacheService 淘汰自检：全量校验 — 节点 {current} 的前驱指针不一致（期望 {prev}，实际 {node.Prev}）");
                    return false;
                }
                counted++;
                prev = current;
                current = node.Next;
                if (counted > _nodeHighWater)
                {
                    LogService.DebugFile("CacheService 淘汰自检：全量校验 — 链表疑似成环或节点数超出节点池容量");
                    return false;
                }
            }

            if (prev != tail)
            {
                LogService.DebugFile($"CacheService 淘汰自检：全量校验 — 链尾指针不一致（期望 {prev}，实际 {tail}）");
                return false;
            }
            return true;
        }

        #endregion

        #region 内部类型

        /// <summary>
        /// 一个 Slab（64MB）及其**自己的**空闲链。
        /// 空闲链按 Slab 分组是"整块归还系统"的前提：聚合了自己的空闲槽位之后，
        /// 一个 Slab 才可能被整体从流通中隔出来并 `free`（全局单栈做不到这一点，见 §6.9）。
        /// </summary>
        private sealed class Slab
        {
            public readonly IntPtr Base;
            public readonly int[] FreeIdx = new int[SLOTS_PER_SLAB];   // 空闲槽号（栈；64KB/块）
            public int FreeTop;                                        // 栈顶 = 当前空闲槽位数（0..SLOTS_PER_SLAB）
            public long WholeFreeSinceTicks;                           // 整块空闲的起点（0 = 不是整块空闲）

            public Slab(IntPtr basePtr) { Base = basePtr; }
        }

        /// <summary>所属 Slab + 槽号 + 时间轮节点索引，归还槽位无需搜索池</summary>
        private readonly struct SlotRef
        {
            public readonly Slab Owner;
            public readonly int SlotIndex;
            public readonly int NodeIndex;
            public IntPtr Slot => SlotAddress(Owner, SlotIndex);

            public SlotRef(Slab owner, int slotIndex, int nodeIndex)
            {
                Owner = owner;
                SlotIndex = slotIndex;
                NodeIndex = nodeIndex;
            }
        }

        /// <summary>
        /// 时间轮节点（时间轮与节点池共用的双向链节点，数组化存储）。
        /// 阶段一这里还挂着"所属文件条目"（摘空后好把条目从文件表里移除）；块设备下没有文件，
        /// 只剩一个全局块号——摘除时用它去索引表里认领（见 <see cref="EvictNode"/>）。
        /// </summary>
        private struct CacheNode
        {
            public int Prev;          // 前驱节点索引（-1 = 无）
            public int Next;          // 后继节点索引（-1 = 无）
            public int Epoch;         // 入链时的绝对秒；<= _coldBaseEpoch 表示已在冷尾链上
            public long BlockIndex;   // 全局块号（-1 = 该节点已不在链上）
        }

        #endregion
    }
}
