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
using System.Text;
using System.Threading;
using DiskAccessLibrary;
using FlyDisk.Localization;
using FlyDisk.Models;

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
    /// 读本文件前必须知道六条前提：
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
    ///    别的机器 / 双系统里用过），账本一律作废——**宁可丢缓存，不可返回陈旧块**（那会让 NTFS 读到
    ///    别人的数据，属于卷损坏级事故）。
    ///
    ///    **本层防不住的两类"离线修改"（如实登记，与 PrimoCache 文档所述同类问题）**：
    ///    ① 用户手动把盘联机 → 改了数据 → 自己再脱机：全程没有"联机状态可被我们观测到"的时机；
    ///    ② 把盘接到另一台电脑或另一个操作系统上改写：本机 Windows 里那条"脱机属性"记录根本不会变。
    ///    这两类只能靠用户自觉规避（与 PrimoCache"离线修改"的免责口径一致）。
    /// </summary>
    public sealed class SsdCacheService : IDisposable
    {
        #region 常量

        private const int BLOCK_SIZE = ServiceConstants.BlockSize;

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

        private const int MIN_SLOTS = 4096;                // 容量下限 16 MiB
        // 容量上限 64 GiB。**上限的实质是内存**：索引/槽位结构常驻内存，约 MetadataBytesPerBlock 字节/块，
        // 每 1 GiB 容量约 15 MiB，所以 64 GiB 大约要 930 MiB 常驻内存（设置界面会把这份估算摊给用户看）。
        // 想再往上放就得改块大小（见 后续待办.md）或把索引也落盘，不能只改这个数。
        private const long MAX_CAPACITY_BYTES = 64L * 1024 * 1024 * 1024;
        private const int MAX_SLOTS = (int)(MAX_CAPACITY_BYTES / BLOCK_SIZE);
        private const long MIN_KEEP_FREE_BYTES = 2L * 1024 * 1024 * 1024; // 给目标卷至少留 2 GiB
        private const int RING_SLACK = 4;                  // 环形缓冲余量，避免在边界上反复淘汰

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
        /// <param name="allowLedgerReset">
        /// 允许在"**盘/参数不匹配**"时清空重建。默认 false ⇒ 抛 <see cref="L2LedgerMismatchException"/>，
        /// 由 UI 弹窗问过用户之后再以 true 重试（见 后续待办.md 第五节）。
        /// </param>
        public SsdCacheService(DiskConfig config, BlockSourceInfo source, bool allowLedgerReset = false)
        {
            _dir = Path.GetFullPath(config.SsdCachePath).TrimEnd('\\');
            _deviceIdentity = ComputeDeviceIdentity(source.Model, source.SerialNumber, source.SizeBytes, source.BytesPerSector);

            Directory.CreateDirectory(_dir);

            // 单实例锁：缓存丢了可以接受，但"被自己两个实例写坏"不可接受
            _lockFile = new FileStream(Path.Combine(_dir, LockFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);

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

            // **"盘/参数不匹配"必须在打开容器之前判**：容器随后会以 FileShare.None 打开，
            // 到那时同进程的第二次只读打开也会失败，探测就永远读不到东西了。
            // 顺序也顺理成章：先知道"要不要问用户"，再去碰任何文件。
            string mismatch = DetectLedgerMismatch(config, source);
            if (mismatch.Length > 0 && !allowLedgerReset)
            {
                throw new L2LedgerMismatchException(mismatch);
            }

            _container = new FileStream(Path.Combine(_dir, ContainerFileName), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);

            long required = (long)(_slotCount + 1) * BLOCK_SIZE;   // +1 是容器头部
            long previousId = 0;
            bool containerReady = _container.Length >= required && TryReadContainerHeader(out previousId);
            if (!containerReady)
            {
                // 容器不存在 / 尺寸不符 / 不是我们的容器（例如用户只删了 cache.dat）/ 换了另一块盘
                // ⇒ 尺寸校正到应有值，头部随后由 RotateContainerId 覆写；旧索引的戳反正对不上，不会误信
                EnsureContainerLength(required);
            }

            // **账本可信判据**（见类注释第 6 条）：只有当"打开这块盘时它原本就是脱机"时，
            // 才可能有人打包票说"上次运行之后没人写过它"。盘若是联机的（用户手动联机过、或它去过别的机器），
            // 一律不载入——宁可丢整层缓存，也不能把陈旧块当数据返回。
            bool ledgerTrusted = !source.WasOnline;
            bool loaded = ledgerTrusted && containerReady && TryLoadIndex(previousId);
            if (!loaded)
            {
                string reason = mismatch.Length > 0
                    ? $"用户确认清空重建（{mismatch}）"
                    : (!ledgerTrusted
                        ? "该盘启动时是联机状态（上次运行之后它可能被别的程序写过），账本一律作废"
                        : (containerReady ? "索引缺失/校验失败，或上次未正常关服" : "容器已重建"));
                ResetToEmpty(reason);
            }

            // **开机留痕**（整个方案里唯一一处运行期落盘）：把身份戳换成新的并 FlushFileBuffers。
            // 从此盘上那份旧索引的戳永远对不上 ⇒ 本次运行无论怎么改容器、无论怎么死，下次开机都不会误信它。
            RotateContainerId();

            _loadMs = Environment.TickCount64 - t0;
            _loadedFromDisk = loaded;

            LogService.DebugFile(
                $"SSD 二级缓存 {(loaded ? "已载入" : "已新建")}：{_slotCount} 槽位（{(long)_slotCount * BLOCK_SIZE / 1024 / 1024} MB）" +
                $" / M={_mLimit} G={_ghostLimit}（保守门槛 {_fillingReserve} 空闲槽 / 保守线 {_conservativeOccupancy:P0}）/ 占用 {UsedSlots} 槽、空洞 {HoleSlots}" +
                $" / 用时 {_loadMs} ms / 身份戳 0x{_containerId:X16}" +
                (ledgerTrusted ? "（异常终止会使整层缓存作废）" : "（该盘原本联机，本次空缓存起步）"));
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

        /// <summary>容量（字节）→ 槽位数：下限 16 MiB、上限 64 GiB，并为目标卷留出 2 GiB 余量</summary>
        private static int ComputeSlotCount(string dir, long maxBytes)
        {
            int slots = (int)Math.Clamp(maxBytes / BLOCK_SIZE, MIN_SLOTS, MAX_SLOTS);
            try
            {
                string? root = Path.GetPathRoot(dir);
                if (!string.IsNullOrEmpty(root))
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    long wanted = (long)slots * BLOCK_SIZE;
                    if (wanted > free - MIN_KEEP_FREE_BYTES)
                    {
                        long allowed = Math.Max((long)MIN_SLOTS * BLOCK_SIZE,
                            (free - MIN_KEEP_FREE_BYTES) / BLOCK_SIZE * BLOCK_SIZE);
                        int clamped = (int)Math.Clamp(allowed / BLOCK_SIZE, MIN_SLOTS, MAX_SLOTS);
                        LogService.DebugFile(
                            $"SSD 二级缓存：容量上限超过该卷可用空间，已夹取 {wanted / 1024 / 1024} MiB → {clamped * (long)BLOCK_SIZE / 1024 / 1024} MiB" +
                            $"（可用 {free / 1024 / 1024} MiB，为卷保留 {MIN_KEEP_FREE_BYTES / 1024 / 1024} MiB）");
                        slots = clamped;
                    }
                }
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存：查询 {dir} 可用空间失败（按配置值继续）：{ex.Message}");
            }
            return slots;
        }

        /// <summary>
        /// **只读探测**：上次留下的 L2 账本与当前目标盘 / 当前配置是否对得上。
        ///
        /// 返回"为什么对不上"（空串 = 对得上、或根本没有账本可谈）。调用方只在**非空**时做文章：
        /// 引擎抛 <see cref="L2LedgerMismatchException"/> 让 UI 弹窗问用户，而不是悄悄清空。
        ///
        /// 刻意**不管**"上次没正常关服"（身份戳对不上、索引缺失）——那类是预期内的，静默重建即可；
        /// 这里只抓"用户换了盘 / 改了容量或 ghost 参数"这种**会让人莫名其妙丢掉整层缓存**的情形。
        /// 探测自身出任何异常都按"没有冲突"处理（不拦启动），并落一条警告。
        /// </summary>
        public static string DetectLedgerMismatch(DiskConfig config, BlockSourceInfo info)
        {
            try
            {
                string dir = Path.GetFullPath(config.SsdCachePath).TrimEnd('\\');
                string containerPath = Path.Combine(dir, ContainerFileName);
                if (!File.Exists(containerPath)) return string.Empty;   // 首次运行：没有账本可谈

                long identity = ComputeDeviceIdentity(info.Model, info.SerialNumber, info.SizeBytes, info.BytesPerSector);
                int slotCount = ComputeSlotCount(dir, config.SsdCacheMaxBytes);

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

                int containerSlots = BitConverter.ToInt32(header, 16);
                if (containerSlots != slotCount)
                {
                    // 注意这条可能来自"缓存盘可用空间的自动夹取"（ComputeSlotCount 会为卷留 2 GB），
                    // 不一定真的改过配置 —— 所以措辞要中性，且由用户决定是否重建。
                    return Locale.T("engine.l2.slotMismatch", containerSlots, slotCount);
                }

                // 索引头部记的"槽数 / M 上限 / ghost 上限"是同一批参数的另一份记录（ghost 倍率改了也在这里现形）
                return DetectIndexParameterMismatch(dir, slotCount, config) ?? string.Empty;
            }
            catch (Exception ex)
            {
                LogService.DebugFile(
                    $"SSD 二级缓存账本探测失败（按“无冲突”处理，不拦启动）：{ex.Message}");
                return string.Empty;
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

        /// <summary>索引头部里的"槽数 / M 上限 / ghost 上限"是否与当前配置一致；不一致则返回原因</summary>
        private static string? DetectIndexParameterMismatch(string dir, int slotCount, DiskConfig config)
        {
            string indexPath = Path.Combine(dir, IndexFileName);
            if (!File.Exists(indexPath)) return null;

            using var fs = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < INDEX_HEADER_SIZE) return null;

            byte[] header = new byte[INDEX_HEADER_SIZE];
            fs.ReadExactly(header, 0, INDEX_HEADER_SIZE);

            // 版本/校验和都不符的索引属于"上一版格式"或"没写完"，那是静默重建那一类，不在这里报
            if (BitConverter.ToUInt32(header, 0) != INDEX_MAGIC ||
                BitConverter.ToInt32(header, 4) != INDEX_VERSION ||
                Hash(header, 0, INDEX_HEADER_HASH_OFFSET) != BitConverter.ToUInt64(header, INDEX_HEADER_HASH_OFFSET))
            {
                return null;
            }

            int iSlotCount = BitConverter.ToInt32(header, 12);
            int iSLimit = BitConverter.ToInt32(header, 16);
            int iMLimit = BitConverter.ToInt32(header, 20);
            int iGhostLimit = BitConverter.ToInt32(header, 24);

            double ghostFraction = Clamp(config.SsdCacheGhostFraction, 0.0, 4.0);
            int mLimit = Math.Max(1, slotCount);
            int ghostLimit = (int)(mLimit * ghostFraction);

            if (iSlotCount != slotCount || iSLimit != 0 || iMLimit != mLimit || iGhostLimit != ghostLimit)
            {
                return Locale.T("engine.l2.indexParamMismatch",
                    iSlotCount, iMLimit, iGhostLimit, slotCount, mLimit, ghostLimit);
            }
            return null;
        }

        /// <summary>
        /// 只读探测："容器头部的身份戳"与"索引里记的戳"是否**不符**——不符 = **上次没有正常关服**
        /// （掉电/强杀/崩溃）：索引还是上一次正常关服时的快照，而容器已被之后的运行改过。
        ///
        /// 不一致**不代表账本没救**：索引结构仍自洽（校验器会逐条查），`L2Verifier` 逐块比对能把所有
        /// 错乱的槽（含"索引指向的槽已被复用给别的块"）用源盘数据修回正确内容，修完由
        /// `L2Verifier.ConfirmLedger` 把戳写回即重新自洽。所以这条归"需要校验"，而不是"静默作废"。
        ///
        /// 返回空串 = 正常，或"没有可校验的账本"（容器/索引不存在、头部不可信、槽数不符——后者归参数不匹配那条）。
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
                if (BitConverter.ToInt32(ih, 12) != BitConverter.ToInt32(ch, 16))
                    return string.Empty;   // 槽数不符：归"参数不匹配"那条，交给 DetectLedgerMismatch

                long containerStamp = BitConverter.ToInt64(ch, 8);
                if (BitConverter.ToInt64(ih, 52) == containerStamp) return string.Empty;   // 戳一致 ⇒ 上次正常关服

                return Locale.T("engine.l2.stampMismatch");
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"L2 身份戳探测失败（按“无需校验”处理）：{ex.Message}");
                return string.Empty;
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

            NoteAccessSlot(slot);
            ReadSlot(slot, buffer, offset);
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
            if (_container == null) return false;
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
            if (_frozen) return;
            if (TryFindInL2(blockIndex, out int slot))
            {
                // 已在 M：刷新数据（从盘上读到的才是最新的）并加计数，**不重复入队**（否则队列顺序会被打乱）
                WriteSlotFrom(slot, buffer, offset);
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
            WriteSlotFrom(slot, buffer, offset);
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
        private void WriteSlotFrom(int slot, byte[] buffer, int offset)
        {
            _container!.Position = (long)(slot + 1) * BLOCK_SIZE;
            _container.Write(buffer, offset, BLOCK_SIZE);
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

            WriteSlotFrom(slot, buffer, offset);
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

        /// <summary>把容器调整到应有的长度（不足则补零、多余则截断）。内容是否可信由身份戳判定，不在这里管。</summary>
        private void EnsureContainerLength(long required)
        {
            if (_container!.Length != required)
            {
                _container.SetLength(required);
                LogService.DebugFile($"Ssd 容器尺寸已校正为 {required} 字节（{_slotCount} 槽 + 头部）");
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

        private bool TryReadContainerHeader(out long containerId)
        {
            containerId = 0;
            byte[] buf = new byte[BLOCK_SIZE];
            _container!.Position = 0;
            _container.ReadExactly(buf, 0, BLOCK_SIZE);

            if (Hash(buf, 0, CONTAINER_HASH_LEN) != BitConverter.ToUInt64(buf, CONTAINER_HASH_LEN)) return false;
            if (BitConverter.ToUInt32(buf, 0) != CONTAINER_MAGIC) return false;
            if (BitConverter.ToInt32(buf, 4) != CONTAINER_VERSION) return false;
            if (BitConverter.ToInt32(buf, 16) != _slotCount) return false;
            if (BitConverter.ToInt32(buf, 20) != BLOCK_SIZE) return false;
            if (BitConverter.ToInt64(buf, 24) != _deviceIdentity) return false;   // 换了另一块盘 ⇒ 容器作废

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
            for (int i = 0; i < _slotCount; i++)
            {
                _freeStack[_freeCount++] = i;
            }
            Interlocked.Exchange(ref _usedSlots, 0);
            Interlocked.Exchange(ref _holeSlots, 0);

            try
            {
                long required = (long)(_slotCount + 1) * BLOCK_SIZE;
                if (_container!.Length != required) _container.SetLength(required);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存：容器尺寸校正失败：{ex.Message}");
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
            if (_container == null) return;

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
                int version, blockSize, slotCount, sLimit, mLimit, ghostLimit;
                int usedSlots, sCount, mCount, ghostCount;
                long generation, storedContainerId;
                ulong headerHash;
                using (var ms = new MemoryStream(header))
                using (var hr = new BinaryReader(ms))
                {
                    magic = hr.ReadUInt32();
                    version = hr.ReadInt32();
                    blockSize = hr.ReadInt32();
                    slotCount = hr.ReadInt32();
                    sLimit = hr.ReadInt32();
                    mLimit = hr.ReadInt32();
                    ghostLimit = hr.ReadInt32();
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
                if (slotCount != _slotCount || sLimit != 0 || mLimit != _mLimit || ghostLimit != _ghostLimit) return false;
                if (storedContainerId != containerId) return false;   // 容器被重建过（例如只删了 cache.dat）
                if (Hash(header, 0, INDEX_HEADER_HASH_OFFSET) != headerHash) return false;
                if (usedSlots < 0 || sCount != 0) return false;       // S 已删：正文里不该再有 S 环条目
                if (mCount < 0 || mCount > _mRing.Length || ghostCount < 0 || ghostCount > _ghostRing.Length) return false;

                // 每个槽（下标）只能出现一次，且必须与环上的块号对得上。
                // 最危险的形态是"块索引指向的槽不在环上"——那个槽会被当成空闲复用，旧条目就会读到别人的数据。
                bool[] ringed = new bool[_slotCount];
                int registered = 0;

                using (var body = new HashingStream(fs))
                {
                    using (var br = new BinaryReader(body, Encoding.UTF8, leaveOpen: true))
                    {
                        if (!ReadRing(br, _mRing, ref _mHead, ref _mCount, mCount, ringed, ref registered)) return false;

                        for (int i = 0; i < ghostCount; i++)
                        {
                            long blockIndex = br.ReadInt64();
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

                // 交叉校验：① 环上登记的槽数必须与头部记的"占用槽数"一致；
                //            ② 块索引的条目数必须与之相等（两者都从同一批环条目重建，不等说明正文被改过）
                if (registered != usedSlots) return false;
                if (_blocksByIndex.Count != registered) return false;

                // 未出现在任何环上的槽 ⇒ 空闲
                _freeCount = 0;
                for (int slot = 0; slot < _slotCount; slot++)
                {
                    if (!ringed[slot]) _freeStack[_freeCount++] = slot;
                }
                Interlocked.Exchange(ref _usedSlots, registered);

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
            bool[] ringed, ref int registered)
        {
            int read = 0;
            for (int i = 0; i < entryCount; i++)
            {
                int slot = br.ReadInt32();
                long blockIndex = br.ReadInt64();
                byte flags = br.ReadByte();

                if (slot < 0 || slot >= _slotCount) return false;
                if (blockIndex < 0) continue;               // 空洞：不入环，槽位保持空闲

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

        public void Dispose()
        {
            try
            {
                if (_container != null)
                {
                    Flush();
                    _container.Dispose();
                    _container = null;
                }
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"SSD 二级缓存关闭时刷盘/关闭失败：{ex.Message}");
            }

            _lockFile?.Dispose();
            _lockFile = null;
        }

        #endregion
    }

    /// <summary>
    /// "L2 账本与当前目标盘 / 配置对不上"——由 <see cref="SsdCacheService.DetectLedgerMismatch"/> 判定。
    /// 引擎抛出它而**不是**悄悄清空重建；UI 捕获后弹窗问用户，确认后才以 <c>allowLedgerReset: true</c> 重试启动。
    /// </summary>
    public sealed class L2LedgerMismatchException : Exception
    {
        public L2LedgerMismatchException(string message) : base(message) { }
    }

    /// <summary>
    /// "目标盘启动时是**联机**状态（上次运行之后盘可能被别的程序/机器改过）"，或
    /// "**上次没有正常关服**（掉电/强杀/崩溃 ⇒ 容器头的身份戳与索引里的对不上）"——
    /// 两种情形都表示"盘上这份账本未必还对"。引擎抛它（而不是像以前那样静默作废整层）让 UI 问用户：
    /// 「现在校验」（校验器逐块比对容器与源盘 ⇒ 干净就把身份戳写回、引擎随后自然采信账本）
    /// 或「清空重建」。
    /// </summary>
    public sealed class L2NeedsVerifyException : Exception
    {
        public L2NeedsVerifyException(string message, string reason) : base(message) => Reason = reason;

        /// <summary>一句话原因（写日志、以及用户选"清空重建"时的说明用）</summary>
        public string Reason { get; }
    }
}
