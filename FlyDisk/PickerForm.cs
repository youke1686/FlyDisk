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
using System.Runtime.InteropServices;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Theming;

namespace FlyDisk
{
    /// <summary>
    /// **通用选择框**（左列表 + 右明细 + 确定/取消）：默认单选；「启动加速」的多选模式下每行带复选框。
    ///
    /// 五种用法共用这一个对话框（**按"模式"分，而不是各写一个**）：
    /// - **本地选盘**：列本机物理盘（不能加速的直接标注并禁止选择）；
    /// - **远程选盘**：列**对端磁盘**（配对成功后由对端现取一份最新的清单）；
    /// - **「校验 L2」选盘**：本地形态，选要校验哪块盘；
    /// - **「重新联机源盘」**：本地形态 + <c>offlineOnly: true</c>，只列脱机盘（见 后续待办.md 第十二节）；
    /// - **多选**：本地形态 + 多选构造，列表换成带复选框的 <c>CheckedListBox</c>，**勾选**多块盘
    ///   （结果在 <c>SelectedDisks</c>，供「启动加速」同时加速多块盘）。
    ///
    /// 为什么**本地选盘每次点「启动加速」都要弹一次**（见 后续待办.md 第一节的"选盘时机"）：
    /// 用户改过配置后可能隔几天才点启动，很容易忘了当时选的是哪块盘；把选择动作放在启动的同一刻，
    /// 做到"所见即所选"。代价是每次启动多一次点击，收益是永不选错——而选错的后果是把用户那块盘
    /// **整盘脱机**（当场从系统里消失）。
    /// </summary>
    public sealed class PickerForm : ThemedForm
    {
        /// <summary>
        /// 信息区里的一行。<paramref name="IsError"/> = **会阻止启动**的硬拦原因：
        /// 它既是"渲染成红色"的依据，也是「确定」按钮能否使用的依据（有任意一条 ⇒ 不可用）。
        /// 这类行在文案上以 ❌ 开头（见 <c>selectDisk.reason.*</c>），但**判定不看字符**、只看这个标志。
        /// </summary>
        private readonly record struct DetailLine(string Text, bool IsError);

        /// <summary>列表里的一行（把本地盘 / 对端盘 / L2 缓存目录统一成同一种展示模型）</summary>
        private sealed class Entry
        {
            public string Text = string.Empty;
            public List<DetailLine> Detail = new();
            public bool Selectable = true;
            public PhysicalDiskInfo? Local;
            public RemoteDiskInfo? Remote;
        }

        private readonly List<Entry> _entries = new();

        /// <summary>
        /// 配置里 L2 缓存目录落在哪些物理盘上（可能有多块：每块被加速盘各有自己的 L2 卷）。
        /// 用来拦下"拿缓存所在的那块盘去加速"——自己给自己加速是纯写放大，引擎启动时也会拒。
        /// </summary>
        private readonly HashSet<int> _l2CacheDiskNumbers = new();

        /// <summary>
        /// 承载分页文件（pagefile.sys）的物理盘号集合，用于拦下"拿分页文件所在盘去加速"——
        /// 整盘脱机会让内核换页失败、当场蓝屏（KERNEL_DATA_INPAGE_ERROR 0x7A）。
        /// **一次性算好**：<see cref="BlockReasons"/> 要对清单里每块盘判一次，若在循环里各调一次
        /// <see cref="PhysicalDiskHandle.GetPageFileDiskNumbers"/> 就成了"每块盘各扫一遍全机卷"的重复探测。
        /// </summary>
        private readonly HashSet<int> _pageFileDiskNumbers = new();

        /// <summary>列表为空时的文案键（远程形态与"只列脱机盘"各有各的空态说法）</summary>
        private string _emptyTextKey = "selectDisk.noDisks";

        /// <summary>单选用普通列表；多选（「启动加速」）用带复选框的列表（见 <see cref="_checkedList"/>）</summary>
        private readonly ListBox _list;

        /// <summary>
        /// 多选模式下的列表本体（与 <see cref="_list"/> 同一个对象）；单选模式为 null。
        /// 多选用**复选框**表达选择，不需要 Ctrl/Shift 组合键。
        /// </summary>
        private readonly CheckedListBox? _checkedList;

