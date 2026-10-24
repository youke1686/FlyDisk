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
using System.Linq;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// 设置对话框：**基础（左）/ 高级（右）两栏**。
    ///
    /// - 基础：L1（启用 / 两条水位）、L2 摘要 + 「L2 管理」入口、预计额外内存；
    /// - 高级：iSCSI 端点与自动登录、L1 调参、L2 调参、远程。
    ///
    /// **L2 的盘与容量不在这里配**：每块盘各自的 L2 卷 / 上限统一在「L2 管理」里维护（阶段 3/4）。
    /// 配置在**启动那一刻**被读取，所以主界面在运行时会锁住入口。
    /// </summary>
    public partial class Settings : ThemedForm, ILocalizable
    {
        private readonly DiskConfig _config;

        /// <summary>控件登记表（见 <see cref="ILocalizable"/>）：静态文本键 → 控件</summary>
        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        public Settings(DiskConfig config)
        {
            InitializeComponent();
            _config = config;

            // 静态文本全部登记在这里（Designer 里的中文初值随后被覆盖）；动态文本走 ApplyDynamicText
            Bind("settings.basicHeader", lblBasicHeader);
            Bind("settings.advancedHeader", lblAdvancedHeader);
            Bind("settings.enableMemoryCache", chkEnableCache);
            Bind("settings.eviction", lblEviction);
            Bind("settings.stop", lblStop);
            Bind("settings.save", btnSave);
            Bind("settings.docs", btnDocs);
            Bind("settings.groupDisk", groupBoxDisk);
            Bind("settings.listenAddress", label2);
            Bind("settings.listenPort", lblListenPort);
            Bind("settings.autoIscsiLogin", chkAutoIscsiLogin);
            Bind("settings.groupCache", groupBoxCache);
            Bind("settings.groupSsd", groupBoxSsd);
            Bind("settings.l2Manage", btnL2Manage);
            Bind("settings.groupAdvancedL1", groupBoxAdvancedL1);
            Bind("settings.evictionWindow", lblEvictionWindow);
            Bind("settings.evictionGrace", lblEvictionGrace);
            Bind("settings.evictionBatch", lblEvictionBatch);
            Bind("settings.groupAdvancedL2", groupBoxAdvancedL2);
            Bind("settings.ssdConservative", lblSsdConservative);
            Bind("settings.ghostFraction", lblGhostFraction);
            Bind("settings.groupRemote", groupBoxRemote);
            Bind("settings.remotePort", lblRemotePort);
            Bind("settings.remoteAddress", lblRemoteAddress);

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

        /// <summary>动态文本：窗体标题不是控件，单独刷（见 <see cref="ILocalizable"/>）</summary>
        public void ApplyDynamicText()
        {
            Text = Locale.T("settings.title");
            // 底部估算与 L2 摘要都含数字，不在 TextBindings 里；切语言时跟着重刷
            RefreshL2Summary();
            UpdateMemoryEstimate();
        }

        private void Settings_Load(object sender, EventArgs e)
        {
            txtListenAddress.Text = _config.ListenAddress;
            numListenPort.Value = ClampDecimal(_config.ListenPort, numListenPort.Minimum, numListenPort.Maximum);
            chkAutoIscsiLogin.Checked = _config.EnableAutoIscsiLogin;

            chkEnableCache.Checked = _config.EnableMemoryCache;

            // 手改过 config.json 时阈值可能越界 / 两条水位互相颠倒：夹到滑块范围内并保证"淘汰线 < 停止线"
            int evictionPercent = Math.Clamp((int)Math.Round(_config.EvictionThreshold * 100),
                trackEviction.Minimum, trackEviction.Maximum - 1);
            int stopPercent = Math.Clamp((int)Math.Round(_config.StopCachingThreshold * 100),
                trackStop.Minimum + 1, trackStop.Maximum);
            if (evictionPercent >= stopPercent) evictionPercent = stopPercent - 1;

            trackEviction.Value = evictionPercent;
            trackStop.Value = stopPercent;

            numEvictionWindow.Value = ClampDecimal(_config.EvictionWindowSeconds,
                numEvictionWindow.Minimum, numEvictionWindow.Maximum);
            numEvictionGrace.Value = ClampDecimal(_config.EvictionGraceSeconds,
                numEvictionGrace.Minimum, numEvictionGrace.Maximum);
            numEvictionBatch.Value = ClampDecimal(_config.EvictionBatchBlocks,
                numEvictionBatch.Minimum, numEvictionBatch.Maximum);

            int conservative = (int)Math.Round(_config.SsdConservativeThreshold * 100);
            trackSsdConservative.Value = Math.Clamp(conservative,
                trackSsdConservative.Minimum, trackSsdConservative.Maximum);
            numGhostFraction.Value = ClampDecimal((decimal)_config.SsdCacheGhostFraction,
                numGhostFraction.Minimum, numGhostFraction.Maximum);

            numRemotePort.Value = ClampDecimal(_config.RemoteListenPort, numRemotePort.Minimum, numRemotePort.Maximum);
            txtRemoteAddress.Text = _config.RemotePeerAddress;

            RefreshL2Summary();
            UpdateValueLabels();
        }

        private static decimal ClampDecimal(decimal value, decimal min, decimal max)
            => value < min ? min : (value > max ? max : value);

        private void trackEviction_Scroll(object sender, EventArgs e)
        {
            if (trackEviction.Value >= trackStop.Value)
            {
                trackEviction.Value = trackStop.Value - 1;
            }
            UpdateValueLabels();
        }

        private void trackStop_Scroll(object sender, EventArgs e)
        {
            if (trackStop.Value <= trackEviction.Value)
            {
                trackStop.Value = trackEviction.Value + 1;
            }
            UpdateValueLabels();
        }

        private void trackSsdConservative_Scroll(object sender, EventArgs e) => UpdateValueLabels();

        private void chkCacheToggle_CheckedChanged(object sender, EventArgs e) => UpdateMemoryEstimate();

        private void UpdateValueLabels()
        {
            lblEvictionVal.Text = $"{trackEviction.Value}%";
            lblStopVal.Text = $"{trackStop.Value}%";
            lblSsdConservativeVal.Text = $"{trackSsdConservative.Value}%";

            // L1 的估算取决于「开始淘汰阈值」，所以这两个滑块一动就要重算
            UpdateMemoryEstimate();
        }

        /// <summary>L2 摘要：有几块盘配了 L2、合计上限多少（具体行在「L2 管理」里改）</summary>
        private void RefreshL2Summary()
        {
            List<L2DiskConfig> configured = _config.L2Disks
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.CacheVolume))
                .ToList();
            long total = configured.Sum(e => e.MaxBytes);
            lblL2Summary.Text = configured.Count == 0
                ? Locale.T("settings.l2SummaryNone")
                : Locale.T("settings.l2Summary", configured.Count, FormatCapacity(total));
        }

        /// <summary>
        /// 刷新窗口底部的「预计额外内存」提示。
        ///
        /// 口径（与引擎同源，见 Engine/SystemMemory.cs）：
        /// - **L2 索引**：按所有已配置 L2 的容量之和折算，约
        ///   <see cref="SsdCacheService.MetadataBytesPerBlock"/> 字节/块（块 = <see cref="ServiceConstants.BlockSize"/>）；
        /// - **L1 索引**：按"当前内存到开始淘汰水位的差距"估；读不到系统内存时该项显示"—"。
        /// </summary>
        private void UpdateMemoryEstimate()
        {
            long l2Bytes = _config.L2Disks
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.CacheVolume))
                .Sum(e => e.MaxBytes / ServiceConstants.BlockSize * SsdCacheService.MetadataBytesPerBlock);

            long l1Bytes = 0;
            bool l1Known = true;
            if (chkEnableCache.Checked)
            {
                if (SystemMemory.TryRead(out MemoryStatus mem))
                {
                    long total = (long)mem.TotalPhysicalBytes;
                    long used = (long)(mem.MemoryLoadPercent / 100.0 * total);
                    long headroom = (long)(trackEviction.Value / 100.0 * total) - used;
                    if (headroom > 0)
                    {
                        l1Bytes = headroom / ServiceConstants.BlockSize * CacheService.MetadataBytesPerBlock;
                    }
                }
                else
                {
                    l1Known = false;
                }
            }

            string l1Text = l1Known ? FormatCapacity(l1Bytes) : "—";
            string totalText = l1Known ? FormatCapacity(l2Bytes + l1Bytes) : "≥ " + FormatCapacity(l2Bytes);
            lblMemEstimate.Text = string.Format(Locale.T("settings.memEstimate"),
                totalText, FormatCapacity(l2Bytes), l1Text);
        }

        /// <summary>容量按 1024 进制显示（MiB/GiB）</summary>
        private static string FormatCapacity(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return string.Format("{0:0.##} GiB", bytes / 1024.0 / 1024 / 1024);
            }
            return string.Format("{0:0.#} MiB", bytes / 1024.0 / 1024);
        }

        /// <summary>
        /// 「L2 管理」：维护**每块盘**的 L2 卷与上限（见 <see cref="L2Manage"/>）。
        /// 与 L2 是否正在运行无关；窗口内点「确定」才会改到配置对象上，窗口「取消」不生效。
        /// </summary>
        private void btnL2Manage_Click(object sender, EventArgs e)
        {
            if (!L2Manage.Run(this, _config)) return;
            RefreshL2Summary();
            UpdateMemoryEstimate();
        }

        /// <summary>打开随程序落盘的帮助文档（不在就先补一份）。</summary>
        private void btnDocs_Click(object sender, EventArgs e)
        {
            if (HelpDocument.Open()) return;

            Dialogs.Fail(this, Locale.T("dialog.fail.openDocs"),
                string.Format(Locale.T("settings.msg.docsOpenFailed"), HelpDocument.FilePath));
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            _config.ListenAddress = string.IsNullOrWhiteSpace(txtListenAddress.Text)
                ? "127.0.0.1"
                : txtListenAddress.Text.Trim();
            _config.ListenPort = (int)numListenPort.Value;
            _config.EnableAutoIscsiLogin = chkAutoIscsiLogin.Checked;

            _config.EnableMemoryCache = chkEnableCache.Checked;
            _config.EvictionThreshold = trackEviction.Value / 100.0;
            _config.StopCachingThreshold = trackStop.Value / 100.0;

            _config.EvictionWindowSeconds = (int)numEvictionWindow.Value;
            _config.EvictionGraceSeconds = (int)numEvictionGrace.Value;
            _config.EvictionBatchBlocks = (int)numEvictionBatch.Value;

            _config.SsdConservativeThreshold = trackSsdConservative.Value / 100.0;
            _config.SsdCacheGhostFraction = (double)numGhostFraction.Value;

            _config.RemoteListenPort = (int)numRemotePort.Value;
            _config.RemotePeerAddress = txtRemoteAddress.Text.Trim();

            // L2 的每盘条目由「L2 管理」直接改在 _config.L2Disks 上；这里不覆盖。
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
