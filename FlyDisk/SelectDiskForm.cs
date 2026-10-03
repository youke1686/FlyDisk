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
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// 「选择硬盘」对话框：**每次点「启动加速」时都弹一次**（见 后续待办.md 第一节的"选盘时机"）。
    ///
    /// 为什么不预先在设置里选好：用户改过配置后可能隔几天才点启动，很容易忘了当时选的是哪块盘；
    /// 把选择动作放在启动的同一刻，做到"所见即所选"。代价是每次启动多一次点击，收益是永不选错——
    /// 而选错的后果是把用户那块盘**整盘脱机**（当场从系统里消失）。
    ///
    /// **两种形态共用这一个对话框**：本地形态列本机物理盘（不能加速的直接标注并禁止选择），
    /// 远程形态列**对端磁盘**（配对成功后由对端现取一份最新的清单）。
    ///
    /// 另外两处也复用它：**「校验 L2」**（本地形态，选要校验哪块盘）与**「重新联机源盘」**
    /// （本地形态 + <c>offlineOnly: true</c>，只列脱机盘，见 后续待办.md 第十二节）。
    /// </summary>
    public sealed class SelectDiskForm : ThemedForm
    {
        /// <summary>列表里的一行（把本地盘与对端盘统一成同一种展示模型）</summary>
        private sealed class Entry
        {
            public string Text = string.Empty;
            public string Detail = string.Empty;
            public bool Selectable = true;
            public PhysicalDiskInfo? Local;
            public RemoteDiskInfo? Remote;
        }

        private readonly List<Entry> _entries = new();

        /// <summary>
        /// 配置里 L2 缓存目录落在那块物理盘上（-1 = L2 未启用 / 没配路径 / 查不到盘号）。
        /// 用来拦下"拿缓存所在的那块盘去加速"——自己给自己加速是纯写放大，引擎启动时也会拒。
        /// </summary>
        private readonly int _l2CacheDiskNumber = -1;

        /// <summary>列表为空时的文案键（远程形态与"只列脱机盘"各有各的空态说法）</summary>
        private string _emptyTextKey = "selectDisk.noDisks";

        private readonly ListBox _list = new();
        private readonly Label _detail = new();
        private readonly Button _ok = new();
        private readonly Button _cancel = new();

        /// <summary>本地形态：用户选定的本机磁盘；取消时为 null</summary>
        public PhysicalDiskInfo? SelectedDisk { get; private set; }

        /// <summary>远程形态：用户选定的对端磁盘；取消时为 null</summary>
        public RemoteDiskInfo? SelectedRemoteDisk { get; private set; }

        /// <summary>【本地形态】</summary>
        /// <param name="disks">已枚举好的本机物理盘</param>
        /// <param name="identityToPreselect">上次选的那块盘的身份串，用于默认选中</param>
        /// <param name="offlineOnly">
        /// true = **只列脱机盘**，供「重新联机源盘」用（见 后续待办.md 第十二节）。
        /// 这个模式下**不做** <see cref="BlockReasons"/> 判定、一律可选："能不能加速"的拦阻规则在这里全不适用，
        /// 系统盘恰恰是最需要联机回来的那块。
        /// </param>
        /// <param name="l2CacheDiskNumber">
        /// 配置里 L2 缓存目录所在的那块盘（-1 = L2 未启用 / 没配 / 查不到）。
        /// 该盘会被标成不能加速——自己给自己加速是纯写放大，引擎启动时也会拒（见启动校验 ⑥）。
        /// </param>
        public SelectDiskForm(IReadOnlyList<PhysicalDiskInfo> disks, string identityToPreselect,
            bool offlineOnly = false, int l2CacheDiskNumber = -1)
            : this(offlineOnly ? "selectDisk.title.online" : "selectDisk.title.local", null, offlineOnly)
        {
            _emptyTextKey = offlineOnly ? "selectDisk.noOfflineDisks" : "selectDisk.noDisks";
            _l2CacheDiskNumber = l2CacheDiskNumber;

            int preselect = -1;
            for (int i = 0; i < disks.Count; i++)
            {
                PhysicalDiskInfo info = disks[i];
                if (offlineOnly && info.IsOnline) continue;   // 只列脱机盘：联机盘没什么可"重新联机"的

                // offlineOnly（重新联机）模式不做任何"能不能加速"的判定：系统盘恰恰是最需要联机回来的那块
                List<string> blockReasons = offlineOnly
                    ? new List<string>()
                    : BlockReasons(info, _l2CacheDiskNumber);

                _entries.Add(new Entry
                {
                    Local = info,
                    Selectable = blockReasons.Count == 0,
                    Text = Locale.T("selectDisk.item.local",
                        info.DiskNumber,
                        info.Model.Length > 0 ? info.Model : Locale.T("selectDisk.modelUnknown"),
                        info.SizeText,
                        FormatDriveSlot(info)),
                    Detail = BuildLocalDetail(info, blockReasons)
                });

                if (identityToPreselect.Length > 0 &&
                    PhysicalDiskHandle.DescribeIdentity(info) == identityToPreselect)
                {
                    // 记的必须是 **_entries 的下标**，不是 disks 的下标：offlineOnly 模式下联机盘被
                    // 上面的 continue 跳过，两个下标会错位；错位后赋给 ListBox.SelectedIndex 会越界
                    // （抛 ArgumentOutOfRangeException），而异常又正好被调用方那个 try 兜成
                    // "枚举物理盘失败"，文不对题、极难定位。
                    preselect = _entries.Count - 1;
                }
            }

            FinishFill(preselect);
        }

        /// <summary>【远程形态】列出**对端**磁盘</summary>
        /// <param name="disks">对端刚才报过来的清单</param>
        /// <param name="identityToPreselect">上次用的那块对端盘（若这一轮还在，就默认选中它）</param>
        /// <param name="peerName">对端名字（显示在标题下方）</param>
        public SelectDiskForm(IReadOnlyList<RemoteDiskInfo> disks, string identityToPreselect, string peerName)
            : this("selectDisk.title.remote", peerName)
        {
            int preselect = -1;
            for (int i = 0; i < disks.Count; i++)
            {
                RemoteDiskInfo info = disks[i];
                double gib = (double)info.SizeBytes / 1024 / 1024 / 1024;

                _entries.Add(new Entry
                {
                    Remote = info,
                    Selectable = true,
                    Text = $"{info.DisplayName}",
                    Detail = Locale.T("selectDisk.remote.detail",
                        gib.ToString("F1"),
                        info.BytesPerSector,
                        Locale.T(info.Rotational ? "selectDisk.rotational" : "selectDisk.nonRotational"))
                });

                if (identityToPreselect.Length > 0 && info.Identity == identityToPreselect) preselect = i;
            }

            FinishFill(preselect);
        }

        // 本对话框是模态的（ShowDialog）：打开期间主菜单点不到、语言不会变，
        // 所以文案在构造时按当前语言取一次即可，不必实现 ILocalizable 那套刷新。
        private SelectDiskForm(string titleKey, string? peerName, bool tallHint = false)
        {
            Text = Locale.T(titleKey);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 356);

            var hint = new Label
            {
                // tallHint（"只列脱机盘"）与 peerName 不会同时出现，判定顺序无所谓
                Text = tallHint
                    ? Locale.T("selectDisk.hint.online")
                    : peerName == null
                        ? Locale.T("selectDisk.hint.local")
                        : Locale.T("selectDisk.hint.remote", peerName),
                Location = new Point(12, 10),
                // "重新联机"那句是两行文案、长句还要折行，高度按实测结果在下面补（这里先给个初值）
                Size = new Size(536, tallHint ? 32 : 18),
                ForeColor = Color.Gray
            };

            _list.Location = new Point(12, tallHint ? 46 : 32);
            _list.Size = new Size(536, tallHint ? 182 : 196);
            _list.IntegralHeight = false;
            // 条目不再走自绘换行（试过 DrawMode.OwnerDrawVariable 始终没折行，已放弃）：
            // 行内也不再拼"不能加速"的长尾巴，改由下方盘信息区顶部统一列 ❌ 原因，行本身保持一行放得下。
            _list.SelectedIndexChanged += (s, e) => UpdateSelection();
            _list.DoubleClick += (s, e) => { if (_ok.Enabled) Accept(); };

            _detail.Location = new Point(12, 234);
            _detail.Size = new Size(536, 74);

            _ok.Text = Locale.T("common.ok");
            _ok.Location = new Point(376, 316);
            _ok.Size = new Size(84, 28);
            _ok.Click += (s, e) => Accept();

            _cancel.Text = Locale.T("common.cancel");
            _cancel.Location = new Point(466, 316);
            _cancel.Size = new Size(84, 28);
            _cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { hint, _list, _detail, _ok, _cancel });
            AcceptButton = _ok;
            CancelButton = _cancel;

            // "只列脱机盘"那句占两行、长句还要折行，且文案长短随语言变：
            // 固定 536 宽（否则单行会横向长出去）→ 实测折行后的高度 → 把列表整体下压。
            // 对话框总高不变；列表底边与"明细"之间的间距也不变（46+182 = 32+196 = 228）。
            if (tallHint)
            {
                hint.AutoSize = false;
                Size measured = TextRenderer.MeasureText(hint.Text, hint.Font,
                    new Size(hint.Width, 4096), TextFormatFlags.WordBreak);
                int height = Math.Max(32, measured.Height + 6);
                hint.Size = new Size(hint.Width, height);
                _list.Location = new Point(12, 46 + height - 32);
                _list.Size = new Size(536, 182 - (height - 32));
            }
        }

        private void FinishFill(int preselect)
        {
            foreach (Entry entry in _entries) _list.Items.Add(entry.Text);

            if (_list.Items.Count == 0)
            {
                _detail.Text = Locale.T(_emptyTextKey);
                _ok.Enabled = false;
                return;
            }

            if (preselect >= 0) _list.SelectedIndex = preselect;
            else
            {
                int first = _entries.FindIndex(e => e.Selectable);
                _list.SelectedIndex = first >= 0 ? first : 0;
            }
            UpdateSelection();
        }

        /// <summary>
        /// 该盘**不能被加速**的全部原因（本地化文案，逐条一行；空 = 可以）。
        ///
        /// 四条硬拦：
        /// - **只读**：做不了写透传；
        /// - **系统盘**：整盘脱机等于把 Windows 送走；
        /// - **本程序所在盘**：整盘脱机会把本程序（连同配置/日志/L2 容器）一起送走；
        /// - **L2 缓存所在盘**：自己给自己加速是纯写放大，引擎启动时也会拒。
        ///
        /// 前两条与引擎启动校验、L2 校验/修复共用 <see cref="PhysicalDiskHandle.DescribeTargetDiskBlockReason"/>
        /// 的同一条判据；L2 那条与启动校验 ⑥（缓存目录不得落在被加速的盘上）同源。
        /// 系统盘与程序盘同时成立时只报系统盘（既是更硬的理由，也避免"把程序换到系统盘"这句建议自相矛盾）。
        /// </summary>
        private static List<string> BlockReasons(PhysicalDiskInfo info, int l2CacheDiskNumber)
        {
            var reasons = new List<string>();
            if (info.IsReadOnly) reasons.Add(Locale.T("selectDisk.reason.readOnly"));

            int system = PhysicalDiskHandle.GetSystemDiskNumber();
            int program = PhysicalDiskHandle.GetProgramDiskNumber();
            if (system >= 0 && info.DiskNumber == system)
                reasons.Add(Locale.T("selectDisk.reason.systemDisk"));
            else if (program >= 0 && info.DiskNumber == program)
                reasons.Add(Locale.T("selectDisk.reason.programDisk"));

            if (l2CacheDiskNumber >= 0 && info.DiskNumber == l2CacheDiskNumber)
                reasons.Add(Locale.T("selectDisk.reason.l2CacheDisk"));

            return reasons;
        }

        /// <summary>
        /// 列表行里的**盘符槽**（含括号；无内容时返回空串，整段省略，不留空括号）。
        /// 一块盘可能有多个卷 / 多个盘符，全部按序列出（引擎已在 <see cref="PhysicalDiskHandle.FillDriveLetters"/>
        /// 里排过序），分隔符**统一英文逗号**，不随语言分叉。
        ///
        /// **脱机盘没有盘符**（整盘脱机后卷随之消失、<c>\\.\X:</c> 开不出来），所以这个槽里改放"脱机"一词：
        /// 一来如实说明"这儿没盘符"的原因，二来行尾原本那句联机/脱机状态就多余了，已删（见 item.local 模板）。
        /// 联机盘若确实没有卷（未格式化），槽为空、整段省略。
        /// </summary>
        private static string FormatDriveSlot(PhysicalDiskInfo info)
        {
            if (!info.IsOnline) return Locale.T("selectDisk.item.drives", Locale.T("selectDisk.offline"));
            if (info.DriveLetters.Count == 0) return string.Empty;
            return Locale.T("selectDisk.item.drives", string.Join(", ", info.DriveLetters));
        }

        private static string BuildLocalDetail(PhysicalDiskInfo info, IReadOnlyList<string> blockReasons)
        {
            var sb = new StringBuilder();

            // 第一行留给**基本信息**（字节/扇区、容量、序列号）：它是"这块盘是谁"的锚点，任何时候都要看得见。
            sb.AppendLine(Locale.T("selectDisk.detail.header",
                info.BytesPerSector,
                info.SizeText,
                info.SerialNumber.Length > 0 ? info.SerialNumber : Locale.T("selectDisk.serialNone")));

            // 不能加速的原因紧跟在基本信息之后（再往上顶会把基本信息挤出可视区）
            foreach (string reason in blockReasons) sb.AppendLine(reason);

            if (!PhysicalDiskHandle.HasStableIdentity(info))
            {
                sb.AppendLine(Locale.T("selectDisk.detail.noStableIdentity"));
            }
            if (info.RemovableMedia)
            {
                sb.AppendLine(Locale.T("selectDisk.detail.removable"));
            }
            // 机械盘/SSD 提示落在这里、而不是启动后的日志里：用户**选它的那一刻**就该看到
            //（本程序的收益来自机械盘；SSD 做块级缓存通常净亏——缓存读写会和源数据抢同一块盘）
            if (info.SeekPenaltyKnown && !info.IncursSeekPenalty)
            {
                sb.AppendLine(Locale.T("selectDisk.detail.noSeekPenalty"));
            }
            else if (!info.SeekPenaltyKnown)
            {
                sb.AppendLine(Locale.T("selectDisk.detail.seekUnknown"));
            }
            return sb.ToString();
        }

        private void UpdateSelection()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _entries.Count)
            {
                _ok.Enabled = false;
                return;
            }

            Entry entry = _entries[index];
            _ok.Enabled = entry.Selectable;
            _detail.Text = entry.Detail;
        }

        private void Accept()
        {
            int index = _list.SelectedIndex;
            if (index < 0 || index >= _entries.Count || !_entries[index].Selectable) return;

            SelectedDisk = _entries[index].Local;
            SelectedRemoteDisk = _entries[index].Remote;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