        private readonly RichTextBox _detail = new();
        private readonly Button _ok = new();
        private readonly Button _cancel = new();

        /// <summary>
        /// 信息区是否已经可以写入了（句柄就绪 + 主题上色完毕）。
        /// 构造期控件还没有句柄，`RichTextBox` 的选色 / 追加都落不下去，此时写只会得到错色或空内容；
        /// 所以第一屏推迟到 <see cref="OnLoad"/> 里渲染。见 <see cref="ShowDetail"/>。
        /// </summary>
        private bool _detailReady;

        /// <summary>本地形态：用户选定的本机磁盘；取消时为 null（多选模式下 = <see cref="SelectedDisks"/> 的第一块）</summary>
        public PhysicalDiskInfo? SelectedDisk { get; private set; }

        /// <summary>【多选模式】用户选定的全部本机磁盘（顺序跟随列表；单选模式下是 0 或 1 项）</summary>
        public List<PhysicalDiskInfo> SelectedDisks { get; } = new();

        /// <summary>是否多选（「启动加速」同时选多块盘用）</summary>
        private readonly bool _multiSelect;

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
        public PickerForm(IReadOnlyList<PhysicalDiskInfo> disks, string identityToPreselect,
            bool offlineOnly = false, int l2CacheDiskNumber = -1)
            : this(disks,
                   identityToPreselect.Length > 0 ? new List<string> { identityToPreselect } : new List<string>(),
                   offlineOnly,
                   l2CacheDiskNumber >= 0 ? new[] { l2CacheDiskNumber } : Array.Empty<int>(),
                   multiSelect: false)
        {
        }

        /// <summary>
        /// 【多选形态】同一份本机磁盘清单，但允许**一次选多块**（「启动加速」用它同时加速多块盘）。
        /// 选中的结果在 <see cref="SelectedDisks"/>；<see cref="SelectedDisk"/> 仍是第一块，方便调用方只看一块。
        /// </summary>
        /// <param name="disks">已枚举好的本机物理盘</param>
        /// <param name="identitiesToPreselect">上次加速过的盘（配置里的身份串），用来自动勾选</param>
        /// <param name="l2CacheDiskNumbers">配置里各 L2 缓存目录所在的物理盘号（空 = 未启用 / 查不到）；这些盘会被标成不能加速</param>
        public PickerForm(IReadOnlyList<PhysicalDiskInfo> disks, IReadOnlyList<string> identitiesToPreselect,
            IReadOnlyCollection<int> l2CacheDiskNumbers)
            : this(disks, identitiesToPreselect, offlineOnly: false, l2CacheDiskNumbers, multiSelect: true)
        {
        }

