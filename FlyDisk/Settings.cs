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
using System.Linq;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// 设置对话框：iSCSI 监听端口 + 两级缓存参数。
    ///
    /// **2026-10-01 的改动：这里不再选目标盘**。选盘改成"每次点「启动加速」时弹一次"
    /// （见 后续待办.md 第一节的"选盘时机"），目的是防止"改过配置后隔几天才启动、忘了当时选的是哪块"。
    /// 于是本对话框**不再分形态**：远程形态下同样用它配缓存参数（客户端照样要装缓存）。
    ///
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
            Bind("settings.enableMemoryCache", chkEnableCache);
            Bind("settings.eviction", lblEviction);
            Bind("settings.stop", lblStop);
            Bind("settings.save", btnSave);
            Bind("settings.docs", btnDocs);
            Bind("settings.groupDisk", groupBoxDisk);
            Bind("settings.listenPort", label2);
            Bind("settings.groupCache", groupBoxCache);
            Bind("settings.groupSsd", groupBoxSsd);
            Bind("settings.enableSsd", chkEnableSsdCache);
            Bind("settings.ssdDrive", lblSsdDrive);
            Bind("settings.ssdSize", lblSsdSize);
            Bind("settings.ssdConservative", lblSsdConservative);

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
            // 底部那条估算含数字，不在 TextBindings 里；但它的措辞是本地化的，切语言时要跟着重刷
            UpdateMemoryEstimate();
        }

        private void Settings_Load(object sender, EventArgs e)
        {
            numListenPort.Value = ClampDecimal(_config.ListenPort, numListenPort.Minimum, numListenPort.Maximum);

            chkEnableCache.Checked = _config.EnableMemoryCache;
            trackEviction.Value = (int)(_config.EvictionThreshold * 100);
            trackStop.Value = (int)(_config.StopCachingThreshold * 100);

            LoadSsdSettings();
            UpdateValueLabels();
            UpdateMemoryEstimate();
        }

        #region L2 缓存盘

        private void LoadSsdSettings()
        {
            chkEnableSsdCache.Checked = _config.EnableSsdCache;
            numSsdCacheGb.Value = ClampDecimal(_config.SsdCacheMaxBytes / (1024m * 1024 * 1024),
                numSsdCacheGb.Minimum, numSsdCacheGb.Maximum);

            // 保守线存的是占用率；这里只把滑块拨到配置值（真夹取在引擎侧，越界会落盘提醒）
            int conservative = (int)Math.Round(_config.SsdConservativeThreshold * 100);
            trackSsdConservative.Value = Math.Clamp(conservative,
                trackSsdConservative.Minimum, trackSsdConservative.Maximum);

            LoadSsdCacheDriveList();
        }

        /// <summary>
        /// 列出可选的 L2 缓存盘。
        ///
        /// **不再排除"被加速的那块盘"**：目标盘改成启动时才选，这里根本不知道会是哪一块。
        /// 那条"缓存目录不能与被加速盘同盘"的校验由引擎在**启动时**执行
        /// （见 <see cref="TargetService"/> 的启动校验 ⑥），所以这里不拦、也不该拦。
        /// </summary>
        private void LoadSsdCacheDriveList()
        {
            cmbSsdCacheDrive.Items.Clear();
            foreach (string root in DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.Name.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
            {
                cmbSsdCacheDrive.Items.Add(root);
            }

            // 配置里已选的缓存盘（可能已不存在）临时补进列表，保证能如实展示当前配置
            string? configured = string.IsNullOrWhiteSpace(_config.SsdCachePath)
                ? null
                : (Path.GetPathRoot(_config.SsdCachePath) ?? string.Empty).TrimEnd('\\');
            if (!string.IsNullOrEmpty(configured))
            {
                if (!cmbSsdCacheDrive.Items.Contains(configured))
                {
                    cmbSsdCacheDrive.Items.Add(configured);
                }
                cmbSsdCacheDrive.SelectedItem = configured;
            }
            if (cmbSsdCacheDrive.SelectedItem == null && cmbSsdCacheDrive.Items.Count > 0)
            {
                cmbSsdCacheDrive.SelectedIndex = 0;
            }
        }

        #endregion

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

        private void numSsdCacheGb_ValueChanged(object sender, EventArgs e) => UpdateMemoryEstimate();

        private void chkCacheToggle_CheckedChanged(object sender, EventArgs e) => UpdateMemoryEstimate();

        private void UpdateValueLabels()
        {
            lblEvictionVal.Text = $"{trackEviction.Value}%";
            lblStopVal.Text = $"{trackStop.Value}%";
            lblSsdConservativeVal.Text = $"{trackSsdConservative.Value}%";

            // L1 的估算取决于「开始淘汰阈值」，所以这两个滑块一动就要重算
            UpdateMemoryEstimate();
        }

        /// <summary>
        /// 刷新窗口底部的「预计额外内存」提示。
        ///
        /// 口径（与引擎同源，见 Engine/SystemMemory.cs）：
        /// - **L2 索引**：槽位/索引结构常驻内存，约 <see cref="SsdCacheService.MetadataBytesPerBlock"/> 字节/块
        ///   （块 = <see cref="ServiceConstants.BlockSize"/>），所以"每 1 GiB 容量 ≈ 15 MiB 内存"；
        /// - **L1 索引**：L1 是动态池、没有固定上限，"预期能攒到的块数"按"**当前内存到开始淘汰水位的差距**"
        ///   （(开始淘汰阈值 − 当前系统内存使用率) × 物理内存）估；再按**与 L2 同一口径**只折算"缓存以外"的
        ///   管理内存（块数 × <see cref="CacheService.MetadataBytesPerBlock"/>），缓存数据本身不计入。
        ///   读不到系统内存时该项显示"—"，总量退化为"≥ L2 部分"（宁可少报，不多报）。
        /// </summary>
        private void UpdateMemoryEstimate()
        {
            long l2Bytes = chkEnableSsdCache.Checked
                ? (long)numSsdCacheGb.Value * 1024 * 1024 * 1024 / ServiceConstants.BlockSize
                    * SsdCacheService.MetadataBytesPerBlock
                : 0;

            long l1Bytes = 0;
            bool l1Known = true;
            if (chkEnableCache.Checked)
            {
                if (SystemMemory.TryRead(out MemoryStatus mem))
                {
                    long total = (long)mem.TotalPhysicalBytes;
                    long used = (long)(mem.MemoryLoadPercent / 100.0 * total);
                    long headroom = (long)(trackEviction.Value / 100.0 * total) - used;
                    // 口径同 L2：只算"缓存数据以外"的管理结构 ⇒ L1 预期块数 × 每块管理内存
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

        /// <summary>容量按 1024 进制显示（MiB/GiB），与界面上的「容量预算(GiB)」同一口径</summary>
        private static string FormatCapacity(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return string.Format("{0:0.##} GiB", bytes / 1024.0 / 1024 / 1024);
            }
            return string.Format("{0:0.#} MiB", bytes / 1024.0 / 1024);
        }

        /// <summary>打开随程序落盘的帮助文档（不在就先补一份）。</summary>
        private void btnDocs_Click(object sender, EventArgs e)
        {
            if (HelpDocument.Open()) return;

            MessageBox.Show(string.Format(Locale.T("settings.msg.docsOpenFailed"), HelpDocument.FilePath),
                Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            bool ssdEnabled = chkEnableSsdCache.Checked;
            string? ssdDrive = cmbSsdCacheDrive.SelectedItem as string;
            if (ssdEnabled && string.IsNullOrEmpty(ssdDrive))
            {
                MessageBox.Show(Locale.T("settings.msg.selectSsdDrive"), Locale.T("dialog.tip"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _config.ListenPort = (int)numListenPort.Value;

            _config.EnableMemoryCache = chkEnableCache.Checked;
            _config.EvictionThreshold = trackEviction.Value / 100.0;
            _config.StopCachingThreshold = trackStop.Value / 100.0;

            _config.EnableSsdCache = ssdEnabled;
            _config.SsdCachePath = string.IsNullOrEmpty(ssdDrive)
                ? string.Empty
                : Path.Combine(ssdDrive + Path.DirectorySeparatorChar, ServiceConstants.SsdCacheDirectoryName);
            _config.SsdCacheMaxBytes = (long)numSsdCacheGb.Value * 1024 * 1024 * 1024;
            _config.SsdConservativeThreshold = trackSsdConservative.Value / 100.0;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
