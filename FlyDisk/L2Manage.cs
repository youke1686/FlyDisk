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
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
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
    /// **「L2 管理」**（阶段 4）：一张表维护**每块盘**的 L2 配置与缓存。
    ///
    /// 列：盘 | L2 卷 | 上限 | 实占 | 启用 | 状态。
    /// - 新建 = 末尾加空行（卷与上限都留空）；盘与卷都是行内下拉，上限是行内输入；
    /// - 「启用」是行内复选框：取消 = 本次只用 L1，缓存文件与账本保留；
    /// - 删除 = 移除配置条目**并删除缓存文件**（确认框明确说明会删文件）；
    /// - 「清空缓存内容」= 删除容器/索引、实占归 0，配置保留；
    /// - 未识别行只读（盘=未知、状态=未识别）；
    /// - 关窗「确定」保存、「取消」不生效。
    /// </summary>
    internal static class L2Manage
    {
        public static bool Run(IWin32Window owner, DiskConfig config)
        {
            using var form = new L2ManageForm(config);
            return form.ShowDialog(owner) == DialogResult.OK;
        }

        /// <summary>
        /// **清空一份缓存**（删掉容器与索引，再删掉整个目录）。
        ///
        /// 安全闸：**复用引擎与校验器共同持有的那把单实例锁**（两者都以 <c>FileShare.None</c> 打开它）。
        /// 能拿到锁 = 没人在用（没在加速、没在跑校验、没有别的实例），此时删才安全；
        /// 拿不到就如实告诉用户"正在使用"，不做任何猜测。
        /// </summary>
        public static bool TryClear(L2CacheInfo cache, out string error)
        {
            error = string.Empty;

            try
            {
                using (new FileStream(cache.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    // 闸已拿下：此刻没人在用，可以安全删
                    if (File.Exists(cache.ContainerPath)) File.Delete(cache.ContainerPath);
                    if (File.Exists(cache.IndexPath)) File.Delete(cache.IndexPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = Locale.T("l2m.inUse", ex.Message);
                return false;
            }
            catch (Exception ex)
            {
                error = Locale.T("l2m.clearFailed", ex.Message);
                return false;
            }

            // 数据文件已删；顺手把只剩锁文件的目录也清掉。
            // **只能非递归删**：旧布局根目录（`<卷根>\FlyDisk.Cache`）下面可能还放着其它盘的子目录，
            // 递归删会把别人的缓存一起端掉；非递归删在"目录非空"时自然失败，正合我们的要求。
            try
            {
                if (File.Exists(cache.LockPath)) File.Delete(cache.LockPath);
                Directory.Delete(cache.Directory);
            }
            catch (Exception ex) { LogService.DebugFile($"管理 L2：删除空目录 {cache.Directory} 失败（无害）：{ex.Message}"); }

            LogService.DebugFile($"管理 L2：已清空 L2 缓存目录 {cache.Directory}");
            return true;
        }

        /// <summary>
        /// 软件启动时处理**旧布局**（`&lt;卷根&gt;\FlyDisk.Cache\cache.dat|index.bin`）。
        ///
        /// 新版**不再支持旧布局、也不再迁移**：发现了就询问用户是否删除；
        /// **「否」= 退出软件**（返回 false），避免带着一份无法解释的旧缓存继续跑。
        /// 删除失败只报错、**不拦启动**——旧文件已不再被任何代码读取，不应把用户卡死在启动阶段。
        /// </summary>
        /// <returns>true = 可以继续启动；false = 用户选择退出</returns>
        public static bool PromptRemoveLegacyCaches(DiskConfig config)
        {
            List<string> dirs;
            try
            {
                dirs = SsdCacheService.FindLegacyCacheDirectories();
            }
            catch (Exception ex)
            {
                // 探测本身失败：按"没有旧缓存"继续（不能让一个只读探测拦住整个程序）
                LogService.DebugFile($"旧 L2 缓存探测失败（按没有处理）：{ex.Message}");
                return true;
            }

            if (dirs.Count == 0) return true;

            string list = string.Join(Environment.NewLine, dirs);
            int legacyChoice = Dialogs.Choose(null, Locale.T("dialog.legacyL2Found"),
                Locale.T("main.msg.legacyL2Found", list),
                Locale.T("l2.choose.deleteAndStart"), Locale.T("l2.choose.exit"));

            // 关闭 / Esc 与「退出程序」同义：两个分支都会改变状态，最保守的是"一个文件都不动"
            if (legacyChoice != 0) return false;

            var failed = new List<string>();
            foreach (string dir in dirs)
            {
                L2CacheInfo cache = SsdCacheService.DescribeL2Cache(dir, config);
                if (!TryClear(cache, out string error))
                {
                    failed.Add($"{dir}{Environment.NewLine}  {error}");
                }
            }

            if (failed.Count > 0)
            {
                LogService.DebugFile($"删除旧 L2 缓存失败：{string.Join(" | ", failed)}");
                Dialogs.Fail(null, Locale.T("dialog.fail.deleteLegacyL2"),
                    Locale.T("main.msg.legacyL2DeleteFailed",
                        string.Join(Environment.NewLine + Environment.NewLine, failed)));
            }
            else
            {
                LogService.DebugFile($"已删除旧 L2 缓存：{list}");
            }

            return true;
        }
    }

    /// <summary>「L2 管理」窗口：单窗口配置表（见 <see cref="L2Manage"/> 类注释）。</summary>
    internal sealed class L2ManageForm : ThemedForm, ILocalizable
    {
        private const decimal GiB = 1024m * 1024 * 1024;
        private const long DefaultMaxBytes = 8L * 1024 * 1024 * 1024;

        private readonly DiskConfig _config;
        private readonly List<string> _volumes = new();
        private readonly List<DiskChoice> _diskChoices = new();
        private readonly List<string> _pendingDeletes = new();
        private readonly List<RowView> _rowViews = new();
        private RowView? _currentView;

        private readonly Panel _rowsPanel = new();
        private readonly FlowLayoutPanel _rows = new();
        private readonly Label _hdrDisk = new();
        private readonly Label _hdrVolume = new();
        private readonly Label _hdrLimit = new();
        private readonly Label _hdrUsed = new();
        private readonly Label _hdrEnabled = new();
        private readonly Label _hdrStatus = new();

        private readonly Button _add = new();
        private readonly Button _delete = new();
        private readonly Button _clear = new();
        private readonly Button _open = new();
        private readonly Button _ok = new();
        private readonly Button _cancel = new();

        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        private sealed class Row
        {
            public string DiskIdentity = string.Empty;
            public long OwnerHash;
            public string CacheVolume = string.Empty;
            public long MaxBytes;
            public string CacheDirectory = string.Empty;
            public long ActualBytes;
            public bool IsRecognized = true;
            public bool IsConfig;
            public bool Enabled = true;
            public bool Initialized;
            public string Status = string.Empty;
            public string StatusEnabled = string.Empty;
            public string StatusDisabled = string.Empty;
        }

        private sealed class DiskChoice
        {
            public string Identity = string.Empty;
            public long OwnerHash;
            public PhysicalDiskInfo? Info;
            public string Text = string.Empty;
            public override string ToString() => Text;
        }

        /// <summary>一行对应的标准控件（不再用 DataGridView：ComboBox/TextBox 的行为可控）。</summary>
        private sealed class RowView
        {
            public Row Data = new();
            public Panel Container = new();
            public Panel Accent = new();
            public ComboBox Disk = new();
            public Label DiskText = new();
            public Label DiskPrompt = new();
            public ComboBox Volume = new();
            public Label VolumeText = new();
            public TextBox Limit = new();
            public Label Used = new();
            public CheckBox EnableBox = new();
            public Label Status = new();
            public string LastValidLimit = string.Empty;
        }

        public L2ManageForm(DiskConfig config)
        {
            _config = config;
            BuildChoices();

            Text = Locale.T("l2m.title");
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(860, 460);

            BuildLayout();
            BuildButtons();
            FillRows();

            Bind("l2m.title", this);   // 占位：窗口标题由 ApplyDynamicText 刷
            Bind("l2m.add", _add);
            Bind("l2m.delete", _delete);
            Bind("l2m.clearContent", _clear);
            Bind("l2m.openDir", _open);
            Bind("l2m.ok", _ok);
            Bind("l2m.cancel", _cancel);

            LocalizationManager.Apply(this);
            if (_rowViews.Count > 0) SelectView(_rowViews[0]);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // 基类会重新上色（提示标签的底色 / 行左侧色条都会被刷掉），这里补回来
            foreach (RowView view in _rowViews)
            {
                StylePrompt(view);
                UpdateDiskPrompt(view);
            }
            SelectView(_currentView);
        }

        private void Bind(string key, params Control[] controls)
        {
            if (!TextBindings.TryGetValue(key, out List<Control>? list))
            {
                list = new List<Control>();
                TextBindings[key] = list;
            }
            list.AddRange(controls);
        }

        public void ApplyDynamicText()
        {
            Text = Locale.T("l2m.title");
            _hdrDisk.Text = Locale.T("l2m.col.disk");
            _hdrVolume.Text = Locale.T("l2m.col.volume");
            _hdrLimit.Text = Locale.T("l2m.col.limit");
            _hdrUsed.Text = Locale.T("l2m.col.used");
            _hdrEnabled.Text = Locale.T("l2m.col.enabled");
            _hdrStatus.Text = Locale.T("l2m.col.status");

            foreach (RowView view in _rowViews)
            {
                bool unknown = !view.Data.IsRecognized || IsScannedUnmapped(view.Data);
                DiskChoice? selected = _diskChoices.Find(c =>
                    string.Equals(c.Identity, view.Data.DiskIdentity, StringComparison.Ordinal));
                view.Disk.BeginUpdate();
                view.Disk.Items.Clear();
                if (unknown)
                {
                    view.Disk.Items.Add(Locale.T("l2m.unknownDisk"));
                    view.Disk.SelectedIndex = 0;
                }
                else
                {
                    foreach (DiskChoice choice in _diskChoices) view.Disk.Items.Add(choice);
                    view.Disk.SelectedIndex = selected != null ? view.Disk.Items.IndexOf(selected) : -1;
                }
                view.Disk.EndUpdate();
                view.DiskPrompt.Text = Locale.T("l2m.pickDisk");
                view.DiskText.Text = unknown ? Locale.T("l2m.unknownDisk")
                                             : (selected?.Text ?? Locale.T("l2m.unknownDisk"));
                view.VolumeText.Text = view.Data.CacheVolume;
                UpdateDiskPrompt(view);
            }
        }

        private void BuildChoices()
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    _volumes.Add(drive.Name.TrimEnd('\\'));
                }
                catch { /* 单个卷枚举失败不影响其它卷 */ }
            }

            try
            {
                foreach (PhysicalDiskInfo disk in PhysicalDiskHandle.Enumerate())
                {
                    string identity = PhysicalDiskHandle.DescribeIdentity(disk);
                    if (identity.Length == 0) continue;
                    _diskChoices.Add(new DiskChoice
                    {
                        Identity = identity,
                        OwnerHash = SsdCacheService.ComputeIdentity(BlockSourceInfo.FromDisk(disk, disk.IsOnline)),
                        Info = disk,
                        Text = Locale.T("insp.b.diskItem", disk.DiskNumber,
                            disk.Model.Length > 0 ? disk.Model : Locale.T("selectDisk.modelUnknown")),
                    });
                }
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"管理 L2：枚举物理盘失败：{ex.Message}");
            }

            // 已配置但当前没接上的盘：也放进下拉，保证那一行能正常显示
            foreach (L2DiskConfig entry in _config.L2Disks)
            {
                if (entry.DiskIdentity.Length == 0) continue;
                if (_diskChoices.Exists(c => string.Equals(c.Identity, entry.DiskIdentity, StringComparison.Ordinal))) continue;
                _diskChoices.Add(new DiskChoice
                {
                    Identity = entry.DiskIdentity,
                    OwnerHash = entry.OwnerHash,
                    Text = Locale.T("l2m.diskNotConnected", entry.DiskIdentity),
                });
            }
        }

        private void BuildLayout()
        {
            var header = new Panel { Location = new Point(12, 12), Size = new Size(836, 24) };
            ConfigureHeader(_hdrDisk, 8, 270, "l2m.col.disk");
            ConfigureHeader(_hdrVolume, 282, 106, "l2m.col.volume");
            ConfigureHeader(_hdrLimit, 392, 88, "l2m.col.limit");
            ConfigureHeader(_hdrUsed, 484, 86, "l2m.col.used");
            ConfigureHeader(_hdrEnabled, 574, 36, "l2m.col.enabled");
            ConfigureHeader(_hdrStatus, 614, 192, "l2m.col.status");
            header.Controls.AddRange(new Control[] { _hdrDisk, _hdrVolume, _hdrLimit, _hdrUsed, _hdrEnabled, _hdrStatus });
            Controls.Add(header);

            _rowsPanel.Location = new Point(12, 38);
            _rowsPanel.Size = new Size(836, 332);
            _rowsPanel.AutoScroll = true;

            _rows.Location = new Point(0, 0);
            _rows.AutoSize = true;
            _rows.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _rows.FlowDirection = FlowDirection.TopDown;
            _rows.WrapContents = false;

            _rowsPanel.Controls.Add(_rows);
            _rowsPanel.ClientSizeChanged += (s, e) => ResizeRows();
            Controls.Add(_rowsPanel);
        }

        private static void ConfigureHeader(Label label, int x, int width, string key)
        {
            label.Location = new Point(x, 2);
            label.Size = new Size(width, 20);
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.Text = Locale.T(key);
        }

        private void BuildButtons()
        {
            void Place(Button button, int x, int width)
            {
                button.Location = new Point(x, 375);
                button.Size = new Size(width, 30);
                button.UseVisualStyleBackColor = true;
                Controls.Add(button);
            }

            Place(_add, 12, 90);
            Place(_delete, 110, 90);
            Place(_clear, 208, 130);
            Place(_open, 346, 100);
            Place(_cancel, 646, 90);
            Place(_ok, 744, 90);

            _add.Click += (s, e) => AddNewRow();
            _delete.Click += (s, e) => DeleteCurrentRow();
            _clear.Click += (s, e) => ClearCurrentRow();
            _open.Click += (s, e) => OpenCurrentRowDirectory();
            _cancel.DialogResult = DialogResult.Cancel;
            _ok.Click += (s, e) => SaveAndClose();
            CancelButton = _cancel;
            AcceptButton = _ok;
        }

        private void FillRows()
        {
            List<L2CacheInfo> caches;
            try
            {
                caches = SsdCacheService.DescribeAllL2Caches(_config);
            }
            catch (Exception ex)
            {
                Dialogs.Fail(this, Locale.T("dialog.fail.scanL2"), Locale.T("l2m.scanFailed", ex.Message));
                caches = new List<L2CacheInfo>();
            }

            var usedCacheDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ① 配置条目：每块盘一行（未接上的盘也显示，卷/上限仍可编辑）
            foreach (L2DiskConfig entry in _config.L2Disks)
            {
                DiskChoice? choice = _diskChoices.Find(c => string.Equals(c.Identity, entry.DiskIdentity, StringComparison.Ordinal));

                // 手工改过配置时可能没有散列：盘接着就补算，之后保存会写回
                long ownerHash = entry.OwnerHash;
                if (ownerHash == 0 && choice?.Info != null)
                {
                    ownerHash = choice.OwnerHash;
                }

                string dir = ownerHash != 0 && entry.CacheVolume.Length > 0
                    ? SsdCacheService.GetDiskCacheDirectory(entry.CacheVolume, ownerHash)
                    : string.Empty;
                L2CacheInfo? cache = caches.Find(c => c.Recognized && c.OwnerIdentity == ownerHash);
                // 只有"缓存就在配置的这个卷上"才算已初始化：卷不匹配时允许用户修正配置。
                // 但无论匹配与否都把它标记为"已认领"，避免同一块盘出现两行（配置行 + 孤儿行）。
                bool initialized = cache != null && SameVolume(cache.DriveRoot, entry.CacheVolume);
                if (cache != null) usedCacheDirs.Add(cache.Directory);

                bool complete = !string.IsNullOrWhiteSpace(entry.CacheVolume) && entry.MaxBytes > 0;
                bool needsVerify = initialized && cache!.LastShutdownClean != true;
                string enabledStatus;
                if (!complete) enabledStatus = Locale.T("l2m.statusIncomplete");
                else if (!initialized) enabledStatus = Locale.T(choice?.Info == null ? "l2m.statusNotConnected" : "l2m.statusNoCache");
                else if (needsVerify) enabledStatus = Locale.T("l2m.statusNeedsVerify");
                else enabledStatus = Locale.T("l2m.statusConfigured");

                string disabledStatus = !complete
                    ? Locale.T("l2m.statusIncomplete")
                    : needsVerify
                        ? Locale.T("l2m.statusNeedsVerify")
                        : Locale.T("l2m.statusDisabled");

                AddRowView(new Row
                {
                    DiskIdentity = entry.DiskIdentity,
                    OwnerHash = ownerHash,
                    CacheVolume = entry.CacheVolume,
                    MaxBytes = entry.MaxBytes,
                    CacheDirectory = initialized ? cache!.Directory : dir,
                    ActualBytes = initialized ? cache!.TotalBytes : 0,
                    IsConfig = true,
                    Enabled = entry.Enabled,
                    Initialized = initialized,
                    StatusEnabled = enabledStatus,
                    StatusDisabled = disabledStatus,
                    Status = entry.Enabled ? enabledStatus : disabledStatus,
                });
            }

            // ② 扫描到、但没被配置认领的目录
            foreach (L2CacheInfo cache in caches)
            {
                if (usedCacheDirs.Contains(cache.Directory)) continue;
                if (cache.Recognized)
                {
                    string identity = FindIdentity(cache.OwnerIdentity);
                    AddRowView(new Row
                    {
                        DiskIdentity = identity,
                        OwnerHash = cache.OwnerIdentity,
                        CacheVolume = cache.DriveRoot.TrimEnd('\\'),
                        MaxBytes = cache.StoredSlots > 0 ? (long)cache.StoredSlots * ServiceConstants.BlockSize : DefaultMaxBytes,
                        CacheDirectory = cache.Directory,
                        ActualBytes = cache.TotalBytes,
                        Enabled = identity.Length > 0,
                        Initialized = true,
                        StatusEnabled = Locale.T("l2m.statusUnconfigured"),
                        StatusDisabled = Locale.T("l2m.statusUnconfigured"),
                        Status = Locale.T("l2m.statusUnconfigured"),
                    });
                }
                else
                {
                    AddRowView(new Row
                    {
                        CacheDirectory = cache.Directory,
                        ActualBytes = cache.TotalBytes,
                        CacheVolume = cache.DriveRoot.TrimEnd('\\'),
                        IsRecognized = false,
                        Enabled = false,
                        Initialized = true,
                        StatusEnabled = Locale.T("l2m.statusUnrecognized"),
                        StatusDisabled = Locale.T("l2m.statusUnrecognized"),
                        Status = Locale.T("l2m.statusUnrecognized"),
                    });
                }
            }

            ResizeRows();
        }

        private string FindIdentity(long ownerHash)
        {
            DiskChoice? choice = _diskChoices.Find(c => c.OwnerHash == ownerHash && c.Identity.Length > 0);
            return choice?.Identity ?? string.Empty;
        }

        /// <summary>缓存实际所在卷与配置卷是否是同一个（"D:" / "D:\" 视为同一个）</summary>
        private static bool SameVolume(string driveRoot, string cacheVolume)
            => cacheVolume.Length > 0 &&
               string.Equals(driveRoot.TrimEnd('\\'), cacheVolume.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        private static bool IsScannedUnmapped(Row row)
            => !row.IsConfig && row.CacheDirectory.Length > 0 && row.DiskIdentity.Length == 0;

        private int RowWidth => Math.Max(600, _rowsPanel.ClientSize.Width - 4);

        private void ResizeRows()
        {
            int width = RowWidth;
            foreach (RowView view in _rowViews) view.Container.Width = width;
        }

        private RowView AddRowView(Row row)
        {
            var view = new RowView { Data = row };

            view.Container = new Panel
            {
                Height = 30,
                Width = RowWidth,
                Margin = new Padding(0, 0, 0, 4),
            };
            view.Accent = new Panel { Location = new Point(0, 0), Size = new Size(3, 30) };
            view.Disk = new ComboBox
            {
                Location = new Point(8, 3),
                Size = new Size(270, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
            };
            view.DiskPrompt = new Label
            {
                Location = new Point(12, 6),
                Size = new Size(238, 18),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = Locale.T("l2m.pickDisk"),
                Cursor = Cursors.Hand,
            };
            view.DiskText = new Label
            {
                Location = new Point(8, 3),
                Size = new Size(270, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            view.Volume = new ComboBox
            {
                Location = new Point(282, 3),
                Size = new Size(106, 24),
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
            };
            view.VolumeText = new Label
            {
                Location = new Point(282, 3),
                Size = new Size(106, 24),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            view.Limit = new TextBox { Location = new Point(392, 3), Size = new Size(88, 24) };
            view.Used = new Label
            {
                Location = new Point(484, 7),
                Size = new Size(86, 20),
                TextAlign = ContentAlignment.MiddleLeft,
            };
            view.Status = new Label
            {
                Location = new Point(614, 7),
                Size = new Size(192, 20),
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
            };
            view.EnableBox = new CheckBox
            {
                Location = new Point(574, 5),
                Size = new Size(20, 20),
                Checked = row.Enabled,
                CheckAlign = ContentAlignment.MiddleCenter,
            };

            DiskChoice? selected = _diskChoices.Find(c =>
                string.Equals(c.Identity, row.DiskIdentity, StringComparison.Ordinal));
            bool unknown = !row.IsRecognized || IsScannedUnmapped(row);
            if (unknown)
            {
                view.Disk.Items.Add(Locale.T("l2m.unknownDisk"));
                view.Disk.SelectedIndex = 0;
                view.Disk.Enabled = false;
            }
            else
            {
                foreach (DiskChoice choice in _diskChoices) view.Disk.Items.Add(choice);
                view.Disk.SelectedIndex = selected != null ? view.Disk.Items.IndexOf(selected) : -1;
            }

            foreach (string volume in _volumes) view.Volume.Items.Add(volume);
            if (row.CacheVolume.Length > 0 && !view.Volume.Items.Contains(row.CacheVolume))
            {
                view.Volume.Items.Add(row.CacheVolume);
            }
            view.Volume.SelectedItem = row.CacheVolume.Length > 0 ? row.CacheVolume : null;

            // 已初始化的行：盘与卷用文字展示（不能改）；未初始化才显示下拉。
            view.DiskText.Text = unknown ? Locale.T("l2m.unknownDisk") : (selected?.Text ?? Locale.T("l2m.unknownDisk"));
            view.VolumeText.Text = row.CacheVolume;
            view.Disk.Visible = !row.Initialized;
            view.Volume.Visible = !row.Initialized;
            view.DiskText.Visible = row.Initialized;
            view.VolumeText.Visible = row.Initialized;

            view.Limit.Text = row.MaxBytes > 0
                ? ((decimal)row.MaxBytes / GiB).ToString(CultureInfo.CurrentCulture)
                : string.Empty;
            view.LastValidLimit = view.Limit.Text;
            view.Used.Text = FormatBytes(row.ActualBytes);
            view.Status.Text = row.Status;

            bool diskReadOnly = !row.IsRecognized || (!row.IsConfig && row.CacheDirectory.Length > 0);
            bool valueReadOnly = !row.IsRecognized || IsScannedUnmapped(row);
            view.Disk.Enabled = view.Disk.Enabled && !diskReadOnly;
            view.Volume.Enabled = !valueReadOnly;
            view.Limit.ReadOnly = valueReadOnly;
            view.EnableBox.Enabled = !valueReadOnly;

            // 初始值设完再挂事件，避免被当成用户修改
            view.Disk.SelectionChangeCommitted += (s, e) => DiskChanged(view);
            view.Disk.SelectedIndexChanged += (s, e) => UpdateDiskPrompt(view);
            view.Disk.DropDown += (s, e) => UpdateDiskPrompt(view);
            view.Disk.DropDownClosed += (s, e) => UpdateDiskPrompt(view);
            view.DiskPrompt.Click += (s, e) =>
            {
                SelectView(view);
                view.Disk.Focus();
                view.Disk.DroppedDown = true;
            };
            view.Volume.SelectionChangeCommitted += (s, e) => VolumeChanged(view);
            view.Limit.KeyPress += LimitBox_KeyPress;
            view.Limit.TextChanged += (s, e) => LimitTextChanged(view);
            view.Limit.Leave += (s, e) => NormalizeLimit(view);

            view.Disk.Enter += (s, e) => SelectView(view);
            view.Volume.Enter += (s, e) => SelectView(view);
            view.Limit.Enter += (s, e) => SelectView(view);
            view.EnableBox.Enter += (s, e) => SelectView(view);
            view.EnableBox.CheckedChanged += (s, e) =>
            {
                row.Enabled = view.EnableBox.Checked;
                row.Status = row.Enabled ? row.StatusEnabled : row.StatusDisabled;
                view.Status.Text = row.Status;
            };
            view.Used.Click += (s, e) => SelectView(view);
            view.Status.Click += (s, e) => SelectView(view);
            view.DiskText.Click += (s, e) => SelectView(view);
            view.VolumeText.Click += (s, e) => SelectView(view);
            view.Accent.Click += (s, e) => SelectView(view);
            view.Container.Click += (s, e) => SelectView(view);

            view.Container.Controls.AddRange(new Control[]
            {
                view.Accent, view.Disk, view.DiskText, view.Volume, view.VolumeText, view.Limit, view.Used,
                view.EnableBox, view.Status, view.DiskPrompt,
            });
            view.DiskPrompt.BringToFront();
            StylePrompt(view);
            UpdateDiskPrompt(view);

            _rows.Controls.Add(view.Container);
            _rowViews.Add(view);
            return view;
        }

        private void SelectView(RowView? view)
        {
            _currentView = view;
            ThemePalette palette = ThemeManager.Palette;
            foreach (RowView rowView in _rowViews)
            {
                rowView.Accent.BackColor = ReferenceEquals(rowView, view)
                    ? palette.SelectionBg
                    : palette.WindowBg;
            }
        }

        /// <summary>提示标签要贴住盘下拉的底色，并跟随主题（主题刷新后需重调）</summary>
        private static void StylePrompt(RowView view)
        {
            view.DiskPrompt.BackColor = view.Disk.BackColor;
            view.DiskPrompt.ForeColor = ThemeManager.Palette.TextSecondary;
        }

        /// <summary>只有"下拉可用、尚未选择、且没展开"时才显示提示标签</summary>
        private static void UpdateDiskPrompt(RowView view)
        {
            view.DiskPrompt.Visible = !view.Data.Initialized && view.Disk.Enabled &&
                                      view.Disk.SelectedIndex < 0 && !view.Disk.DroppedDown;
        }

        private void DiskChanged(RowView view)
        {
            Row row = view.Data;
            if (row.Initialized) return;   // 已初始化的行：盘/卷是文字，不允许改

            if (view.Disk.SelectedItem is not DiskChoice choice)
            {
                row.DiskIdentity = string.Empty;
                row.OwnerHash = 0;
                row.CacheDirectory = string.Empty;
                row.ActualBytes = 0;
                view.Used.Text = FormatBytes(0);
                ResetEditedStatus(row);
                view.Status.Text = row.Status;
                UpdateDiskPrompt(view);
                return;
            }

            bool duplicate = _rowViews.Any(other =>
                !ReferenceEquals(other, view) &&
                string.Equals(other.Data.DiskIdentity, choice.Identity, StringComparison.Ordinal));
            if (duplicate)
            {
                Dialogs.Notice(this, Locale.T("dialog.notice.duplicateDisk"), Locale.T("l2m.msg.duplicateDisk"));

                DiskChoice? previous = row.DiskIdentity.Length > 0
                    ? _diskChoices.Find(c => string.Equals(c.Identity, row.DiskIdentity, StringComparison.Ordinal))
                    : null;
                view.Disk.SelectedIndex = previous != null ? view.Disk.Items.IndexOf(previous) : -1;
                UpdateDiskPrompt(view);
                return;
            }

            row.DiskIdentity = choice.Identity;
            row.OwnerHash = choice.OwnerHash;
            row.IsConfig = true;

            // 换盘后旧目录/实占不再属于这一行；按新盘 + 当前卷给出预期目录（可能尚不存在）。
            string volume = view.Volume.SelectedItem as string ?? string.Empty;
            row.CacheDirectory = volume.Length > 0
                ? SsdCacheService.GetDiskCacheDirectory(volume, choice.OwnerHash)
                : string.Empty;
            row.ActualBytes = 0;
            view.Used.Text = FormatBytes(0);
            ResetEditedStatus(row);
            view.Status.Text = row.Status;
            UpdateDiskPrompt(view);
        }

        /// <summary>盘/卷被编辑后：该行要重新保存才生效，状态先回到「待配置」</summary>
        private static void ResetEditedStatus(Row row)
        {
            row.StatusEnabled = Locale.T("l2m.statusNew");
            row.StatusDisabled = Locale.T("l2m.statusNew");
            row.Status = row.Enabled ? row.StatusEnabled : row.StatusDisabled;
        }

        private void VolumeChanged(RowView view)
        {
            Row row = view.Data;
            if (row.Initialized) return;
            row.CacheVolume = view.Volume.SelectedItem as string ?? string.Empty;

            // 换卷后旧目录/实占不再属于这一行；按当前盘 + 新卷给出预期目录（可能尚不存在）。
            row.CacheDirectory = row.OwnerHash != 0 && row.CacheVolume.Length > 0
                ? SsdCacheService.GetDiskCacheDirectory(row.CacheVolume, row.OwnerHash)
                : string.Empty;
            row.ActualBytes = 0;
            view.Used.Text = FormatBytes(0);
            ResetEditedStatus(row);
            view.Status.Text = row.Status;
        }

        /// <summary>上限输入：只放行数字 + 一个当前区域的小数分隔符</summary>
        private static void LimitBox_KeyPress(object? sender, KeyPressEventArgs e)
        {
            if (char.IsControl(e.KeyChar)) return;   // 退格 / 删除 / Ctrl 组合
            if (e.KeyChar >= '0' && e.KeyChar <= '9') return;

            char separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0];
            if (e.KeyChar == separator && sender is TextBox box &&
                (box.SelectionLength == box.TextLength || box.Text.IndexOf(separator) < 0))
            {
                return;
            }

            e.Handled = true;
        }

        /// <summary>粘贴 / 输入法上屏出现非法字符时整段回退，避免把 "1e3" 静默改成别的数字</summary>
        private static void LimitTextChanged(RowView view)
        {
            TextBox box = view.Limit;
            if (IsLimitText(box.Text))
            {
                view.LastValidLimit = box.Text;
                return;
            }

            int caret = box.SelectionStart;
            box.Text = view.LastValidLimit;
            box.SelectionStart = Math.Min(caret, box.Text.Length);
        }

        private static bool IsLimitText(string text)
        {
            if (text.Length == 0) return true;

            char separator = CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator[0];
            bool hasSeparator = false;
            foreach (char c in text)
            {
                if (c >= '0' && c <= '9') continue;
                if (c == separator && !hasSeparator)
                {
                    hasSeparator = true;
                    continue;
                }
                return false;
            }
            return true;
        }

        /// <summary>结束编辑时归一化："1." / "001" → 1；空 / 非法 / &lt;=0 → 空白（未设置）</summary>
        private static void NormalizeLimit(RowView view)
        {
            if (TryReadLimit(view.Limit.Text, out decimal parsed) && parsed > 0)
            {
                view.Data.MaxBytes = (long)(parsed * GiB);
                view.Limit.Text = parsed.ToString(CultureInfo.CurrentCulture);
            }
            else
            {
                view.Data.MaxBytes = 0;
                view.Limit.Text = string.Empty;
            }

            // 配置不完整时状态必须是「待配置」；补全后恢复该行原本的状态（正常 / 待校验 / 未启用）。
            // 扫描到的未配置行（"请插入属于它的硬盘…"）不因编辑上限而改状态。
            if (!view.Data.IsConfig) return;
            string volume = view.Volume.SelectedItem as string ?? string.Empty;
            bool complete = view.Data.DiskIdentity.Length > 0 && volume.Length > 0 && view.Data.MaxBytes > 0;
            view.Data.Status = complete
                ? (view.Data.Enabled ? view.Data.StatusEnabled : view.Data.StatusDisabled)
                : Locale.T("l2m.statusIncomplete");
            view.Status.Text = view.Data.Status;
        }

        private static bool TryReadLimit(string text, out decimal value)
            => decimal.TryParse(text, out value);

        private void AddNewRow()
        {
            var row = new Row
            {
                CacheVolume = string.Empty,
                MaxBytes = 0,
                StatusEnabled = Locale.T("l2m.statusNew"),
                StatusDisabled = Locale.T("l2m.statusNew"),
                Status = Locale.T("l2m.statusNew"),
            };

            RowView view = AddRowView(row);
            ThemeManager.Apply(this);   // 新加的标准控件也要走现有主题
            foreach (RowView rowView in _rowViews)
            {
                StylePrompt(rowView);
                UpdateDiskPrompt(rowView);
            }
            SelectView(view);

            BeginInvoke(new Action(() =>
            {
                _rowsPanel.ScrollControlIntoView(view.Container);
                view.Disk.Focus();
                view.Disk.DroppedDown = true;
            }));
        }

        private void DeleteCurrentRow()
        {
            RowView? view = _currentView;
            if (view == null) return;
            Row row = view.Data;

            if (!Dialogs.Confirm(this, Locale.T("l2m.delete"),
                    Locale.T("l2m.deleteConfirm", Describe(row)))) return;

            // 文件删除**延后到「确定」**：这样「取消」不会已经删掉东西
            if (row.CacheDirectory.Length > 0 && Directory.Exists(row.CacheDirectory))
            {
                _pendingDeletes.Add(row.CacheDirectory);
            }

            int index = _rowViews.IndexOf(view);
            _rowViews.Remove(view);
            _rows.Controls.Remove(view.Container);
            view.Container.Dispose();

            if (_rowViews.Count > 0)
            {
                SelectView(_rowViews[Math.Min(Math.Max(index, 0), _rowViews.Count - 1)]);
            }
            else
            {
                SelectView(null);
            }
        }

        private void ClearCurrentRow()
        {
            RowView? view = _currentView;
            if (view == null) return;
            Row row = view.Data;
            if (row.CacheDirectory.Length == 0 || !Directory.Exists(row.CacheDirectory)) return;

            if (!Dialogs.Confirm(this, Locale.T("l2m.clearContent"),
                    Locale.T("l2m.clearConfirm", Describe(row)))) return;

            L2CacheInfo cache = SsdCacheService.DescribeL2Cache(row.CacheDirectory, _config);
            if (!L2Manage.TryClear(cache, out string error))
            {
                Dialogs.Fail(this, Locale.T("dialog.fail.clearCache"), error);
                return;
            }

            row.ActualBytes = 0;
            row.CacheDirectory = string.Empty;
            row.IsRecognized = true;   // 文件已删干净：这一行回到"可配置"状态

            bool complete = row.DiskIdentity.Length > 0 && row.CacheVolume.Length > 0 && row.MaxBytes > 0;
            row.StatusEnabled = complete ? Locale.T("l2m.statusNoCache") : Locale.T("l2m.statusIncomplete");
            row.StatusDisabled = complete ? Locale.T("l2m.statusDisabled") : Locale.T("l2m.statusIncomplete");
            row.Status = Locale.T("l2m.statusCleared");
            view.Used.Text = FormatBytes(0);
            view.Status.Text = row.Status;
            SetInitialized(view, false);
        }

        /// <summary>
        /// 切换一行"已初始化 / 未初始化"的展示方式：已初始化用文字显示盘与卷（不能改），
        /// 清空缓存内容之后恢复成下拉。
        /// </summary>
        private void SetInitialized(RowView view, bool initialized)
        {
            Row row = view.Data;
            row.Initialized = initialized;
            view.Disk.Visible = !initialized;
            view.Volume.Visible = !initialized;
            view.DiskText.Visible = initialized;
            view.VolumeText.Visible = initialized;

            if (initialized)
            {
                view.DiskPrompt.Visible = false;
                return;
            }

            view.Disk.BeginUpdate();
            view.Disk.Items.Clear();
            foreach (DiskChoice choice in _diskChoices) view.Disk.Items.Add(choice);
            DiskChoice? selected = _diskChoices.Find(c =>
                string.Equals(c.Identity, row.DiskIdentity, StringComparison.Ordinal));
            view.Disk.SelectedIndex = selected != null ? view.Disk.Items.IndexOf(selected) : -1;
            view.Disk.EndUpdate();

            view.Disk.Enabled = true;
            view.Volume.Enabled = true;
            view.Limit.ReadOnly = false;
            view.EnableBox.Enabled = true;
            StylePrompt(view);
            UpdateDiskPrompt(view);
        }

        private void OpenCurrentRowDirectory()
        {
            string? dir = _currentView?.Data.CacheDirectory;
            if (string.IsNullOrEmpty(dir)) return;

            try
            {
                if (!Directory.Exists(dir))
                {
                    Dialogs.Fail(this, Locale.T("dialog.fail.openDir"), Locale.T("l2m.openDirFailed", dir));
                    return;
                }
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Dialogs.Fail(this, Locale.T("dialog.fail.openDir"), Locale.T("l2m.openDirFailed", ex.Message));
            }
        }

        private void SaveAndClose()
        {
            // 把仍在输入中的上限先归一化，再逐行读取
            foreach (RowView view in _rowViews) NormalizeLimit(view);

            var result = new List<L2DiskConfig>();
            foreach (RowView view in _rowViews)
            {
                Row row = view.Data;
                if (!row.IsRecognized) continue;   // 未识别行不写配置
                if (row.DiskIdentity.Length == 0) continue;   // 空行忽略

                string volume = view.Volume.SelectedItem as string ?? string.Empty;
                TryReadLimit(view.Limit.Text, out decimal gib);
                bool enabled = view.EnableBox.Checked;

                // 停用的行只保留配置，不参与本次的上限校验（重新启用时启动前会再校验）
                if (enabled && volume.Length > 0 && (gib < 1 || gib > 64))
                {
                    Dialogs.Notice(this, Locale.T("dialog.notice.limitRange"), Locale.T("l2m.msg.limitRange", Describe(row)));
                    return;
                }

                if (result.Exists(e => string.Equals(e.DiskIdentity, row.DiskIdentity, StringComparison.Ordinal)))
                {
                    Dialogs.Notice(this, Locale.T("dialog.notice.duplicateDisk"), Locale.T("l2m.msg.duplicateDisk"));
                    return;
                }

                result.Add(new L2DiskConfig
                {
                    DiskIdentity = row.DiskIdentity,
                    OwnerHash = row.OwnerHash,
                    Enabled = enabled,
                    CacheVolume = volume,
                    // 卷为空 = 这一行还没配完：上限存 0，启动前会被拒绝（不静默当成"不启用 L2"）
                    MaxBytes = volume.Length > 0 ? (long)(gib * GiB) : 0,
                });
            }

            // 按**卷**核对总上限：同一块缓存盘上多块盘的上限合计不能超过可用空间
            // （提示只点名卷，不点名具体盘）。
            var volumeTotals = result
                .Where(e => e.Enabled && !string.IsNullOrWhiteSpace(e.CacheVolume))
                .GroupBy(e => e.CacheVolume.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase);

            foreach (var group in volumeTotals)
            {
                long configured = group.Sum(e => e.MaxBytes);
                long used = 0;
                foreach (RowView view in _rowViews)
                {
                    if (!view.EnableBox.Checked) continue;   // 停用行的占用不参与可回收预算
                    string volume = view.Volume.SelectedItem as string ?? string.Empty;
                    if (string.Equals(volume.TrimEnd('\\'), group.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        used += view.Data.ActualBytes;
                    }
                }

                try
                {
                    long free = new DriveInfo(group.Key + "\\").AvailableFreeSpace;
                    long budget = free - SsdCacheService.KeepFreeBytes + used;
                    if (configured > budget)
                    {
                        Dialogs.Notice(this, Locale.T("dialog.notice.volumeFull"),
                            Locale.T("l2m.msg.volumeOverLimit", group.Key,
                                FormatBytes(configured), FormatBytes(Math.Max(0, budget))));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"管理 L2：核对卷 {group.Key} 可用空间失败（跳过）：{ex.Message}");
                }
            }

            // 删除动作在这里真正落盘（取消不会删文件）
            foreach (string dir in _pendingDeletes)
            {
                L2CacheInfo cache = SsdCacheService.DescribeL2Cache(dir, _config);
                if (!L2Manage.TryClear(cache, out string error))
                {
                    Dialogs.Fail(this, Locale.T("dialog.fail.clearCache"), error);
                    return;
                }
            }

            _config.L2Disks.Clear();
            _config.L2Disks.AddRange(result);

            DialogResult = DialogResult.OK;
            Close();
        }

        private string Describe(Row row)
        {
            if (row.CacheDirectory.Length > 0) return row.CacheDirectory;
            return row.DiskIdentity.Length > 0 ? row.DiskIdentity : Locale.T("l2m.unknownDisk");
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) return string.Format("{0:0.##} GiB", bytes / 1024.0 / 1024 / 1024);
            if (bytes >= 1024L * 1024) return string.Format("{0:0.#} MiB", bytes / 1024.0 / 1024);
            return string.Format("{0} B", bytes);
        }
    }
}