        /// <summary>唯一的实现体：本地清单 + 预选（单个或多选）+ 是否只列脱机盘 + 是否多选</summary>
        private PickerForm(IReadOnlyList<PhysicalDiskInfo> disks, IReadOnlyList<string> identitiesToPreselect,
            bool offlineOnly, IReadOnlyCollection<int> l2CacheDiskNumbers, bool multiSelect)
            : this(offlineOnly ? "selectDisk.title.online" : "selectDisk.title.local", multiSelect)
        {
            _emptyTextKey = offlineOnly ? "selectDisk.noOfflineDisks" : "selectDisk.noDisks";
            foreach (int number in l2CacheDiskNumbers)
            {
                if (number >= 0) _l2CacheDiskNumbers.Add(number);
            }
            // offlineOnly（重新联机）模式下用不到，但算一次也无妨：分页文件盘恰恰是最该联机回来的那块
            _pageFileDiskNumbers = PhysicalDiskHandle.GetPageFileDiskNumbers();

            var preselect = new List<int>();
            for (int i = 0; i < disks.Count; i++)
            {
                PhysicalDiskInfo info = disks[i];
                if (offlineOnly && info.IsOnline) continue;   // 只列脱机盘：联机盘没什么可"重新联机"的

                // offlineOnly（重新联机）模式不做任何"能不能加速"的判定：系统盘恰恰是最需要联机回来的那块
                List<string> blockReasons = offlineOnly
                    ? new List<string>()
                    : BlockReasons(info, _l2CacheDiskNumbers, _pageFileDiskNumbers);

                List<DetailLine> detail = BuildLocalDetail(info, blockReasons);
                if (offlineOnly)
                {
                    // 「重新联机」那句说明本来在对话框顶部；顶部提示行已删（2026-10-11），
                    // 改放**信息区里"盘信息"那一行下面**（第 0 行是这块盘的字节/容量/序列号）
                    detail.Insert(Math.Min(1, detail.Count), new DetailLine(Locale.T("selectDisk.hint.online"), false));
                }
                _entries.Add(new Entry
                {
                    Local = info,
                    // 「确定」能不能用**只由错误行决定**：有任意一条"会阻止启动"的原因就选不了
                    Selectable = !detail.Exists(line => line.IsError),
                    Text = Locale.T("selectDisk.item.local",
                        info.DiskNumber,
                        info.Model.Length > 0 ? info.Model : Locale.T("selectDisk.modelUnknown"),
                        info.SizeText,
                        FormatDriveSlot(info)),
                    Detail = detail
                });

                if (identitiesToPreselect.Count > 0 && MatchesIdentity(info, identitiesToPreselect))
                {
                    // 记的必须是 **_entries 的下标**，不是 disks 的下标：offlineOnly 模式下联机盘被
                    // 上面的 continue 跳过，两个下标会错位；错位后赋给 ListBox.SelectedIndex 会越界
                    // （抛 ArgumentOutOfRangeException），而异常又正好被调用方那个 try 兜成
                    // "枚举物理盘失败"，文不对题、极难定位。
                    preselect.Add(_entries.Count - 1);
                }
            }

            FinishFill(preselect);
        }

        /// <summary>该盘的身份串是否在预选清单里（身份串的生成与比对都走同一处，见 DescribeIdentity）</summary>
        private static bool MatchesIdentity(PhysicalDiskInfo info, IReadOnlyList<string> identities)
        {
            string identity = PhysicalDiskHandle.DescribeIdentity(info);
            for (int i = 0; i < identities.Count; i++)
            {
                if (string.Equals(identities[i], identity, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>【远程形态】列出**对端**磁盘</summary>
        /// <param name="disks">对端刚才报过来的清单</param>
        /// <param name="identityToPreselect">上次用的那块对端盘（若这一轮还在，就默认选中它）</param>
        public PickerForm(IReadOnlyList<RemoteDiskInfo> disks, string identityToPreselect)
            : this("selectDisk.title.remote")
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
                    // 对端盘不做本地那套硬拦判定（能不能用由对端决定），所以整段都是普通行
                    Detail = new List<DetailLine>
                    {
                        new(Locale.T("selectDisk.remote.detail",
                            gib.ToString("F1"),
                            info.BytesPerSector,
                            Locale.T(info.Rotational ? "selectDisk.rotational" : "selectDisk.nonRotational")), false)
                    }
                });

                if (identityToPreselect.Length > 0 && info.Identity == identityToPreselect) preselect = i;
            }

            FinishFill(preselect >= 0 ? new List<int> { preselect } : new List<int>());
        }

