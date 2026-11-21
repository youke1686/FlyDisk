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
    /// **风险绕过开关的确认框**：只有手动把 `config.json` 的 <c>AllowRiskBypass</c> 改成 true
    /// 才会在启动时出现。与 <see cref="WarningForm"/> 同样式，但两个选项是：
    /// 「确认了解风险」（左）/「恢复关闭」（右，关闭与 Esc 同义）。
    /// </summary>
    public sealed class RiskBypassForm : ThemedForm
    {
        private readonly RichTextBox _body = new();
        private readonly Button _confirm = new();
        private readonly Button _revert = new();

        public RiskBypassForm()
        {
            Text = Locale.T("riskBypass.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 340);
            ShowInTaskbar = true;

            _body.Location = new Point(16, 16);
            _body.Size = new Size(528, 260);
            _body.ReadOnly = true;
            _body.BorderStyle = BorderStyle.None;
            _body.ScrollBars = RichTextBoxScrollBars.Vertical;
            _body.WordWrap = true;
            _body.TabStop = false;
            _body.Text = Locale.T("riskBypass.body");

            _confirm.Text = Locale.T("riskBypass.confirm");
            _confirm.Location = new Point(250, 292);
            _confirm.Size = new Size(180, 30);
            _confirm.DialogResult = DialogResult.OK;

            _revert.Text = Locale.T("riskBypass.revert");
            _revert.Location = new Point(440, 292);
            _revert.Size = new Size(104, 30);
            _revert.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _body, _confirm, _revert });
            AcceptButton = _confirm;
            CancelButton = _revert;
        }
    }
}
