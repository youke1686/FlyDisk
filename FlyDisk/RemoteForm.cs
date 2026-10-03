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
using System.Windows.Forms;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;
using Timer = System.Windows.Forms.Timer;

namespace FlyDisk
{
    /// <summary>
    /// 「远程」对话框（菜单「远程」）：**配对关系与监听的唯一管理入口**。
    /// 完整设计见 后续待办.md 第一节的「UI 与配置的定稿」。
    ///
    /// 两种形态（同一屏，两组控件就地切换可见性）：
    /// · **空闲**：单选「开放远程服务 / 配对远程服务」+ 随选择切换的「监听端口」或「对端地址」
    ///   + 不加密警告 + `[配对] [取消]`
    /// · **非空闲**：状态行 + `[断开] [关闭]`（断开只在**加速停止**时可用——它是连接层的动作，
    ///   不能从正在跑的数据层脚下抽走连接）
    ///
    /// 本窗体**一次性**：操作做完就关，长期状态由主界面那四处可变文本显示。
    /// 状态与动作全部由主窗体提供（它才是远程会话的持有者）。
    /// </summary>
    public sealed class RemoteForm : ThemedForm, ILocalizable
    {
        private readonly DiskConfig _config;

        /// <summary>控件登记表（见 <see cref="ILocalizable"/>）：静态文本键 → 控件</summary>
        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        private readonly Func<bool> _isActive;
        private readonly Func<string> _statusText;
        private readonly Func<bool> _canDisconnect;
        private readonly Action<int> _onOpenServer;
        private readonly Action<string> _onPair;
        private readonly Action _onDisconnect;

        private readonly Timer _refresh = new() { Interval = 1000 };

        private readonly Panel _idlePanel = new();
        private readonly Panel _activePanel = new();
        private readonly RadioButton _rdoOpen = new();
        private readonly RadioButton _rdoPair = new();
        private readonly Label _lblPort = new();
        private readonly Label _lblAddress = new();
        private readonly NumericUpDown _numPort = new();
        private readonly TextBox _txtAddress = new();
        private readonly Label _lblWarning = new();
        private readonly Button _btnAction = new();
        private readonly Button _btnCancel = new();
        private readonly Label _lblActiveStatus = new();
        private readonly Button _btnDisconnect = new();
        private readonly Button _btnClose = new();

