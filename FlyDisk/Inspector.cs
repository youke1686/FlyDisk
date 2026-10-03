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
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;
using Timer = System.Windows.Forms.Timer;

namespace FlyDisk
{
    /// <summary>
    /// 缓存检查器（见 后续待办.md 第七节）。单进程后**直接**每秒读一次引擎快照
    /// （<see cref="TargetService.CreateSnapshot"/>），不再有 IPC 推送；只读计数器、不遍历集合，
    /// 所以每秒一次没有可观测开销。
    ///
    /// 两个页签分工：
    /// ① **简要**——整页自绘的灰条，回答"跑起来了吗 / 加速有效吗 / 内存吃多少"；
    /// ② **详细**——只读文本框，排障时**可选中、可复制**，按"结论 → 效果 → 负载 → 资源 → 参考 → 口径"排序。
    ///
    /// 只渲染**当前选中的那一页**（切页时再渲一次）：两页内容差得远，没必要每秒都算一遍。
    /// </summary>
    public partial class Inspector : ThemedForm, ILocalizable
    {
        private readonly TargetService _target;
        private readonly Timer _timer;

        /// <summary>控件登记表（见 <see cref="ILocalizable"/>）：静态文本键 → 控件</summary>
        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        /// <summary>最小化时冒出来的悬浮小方块（纯娱乐），恢复/关闭时收掉</summary>
        private SpeedTile? _tile;

        /// <summary>最近一拍的读速。留着是为了 <see cref="OnResize"/> 能**立刻**响应最小化/恢复，不等下个 1 秒节拍</summary>
        private ReadSpeeds _lastSpeeds;

        /// <summary>读速窗口（秒）：简要页上那两个方框与"最近 N 秒平均"用的窗口宽度</summary>
        internal const int SpeedWindowSeconds = 1;

        // ===== 最近 1 秒的两条读速（MB/s）=====
        // 快照里只有**累计**计数，速率必须自己差分：每次取快照采一格"这一刻的累计值"，
        // 用最旧那格与最新那格的差除以两格的时间跨度。**2 格就够**——1 秒的跨度需要 2 个采样点。
        // 采样在 Render() 里**无条件**做：只在简要页采的话，切到详细页再切回来这段历史就断了。
        // 两条读速：**加速后读取** = 交付给发起程序的（`ReadSectorsTotal` 的增量，含缓存挡下的部分）；
        //           **源盘读取**   = 真回源到源盘的（回源块数 × 4 KiB）＝缓存**没**挡住的那部分。
        private readonly (DateTime At, long ReadSectors, long SourceBlocks)[] _throughput =
            new (DateTime, long, long)[SpeedWindowSeconds + 1];
        private int _throughputCount;
        private int _throughputNext;
        private long _lastReadSectors;      // 两个"上次值"用来识别"目标重启过"（累计计数归零）
        private long _lastSourceBlocks;
        private double _peakDeliveredMBps;  // 两条读速各自的"本窗口打开以来最忙"
        private double _peakSourceMBps;

        public Inspector(TargetService target)
        {
            InitializeComponent();
            _target = target;

            tabPages.SelectedIndexChanged += (s, e) => Render();

            _timer = new Timer { Interval = 1000 };
            _timer.Tick += (s, e) => Render();
            _timer.Start();

            Render();   // 打开即显示当前一帧

            // 静态文本全部登记在这里（Designer 里的中文初值随后被覆盖）；动态文本走 ApplyDynamicText
            Bind("insp.tab.brief", pageBrief);
            Bind("insp.tab.detail", pageDetail);
            Bind("insp.topMost", chkTopMost);

            LocalizationManager.Apply(this);
        }

        /// <summary>登记一条「键 → 控件」（一条键可绑多个控件）</summary>
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
        /// 动态文本：窗体标题不是控件，单独刷（**复用主菜单那一条键**，菜单项与窗口标题永远一致）。
        /// 两页的内容都是按当前语言取词拼出来的，所以顺带让它们重来一遍：简要页靠重绘，详细页靠重建文本。
        /// </summary>
        public void ApplyDynamicText()
        {
            Text = Locale.T("menu.inspector");
            Render();
            briefView.Invalidate();
        }

