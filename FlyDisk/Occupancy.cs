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
    /// <summary>
    /// 「占用查询」的界面部分：进度框 + 结果框 + 串联逻辑。
    /// 选盘框是通用的 <see cref="PickerForm"/>（onlineOnly + 确定按钮文案「开始检查」），
    /// 本文件不重复造一个。
    ///
    /// 三框串联的语义（见 docs/停机改造与占用查询_施工计划.md §9）：
    /// * 进度框可随时取消，取消后**不弹结果框**；
    /// * 结果框无论有没有占用都打开，空结果也要给一句解释；
    /// * 扫描出错也打开结果框，正文如实写原因，不静默。
    /// </summary>
    internal static class OccupancyQuery
    {
        /// <summary>对已选定的盘执行一次完整查询（进度 + 结果）。取消不显示结果。</summary>
        public static void Run(IWin32Window owner, PhysicalDiskInfo disk)
        {
            using var progress = new OccupancyProgressForm(disk);
            if (progress.ShowDialog(owner) != DialogResult.OK) return;

            OccupancyScanResult? result = progress.Result;
            if (result == null) return;   // 取消 / 出错到拿不到结果

            using var resultForm = new OccupancyResultForm(disk, result);
            resultForm.ShowDialog(owner);
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

        public OccupancyProgressForm(PhysicalDiskInfo disk)
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
            _lblInfo.Text = Locale.T("occupy.progress.info");

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
    /// 结果框：整段只读文本，可直接选中复制（与检查器详细页同一形态），不设"复制"按钮。
    /// </summary>
    internal sealed class OccupancyResultForm : ThemedForm
    {
        private readonly Button _btnClose = new();
        private readonly TextBox _text = new();

        public OccupancyResultForm(PhysicalDiskInfo disk, OccupancyScanResult result)
        {
            Text = Locale.T("occupy.title.result");
            ClientSize = new Size(620, 420);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            CancelButton = _btnClose;

            _text.Location = new Point(12, 12);
            _text.Size = new Size(596, 356);
            _text.Multiline = true;
            _text.ReadOnly = true;
            _text.ScrollBars = ScrollBars.Both;
            _text.WordWrap = false;
            _text.TabStop = false;          // 打开时不抢焦点，也就不会出现"整段被选中"的样子
            _text.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _text.BackColor = ThemeManager.Palette.InputBg;
            _text.ForeColor = ThemeManager.InputTextColor;
            _text.Text = Render(disk, result);

            _btnClose.Size = new Size(110, 28);
            _btnClose.Text = Locale.T("common.close");
            _btnClose.UseVisualStyleBackColor = true;
            _btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            _btnClose.Location = new Point(ClientSize.Width - 12 - _btnClose.Width,
                ClientSize.Height - 12 - _btnClose.Height);
            _btnClose.Click += (_, _) => Close();

            Controls.Add(_text);
            Controls.Add(_btnClose);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 深色下把原生滚动条也切成深色（系统默认给浅色应用画白条）
            ThemeManager.ApplyDarkScrollbar(_text);
            // 不要默认全选：光标停在开头，焦点给"关闭"
            _text.Select(0, 0);
            ActiveControl = _btnClose;
        }

        /// <summary>把扫描结果渲染成整段文本。空结果与出错都如实写出来。</summary>
        private static string Render(PhysicalDiskInfo disk, OccupancyScanResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Locale.T("occupy.result.header", disk.DiskNumber, disk.Model, disk.SizeText));
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

            int totalHandles = 0;
            foreach (OccupantInfo occupant in result.Processes) totalHandles += occupant.HandleCount;

            if (result.Processes.Count == 0)
            {
                sb.AppendLine(Locale.T("occupy.result.none"));
                sb.AppendLine();
                sb.AppendLine(Locale.T("occupy.result.noneHint"));
            }
            else
            {
                sb.AppendLine(Locale.T("occupy.result.summary",
                    result.Processes.Count, totalHandles));
                if (result.UnreadableHandles > 0 || result.UnreadableProcesses > 0)
                    sb.AppendLine(Locale.T("occupy.result.unreadable", result.UnreadableHandles, result.UnreadableProcesses));

                sb.AppendLine();
                foreach (OccupantInfo occupant in result.Processes)
                    AppendOccupant(sb, occupant);
            }

            return sb.ToString();
        }

        /// <summary>
        /// 一个程序一段：首行说明它占了多少句柄、进程信息；下面按路径去重列出它打开了哪些
        /// 文件/目录（树形符号 ├ / └），末尾一项用 └。
        /// </summary>
        private static void AppendOccupant(StringBuilder sb, OccupantInfo occupant)
        {
            occupant.Pids.Sort();
            string pids = string.Join(", ", occupant.Pids);

            sb.AppendLine();
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
        }
    }
}