        public RemoteForm(DiskConfig config,
                          Func<bool> isActive,
                          Func<string> statusText,
                          Func<bool> canDisconnect,
                          Action<int> onOpenServer,
                          Action<string> onPair,
                          Action onDisconnect)
        {
            _config = config;
            _isActive = isActive;
            _statusText = statusText;
            _canDisconnect = canDisconnect;
            _onOpenServer = onOpenServer;
            _onPair = onPair;
            _onDisconnect = onDisconnect;

            Text = "远程";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(470, 250);

            BuildIdlePanel();
            BuildActivePanel();
            Controls.Add(_idlePanel);
            Controls.Add(_activePanel);

            // 静态文本登记（Build* 里的中文初值随后被覆盖）；按钮动作文案与状态行走 ApplyDynamicText
            Bind("remote.rdo.open", _rdoOpen);
            Bind("remote.rdo.pair", _rdoPair);
            Bind("remote.label.port", _lblPort);
            Bind("remote.label.address", _lblAddress);
            Bind("remote.warning", _lblWarning);
            Bind("remote.button.disconnect", _btnDisconnect);
            Bind("common.cancel", _btnCancel);
            Bind("common.close", _btnClose);

            _refresh.Tick += (s, e) => ApplyState();
            _refresh.Start();

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
        /// 动态文本：窗体标题；「动作」按钮随单选在「开放 / 配对」间切换；非空闲时状态行走主窗体回调。
        /// 这些都随语言变化，登记表覆盖不到（见 <see cref="ILocalizable"/>）。
        /// </summary>
        public void ApplyDynamicText()
        {
            Text = Locale.T("remote.title");
            ApplyState();
        }

        private void BuildIdlePanel()
        {
            _idlePanel.Dock = DockStyle.Fill;

            _rdoOpen.Text = "开放远程服务（本机监听，等对方来配对）";
            _rdoOpen.Location = new Point(14, 14);
            _rdoOpen.Size = new Size(430, 22);
            _rdoOpen.Checked = true;
            _rdoOpen.CheckedChanged += (s, e) => UpdateIdleFields();

            _rdoPair.Text = "配对远程服务（连接到对方）";
            _rdoPair.Location = new Point(14, 40);
            _rdoPair.Size = new Size(430, 22);

            _lblPort.Text = "监听端口";
            _lblPort.Location = new Point(34, 76);
            _lblPort.Size = new Size(70, 20);

            _numPort.Location = new Point(112, 73);
            _numPort.Size = new Size(90, 25);
            _numPort.Minimum = 1;
            _numPort.Maximum = 65535;
            _numPort.Value = _config.RemoteListenPort is >= 1 and <= 65535 ? _config.RemoteListenPort : 10808;

            _lblAddress.Text = "对端地址";
            _lblAddress.Location = new Point(34, 76);
            _lblAddress.Size = new Size(70, 20);

            _txtAddress.Location = new Point(112, 73);
            _txtAddress.Size = new Size(210, 25);
            _txtAddress.Text = _config.RemotePeerAddress;

            _lblWarning.ForeColor = Color.FromArgb(160, 80, 0);
            _lblWarning.Location = new Point(14, 110);
            _lblWarning.Size = new Size(440, 60);
            _lblWarning.Text = "⚠ 不加密：任何能连到本机的人都能**列出**磁盘清单；\r\n" +
                               "真正使用某块盘仍需你在弹窗上逐次确认。";

            _btnAction.Text = "开放远程服务";
            _btnAction.Location = new Point(280, 208);
            _btnAction.Size = new Size(120, 28);
            _btnAction.Click += (s, e) => SubmitIdle();

            _btnCancel.Text = "取消";
            _btnCancel.Location = new Point(406, 208);
            _btnCancel.Size = new Size(52, 28);
            _btnCancel.DialogResult = DialogResult.Cancel;

            _idlePanel.Controls.AddRange(new Control[]
            {
                _rdoOpen, _rdoPair, _lblPort, _numPort, _lblAddress, _txtAddress,
                _lblWarning, _btnAction, _btnCancel
            });
        }

        private void BuildActivePanel()
        {
            _activePanel.Dock = DockStyle.Fill;
            _activePanel.Visible = false;

            _lblActiveStatus.Location = new Point(14, 20);
            _lblActiveStatus.Size = new Size(440, 150);

            _btnDisconnect.Text = "断开";
            _btnDisconnect.Location = new Point(280, 208);
            _btnDisconnect.Size = new Size(90, 28);
            _btnDisconnect.Click += (s, e) =>
            {
                _onDisconnect();
                ApplyState();
            };

            _btnClose.Text = "关闭";
            _btnClose.Location = new Point(376, 208);
            _btnClose.Size = new Size(82, 28);
            _btnClose.DialogResult = DialogResult.OK;

            _activePanel.Controls.AddRange(new Control[] { _lblActiveStatus, _btnDisconnect, _btnClose });
        }

        private void UpdateIdleFields()
        {
            bool open = _rdoOpen.Checked;
            _lblPort.Visible = open;
            _numPort.Visible = open;
            _lblAddress.Visible = !open;
            _txtAddress.Visible = !open;
            _btnAction.Text = Locale.T(open ? "remote.button.open" : "remote.button.pair");
        }

        /// <summary>按"是否已激活"切换两组控件（就地切换，不新增控件）</summary>
        private void ApplyState()
        {
            bool active = _isActive();
            _idlePanel.Visible = !active;
            _activePanel.Visible = active;

            if (active)
            {
                _lblActiveStatus.Text = _statusText();
                _btnDisconnect.Enabled = _canDisconnect();
            }
            else
            {
                UpdateIdleFields();
            }
        }

        private void SubmitIdle()
        {
            if (_rdoOpen.Checked)
            {
                _onOpenServer((int)_numPort.Value);
            }
            else
            {
                string address = _txtAddress.Text.Trim();
                if (address.Length == 0)
                {
                    MessageBox.Show(Locale.T("remote.msg.needAddress"), Locale.T("dialog.tip"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                _config.RemotePeerAddress = address;   // 记住地址（只是输入框的记忆，不等于"保持配对"）
                _onPair(address);
            }
            ApplyState();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _refresh.Stop();
            _refresh.Dispose();
            base.OnFormClosed(e);
        }
    }
}
