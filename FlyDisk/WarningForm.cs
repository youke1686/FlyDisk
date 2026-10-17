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

using System.Drawing;
using System.Windows.Forms;
using FlyDisk.Localization;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// **首次打开时的使用前确认**（`Program.Main` 在语言确定后、旧 L2 检查前弹出）。
    ///
    /// 文案是帮助文档「使用前请知悉」的软件内副本；**只有点「同意并继续」才会进入主界面**，
    /// 「不同意，退出」/ Esc / Alt+F4 一律直接退出进程。同意后由调用方写入
    /// <c>DiskConfig.WarningAccepted</c>，之后不再显示。
    /// </summary>
    public sealed class WarningForm : ThemedForm
    {
        private readonly RichTextBox _body = new();
        private readonly Button _agree = new();
        private readonly Button _decline = new();

        public WarningForm()
        {
            Text = Locale.T("warn.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            // 不给右上角关闭按钮：把选择收敛成下面两个按钮（Esc 仍等于「不同意」）。
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 400);
            ShowInTaskbar = true;

            _body.Location = new Point(16, 16);
            _body.Size = new Size(528, 320);
            _body.ReadOnly = true;
            _body.BorderStyle = BorderStyle.None;
            _body.ScrollBars = RichTextBoxScrollBars.Vertical;
            _body.WordWrap = true;
            _body.TabStop = false;
            _body.Text = Locale.T("warn.body");

            _agree.Text = Locale.T("warn.agree");
            _agree.Location = new Point(330, 352);
            _agree.Size = new Size(110, 30);
            _agree.DialogResult = DialogResult.OK;

            _decline.Text = Locale.T("warn.decline");
            _decline.Location = new Point(446, 352);
            _decline.Size = new Size(98, 30);
            _decline.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _body, _agree, _decline });
            AcceptButton = _agree;
            CancelButton = _decline;
        }
    }
}