        /// <summary>
        /// 通用外壳：各模式只给标题。**顶部不再有任何提示行**（2026-10-11 删掉了这个功能）——
        /// 原来那句「重新联机」的说明改放下方信息区（见本地构造函数的 <c>offlineOnly</c> 分支）。
        /// </summary>
        /// <param name="titleKey">窗口标题的文案键</param>
        /// <param name="multiSelect">
        /// true = 多选（「启动加速」）：列表换成 <see cref="CheckedListBox"/>，**勾选复选框**即选择多块盘。
        /// </param>
        private PickerForm(string titleKey, bool multiSelect = false)
        {
            _multiSelect = multiSelect;

            if (multiSelect)
            {
                // CheckOnClick：单击复选框就切换勾选。行的"选中"只用来决定右侧显示哪块盘的详情，
                // 不再承担"选了哪些盘"的语义——所以不需要 MultiExtended，也就不需要 Ctrl/Shift。
                var checkedList = new CheckedListBox
                {
                    CheckOnClick = true,
                    SelectionMode = SelectionMode.One,
                };
                checkedList.ItemCheck += OnItemCheck;
                _checkedList = checkedList;
                _list = checkedList;
            }
            else
            {
                _list = new ListBox();
            }

            Text = Locale.T(titleKey);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(560, 356);

            _list.Location = new Point(12, 12);
            _list.Size = new Size(536, 180);
            _list.IntegralHeight = false;
            // 条目不再走自绘换行（试过 DrawMode.OwnerDrawVariable 始终没折行，已放弃）：
            // 行内也不再拼"不能加速"的长尾巴，改由下方盘信息区顶部统一列 ❌ 原因，行本身保持一行放得下。
            _list.SelectedIndexChanged += (s, e) => UpdateSelection();
            // 多选下双击不做"直接确定"：双击会先触发两次勾选切换，再确认太容易误触
            _list.DoubleClick += (s, e) => { if (!_multiSelect && _ok.Enabled) Accept(); };

            // 信息区：只读、可滚动——"不能加速"的原因可能有好几条，超出高度的靠滚动看
            _detail.Location = new Point(12, 198);
            _detail.Size = new Size(536, 110);
            _detail.ReadOnly = true;
            _detail.ScrollBars = RichTextBoxScrollBars.Vertical;
            _detail.WordWrap = true;
            // 不要系统的边框（深色下 RichEdit 会把它画成白线）；主题上完之后在 OnLoad 里还要再按一次
            _detail.BorderStyle = BorderStyle.None;
            _detail.TabStop = false;

            _ok.Text = Locale.T("common.ok");
            _ok.Location = new Point(376, 316);
            _ok.Size = new Size(84, 28);
            _ok.Click += (s, e) => Accept();

            _cancel.Text = Locale.T("common.cancel");
            _cancel.Location = new Point(466, 316);
            _cancel.Size = new Size(84, 28);
            _cancel.DialogResult = DialogResult.Cancel;

            Controls.AddRange(new Control[] { _list, _detail, _ok, _cancel });
            AcceptButton = _ok;
            CancelButton = _cancel;
        }

