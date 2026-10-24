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
using System.Drawing;
using System.Windows.Forms;
using FlyDisk.Localization;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// 全程序统一的弹窗外壳（见 docs/停机改造与占用查询_施工计划.md §8）。
    ///
    /// 五类模板对应用户要做的五种决定，避免同一个"操作没做成"在不同窗口里长出不同形状：
    /// * <see cref="Notice"/> —— T1 告知：操作**没有执行**（前提不满足），Warning + OK；
    /// * <see cref="Fail"/>   —— T2 失败：已尝试但未完成，Error + OK；
    /// * <see cref="Confirm"/>—— T3 确认：即将执行动作，Warning + YesNo，默认"否"；
    /// * <see cref="Proceed"/>—— T3 的"继续/取消"变体，Warning + OKCancel，默认"取消"；
    /// * <see cref="Choose"/> —— T4 分支：两个或三个选项**各有后果**，自绘按钮。
    ///
    /// 正文的排版约定（由调用方拼，见 <see cref="Body"/>）：结论一句 → 空行 → 明细 → 空行 →
    /// 以"请"打头的收尾句；后果段落以 ⚠️ 打头。
    ///
    /// **分支框的三条硬规则**（T4）：
    /// 1. 不设默认按钮，Enter 不触发任何分支；
    /// 2. 选项按"代价从低到高"从左到右排列，**最右固定是"什么都不做"**；
    /// 3. 关闭（X）与 Esc 都等于"什么都不做"，调用方永远拿得到一个确定的取消下标。
    /// </summary>
    internal static class Dialogs
    {
        #region T1 / T2 / T3

        /// <summary>T1 告知：操作没有执行。用户不需要决策，只需要知道原因与出口。</summary>
        public static void Notice(IWin32Window? owner, string title, string body)
        {
            Show(owner, body, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        /// <summary>T2 失败：操作已尝试但未完成。</summary>
        public static void Fail(IWin32Window? owner, string title, string body)
        {
            Show(owner, body, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        /// <summary>T3 确认：即将执行动作。<c>true</c> = 用户确认执行。</summary>
        public static bool Confirm(IWin32Window? owner, string title, string body)
        {
            return Show(owner, body, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        /// <summary>T3 的"继续 / 取消"变体（实验性、风险提示）。<c>true</c> = 继续。</summary>
        public static bool Proceed(IWin32Window? owner, string title, string body)
        {
            return Show(owner, body, title, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
        }

        /// <summary>
        /// 统一出口：owner 为空时退回无 owner 的 <c>MessageBox</c>（只有崩溃兜底会走到）。
        /// 确认类一律把默认按钮压在保守项上。
        /// </summary>
        private static DialogResult Show(IWin32Window? owner, string body, string title,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            // 确认框把默认焦点压在"否 / 取消"上：误按回车不该替用户做决定
            MessageBoxDefaultButton defaultButton = buttons == MessageBoxButtons.YesNo
                ? MessageBoxDefaultButton.Button2
                : buttons == MessageBoxButtons.OKCancel
                    ? MessageBoxDefaultButton.Button2
                    : MessageBoxDefaultButton.Button1;

            return owner == null
                ? MessageBox.Show(body, title, buttons, icon, defaultButton)
                : MessageBox.Show(owner, body, title, buttons, icon, defaultButton);
        }

        #endregion

        #region T4 分支

        /// <summary>
        /// 分支选择框：<paramref name="choices"/> 按"代价从低到高"排列，**最后一项必须是
        /// "什么都不做"**（确认、取消、中止启动、退出程序之类）。返回用户选中的下标；
        /// 点 X / 按 Esc / 无法显示时一律返回最后一项的下标。
        /// </summary>
        public static int Choose(IWin32Window? owner, string title, string body, params string[] choices)
        {
            if (choices.Length < 2)
                throw new ArgumentException("分支框至少需要两个选项（最后一项是\"什么都不做\"）。", nameof(choices));

            using var form = new ChoiceForm(title, body, choices);
            if (owner != null) form.ShowDialog(owner);
            else form.ShowDialog();
            return form.ChosenIndex;
        }

        /// <summary>
        /// 自绘的分支框。正文用只读文本框（可选中复制，长原因不会撑爆窗口），
        /// 按钮从右往左排：最右是"什么都不做"。
        /// </summary>
        private sealed class ChoiceForm : ThemedForm
        {
            /// <summary>用户选择的下标；默认就是最后一项（关闭 = 什么都不做）</summary>
            public int ChosenIndex { get; private set; }

            private readonly TextBox _body = new();
            private Button? _cancelButton;

            public ChoiceForm(string title, string body, string[] choices)
            {
                ChosenIndex = choices.Length - 1;

                Text = title;
                ClientSize = new Size(540, 300);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;

                _body.Location = new Point(14, 14);
                _body.Size = new Size(512, 214);
                _body.Multiline = true;
                _body.ReadOnly = true;
                _body.WordWrap = true;
                _body.ScrollBars = ScrollBars.Vertical;
                _body.BorderStyle = BorderStyle.None;
                _body.TabStop = false;
                _body.BackColor = ThemeManager.Palette.InputBg;
                _body.ForeColor = ThemeManager.InputTextColor;
                _body.Text = body;
                Controls.Add(_body);

                // 从右往左排：最后一项固定在右下角
                const int gap = 8;
                const int width = 112;
                const int height = 30;
                int right = ClientSize.Width - 14;
                for (int i = choices.Length - 1; i >= 0; i--)
                {
                    var button = new Button
                    {
                        Text = choices[i],
                        Size = new Size(width, height),
                        Location = new Point(right - width, 252),
                        UseVisualStyleBackColor = true,
                    };
                    int index = i;
                    button.Click += (_, _) =>
                    {
                        ChosenIndex = index;
                        DialogResult = DialogResult.OK;
                        Close();
                    };
                    Controls.Add(button);
                    if (i == choices.Length - 1) _cancelButton = button;
                    right -= width + gap;
                }

                // 不设 AcceptButton（Enter 不选任何分支）；Esc 等价于"什么都不做"
                CancelButton = _cancelButton;
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                ThemeManager.ApplyDarkScrollbar(_body);
                // 不要默认全选：光标停在开头，焦点落在"什么都不做"那个按钮上
                _body.Select(0, 0);
                if (_cancelButton != null) ActiveControl = _cancelButton;
            }
        }

        #endregion

        #region 正文拼装

        /// <summary>
        /// 拼正文：各段之间空一行。段内换行由调用方负责（明细行直接每行一条）。
        /// </summary>
        public static string Body(params string[] blocks) =>
            string.Join(Environment.NewLine + Environment.NewLine, blocks);

        /// <summary>
        /// 明细行：<c>磁盘 0（脱机，型号，111.8 GiB）：原因</c>。
        /// 盘名由 <see cref="DiskText"/> 统一生成，本方法只负责拼分隔符与原因。
        /// </summary>
        public static string DiskLine(string disk, string reason) =>
            Locale.T("dialog.diskLine", disk, reason);

        #endregion
    }
}