        private void Render()
        {
            CacheStats? stats = null;
            string? error = null;
            try
            {
                stats = _target.CreateSnapshot();
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            ReadSpeeds speeds = stats != null ? SampleReadSpeeds(stats) : CurrentSpeeds(0, 0);
            _lastSpeeds = speeds;
            SyncSpeedTile(speeds);

            if (tabPages.SelectedIndex == 0)
            {
                briefView.SetFrame(stats, error, speeds);
            }
            else
            {
                SetDetailText(error != null ? Locale.T("insp.b.snapshotFailed", error) : RenderDetail(stats!, speeds));
            }
        }

        /// <summary>
        /// 最小化 ⇒ 弹一个 50×50 的置顶小方块（左半 = 加速后读取的灰阶，右半 = 源盘读取的灰阶）；
        /// 恢复就把收它起来。**纯娱乐件，不承担任何功能**（那两半的颜色与简要页的方框同源）。
        /// 方块只建一次、之后靠 Show/Hide 开关，这样用户拖到的位置能记住。
        /// </summary>
        private void SyncSpeedTile(ReadSpeeds speeds)
        {
            if (WindowState != FormWindowState.Minimized)
            {
                if (_tile is { IsDisposed: false, Visible: true }) _tile.Hide();
                return;
            }

            if (_tile == null || _tile.IsDisposed)
            {
                _tile = new SpeedTile(RestoreFromTile);
                _tile.LocateDefault();
                _tile.Show();
            }
            _tile.SetSpeeds(speeds);
            if (!_tile.Visible) _tile.Show();
        }

        /// <summary>
        /// 悬浮方块被**点击**（不是拖动）时把检查器还原出来，省得再去任务栏找它。
        /// 方块自己会跟着消失——`WindowState` 一变就触发 <see cref="OnResize"/>，那里会把它收掉。
        /// </summary>
        private void RestoreFromTile()
        {
            WindowState = FormWindowState.Normal;
            Activate();
        }

        /// <summary>读快照失败时也要把峰值带出去（方框的刻度不该因为一拍读失败就归零）</summary>
        private ReadSpeeds CurrentSpeeds(double delivered, double source)
            => new(delivered, _peakDeliveredMBps, source, _peakSourceMBps);

        /// <summary>
        /// 采一格，返回两条读速（最近 <see cref="SpeedWindowSeconds"/> 秒平均 MB/s）与各自的历史峰值。
        ///
        /// **单位是 MB/s（10^6 字节/秒）**，不是 MiB/s——吞吐按通行口径走 10 进制，容量才用 1024 进制的
        /// GiB（盘厂商标称的 TB/GB 也是 10 进制，两者本就不是一套）。
        /// 窗口没满时用已有的那几格算（头几秒是短窗口平均，1 秒后自动收敛）。
        /// </summary>
        private ReadSpeeds SampleReadSpeeds(CacheStats s)
        {
            long readSectors = s.ReadSectorsTotal;
            long sourceBlocks = StatText.SessionWindow(s).Source;

            // 目标重启过 ⇒ 累计计数归零，整段历史作废（否则会算出一个大负数）
            if (_throughputCount > 0 && (readSectors < _lastReadSectors || sourceBlocks < _lastSourceBlocks))
            {
                _throughputCount = 0;
                _throughputNext = 0;
            }
            _lastReadSectors = readSectors;
            _lastSourceBlocks = sourceBlocks;

            _throughput[_throughputNext] = (s.Timestamp, readSectors, sourceBlocks);
            _throughputNext = (_throughputNext + 1) % _throughput.Length;
            if (_throughputCount < _throughput.Length) _throughputCount++;

            if (_throughputCount < 2) return CurrentSpeeds(0, 0);

            (DateTime at0, long read0, long source0) =
                _throughput[(_throughputNext - _throughputCount + _throughput.Length) % _throughput.Length];
            double seconds = (s.Timestamp - at0).TotalSeconds;
            if (seconds <= 0) return CurrentSpeeds(0, 0);

            long bytesPerSector = s.BytesPerSector > 0 ? s.BytesPerSector : 512;
            double delivered = (readSectors - read0) * (double)bytesPerSector / 1_000_000.0 / seconds;
            double source = (sourceBlocks - source0) * (double)ServiceConstants.BlockSize / 1_000_000.0 / seconds;

            if (delivered > _peakDeliveredMBps) _peakDeliveredMBps = delivered;
            if (source > _peakSourceMBps) _peakSourceMBps = source;

            return new ReadSpeeds(delivered, _peakDeliveredMBps, source, _peakSourceMBps);
        }

        /// <summary>
        /// 把文本写进详细页的只读框。两条自我约束：
        /// ① **用户正在选中时这一拍不更新**——每秒重设 Text 会把选区清掉，那样永远复制不完；
        /// ② 内容没变就不重设（少一次重绘）。
        /// </summary>
        private void SetDetailText(string text)
        {
            if (textBox1.SelectionLength > 0) return;
            if (textBox1.Text == text) return;
            textBox1.Text = text;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // 最小化/恢复要**立刻**反应，不能等下一个 1 秒节拍（那会看见方块晚一步才冒出来/消失）
            SyncSpeedTile(_lastSpeeds);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _timer.Stop();
            _timer.Dispose();
            _tile?.Dispose();   // 检查器关掉时方块不能留在桌面上（它是置顶的，留着就没人收得掉）
            base.OnFormClosed(e);
        }

        /// <summary>
        /// 置顶开关。检查器是"盯着看"的窗口（看吞吐、看命中率），被主界面盖住就没意义了。
        /// **默认不置顶**——它只是个观察窗口，默认抢在最前面会挡住别人。
        /// 目前只作用于本次打开（关掉再开恢复默认），要跨会话记住得落到 config.json。
        /// </summary>
        private void chkTopMost_CheckedChanged(object? sender, EventArgs e)
        {
            TopMost = chkTopMost.Checked;
        }

        #region 详细页文本

        private static string RenderDetail(CacheStats s, ReadSpeeds speeds)
        {
            var sb = new StringBuilder();
            sb.AppendLine(StatText.Line(Locale.T("insp.f.snapshotTime"), s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")));

            // ===== 结论：跑没跑起来 =====
            sb.AppendLine();
            sb.AppendLine(Locale.T("insp.section.run"));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.targetState"),
                Locale.T(s.IsRunning ? "insp.w.running" : "insp.w.notStarted")));
            if (s.IsRunning)
            {
                sb.AppendLine(StatText.Line(Locale.T("insp.f.targetDisk"),
                    Locale.T("insp.v.diskOfflineOwned", StatText.Num(s.PhysicalDiskNumber))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.iscsiConn"),
                    Locale.T("insp.v.connCount", StatText.Num(s.ActiveConnections))));
            }

            // ===== 效果：命中落在哪一层 =====
            ReadOutcomeWindow session = StatText.SessionWindow(s);
            long blocks1M = 1024L * 1024 / ServiceConstants.BlockSize;
            long blocks1G = 1024L * 1024 * 1024 / ServiceConstants.BlockSize;

            sb.AppendLine();
            sb.AppendLine(Locale.T("insp.section.outcome"));
            bool anyWindow = false;
            if (s.Recent1MB.Total >= blocks1M)
            {
                sb.AppendLine(OutcomeRow(Locale.T("insp.b.rows.1mb"), s.Recent1MB));
                anyWindow = true;
            }
            if (s.Recent1GB.Total >= blocks1G)
            {
                sb.AppendLine(OutcomeRow(Locale.T("insp.b.rows.1gb"), s.Recent1GB));
                anyWindow = true;
            }
            if (session.Total > 0)
            {
                sb.AppendLine(OutcomeRow(Locale.T("insp.b.rows.session"), session));
                anyWindow = true;
            }
            if (!anyWindow) sb.AppendLine(StatText.Line(string.Empty, Locale.T("insp.w.noBlocksRead")));

            // ===== 负载：这条链路到底被用了多少 =====
            sb.AppendLine();
            sb.AppendLine(Locale.T("insp.section.io"));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.accelerated"),
                Locale.T("insp.v.speed", speeds.Delivered.ToString("F1", CultureInfo.CurrentCulture),
                    SpeedWindowSeconds, speeds.DeliveredPeak.ToString("F1", CultureInfo.CurrentCulture))));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.sourceRead"),
                Locale.T("insp.v.speed", speeds.Source.ToString("F1", CultureInfo.CurrentCulture),
                    SpeedWindowSeconds, speeds.SourcePeak.ToString("F1", CultureInfo.CurrentCulture))));
            sb.AppendLine(StatText.Line("READ",
                Locale.T("insp.v.read", StatText.Num(s.ReadCommandsTotal), StatText.Num(s.ReadSectorsTotal),
                    StatText.Num(s.ReadFullHitCommandsTotal), StatText.Pct(s.ReadFullHitCommandsTotal, s.ReadCommandsTotal))));
            sb.AppendLine(StatText.Line("WRITE",
                Locale.T("insp.v.write", StatText.Num(s.WriteCommandsTotal), StatText.Num(s.WriteSectorsTotal))));

            // ===== 资源与风险：L1 → L2（两层同一套骨架：共有的在前，独有的在后，空行分隔）=====
            sb.AppendLine();
            sb.AppendLine(Locale.T("insp.section.l1"));
            if (!s.EnableMemoryCache)
            {
                sb.AppendLine(StatText.Line(Locale.T("insp.f.state"), Locale.T("insp.w.l1Off")));
            }
            else
            {
                long access = s.L1HitBlocksTotal + s.L1MissBlocksTotal;
                sb.AppendLine(StatText.Line(Locale.T("insp.f.state"), Locale.T("insp.w.enabled")));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.memoryLoad"),
                    s.SystemMemoryLoad.ToString("P1", CultureInfo.CurrentCulture)));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.watermark"),
                    Locale.T("insp.v.watermarksL1", s.EvictionThreshold.ToString("P0", CultureInfo.CurrentCulture),
                        s.StopCachingThreshold.ToString("P0", CultureInfo.CurrentCulture))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.evictionState"), StatText.L1State(s)));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.hit"),
                    Locale.T("insp.v.hits", StatText.Num(s.L1HitBlocksTotal), StatText.Num(s.L1MissBlocksTotal),
                        StatText.Pct(s.L1HitBlocksTotal, access))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.evicted"),
                    Locale.T("insp.v.evictionsTotal", StatText.Num(s.EvictedBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.writeUpdate"),
                    Locale.T("insp.v.blocks", StatText.Num(s.L1WriteUpdateBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.containerSlots"),
                    Locale.T("insp.v.slotsWithSlabs", StatText.Num(s.TotalCacheSize / ServiceConstants.BlockSize),
                        StatText.Num(s.SlabCount), StatText.CapText(s.TotalCacheSize))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.slotLayout"), string.Empty));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.occupied"),
                    Locale.T("insp.v.slotsWithCap", StatText.Num(s.UsedSlots),
                        StatText.CapText(s.UsedSlots * ServiceConstants.BlockSize))));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.free"),
                    Locale.T("insp.v.slotsWithCap", StatText.Num(s.FreeSlots),
                        StatText.CapText(s.FreeSlots * ServiceConstants.BlockSize))));

                sb.AppendLine();
                sb.AppendLine(StatText.Line(Locale.T("insp.f.autoEvict"),
                    Locale.T(s.EvictionEnabled ? "insp.w.enabled" : "insp.w.notEnabled")));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.watermarkTrigger"),
                    Locale.T("insp.v.times", StatText.Num(s.WatermarkEvictionsTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.lastRound"),
                    Locale.T("insp.v.evictionRound", StatText.Num(s.LastEvictionBlocks),
                        s.LastEvictionMs.ToString("F0", CultureInfo.CurrentCulture))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.totalReleased"),
                    Locale.T("insp.v.slabsReleased", StatText.Num(s.ReleasedSlabsTotal),
                        StatText.CapText(s.ReleasedSlabsTotal * ServiceConstants.SlabSize))));
            }

            sb.AppendLine();
            sb.AppendLine(Locale.T("insp.section.l2"));
            if (!s.SsdCacheEnabled)
            {
                sb.AppendLine(StatText.Line(Locale.T("insp.f.state"), Locale.T("insp.w.notEnabled")));
            }
            else
            {
                // "占用" = 数据 + 空洞：空洞的块已作废，但仍占着环位拿不回来
                long occupied = s.SsdUsedSlots + s.SsdHoleSlots;
                double occupancy = s.SsdTotalSlots > 0 ? (double)occupied / s.SsdTotalSlots : 0;
                long l2Access = s.SsdHitBlocksTotal + s.SsdMissBlocksTotal;

                sb.AppendLine(StatText.Line(Locale.T("insp.f.state"), Locale.T("insp.w.l2EnabledPersistent")));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.occupancy"),
                    occupancy.ToString("P1", CultureInfo.CurrentCulture)));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.watermark"),
                    Locale.T("insp.v.conservativeLine",
                        s.SsdConservativeThreshold.ToString("P0", CultureInfo.CurrentCulture))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.admission"),
                    Locale.T(occupancy >= s.SsdConservativeThreshold ? "insp.w.conservative" : "insp.w.aggressive")));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.hit"),
                    Locale.T("insp.v.hits", StatText.Num(s.SsdHitBlocksTotal), StatText.Num(s.SsdMissBlocksTotal),
                        StatText.Pct(s.SsdHitBlocksTotal, l2Access))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.evicted"),
                    Locale.T("insp.v.evictionsTotal", StatText.Num(s.SsdEvictBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.writeUpdate"),
                    Locale.T("insp.v.blocks", StatText.Num(s.SsdWriteUpdateBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.containerSlots"),
                    Locale.T("insp.v.slotsWithCap", StatText.Num(s.SsdTotalSlots),
                        StatText.CapText(s.SsdTotalSlots * ServiceConstants.BlockSize))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.slotLayout"), string.Empty));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.data"),
                    Locale.T("insp.v.slotsWithCap", StatText.Num(s.SsdUsedSlots),
                        StatText.CapText(s.SsdUsedSlots * ServiceConstants.BlockSize))));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.hole"),
                    Locale.T("insp.v.slotsWithCap", StatText.Num(s.SsdHoleSlots),
                        StatText.CapText(s.SsdHoleSlots * ServiceConstants.BlockSize))));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.available"),
                    Locale.T("insp.v.slotsWithCapPct", StatText.Num(s.SsdFreeSlots),
                        StatText.CapText(s.SsdFreeSlots * ServiceConstants.BlockSize),
                        StatText.Pct(s.SsdFreeSlots, s.SsdTotalSlots))));

                sb.AppendLine();
                sb.AppendLine(StatText.Line(Locale.T("insp.f.writeM"),
                    Locale.T("insp.v.blocks", StatText.Num(s.SsdWriteBlocksTotal))));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.aggressive"),
                    Locale.T("insp.v.blocks", StatText.Num(s.SsdFillBlocksTotal))));
                sb.AppendLine(StatText.SubLine(Locale.T("insp.f.sub.ghostRevive"),
                    Locale.T("insp.v.blocks", StatText.Num(s.SsdResurrectBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.ghostKeys"),
                    Locale.T("insp.v.keys", StatText.Num(s.SsdGhostPushBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.writeThrough"),
                    Locale.T("insp.v.invalidatedBlocks", StatText.Num(s.SsdInvalidatedBlocksTotal))));
                sb.AppendLine(StatText.Line(Locale.T("insp.f.persistence"), s.SsdCacheLoadedFromDisk
                    ? Locale.T("insp.v.loadedLedger", StatText.Num(s.SsdGeneration),
                        s.SsdLoadMs.ToString("F0", CultureInfo.CurrentCulture))
                    : Locale.T("insp.w.emptyCache")));
            }

            // ===== 静态参考：配发起程序时才看的东西，沉底 =====
            sb.AppendLine();
            sb.AppendLine(Locale.T("insp.section.device"));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.device"),
                Locale.T("insp.v.device", StatText.Model(s), StatText.Serial(s))));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.capacity"), StatText.CapText(s.DeviceSizeBytes)));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.blockSize"),
                Locale.T("insp.v.blockSize", StatText.Num(s.BytesPerSector * s.SectorsPerBlock),
                    StatText.Num(s.BytesPerSector), StatText.Num(s.SectorsPerBlock))));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.endpoint"),
                Locale.T("insp.v.endpoint", s.ListenEndpoint, s.TargetIqn)));
            sb.AppendLine(StatText.Line(Locale.T("insp.f.bootState"),
                Locale.T(s.DeviceWasOffline ? "insp.w.wasOffline" : "insp.w.wasOnline")));

            return sb.ToString();
        }

        /// <summary>
        /// 一个窗口一行。**每个数字前面都带表头**，不靠列对齐——列对齐要求等宽字体，而中文 + 英文
        /// 混排下一个汉字占两个拉丁字符宽，字体一换整张表就散架。写全了虽然啰嗦，但换任何字体都不会错位。
        /// </summary>
        private static string OutcomeRow(string label, ReadOutcomeWindow w)
            => StatText.Line(label, Locale.T("insp.v.outcome", StatText.Num(w.Total),
                StatText.Pct(w.L1Hit, w.Total), StatText.Pct(w.L2Hit, w.Total), StatText.Pct(w.Source, w.Total)));

        #endregion
    }

    /// <summary>
    /// 检查器自己差分算出来的两条读速（MB/s）与各自的历史峰值。
    /// 快照里没有速率，只有累计计数——所以这份东西**只能由 UI 侧按秒采样产生**，不属于 <see cref="CacheStats"/>。
    /// </summary>
    /// <param name="Delivered">交付给发起程序的读速（含被缓存挡下的部分）</param>
    /// <param name="Source">真回源到源盘的读速（缓存没挡住的那部分）</param>
    internal readonly record struct ReadSpeeds(
        double Delivered, double DeliveredPeak, double Source, double SourcePeak);

    /// <summary>
    /// 快照 → 文本的共用换算与排版。两个页签都要用（简要页的条上文字与详细页的表格），
    /// 口径必须是同一套，所以放在一处。
    /// </summary>
    internal static class StatText
    {
        /// <summary>
        /// 字段分隔符（随语言变：中文全角「：」、英文半角「: 」）。
        /// **不做列对齐**——列对齐要靠等宽字体与固定字段宽，中英混排、字体一换就散架；
        /// 这里只用"标签 + 分隔符 + 值"，换任何字体、任何语言都不会错位。
        /// </summary>
        public static string Separator => Locale.T("insp.sep");

        /// <summary>一个字段一行；标签或值为空时退化成单边（如「槽位分布」这种只有标签的分组行）</summary>
        public static string Line(string label, string value)
        {
            if (label.Length == 0) return value;
            if (value.Length == 0) return label;
            return label + Separator + value;
        }

        /// <summary>同上，但缩进两格，表示它是上一行的从属项</summary>
        public static string SubLine(string label, string value) => "  " + label + Separator + value;

        public static string Num(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

        public static double Frac(long part, long total) => total > 0 ? (double)part / total : 0;

        public static string Pct(long part, long total)
            => total > 0 ? Frac(part, total).ToString("P1", CultureInfo.CurrentCulture) : "—";

        /// <summary>容量自动换档：≥1 TiB 用 TiB、≥1 GiB 用 GiB，否则 MiB（都保留 1 位小数）</summary>
        public static string CapText(long bytes)
        {
            double b = bytes;
            if (b >= 1024.0 * 1024 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024 * 1024)).ToString("F1", CultureInfo.CurrentCulture) + " TiB";
            if (b >= 1024.0 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("F1", CultureInfo.CurrentCulture) + " GiB";
            return (b / (1024.0 * 1024)).ToString("F1", CultureInfo.CurrentCulture) + " MiB";
        }

        /// <summary>
        /// 是否"正在淘汰"。**严格大于淘汰线**——正好压在这条线上仍算正常（2026-10-01 定的显示口径）。
        /// 注：引擎那边的触发条件是 `load &gt;= EvictionThreshold`（见 `WaterLevelTick` 与 `AcquireSlot`），
        /// 所以只有在"负载恰好等于阈值"这一格上，显示与引擎会差一丝。
        /// </summary>
        public static bool IsEvicting(CacheStats s) => s.SystemMemoryLoad > s.EvictionThreshold;

        /// <summary>是否"已停止缓存"（= 引擎 <c>CanCache()</c> 的 `load &lt; StopCaching` 取反）</summary>
        public static bool IsStopCaching(CacheStats s) => s.SystemMemoryLoad >= s.StopCachingThreshold;

        /// <summary>L1 的三个水位状态词（简要条与详细页用同一套词）</summary>
        public static string L1State(CacheStats s)
        {
            if (IsStopCaching(s)) return Locale.T("insp.w.stopCaching");
            if (IsEvicting(s)) return Locale.T("insp.w.evicting");
            return Locale.T("insp.w.normal");
        }

        /// <summary>
        /// "本次加速"这个窗口 = 累计计数器。回源数按 L2 是否启用来取：
        /// L2 开着时它是"L1 未命中且 L2 也没命中"的 <see cref="CacheStats.SsdMissBlocksTotal"/>，
        /// 关着时整条 L1 未命中都是回源。
        /// </summary>
        public static ReadOutcomeWindow SessionWindow(CacheStats s) => new(
            s.L1HitBlocksTotal,
            s.SsdHitBlocksTotal,
            s.SsdCacheEnabled ? s.SsdMissBlocksTotal : s.L1MissBlocksTotal);

        public static string Model(CacheStats s)
            => string.IsNullOrWhiteSpace(s.DeviceModel) ? Locale.T("insp.w.unknownModel") : s.DeviceModel;

        public static string Serial(CacheStats s)
            => string.IsNullOrWhiteSpace(s.DeviceSerial) ? "—" : s.DeviceSerial;
    }

    /// <summary>
    /// 简要页：整页自绘的灰条（见 后续待办.md 第七节）。
    ///
    /// **为什么自绘**：系统 <see cref="ProgressBar"/> 画不了刻度线、嵌不了文字，改色还要 P/Invoke
    /// 去关视觉样式；而这里要的恰恰是"条上带阈值刻度"和"条内分三段"。全是矩形和文字，自绘最省事。
    ///
    /// **一律灰色、只靠深浅区分**：颜色不承载语义（红黄绿会被读成"警告 / 正常"），状态由文字说。
    /// </summary>
    internal sealed class BriefView : Control, IThemedSurface
    {
        // ===== 灰阶（浅色：越深 = 越"占住"；深色：越亮 = 越"占住"，两套成对翻转，见 ThemePalette）=====
        // 颜色与画笔**随主题重建**（见 ApplyTheme），因此都不是 static readonly。
        private Color TrackColor;
        private Color BorderColor;
        private Color TickColor;
        private Color TextColor;
        private Color SubTextColor;

        private Brush TrackBrush = null!;
        private Brush DeepBrush = null!;    // L1 命中 / 数据
        private Brush MidBrush = null!;     // L2 命中
        private Brush LightBrush = null!;   // 源盘读取
        private Brush HoleBrush = null!;    // 空洞
        private Brush FreeBrush = null!;    // 可用

        // 内存水位条的填充色随状态加深（丢掉红黄绿之后，"紧张程度"靠深浅表达）
        private Brush LoadNormalBrush = null!;
        private Brush LoadEvictBrush = null!;
        private Brush LoadStopBrush = null!;

        private Pen BorderPen = null!;
        private Pen TickPen = null!;

        private const int PadX = 12;
        private const int PadY = 12;
        private const int LabelWFallback = 92;   // 像素：标签列的兜底宽度（量不出来时用）
        private const int RightWFallback = 96;   // 像素：右侧数值列的兜底宽度
        private const int BarH = 13;
        private const int LineH = 19;
        private const int BoxSide = 16;     // 吞吐"方框"的边长（略高于条，好看出深浅）

        // 窗口"满了"的判据：引擎那边给的就是 min(已写格数, 窗口长度)，所以取到的格数等于窗口长度即为满
        private const int BlocksPerMB = 1024 * 1024 / ServiceConstants.BlockSize;
        private const int BlocksPerGB = 1024 * 1024 * 1024 / ServiceConstants.BlockSize;

        private CacheStats? _stats;
        private string? _error;
        private ReadSpeeds _speeds;

        // 标签列与右侧数值列的宽度**按语言量出来**：中文的「加速后读取」与英文的 "Accelerated read" 差得远，
        // 写死一个常量必然有一边错位。量一次按语言缓存（见 EnsureMetrics），窗口宽度仍完全交给用户。
        private readonly Dictionary<string, (int LabelW, int RightW)> _metricsCache = new(StringComparer.Ordinal);
        private int _labelW = LabelWFallback;
        private int _rightW = RightWFallback;

        public BriefView()
        {
            DoubleBuffered = true;   // 每秒重绘，双缓冲避免闪烁
            ResizeRedraw = true;
            ApplyPalette(ThemeManager.Palette);   // 构造时先按当前主题就位（Designer 里不写死颜色）
        }

        void IThemedSurface.ApplyTheme(ThemePalette p) => ApplyPalette(p);

        /// <summary>
        /// 按调色板重建整套颜色与画笔（整页是画出来的，递归上色管不到）。
        /// 反复调用安全：先释放旧的再建新的——换肤会走两遍（建句柄 + Load），不释放会攒 GDI 句柄。
        /// </summary>
        private void ApplyPalette(ThemePalette p)
        {
            BackColor = p.CanvasBg;
            TextColor = p.BarText;
            SubTextColor = p.BarSubText;
            TrackColor = p.BarTrack;
            BorderColor = p.BarBorder;
            TickColor = p.BarTick;

            ReleaseGdi();
            TrackBrush = new SolidBrush(TrackColor);
            DeepBrush = new SolidBrush(p.BarDeep);      // L1 命中 / 数据
            MidBrush = new SolidBrush(p.BarMid);        // L2 命中
            LightBrush = new SolidBrush(p.BarLight);    // 源盘读取
            HoleBrush = new SolidBrush(p.BarHole);      // 空洞
            FreeBrush = new SolidBrush(p.BarFree);      // 可用
            LoadNormalBrush = new SolidBrush(p.LoadNormal);
            LoadEvictBrush = new SolidBrush(p.LoadEvict);
            LoadStopBrush = new SolidBrush(p.LoadStop);
            BorderPen = new Pen(BorderColor);
            TickPen = new Pen(TickColor);

            Invalidate();
        }

        /// <summary>释放当前这套画笔（换肤与销毁时都要走一遍）</summary>
        private void ReleaseGdi()
        {
            TrackBrush?.Dispose();
            DeepBrush?.Dispose();
            MidBrush?.Dispose();
            LightBrush?.Dispose();
            HoleBrush?.Dispose();
            FreeBrush?.Dispose();
            LoadNormalBrush?.Dispose();
            LoadEvictBrush?.Dispose();
            LoadStopBrush?.Dispose();
            BorderPen?.Dispose();
            TickPen?.Dispose();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseGdi();
            base.Dispose(disposing);
        }

        /// <param name="stats">为 null 表示这一拍读快照失败，此时 <paramref name="error"/> 给出原因</param>
        /// <param name="speeds">两条读速（最近 <see cref="Inspector.SpeedWindowSeconds"/> 秒平均）与各自的历史峰值</param>
        public void SetFrame(CacheStats? stats, string? error, ReadSpeeds speeds)
        {
            _stats = stats;
            _error = error;
            _speeds = speeds;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            EnsureMetrics(g);
            int barLeft = PadX + _labelW + 8;   // 条起点跟在标签列之后；标签列随语言变宽 ⇒ 条自动变短
            int barW = Math.Max(80, ClientSize.Width - barLeft - _rightW - PadX);
            int y = PadY;

            if (_stats == null)
            {
                Draw(g, _error == null ? Locale.T("insp.b.loading") : Locale.T("insp.b.snapshotFailed", _error),
                     PadX, y, SubTextColor);
                return;
            }

            CacheStats s = _stats;

            // ① 结论
            Draw(g, Locale.T("insp.b.state"), PadX, y, TextColor);
            Draw(g, StatusLine(s), barLeft, y, TextColor);
            y += LineH + 8;

            if (!s.IsRunning)
            {
                Draw(g, Locale.T("insp.b.clickStart"), PadX, y, SubTextColor);
                return;
            }

            // ② 效果（一）：两条读速。方框的**深浅**是唯一刻度（纯白 = 0，纯黑 = 历史最忙）——
            //    它表达"现在有多忙"，不是"占了多少容量"，所以不给长度刻度。
            //    两个方框并排看正好是一组对照：加速后亮、源盘暗 ⇒ 缓存挡下了大部分。
            DrawSpeedRows(g, ref y, barLeft);
            y += 6;

            // ③ 效果（二）：三根堆叠条（窗口没满不画——没满的窗口只是噪声）
            Draw(g, Locale.T("insp.section.outcome"), PadX, y, SubTextColor);
            y += LineH;
            DrawOutcomeRow(g, ref y, barLeft, barW, Locale.T("insp.b.rows.1mb"), s.Recent1MB, BlocksPerMB);
            DrawOutcomeRow(g, ref y, barLeft, barW, Locale.T("insp.b.rows.1gb"), s.Recent1GB, BlocksPerGB);
            DrawOutcomeRow(g, ref y, barLeft, barW, Locale.T("insp.b.rows.session"), StatText.SessionWindow(s), 0);
            y += 8;

            // ④ 风险：L1 内存水位（条上画两条水位线）
            DrawL1Row(g, ref y, barLeft, barW, s);
            y += 8;

            // ⑤ 风险：L2 占用（条上画保守线）
            DrawL2Row(g, ref y, barLeft, barW, s);
            y += 10;

            // ⑥ 页脚
            Draw(g, Locale.T("insp.b.connFooter", StatText.Num(s.ActiveConnections)), PadX, y, TextColor);
            DrawRight(g, Locale.T("insp.b.snapshotFooter", s.Timestamp.ToString("HH:mm:ss")), y, SubTextColor);
        }

        /// <summary>
        /// 量出当前语言的标签列与右侧数值列宽度（切语言后第一次绘制时重算，之后按语言缓存）。
        /// 度量与绘制都用 <see cref="TextRenderer"/> + <c>NoPadding</c>，两边字宽才对得上。
        /// 右侧那列带数字、长度随数值变，所以**按"最坏情况的数字样本"量**，否则列宽会随每秒读数抖动。
        /// </summary>
        private void EnsureMetrics(Graphics g)
        {
            if (_metricsCache.TryGetValue(Locale.Current, out (int LabelW, int RightW) cached))
            {
                _labelW = cached.LabelW;
                _rightW = cached.RightW;
                return;
            }

            int labelW = LabelWFallback;
            foreach (string label in LabelTexts()) labelW = Math.Max(labelW, Measure(g, label));

            int rightW = RightWFallback;
            foreach (string sample in RightTextSamples()) rightW = Math.Max(rightW, Measure(g, sample));

            _labelW = labelW;
            _rightW = rightW;
            _metricsCache[Locale.Current] = (labelW, rightW);
        }

        private int Measure(Graphics g, string text)
            => TextRenderer.MeasureText(g, text, Font, new Size(int.MaxValue, int.MaxValue),
                                        TextFormatFlags.NoPadding).Width;

        /// <summary>标签列里会出现的所有文字（用来量列宽）</summary>
        private static string[] LabelTexts() => new[]
        {
            Locale.T("insp.b.state"),
            Locale.T("insp.section.outcome"),
            Locale.T("insp.b.rows.1mb"),
            Locale.T("insp.b.rows.1gb"),
            Locale.T("insp.b.rows.session"),
            Locale.T("insp.b.l1Header"),
            Locale.T("insp.b.l2Header"),
            Locale.T("insp.b.speedDelivered"),
            Locale.T("insp.b.speedSource"),
        };

        /// <summary>右侧数值列里会出现的文字（数字取"最坏情况"样本，避免列宽随读数抖动）</summary>
        private static string[] RightTextSamples() => new[]
        {
            Locale.T("insp.b.peak", "8888.8"),
            Locale.T("insp.b.blocksValue", "999,999,999"),
            "100.0%   " + Locale.T("insp.w.stopCaching"),
            "100.0%   " + Locale.T("insp.w.conservative"),
            Locale.T("insp.b.snapshotFooter", "88:88:88"),
        };

        /// <summary>两条读速各一行。**先"加速后"再"源盘"**：前者是用户感知到的速度，后者是缓存没挡住的那部分。</summary>
        private void DrawSpeedRows(Graphics g, ref int y, int barLeft)
        {
            // **两个方框共用同一个刻度**（= 加速后读取的历史最大值）：只有这样两半的深浅才可比——
            // 右边越深就代表源盘扛得越多。若各用各的峰值，两块都会各自顶到纯黑，反而看不出差别。
            double scale = _speeds.DeliveredPeak;

            DrawSpeedRow(g, ref y, barLeft, Locale.T("insp.b.speedDelivered"),
                         _speeds.Delivered, _speeds.DeliveredPeak, scale,
                         Locale.T("insp.b.noteDelivered", Inspector.SpeedWindowSeconds));
            DrawSpeedRow(g, ref y, barLeft, Locale.T("insp.b.speedSource"),
                         _speeds.Source, _speeds.SourcePeak, scale,
                         Locale.T("insp.b.noteSource", Inspector.SpeedWindowSeconds));
        }

        /// <param name="ownPeak">这一条自己的历史峰值（右侧文字，是事实）</param>
        /// <param name="scale">方框的"纯黑"刻度——两条共用加速后读取的峰值，不是各自的</param>
        private void DrawSpeedRow(Graphics g, ref int y, int barLeft, string label,
                                  double speed, double ownPeak, double scale, string note)
        {
            Draw(g, label, PadX, y, TextColor);
            DrawSpeedBox(g, barLeft, y + 2, speed, scale);
            Draw(g, speed.ToString("F1", CultureInfo.CurrentCulture) + " MB/s", barLeft + BoxSide + 8, y, TextColor);
            DrawRight(g, Locale.T("insp.b.peak", ownPeak.ToString("F1", CultureInfo.CurrentCulture)), y, SubTextColor);
            y += LineH;

            Draw(g, note, barLeft, y, SubTextColor);
            y += LineH;
        }

        /// <summary>
        /// 读速方框：**0 速 = 纯白，达到历史最忙 = 纯黑**，中间线性。
        /// 峰值还是 0（刚打开、还没读到东西）时按 0 速处理，画成白的。
        /// </summary>
        private void DrawSpeedBox(Graphics g, int x, int y, double speed, double peak)
        {
            using var brush = new SolidBrush(SpeedShade(speed, peak));
            g.FillRectangle(brush, x, y, BoxSide, BoxSide);
            g.DrawRectangle(BorderPen, x, y, BoxSide - 1, BoxSide - 1);   // 纯白时也要看得见这个框
        }

        /// <summary>
        /// 读速 → 灰阶：**0 速纯白、达到"历史最忙"纯黑**，中间线性。
        /// 简要页的方框与最小化后的悬浮窗**共用这一套映射**——两边颜色对得上，才谈得上"看颜色就知道忙不忙"。
        ///
        /// **刻意不跟随深色主题**：这两处是"方框里一块灰度"，白→黑本身就自成一个色块，
        /// 不靠窗体底色衬托；两套主题下用同一套配色，跨主题对照读数也免得换算。
        /// </summary>
        internal static Color SpeedShade(double speed, double peak)
        {
            if (peak <= 0 || speed <= 0) return Color.White;
            int level = (int)Math.Round(255 * (1 - Math.Min(1.0, speed / peak)));
            return Color.FromArgb(level, level, level);
        }

        private void DrawOutcomeRow(Graphics g, ref int y, int barLeft, int barW,
                                    string label, ReadOutcomeWindow w, int fullBlocks)
        {
            if (w.Total <= 0) return;
            if (fullBlocks > 0 && w.Total < fullBlocks) return;   // 近 1MB / 近 1GB：满了才画

            Draw(g, label, PadX, y, TextColor);
            DrawBar(g, barLeft, y + 3, barW, BarH,
                new[]
                {
                    (StatText.Frac(w.L1Hit, w.Total), DeepBrush),
                    (StatText.Frac(w.L2Hit, w.Total), MidBrush),
                    (StatText.Frac(w.Source, w.Total), LightBrush)
                },
                Array.Empty<double>());
            DrawRight(g, Locale.T("insp.b.blocksValue", StatText.Num(w.Total)), y, SubTextColor);
            y += LineH;

            Draw(g, Locale.T("insp.b.hitRow", StatText.Pct(w.L1Hit, w.Total),
                    StatText.Pct(w.L2Hit, w.Total), StatText.Pct(w.Source, w.Total)),
                 barLeft, y, SubTextColor);
            y += LineH;
        }

        private void DrawL1Row(Graphics g, ref int y, int barLeft, int barW, CacheStats s)
        {
            Draw(g, Locale.T("insp.b.l1Header"), PadX, y, TextColor);

            if (!s.EnableMemoryCache)
            {
                Draw(g, Locale.T("insp.b.l1Off"), barLeft, y, SubTextColor);
                y += LineH;
                return;
            }

            double load = Math.Min(1.0, s.SystemMemoryLoad);
            Brush fill = StatText.IsStopCaching(s) ? LoadStopBrush
                       : StatText.IsEvicting(s) ? LoadEvictBrush
                       : LoadNormalBrush;

            DrawBar(g, barLeft, y + 3, barW, BarH,
                new[] { (load, fill) },
                new[] { s.EvictionThreshold, s.StopCachingThreshold });
            DrawRight(g, s.SystemMemoryLoad.ToString("P1", CultureInfo.CurrentCulture) + "   " + StatText.L1State(s),
                      y, TextColor);
            y += LineH;

            Draw(g, Locale.T("insp.b.l1Note", StatText.CapText(s.TotalCacheSize),
                    s.EvictionThreshold.ToString("P0", CultureInfo.CurrentCulture),
                    s.StopCachingThreshold.ToString("P0", CultureInfo.CurrentCulture)),
                 barLeft, y, SubTextColor);
            y += LineH;
        }

        private void DrawL2Row(Graphics g, ref int y, int barLeft, int barW, CacheStats s)
        {
            Draw(g, Locale.T("insp.b.l2Header"), PadX, y, TextColor);

            if (!s.SsdCacheEnabled || s.SsdTotalSlots <= 0)
            {
                Draw(g, Locale.T(s.SsdCacheEnabled ? "insp.b.l2Unavailable" : "insp.b.l2Off"),
                     barLeft, y, SubTextColor);
                y += LineH;
                return;
            }

            long total = s.SsdTotalSlots;
            // "占用" = 数据 + 空洞（空洞占着环位拿不回来）；分界线画在占用率达到保守线处
            double occupancy = (double)(s.SsdUsedSlots + s.SsdHoleSlots) / total;
            bool conservative = occupancy >= s.SsdConservativeThreshold;

            DrawBar(g, barLeft, y + 3, barW, BarH,
                new[]
                {
                    ((double)s.SsdUsedSlots / total, DeepBrush),
                    ((double)s.SsdHoleSlots / total, HoleBrush),
                    ((double)s.SsdFreeSlots / total, FreeBrush)
                },
                new[] { s.SsdConservativeThreshold });
            DrawRight(g, occupancy.ToString("P1", CultureInfo.CurrentCulture) + "   " +
                          Locale.T(conservative ? "insp.w.conservative" : "insp.w.aggressive"), y, TextColor);
            y += LineH;

            Draw(g, Locale.T("insp.b.l2Note",
                    StatText.CapText(s.SsdUsedSlots * ServiceConstants.BlockSize),
                    StatText.CapText(s.SsdHoleSlots * ServiceConstants.BlockSize),
                    StatText.Pct(s.SsdFreeSlots, total),
                    s.SsdConservativeThreshold.ToString("P0", CultureInfo.CurrentCulture)),
                 barLeft, y, SubTextColor);
            y += LineH;
        }

        private void Draw(Graphics g, string text, int x, int y, Color color)
            => TextRenderer.DrawText(g, text, Font, new Point(x, y), color, TextFormatFlags.NoPadding);

        private void DrawRight(Graphics g, string text, int y, Color color)
        {
            Size sz = TextRenderer.MeasureText(g, text, Font, new Size(int.MaxValue, int.MaxValue),
                                               TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, text, Font, new Point(ClientSize.Width - PadX - sz.Width, y),
                                  color, TextFormatFlags.NoPadding);
        }

        /// <summary>画一根条：底槽 → 若干分段（按权重从左往右铺）→ 边框 → 竖线（阈值 / 分界）</summary>
        private void DrawBar(Graphics g, int x, int y, int w, int h,
                             (double Weight, Brush Brush)[] segments, double[] ticks)
        {
            g.FillRectangle(TrackBrush, x, y, w, h);

            double acc = 0;
            foreach ((double weight, Brush brush) in segments)
            {
                if (weight <= 0) continue;
                int sx = x + (int)Math.Round(w * acc);
                int sw = (int)Math.Round(w * weight);
                if (sx + sw > x + w) sw = x + w - sx;   // 浮点累加可能溢出 1px
                if (sw > 0) g.FillRectangle(brush, sx, y, sw, h);
                acc += weight;
            }

            g.DrawRectangle(BorderPen, x, y, w - 1, h - 1);

            // 竖线画到条外上下各 2px：它是"水位线"，压过边框才看得清
            foreach (double t in ticks)
            {
                int tx = x + (int)Math.Round(w * t);
                if (tx <= x || tx >= x + w - 1) continue;
                g.DrawLine(TickPen, tx, y - 2, tx, y + h + 2);
            }
        }

        private string StatusLine(CacheStats s)
        {
            if (!s.IsRunning) return Locale.T("insp.w.notStarted");
            return Locale.T("insp.b.runningLine", StatText.Num(s.PhysicalDiskNumber),
                StatText.Model(s), StatText.CapText(s.DeviceSizeBytes));
        }
    }

    /// <summary>
    /// 最小化检查器时冒出来的 50×50 悬浮小方块。**纯娱乐件**：把一个 1 秒刷新一次的观察窗口缩成一个
    /// 能一直摆在桌面角落的色块，干活时用余光就能看出"读得忙不忙、缓存有没有挡住"。
    ///
    /// **左半 = 加速后读取的灰阶，右半 = 源盘读取的灰阶**，用的是简要页那两个方框的同一套映射
    /// （见 <see cref="BriefView.SpeedShade"/>）：**左边亮、右边暗 ⇒ 缓存挡下了大部分**。
    /// **两半共用同一个刻度**（加速后读取的历史最大值），所以深浅是可直接对比的。
    ///
    /// 三个刻意的做法：
    /// ① 无边框 + 置顶 + 不进任务栏，且 <see cref="ShowWithoutActivation"/> 不抢焦点（它只是个色块）；
    /// ② **可拖动**——置顶的东西挡住画面时总得能让开；
    /// ③ 由 <see cref="Inspector"/> 只建一次、之后 Show/Hide，因此**拖到的位置会记住**。
    ///
    /// **点击（几乎没有移动的按下+抬起）＝ 把检查器还原出来**，不需要再去任务栏找它。
    /// 靠"移动没超过系统拖拽阈值"来区分点击与拖动——不能直接用 <see cref="Control.OnClick"/>，
    /// 那个拖完也会触发，等于拖一次就把检查器叫回来。
    ///
    /// 注意：**整块不加任何描边**（按用户要求，就是两个纯色块）。所以两半都还是纯白时（刚打开、
    /// 峰值尚未建立），它会和白底融为一体——这是有意接受的。
    /// </summary>
    internal sealed class SpeedTile : Form
    {
        private const int TileSize = 50;

        private readonly Action _onActivate;
        private ReadSpeeds _speeds;
        private Point _downAt;       // 按下的位置（屏幕坐标）
        private Point _dragOffset;   // 按下时鼠标在窗口内的位置，拖动时用它保持光标与窗口的相对位置
        private bool _pressed;
        private bool _moved;

        /// <param name="onActivate">点击时回调（由 <see cref="Inspector"/> 用来把自己还原出来）</param>
        public SpeedTile(Action onActivate)
        {
            _onActivate = onActivate;

            // **先关掉自动缩放**：这里要的是实打实的 50×50 像素方块，任何 AutoScale / DPI 缩放
            // 参与进来都可能把它拉成长方形（实测出现过 150×100）。
            AutoScaleMode = AutoScaleMode.None;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;

            // 再把上下限钉死：光靠 AutoScaleMode.None 只堵住"自动缩放"这一条路
            MinimumSize = new Size(TileSize, TileSize);
            MaximumSize = new Size(TileSize, TileSize);
            ClientSize = new Size(TileSize, TileSize);

            DoubleBuffered = true;
            BackColor = Color.White;   // 两半都是"空"时与色块同色；与方框配色一致，刻意不跟随深色主题
        }

        /// <summary>别抢焦点：它只是从最小化状态里冒出来的一个色块</summary>
        protected override bool ShowWithoutActivation => true;

        public void SetSpeeds(ReadSpeeds speeds)
        {
            _speeds = speeds;
            Invalidate();
        }

        /// <summary>首次出现时贴到主屏工作区右下角；之后记住用户拖到的位置（只在新建时调一次）</summary>
        public void LocateDefault()
        {
            Rectangle work = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 800, 600);
            Location = new Point(work.Right - Width - 24, work.Bottom - Height - 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            // **按实际 ClientSize 铺满**（不是按 TileSize 常量）：万一尺寸又被谁改大，也不会露出一条边。
            // **不加任何描边**——整块就是左右两个纯色块。
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int mid = w / 2;

            using var left = new SolidBrush(BriefView.SpeedShade(_speeds.Delivered, _speeds.DeliveredPeak));
            // 右半用**左半的刻度**（加速后读取的历史最大值），两半才可比——见 BriefView.DrawSpeedRows
            using var right = new SolidBrush(BriefView.SpeedShade(_speeds.Source, _speeds.DeliveredPeak));

            g.FillRectangle(left, 0, 0, mid, h);
            g.FillRectangle(right, mid, 0, w - mid, h);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _pressed = true;
            _moved = false;
            _downAt = PointToScreen(e.Location);
            _dragOffset = e.Location;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (!_pressed) return;

            if (!_moved)
            {
                // 还没超过系统拖拽阈值 ⇒ 仍当成"点击"，窗口先别动
                Size threshold = SystemInformation.DragSize;
                Point now = PointToScreen(e.Location);
                if (Math.Abs(now.X - _downAt.X) < threshold.Width &&
                    Math.Abs(now.Y - _downAt.Y) < threshold.Height) return;
                _moved = true;
            }

            Location = new Point(Location.X + e.X - _dragOffset.X, Location.Y + e.Y - _dragOffset.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;

            // 全程没挪动过 ⇒ 这是一次点击：把检查器还原出来（拖动则不触发）
            if (!_moved) _onActivate();
        }
    }
}