        private void FinishFill(IReadOnlyList<int> preselect)
        {
            foreach (Entry entry in _entries) _list.Items.Add(entry.Text);

            if (_list.Items.Count == 0)
            {
                _ok.Enabled = false;   // 空态文案由 UpdateSelection 在 OnLoad 里补（见 _detailReady）
                return;
            }

            if (_multiSelect && _checkedList != null)
            {
                // 多选：把配置里记着的那些盘逐个**勾上**（复选框才是"选了哪几块盘"的依据）
                foreach (int index in preselect)
                {
                    if (index >= 0 && index < _checkedList.Items.Count) _checkedList.SetItemChecked(index, true);
                }

                // 行高亮只用于显示详情：优先预选的第一块，否则第一块可加速的盘
                int highlight = preselect.Count > 0 ? preselect[0] : _entries.FindIndex(e => e.Selectable);
                _list.SelectedIndex = highlight >= 0 ? highlight : 0;
            }
            else if (preselect.Count > 0 && preselect[0] >= 0)
            {
                _list.SelectedIndex = preselect[0];
            }
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
        /// 五条硬拦，**逐条独立判定、全部列出**（一块盘可能同时踩中好几条，例如本程序就装在系统盘上、
        /// 而系统盘又承载分页文件——用户需要一次看全"这块盘到底哪里不行"，而不是修好一条、重试、
        /// 再发现下一条）：
        /// - **只读**：做不了写透传；
        /// - **系统盘**：整盘脱机等于把 Windows 送走；
        /// - **本程序所在盘**：整盘脱机会把本程序（连同配置/日志/L2 容器）一起送走；
        /// - **分页文件所在盘**：整盘脱机会让内核换页失败、当场蓝屏（KERNEL_DATA_INPAGE_ERROR 0x7A）；
        /// - **L2 缓存所在盘**：自己给自己加速是纯写放大，引擎启动时也会拒。
        ///
        /// 系统盘 / 程序盘 / 分页文件盘三者与引擎启动校验、L2 校验/修复共用
        /// <see cref="PhysicalDiskHandle.DescribeTargetDiskBlockReason"/> 的同一条判据
        /// （<paramref name="pageFileDiskNumbers"/> 由调用方一次性算好再传进来，避免逐盘重复全机扫描）；
        /// L2 那条与启动校验 ⑥（缓存目录不得落在被加速的盘上）同源。
        /// </summary>
        private static List<string> BlockReasons(PhysicalDiskInfo info, HashSet<int> l2CacheDiskNumbers,
            HashSet<int> pageFileDiskNumbers)
        {
            var reasons = new List<string>();

            if (info.IsReadOnly) reasons.Add(Locale.T("selectDisk.reason.readOnly"));

            if (!PhysicalDiskHandle.HasStableIdentity(info))
                reasons.Add(Locale.T("selectDisk.reason.noStableIdentity"));

            int system = PhysicalDiskHandle.GetSystemDiskNumber();
            if (system >= 0 && info.DiskNumber == system)
                reasons.Add(Locale.T("selectDisk.reason.systemDisk"));

            int program = PhysicalDiskHandle.GetProgramDiskNumber();
            if (program >= 0 && info.DiskNumber == program)
                reasons.Add(Locale.T("selectDisk.reason.programDisk"));

            if (pageFileDiskNumbers.Contains(info.DiskNumber))
                reasons.Add(Locale.T("selectDisk.reason.pageFile"));

            if (l2CacheDiskNumbers.Contains(info.DiskNumber))
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

        /// <summary>
        /// 本地盘的明细行。**只有"会阻止启动"的硬拦原因标 <see cref="DetailLine.IsError"/>=true**
        /// （渲染成红色、并使「确定」不可用）；基本信息、⚠ 警示、状态提示一律是普通行，保持正文色。
        /// </summary>
        private static List<DetailLine> BuildLocalDetail(PhysicalDiskInfo info, IReadOnlyList<string> blockReasons)
        {
            var lines = new List<DetailLine>();

            // 第一行留给**基本信息**（字节/扇区、容量、序列号）：它是"这块盘是谁"的锚点，任何时候都要看得见。
            lines.Add(new DetailLine(Locale.T("selectDisk.detail.header",
                info.BytesPerSector,
                info.SizeText,
                info.SerialNumber.Length > 0 ? info.SerialNumber : Locale.T("selectDisk.serialNone")), false));

            // 不能加速的原因紧跟在基本信息之后（再往上顶会把基本信息挤出可视区）；
            // 它们就是"会阻止启动"的那几条，逐条标红
            foreach (string reason in blockReasons) lines.Add(new DetailLine(reason, true));

            if (info.RemovableMedia)
            {
                lines.Add(new DetailLine(Locale.T("selectDisk.detail.removable"), false));
            }
            // 机械盘/SSD 提示落在这里、而不是启动后的日志里：用户**选它的那一刻**就该看到
            //（本程序的收益来自机械盘；SSD 做块级缓存通常净亏——缓存读写会和源数据抢同一块盘）
            if (info.SeekPenaltyKnown && !info.IncursSeekPenalty)
            {
                lines.Add(new DetailLine(Locale.T("selectDisk.detail.noSeekPenalty"), false));
            }
            else if (!info.SeekPenaltyKnown)
            {
                lines.Add(new DetailLine(Locale.T("selectDisk.detail.seekUnknown"), false));
            }
            return lines;
        }

        /// <summary>
        /// 句柄就绪、主题上色之后才渲染信息区**第一屏**：构造期控件还没有句柄，
        /// `RichTextBox` 的选色 / 追加都落不下去（只会得到错色或空内容），所以统一推迟到这里。
        /// 「确定」的可用性不依赖它——构造期在 <see cref="FinishFill"/> 里就已经定好。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 深色下 ThemeManager 会把输入框**强制**成 FixedSingle 边框（构造里设的 None 会被它盖掉），
            // 而 RichEdit 画这条边用的是系统"浅色应用"的颜色 ⇒ 深色底上就是一圈白线。
            // 信息区不要边框（与检查器那块只读文本框观感一致），所以在主题上完之后按回 None。
            // **顺序要紧**：改 BorderStyle 会重建控件句柄，必须排在 ApplyDetailScrollbarTheme 之前，
            // 否则刚设好的深色滚动条会被新建的句柄丢掉。
            _detail.BorderStyle = BorderStyle.None;

            ApplyDetailScrollbarTheme();   // 句柄此时已就绪：深色下把原生滚动条也切深
            _detailReady = true;
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            int index = _list.SelectedIndex;
            bool hasSelection = index >= 0 && index < _entries.Count;

            if (_multiSelect)
            {
                // 多选：**勾选状态**决定"选了哪些盘"；行高亮只决定右侧显示哪块盘的详情。
                _ok.Enabled = CheckedAllSelectable();
                if (_detailReady)
                {
                    ShowDetail(hasSelection
                        ? _entries[index].Detail
                        : new List<DetailLine> { new(Locale.T(_emptyTextKey), false) });
                }
                return;
            }

            if (!hasSelection)
            {
                _ok.Enabled = false;
                if (_detailReady) ShowDetail(new List<DetailLine> { new(Locale.T(_emptyTextKey), false) });
                return;
            }

            Entry entry = _entries[index];
            _ok.Enabled = entry.Selectable;
            if (_detailReady) ShowDetail(entry.Detail);
        }

        /// <summary>
        /// 多选：勾选状态变化后刷新「确定」的可用性。
        /// 勾选状态在 <see cref="ItemCheck"/> 返回后才真正落定，所以这里延后一拍再读 <see cref="CheckedListBox.CheckedIndices"/>。
        /// 同时拒绝勾选 ❌ 不可加速的盘（但允许选中该行查看原因）。
        /// </summary>
        private void OnItemCheck(object? sender, ItemCheckEventArgs e)
        {
            if (_checkedList == null) return;

            if (e.Index >= 0 && e.Index < _entries.Count && !_entries[e.Index].Selectable
                && e.NewValue == CheckState.Checked)
            {
                e.NewValue = CheckState.Unchecked;
                return;
            }

            // 构造期（FinishFill 里的 SetItemChecked）还没有窗口句柄，不能 BeginInvoke；
            // 构造末尾会统一 UpdateSelection 一次。
            if (IsHandleCreated) BeginInvoke(new Action(UpdateSelection));
        }

        /// <summary>多选模式下：勾选的项是否全都可选（且至少勾选一项）</summary>
        private bool CheckedAllSelectable()
        {
            if (_checkedList == null) return false;

            bool any = false;
            foreach (int index in _checkedList.CheckedIndices)
            {
                if (index < 0 || index >= _entries.Count) continue;
                if (!_entries[index].Selectable) return false;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// 把明细行刷进信息区：<see cref="DetailLine.IsError"/> 的行用告警红，其余用正文色（⚠ 警示保持原色）；
        /// 刷完把滚动位置**复位到顶部**，否则切到下一块盘会停在上一块盘的滚动位置。
        /// **只在句柄就绪之后调用**（见 <see cref="_detailReady"/>）。
        /// </summary>
        private void ShowDetail(IReadOnlyList<DetailLine> lines)
        {
            Color error = ThemeManager.Palette.DangerText;
            Color normal = ThemeManager.InputTextColor;

            _detail.Clear();
            for (int i = 0; i < lines.Count; i++)
            {
                _detail.SelectionColor = lines[i].IsError ? error : normal;
                _detail.AppendText(lines[i].Text);
                if (i < lines.Count - 1) _detail.AppendText("\n");
            }

            // Clear() 会把段落格式也重置回默认，所以每次渲染都得重新收紧行距
            TightenDetailLineSpacing();

            // 滚动位置复位到顶部，否则切到下一块盘会停在上一块盘的滚动位置
            _detail.SelectionLength = 0;
            _detail.SelectionStart = 0;
            _detail.ScrollToCaret();
        }

        /// <summary>
        /// 收紧信息区行距。RichEdit 默认按字体的**单倍行距**排（含行间留白），中文下明显偏松；
        /// 这里压到**字身框高度**（ascent + descent，不含行间留白）再留 <see cref="DetailLineSpacingSlack"/> 余量。
        /// 这是"不裁字"前提下的最小值——比它再小就会把上下笔画切掉。
        /// 只压行距、不动字号。必须在下文 <see cref="ShowDetail"/> 渲染完之后调用（`Clear()` 会重置段落格式）；
        /// 会把选区拉到全文，**调用方负责复位**。
        /// </summary>
        private void TightenDetailLineSpacing()
        {
            Font font = _detail.Font;
            FontFamily family = font.FontFamily;
            // 字体内部用"设计单位"记字身框，换算成磅要除以 em：字身框(设计单位) / em × 字号
            float cellPoints = (family.GetCellAscent(font.Style) + family.GetCellDescent(font.Style))
                               * font.SizeInPoints / family.GetEmHeight(font.Style);

            var format = new PARAFORMAT2
            {
                cbSize = Marshal.SizeOf<PARAFORMAT2>(),
                dwMask = PFM_LINESPACING,
                // ByValArray 必须给足 32 个元素，否则封送时会抛
                rgxTabs = new int[32],
                dyLineSpacing = (int)Math.Round(cellPoints * 20 * DetailLineSpacingSlack),   // 磅 → twips
                bLineSpacingRule = LineSpacingExactly,
            };

            _detail.SelectAll();
            SendMessage(_detail.Handle, EM_SETPARAFORMAT, IntPtr.Zero, ref format);
        }

        /// <summary>
        /// 深色下把信息区的**原生滚动条**也切成深色。
        /// 系统不知道本程序是深色应用，默认给控件画的是浅色滚动条——在深色底上就是一道白条。
        /// <c>DarkMode_Explorer</c> 需要 Windows 10 1809+；浅色主题下不必调（默认就是浅色）。
        /// </summary>
        private void ApplyDetailScrollbarTheme()
        {
            if (ThemeManager.IsDark) SetWindowTheme(_detail.Handle, "DarkMode_Explorer", null);
        }

        private void Accept()
        {
            if (_multiSelect)
            {
                // 多选（「启动加速」）：把**勾选**的本机盘全部收下；一块都没勾就什么都不做（不关闭对话框）
                SelectedDisks.Clear();
                if (_checkedList != null)
                {
                    foreach (int index in _checkedList.CheckedIndices)
                    {
                        if (index < 0 || index >= _entries.Count) continue;
                        Entry entry = _entries[index];
                        if (!entry.Selectable || entry.Local == null) continue;
                        SelectedDisks.Add(entry.Local);
                    }
                }
                if (SelectedDisks.Count == 0) return;

                // 单值这个属性仍保留：多选模式下 = 勾选顺序中的第一块（列表顺序），方便只看一块的调用方
                SelectedDisk = SelectedDisks[0];
            }
            else
            {
                int index = _list.SelectedIndex;
                if (index < 0 || index >= _entries.Count || !_entries[index].Selectable) return;

                SelectedDisk = _entries[index].Local;
                SelectedRemoteDisk = _entries[index].Remote;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>行距在"字身框"之上再留的余量：吸收取整误差与 CJK 字形的出框外溢</summary>
        private const double DetailLineSpacingSlack = 1.05;

        #region Win32（行距 + 深色滚动条）
        // 这两件事都没有托管 API：
        // ① 行距：RichEdit 只认 PARAFORMAT2，得走 EM_SETPARAFORMAT；
        // ② 滚动条：系统按"浅色应用"给控件画**原生**滚动条，得用 SetWindowTheme 切到深色变体。

        private const int EM_SETPARAFORMAT = 0x0447;      // WM_USER + 71
        private const uint PFM_LINESPACING = 0x00000100;
        private const byte LineSpacingExactly = 4;        // dyLineSpacing 的单位是 twips，按"精确行高"用

        /// <summary>Richedit.h 的 PARAFORMAT2。**字段顺序与尺寸必须与 SDK 一致**，否则 RichEdit 会读错。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct PARAFORMAT2
        {
            public int cbSize;
            public uint dwMask;
            public short wNumbering;
            public short wReserved;
            public int dxStartIndent;
            public int dxRightIndent;
            public int dxOffset;
            public short wAlignment;
            public short cTabCount;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
            public int[] rgxTabs;
            public int dySpaceBefore;
            public int dySpaceAfter;
            public int dyLineSpacing;
            public short sStyle;
            public byte bLineSpacingRule;
            public byte bOutlineLevel;
            public short wShadingWeight;
            public short wShadingStyle;
            public short wNumberingStart;
            public short wNumberingStyle;
            public short wNumberingTab;
            public short wBorderSpace;
            public short wBorderWidth;
            public short wBorders;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref PARAFORMAT2 lParam);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);
        #endregion
    }
}
