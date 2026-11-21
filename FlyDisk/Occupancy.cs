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
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Engine.HandleScan;
using FlyDisk.Localization;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>脱机前占用检查的结局：通过 / 用户选择继续 / 用户选择终止。</summary>
    internal enum OccupancyGateResult
    {
        /// <summary>所有盘都检查完，没有发现占用，也没有扫描失败。</summary>
        Passed,
        /// <summary>检查过程中出现过占用或扫描失败，但用户选择继续。</summary>
        Continued,
        /// <summary>用户取消检查、或在分支框选择终止。</summary>
        Aborted,
    }

    /// <summary>
    /// 「占用查询」的界面部分：进度框 + 结果框 + 串联逻辑。
    /// 选盘框是通用的 <see cref="PickerForm"/>（occupancyMode + 确定按钮文案「开始检查」），
    /// 本文件不重复造一个。
    ///
    /// 三框串联的语义（见 docs/停机改造与占用查询_施工计划.md §9）：
    /// * 进度框可随时取消，取消后**不弹结果框**；
    /// * 结果框无论有没有占用都打开，空结果也要给一句解释；
    /// * 扫描出错也打开结果框，正文如实写原因，不静默。
    ///
    /// 此外还承担**脱机前**的占用检查（<see cref="RunForStart"/> / <see cref="RunForVerify"/>）：
    /// 启动加速与校验 L2 都会把源盘脱机，脱机前先逐盘查一遍，并把“继续 / 终止”的决定权交给用户。
    /// </summary>
    internal static class OccupancyQuery
    {
        /// <summary>对已选定的盘执行一次完整查询（进度 + 结果）。取消不显示结果。</summary>
        public static void Run(IWin32Window owner, PhysicalDiskInfo disk, bool accelerated)
        {
            OccupancyScanResult? result = ScanOne(owner, disk, 0, 0);
            if (result == null) return;   // 取消 / 出错到拿不到结果

            using var resultForm = new OccupancyResultForm(disk, result, accelerated);
            resultForm.ShowDialog(owner);
        }

        /// <summary>
        /// **启动前**的逐盘占用检查：按选盘顺序一块一块查，查到占用 / 扫描失败时当场问用户
        /// “继续启动还是终止启动”，选继续才查下一块。
        ///
        /// 调用方保证这里传进来的都是**此刻联机**的盘（脱机盘没有挂载卷，查不到，也没必要查）；
        /// 本方法只负责界面与用户决策，不碰任何盘的脱机状态。
        /// </summary>
        public static OccupancyGateResult RunForStart(IWin32Window owner, IReadOnlyList<PhysicalDiskInfo> disks)
        {
            bool warned = false;

            for (int i = 0; i < disks.Count; i++)
            {
                PhysicalDiskInfo disk = disks[i];
                OccupancyScanResult? result = ScanOne(owner, disk, i + 1, disks.Count);
                if (result == null || result.Cancelled) return OccupancyGateResult.Aborted;

                if (!string.IsNullOrEmpty(result.Error))
                {
                    warned = true;
                    string body = Locale.T("occupy.gate.scanFail.start", result.Error);
                    if (!AskStartContinue(owner, Locale.T("occupy.gate.scanFail.title"), body))
                        return OccupancyGateResult.Aborted;
                    continue;
                }

                if (result.Processes.Count == 0) continue;

                warned = true;
                using (var resultForm = new OccupancyResultForm(disk, result, accelerated: false))
                {
                    resultForm.ShowDialog(owner);
                }

                string occupied = Locale.T("occupy.gate.start.body", DiskText.Short(disk.DiskNumber));
                if (!AskStartContinue(owner, Locale.T("occupy.gate.title.occupied"), occupied))
                    return OccupancyGateResult.Aborted;
            }

            return warned ? OccupancyGateResult.Continued : OccupancyGateResult.Passed;
        }

        /// <summary>
        /// **校验 L2 前**的占用检查：校验同样会把源盘脱机（`PhysicalDiskHandle.OpenReadOnly`），
        /// 所以与启动走同一套检查，只是文案与按钮换成“校验”口径。
        /// </summary>
        public static OccupancyGateResult RunForVerify(IWin32Window owner, PhysicalDiskInfo disk)
        {
            OccupancyScanResult? result = ScanOne(owner, disk, 0, 0);
            if (result == null || result.Cancelled) return OccupancyGateResult.Aborted;

            if (!string.IsNullOrEmpty(result.Error))
            {
                string body = Locale.T("occupy.gate.scanFail.verify", result.Error);
                return AskVerifyContinue(owner, Locale.T("occupy.gate.scanFail.title"), body)
                    ? OccupancyGateResult.Continued
                    : OccupancyGateResult.Aborted;
            }

            if (result.Processes.Count == 0) return OccupancyGateResult.Passed;

            using (var resultForm = new OccupancyResultForm(disk, result, accelerated: false))
            {
                resultForm.ShowDialog(owner);
            }

            string occupied = Locale.T("occupy.gate.verify.body", DiskText.Short(disk.DiskNumber));
            return AskVerifyContinue(owner, Locale.T("occupy.gate.title.occupied"), occupied)
                ? OccupancyGateResult.Continued
                : OccupancyGateResult.Aborted;
        }

        /// <summary>弹一次进度框并等它结束；取消 / 拿不到结果时返回 null。</summary>
        private static OccupancyScanResult? ScanOne(
            IWin32Window owner, PhysicalDiskInfo disk, int index, int total)
        {
            using var progress = new OccupancyProgressForm(disk, index, total);
            if (progress.ShowDialog(owner) != DialogResult.OK) return null;
            return progress.Result;
        }

        /// <summary>
        /// 启动口径的分支框。返回 true = 继续启动（下标 0）；false = 终止（下标 1，也是关闭 / Esc 的默认）。
        /// </summary>
        private static bool AskStartContinue(IWin32Window owner, string title, string body)
        {
            return Dialogs.Choose(owner, title, body,
                Locale.T("occupy.gate.continueStart"),
                Locale.T("occupy.gate.abortStart")) == 0;
        }

        /// <summary>校验口径的分支框。返回 true = 继续校验；false = 终止。</summary>
        private static bool AskVerifyContinue(IWin32Window owner, string title, string body)
        {
            return Dialogs.Choose(owner, title, body,
                Locale.T("occupy.gate.continueVerify"),
                Locale.T("occupy.gate.abortVerify")) == 0;
        }
    }

    /// <summary>
    /// 扫描进度框：形态照 L2 校验窗口（FixedDialog、跑马灯、一行说明、一行计数、一个取消）。
    /// 扫描在后台线程跑，取消粒度由引擎控制（每 256 个句柄 / 每个进程检查一次）。
    /// </summary>
    internal sealed class OccupancyProgressForm : ThemedForm
    {
        private readonly ProgressBar _bar = new();
        private readonly Label _lblInfo = new();
        private readonly Label _lblCount = new();
        private readonly Button _btnCancel = new();
        private readonly PhysicalDiskInfo _disk;
        private readonly CancellationTokenSource _cts = new();

        private bool _running = true;

        /// <summary>扫描结果；取消或窗口提前关闭时为 null</summary>
        public OccupancyScanResult? Result { get; private set; }

        /// <summary>菜单「占用查询」用：单盘，说明行保持原样。</summary>
        public OccupancyProgressForm(PhysicalDiskInfo disk) : this(disk, 0, 0)
        {
        }

        /// <summary>
        /// <paramref name="index"/> / <paramref name="total"/> 都 &gt; 0 时是**多盘模式**
        /// （启动前逐盘检查）：说明行顶部多一行“第几块盘”，其余控件与单盘完全一致。
        /// </summary>
        public OccupancyProgressForm(PhysicalDiskInfo disk, int index, int total)
        {
            _disk = disk;

            Text = Locale.T("occupy.title.progress");
            ClientSize = new Size(470, 170);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = _btnCancel;

            _lblInfo.AutoSize = false;
            _lblInfo.Location = new Point(12, 10);
            _lblInfo.Size = new Size(446, 34);
            _lblInfo.Text = total > 1
                ? Locale.T("occupy.progress.multi", index, total, DiskText.Short(disk.DiskNumber))
                  + "\n" + Locale.T("occupy.progress.info")
                : Locale.T("occupy.progress.info");

            _bar.Location = new Point(12, 48);
            _bar.Size = new Size(446, 18);
            // 用真实进度：总进程数在枚举句柄表后就能确定，比跑马灯更能说明"还在动"
            _bar.Style = ProgressBarStyle.Continuous;
            _bar.Minimum = 0;
            _bar.Maximum = 1;

            _lblCount.AutoSize = false;
            _lblCount.Location = new Point(12, 74);
            _lblCount.Size = new Size(446, 24);
            _lblCount.Text = Locale.T("occupy.progress.count", 0, 0);

            _btnCancel.Location = new Point(348, 130);
            _btnCancel.Size = new Size(110, 28);
            _btnCancel.Text = Locale.T("common.cancel");
            _btnCancel.UseVisualStyleBackColor = true;
            _btnCancel.Click += (_, _) => Cancel();

            Controls.Add(_lblInfo);
            Controls.Add(_bar);
            Controls.Add(_lblCount);
            Controls.Add(_btnCancel);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _ = Task.Run(RunScan);
        }

        private void RunScan()
        {
            OccupancyScanResult result;
            try
            {
                result = VolumeOccupancy.Scan(_disk.DiskNumber, ReportProgress, _cts.Token);
            }
            catch (Exception ex)
            {
                result = new OccupancyScanResult { Error = ex.Message };
            }

            if (IsDisposed || _cts.IsCancellationRequested) return;

            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    Result = result;
                    DialogResult = DialogResult.OK;
                    Close();
                });
            }
            catch (InvalidOperationException)
            {
                // 窗口已经关掉：结果丢弃，用户按了取消
            }
        }

        private void ReportProgress(int scanned, int total)
        {
            if (IsDisposed || !_running) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) return;
                    _bar.Maximum = Math.Max(1, total);
                    _bar.Value = Math.Min(scanned, _bar.Maximum);
                    _lblCount.Text = Locale.T("occupy.progress.count", scanned, total);
                });
            }
            catch (InvalidOperationException)
            {
                // 正在关闭，忽略这一拍
            }
        }

        private void Cancel()
        {
            if (!_running) return;
            _running = false;
            _cts.Cancel();
            _lblCount.Text = Locale.T("occupy.progress.cancelling");
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _running = false;
            _cts.Cancel();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _cts.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 结果框：**上（信息）→ 中（程序列表）→ 下（详细信息）** 三段。
    ///
    /// * 顶部只读框：查到占用时第一行用告警红写「❌硬盘被占用：…」，其余是盘名、检查时间与汇总；
    ///   颜色源与选盘框的报错行完全一致（<see cref="ThemeManager.Palette"/> 的 <c>DangerText</c>）；
    /// * 中间列表：一行一个程序（样式同选盘框），默认选中第一行；
    /// * 底部只读框：显示当前选中程序的 PID / 可执行文件路径与它占用的文件列表；
    ///   没有选中任何一项时提示“选择列表的一项以查看详细信息”。
    ///
    /// 空结果 / 扫描失败时列表与底部框隐藏，顶部框拉满整个窗口（避免出现空列表和空详情）。
    /// </summary>
    internal sealed class OccupancyResultForm : ThemedForm
    {
        private const int MarginSize = 12;
        private const int ContentWidth = 616;

        private readonly Button _btnClose = new();
        private readonly RichTextBox _top = new();
        private readonly ListBox _list = new();
        private readonly RichTextBox _detail = new();
        private readonly PhysicalDiskInfo _disk;
        private readonly OccupancyScanResult _result;
        private readonly bool _accelerated;
        private readonly List<OccupantInfo> _occupants = new();

        public OccupancyResultForm(PhysicalDiskInfo disk, OccupancyScanResult result, bool accelerated)
        {
            _disk = disk;
            _result = result;
            _accelerated = accelerated;

            Text = Locale.T("occupy.title.result");
            ClientSize = new Size(640, 560);
            MinimumSize = new Size(520, 420);   // 再小就会让列表与底部框重叠
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = _btnClose;

            // 顶部：盘信息 + 汇总（红头也在这里）
            _top.Location = new Point(MarginSize, MarginSize);
            _top.Size = new Size(ContentWidth, 104);
            _top.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            ConfigureBox(_top);

            // 中间：程序列表（与选盘框同一个主题样式）
            _list.Location = new Point(MarginSize, 124);
            _list.Size = new Size(ContentWidth, 150);
            _list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = true;
            _list.SelectedIndexChanged += (_, _) => ShowSelectedOccupant();

            // 底部：选中程序的详细信息（窗口拉高时它跟着变高）
            _detail.Location = new Point(MarginSize, 282);
            _detail.Size = new Size(ContentWidth, 226);
            _detail.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            ConfigureBox(_detail);

            _btnClose.Size = new Size(110, 28);
            _btnClose.Text = Locale.T("common.close");
            _btnClose.UseVisualStyleBackColor = true;
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClose.Location = new Point(ClientSize.Width - MarginSize - _btnClose.Width,
                ClientSize.Height - MarginSize - _btnClose.Height);
            _btnClose.Click += (_, _) => Close();

            Controls.Add(_top);
            Controls.Add(_list);
            Controls.Add(_detail);
            Controls.Add(_btnClose);
        }

        private static void ConfigureBox(RichTextBox box)
        {
            box.Multiline = true;
            box.ReadOnly = true;
            box.ScrollBars = RichTextBoxScrollBars.Both;
            box.WordWrap = false;
            box.TabStop = false;          // 打开时不抢焦点，也就不会出现"整段被选中"的样子
            box.BackColor = ThemeManager.Palette.InputBg;
            box.ForeColor = ThemeManager.InputTextColor;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // 深色下 ThemeManager 会把输入框强制成 FixedSingle 边框，而 RichEdit 画这条边用的是
            // 系统“浅色应用”的颜色 ⇒ 深色底上一圈白线。与选盘框一样按回 None；
            // 顺序要紧：改 BorderStyle 会重建句柄，必须排在深色滚动条之前。
            _top.BorderStyle = BorderStyle.None;
            _detail.BorderStyle = BorderStyle.None;
            ThemeManager.ApplyDarkScrollbar(_top);
            ThemeManager.ApplyDarkScrollbar(_detail);

            RenderTop();

            if (!IsOccupied(_result))
            {
                // 没有程序可列：隐藏列表与详情，顶部框拉满（与旧的单文本框观感一致）
                _list.Visible = false;
                _detail.Visible = false;
                _top.Anchor = AnchorStyles.Top | AnchorStyles.Bottom
                              | AnchorStyles.Left | AnchorStyles.Right;
                _top.Size = new Size(ContentWidth, _btnClose.Top - MarginSize - _top.Top);
                _top.Select(0, 0);
                ActiveControl = _btnClose;
                return;
            }

            FillList();
            _top.Select(0, 0);
            ActiveControl = _list;   // 让用户可以直接用方向键切换程序
        }

        /// <summary>顶部只读框：红头（仅查到占用时）+ 盘名 / 时间 / 汇总。</summary>
        private void RenderTop()
        {
            _top.Clear();

            if (IsOccupied(_result))
            {
                _top.SelectionColor = ThemeManager.Palette.DangerText;
                _top.AppendText(Locale.T("occupy.result.occupied", DiskText.Short(_disk.DiskNumber)));
                _top.AppendText("\n\n");
            }

            _top.SelectionColor = ThemeManager.InputTextColor;
            _top.AppendText(RenderSummary(_disk, _result, _accelerated));

            _top.SelectionStart = 0;
            _top.SelectionLength = 0;
        }

        /// <summary>把程序按句柄数从多到少填进列表，并默认选中第一行。</summary>
        private void FillList()
        {
            _occupants.AddRange(_result.Processes);
            _occupants.Sort((a, b) =>
            {
                int byCount = b.HandleCount.CompareTo(a.HandleCount);
                return byCount != 0
                    ? byCount
                    : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
            });

            foreach (OccupantInfo occupant in _occupants)
                _list.Items.Add(Locale.T("occupy.result.item", occupant.Name, occupant.HandleCount));

            if (_list.Items.Count > 0) _list.SelectedIndex = 0;
            else ShowSelectedOccupant();
        }

        /// <summary>切换列表项时刷新底部框；没有选中项时显示提示语。</summary>
        private void ShowSelectedOccupant()
        {
            _detail.Clear();
            _detail.SelectionColor = ThemeManager.InputTextColor;

            int index = _list.SelectedIndex;
            if (index < 0 || index >= _occupants.Count)
            {
                _detail.AppendText(Locale.T("occupy.result.selectHint"));
                return;
            }

            _detail.AppendText(RenderOccupant(_occupants[index]));
            _detail.SelectionStart = 0;
            _detail.SelectionLength = 0;
        }

        /// <summary>“查到占用”的判据：既没有出错、也没有取消，且至少有一个程序。</summary>
        private static bool IsOccupied(OccupancyScanResult result)
            => string.IsNullOrEmpty(result.Error) && !result.Cancelled && result.Processes.Count > 0;

        /// <summary>
        /// 顶部框的正文：盘名、检查时间与汇总（占用总数 / 无法读取的数量）。
        /// **不再把每个程序的明细塞进来**——那些在列表与底部框里。
        /// 空结果与出错也如实写出来。
        /// </summary>
        private static string RenderSummary(PhysicalDiskInfo disk, OccupancyScanResult result, bool accelerated)
        {
            var sb = new StringBuilder();
            sb.AppendLine(DiskText.Full(disk, accelerating: accelerated));
            sb.AppendLine(Locale.T("occupy.result.time", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")));
            sb.AppendLine();

            if (!string.IsNullOrEmpty(result.Error))
            {
                sb.AppendLine(Locale.T("occupy.result.error", result.Error));
                return sb.ToString();
            }

            if (result.Cancelled)
            {
                sb.AppendLine(Locale.T("occupy.result.cancelled"));
                return sb.ToString();
            }

            if (result.Processes.Count == 0)
            {
                sb.AppendLine(Locale.T("occupy.result.none"));
                sb.AppendLine();
                sb.AppendLine(Locale.T("occupy.result.noneHint"));
                return sb.ToString();
            }

            int totalHandles = 0;
            foreach (OccupantInfo occupant in result.Processes) totalHandles += occupant.HandleCount;

            sb.AppendLine(Locale.T("occupy.result.summary", result.Processes.Count, totalHandles));
            if (result.UnreadableHandles > 0 || result.UnreadableProcesses > 0)
                sb.AppendLine(Locale.T("occupy.result.unreadable", result.UnreadableHandles, result.UnreadableProcesses));

            return sb.ToString();
        }

        /// <summary>
        /// 底部框的内容：首行说明该程序占了多少句柄、进程信息；下面按路径去重列出它打开了哪些
        /// 文件/目录（树形符号 ├ / └），末尾一项用 └。
        /// </summary>
        private static string RenderOccupant(OccupantInfo occupant)
        {
            var sb = new StringBuilder();
            occupant.Pids.Sort();
            string pids = string.Join(", ", occupant.Pids);

            sb.AppendLine(string.IsNullOrEmpty(occupant.ExePath)
                ? Locale.T("occupy.result.processLineNoExe", occupant.Name, occupant.HandleCount, pids)
                : Locale.T("occupy.result.processLine", occupant.Name, occupant.HandleCount, occupant.ExePath, pids));

            var items = new List<KeyValuePair<string, int>>(occupant.Paths);
            items.Sort((a, b) =>
            {
                int byCount = b.Value.CompareTo(a.Value);
                return byCount != 0 ? byCount : StringComparer.OrdinalIgnoreCase.Compare(a.Key, b.Key);
            });

            for (int i = 0; i < items.Count; i++)
            {
                string branch = i == items.Count - 1 ? "    └ " : "    ├ ";
                sb.AppendLine(branch + Locale.T("occupy.result.path", items[i].Key, items[i].Value));
            }

            return sb.ToString();
        }
    }
}
