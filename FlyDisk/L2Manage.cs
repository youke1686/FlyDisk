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
using System.IO;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// **「管理 L2」**：把整机上找到的 L2 缓存列出来，让用户挑一份、再对它做动作。
    ///
    /// 为什么需要它：L2 缓存是**故意不在切换时自动删**的（用户可能只是临时关掉 L2，回头还要用，
    /// 比如两块盘换着加速）——但用户**忘了**某块盘上还压着一份缓存时，它会一直占着空间，
    /// 而我们没有任何入口告诉他。这个功能就是那个入口，同时为 TODO「缓存的手动管理」（快照等）
    /// 预留了落点：**新功能长在操作窗口里**（见 <see cref="L2ManageForm"/>）。
    ///
    /// 与"启动前预检"的分工（见 AGENTS.md §2.3）：预检管**本次启动要不要继续**（删掉 / 校验 / 缩放），
    /// 这里管**场外的手工清理与切换**。两者共用引擎那套只读探测（<see cref="SsdCacheService.DescribeAllL2Caches"/>）。
    /// </summary>
    internal static class L2Manage
    {
        /// <summary>
        /// 入口（设置里那个「L2 管理」按钮调它）。
        /// **模态**：返回时用户的动作都已落定。
        /// </summary>
        /// <returns>true = 配置被改过（调用方要刷新设置界面里的 L2 控件）</returns>
        public static bool Run(IWin32Window owner, DiskConfig config)
        {
            List<L2CacheInfo> caches;
            try
            {
                caches = SsdCacheService.DescribeAllL2Caches(config);
            }
            catch (Exception ex)
            {
                // 约定：不静默。扫不出来就如实说，并写诊断日志。
                LogService.DebugFile($"管理 L2：扫描缓存目录失败：{ex}");
                MessageBox.Show(Locale.T("l2m.scanFailed", ex.Message), Locale.T("dialog.error"),
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }

            if (caches.Count == 0)
            {
                MessageBox.Show(Locale.T("l2m.none"), Locale.T("l2m.title"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            using var picker = new PickerForm(caches);
            if (picker.ShowDialog(owner) != DialogResult.OK || picker.SelectedL2Cache == null) return false;

            using var op = new L2ManageForm(picker.SelectedL2Cache, config);
            op.ShowDialog(owner);
            return op.ConfigChanged;
        }

        /// <summary>
        /// 一份缓存的**全部展示行**（纯文本）。选择页与操作窗口共用这一份，避免两处各写一套口径。
        /// </summary>
        public static IReadOnlyList<string> DescribeCacheLines(L2CacheInfo cache)
        {
            var lines = new List<string>
            {
                Locale.T("l2m.line.directory", cache.Directory),
                Locale.T(cache.IsConfigured ? "l2m.line.current" : "l2m.line.unused")
            };

            if (cache.Recognized)
            {
                lines.Add(Locale.T("l2m.line.capacity",
                    ServiceConstants.FormatBytes((long)cache.StoredSlots * ServiceConstants.BlockSize)));
                lines.Add(Locale.T("l2m.line.owner", cache.OwnerText));
                lines.Add(Locale.T(
                    cache.LastShutdownClean switch
                    {
                        true => "l2m.line.cleanYes",
                        false => "l2m.line.cleanNo",
                        _ => "l2m.line.cleanUnknown",
                    }));
            }
            else
            {
                lines.Add(Locale.T("l2m.line.unrecognized"));
            }

            lines.Add(Locale.T("l2m.line.files",
                ServiceConstants.FormatBytes(cache.ContainerBytes),
                ServiceConstants.FormatBytes(cache.IndexBytes)));

            if (!cache.ContainerExists) lines.Add(Locale.T("l2m.line.noContainer"));
            if (!cache.IndexExists) lines.Add(Locale.T("l2m.line.noIndex"));
            return lines;
        }

        /// <summary>
        /// **清空一份缓存**（删掉容器与索引，再删掉整个目录）。
        ///
        /// 安全闸：**复用引擎与校验器共同持有的那把单实例锁**（两者都以 <c>FileShare.None</c> 打开它）。
        /// 能拿到锁 = 没人在用（没在加速、没在跑校验、没有别的实例），此时删才安全；
        /// 拿不到就如实告诉用户"正在使用"，不做任何猜测。
        /// </summary>
        /// <returns>成功返回 true；否则 <paramref name="error"/> 里是给用户看的原因</returns>
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

            // 数据文件已删；把只剩锁文件的目录也清掉（失败也无害——那只是个空目录）
            try { Directory.Delete(cache.Directory, recursive: true); }
            catch (Exception ex) { LogService.DebugFile($"管理 L2：删除空目录 {cache.Directory} 失败（无害）：{ex.Message}"); }

            LogService.DebugFile($"管理 L2：已清空 L2 缓存目录 {cache.Directory}");
            return true;
        }
    }

    /// <summary>
    /// 「管理 L2」的**操作窗口**：对这一份缓存做动作。将来 TODO「缓存的手动管理」（保存 / 录制 /
    /// 应用快照）就长在这里。
    /// </summary>
    internal sealed class L2ManageForm : ThemedForm
    {
        private readonly DiskConfig _config;
        private readonly L2CacheInfo _cache;

        private readonly TextBox _info = new();
        private readonly Button _clear = new();
        private readonly Button _openDir = new();
        private readonly Button _setCurrent = new();
        private readonly Button _close = new();

        /// <summary>用户有没有改过配置（"设为当前 L2 缓存盘"）——由设置对话框决定何时落盘</summary>
        public bool ConfigChanged { get; private set; }

        public L2ManageForm(L2CacheInfo cache, DiskConfig config)
        {
            _cache = cache;
            _config = config;

            Text = Locale.T("l2m.title");
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 300);

            var hint = new Label
            {
                Text = Locale.T("l2m.opHint"),
                Location = new Point(12, 10),
                Size = new Size(536, 18),
                ForeColor = Color.Gray
            };

            _info.Location = new Point(12, 32);
            _info.Size = new Size(536, 190);
            _info.Multiline = true;
            _info.ReadOnly = true;
            _info.ScrollBars = ScrollBars.Vertical;
            _info.WordWrap = true;
            _info.TabStop = false;

            _clear.Text = Locale.T("l2m.clear");
            _clear.Location = new Point(12, 260);
            _clear.Size = new Size(110, 28);
            _clear.Click += (s, e) => ClearCache();

            _openDir.Text = Locale.T("l2m.openDir");
            _openDir.Location = new Point(130, 260);
            _openDir.Size = new Size(110, 28);
            _openDir.Click += (s, e) => OpenDirectory();

            _setCurrent.Text = Locale.T("l2m.setCurrent");
            _setCurrent.Location = new Point(248, 260);
            _setCurrent.Size = new Size(170, 28);
            _setCurrent.Visible = !cache.IsConfigured;
            _setCurrent.Click += (s, e) => SetAsCurrent();

            _close.Text = Locale.T("common.close");
            _close.Location = new Point(464, 260);
            _close.Size = new Size(84, 28);
            _close.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { hint, _info, _clear, _openDir, _setCurrent, _close });
            CancelButton = _close;

            RefreshInfo();
        }

        private void RefreshInfo()
        {
            _info.Text = string.Join("\r\n", L2Manage.DescribeCacheLines(_cache));
            // 已经清空过 / 本来就没有数据文件：没什么可清的
            _clear.Enabled = _cache.ContainerExists || _cache.IndexExists;
            _setCurrent.Enabled = !_cache.IsConfigured;
        }

        /// <summary>清空缓存：二次确认（默认"否"）→ 过锁闸 → 删文件</summary>
        private void ClearCache()
        {
            DialogResult answer = MessageBox.Show(
                Locale.T("l2m.clearConfirm", _cache.Directory), Locale.T("l2m.clear"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (!L2Manage.TryClear(_cache, out string error))
            {
                MessageBox.Show(error, Locale.T("l2m.clear"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            MessageBox.Show(Locale.T("l2m.clearDone", _cache.Directory), Locale.T("l2m.clear"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }

        /// <summary>打开缓存目录（用资源管理器；自己删不掉就手删）</summary>
        private void OpenDirectory()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_cache.Directory}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"管理 L2：打开目录失败：{ex.Message}");
                MessageBox.Show(Locale.T("l2m.openDirFailed", ex.Message), Locale.T("dialog.error"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>把这份缓存所在的盘设为当前 L2 缓存盘（只改内存里的配置，落盘由设置对话框负责）</summary>
        private void SetAsCurrent()
        {
            _config.SsdCachePath = _cache.Directory;
            ConfigChanged = true;
            MessageBox.Show(Locale.T("l2m.setCurrentDone", _cache.Directory), Locale.T("l2m.setCurrent"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
    }
}
