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
using System.Drawing.Text;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;
using FlyDisk.Theming;
using Timer = System.Windows.Forms.Timer;

namespace FlyDisk
{
    /// <summary>
    /// 主窗体：单进程形态下它同时是"管理界面"和"服务宿主"的控制器——
    /// 启停按钮直接驱动 <see cref="TargetService"/>（脱机 / 独占 / 起 iSCSI target），
    /// 状态栏每秒读一次引擎快照，日志窗口直接订阅进程内日志事件。**不再有 IPC、不再有 Windows 服务。**
    /// </summary>
    public partial class Form1 : ThemedForm, ILocalizable
    {
        private readonly DiskConfig _config;
        private readonly TargetService _target;
        private bool _targetOperationPending;
        private bool _targetOperationStarting;
        private bool HasTargetWork => _targetOperationPending || _target.IsStopping;

        private Inspector? _inspector;
        private readonly Timer _statusTimer;
        private readonly ToolStripMenuItem _verifyL2MenuItem;
        private readonly ToolStripMenuItem _reonlineMenuItem;
        private readonly ToolStripMenuItem _remoteMenuItem;
        private readonly ToolStripMenuItem _languageMenuItem;
        private readonly ToolStripMenuItem _zhLanguageMenuItem;
        private readonly ToolStripMenuItem _enLanguageMenuItem;

        // ===== 托盘（加速期间关闭主窗口只是"缩到托盘"，程序与加速照常跑）=====
        /// <summary>
        /// 通知区图标（见 <see cref="OnFormClosing"/>）：**只在主窗口缩到托盘时可见**，左键单击把主窗口叫回来。
        /// 它**不是**退出入口——真正的退出是"先停止加速、再关窗口"。
        /// </summary>
        private readonly NotifyIcon _tray;

        /// <summary>本窗体的控件文本登记表（见 <see cref="ILocalizable"/>；主窗体的文本几乎都是动态的，这里主要先留骨架）</summary>
        public Dictionary<string, List<Control>> TextBindings { get; } = new();

        // ===== 远程形态的运行期状态（见 后续待办.md 第一节）=====
        // **角色不是一个配置字段**：它由"谁开放监听 / 谁配对"协商产生，下面这两个引用就是它的全部体现。
        /// <summary>监听方：接受连接用的监听器（拨号方为 null）。断开配对后仍保留，可以继续等下一个对端</summary>
        private TcpListener? _remoteListener;

        /// <summary>监听循环是否已经在跑（0/1，用 Interlocked 保证只有一个 Accept 循环）</summary>
        private int _accepting;

        /// <summary>
        /// 当前配对的对端——**两端共用这一个类型**，差别只在"谁监听、谁拨号"（见 RemotePeer 的类注释）。
        /// 配对之后两端对等：本端既能被对方要盘，也能反过来要对方的盘。
        /// </summary>
        private RemotePeer? _peer;

        private string _remoteStatus = string.Empty;
        private bool _remoteStartPending;                    // 已发出选盘请求、正在等对端确认
        private string _remoteDiskIdentity = string.Empty;   // 上次用的那块对端盘（选盘对话框里默认选它）

        public Form1()
        {
            InitializeComponent();
            ApplyTitleFont();

            // 日志框：终端造型，但**跟随主题**——浅色白底黑字、深色黑底白字。
            // 仍排除在换肤之外：换肤会给它刷上"输入框"的底色（InputBg，深色下是深灰而非纯黑），
            // 这里要的是纯白 / 纯黑的观感，所以登记 Exclude 后由下面两行自己定色。
            // 主题在启动时定死、运行中不变，构造时设一次即可。要在首次上色之前登记 Exclude。
            ThemeManager.Exclude(txtLog);
            txtLog.BackColor = ThemeManager.IsDark ? Color.Black : Color.White;
            txtLog.ForeColor = ThemeManager.IsDark ? Color.White : Color.Black;

            // 配置在启动那一刻被读进 TargetService，运行中改配置不生效 ⇒ 设置入口在运行时会锁住
            _config = ConfigService.Load();
            _target = new TargetService(_config);

            // 发起程序登录（iSCSI 连接成功）：引擎在工作线程上抛事件，这里直接交给 Log——
            // Log 自身会切回 UI 线程（InvokeRequired），且文案在 UI 侧现取（Locale.T），跟随当前语言。
            _target.OnInitiatorConnected += (initiator, endpoint) =>
                Log(Locale.T("main.log.iscsiConnected", initiator, endpoint));

            设置ToolStripMenuItem.Click += 设置ToolStripMenuItem_Click;
            检查器ToolStripMenuItem.Click += 检查器ToolStripMenuItem_Click;

            // 「校验 L2」入口：校验/修复的整个功能都收在 L2Verify.cs 里，这里只挂一个菜单项
            // （与设置/检查器平级；运行时置灰——它要独占 L2 容器与源盘）
            _verifyL2MenuItem = new ToolStripMenuItem(Locale.T("menu.verifyL2"));
            _verifyL2MenuItem.Click += 校验L2ToolStripMenuItem_Click;
            menuStrip1.Items.Add(_verifyL2MenuItem);

            // 「重新联机源盘」入口：加速停止后那块盘仍保持脱机（刻意口径），要还给系统就点这里——
            // 复用启动时的选盘对话框，只列脱机盘（见 后续待办.md 第十二节）。
            // 运行中置灰（盘此刻是本程序的块源，联机 = 同签名双盘 + 双写）。
            _reonlineMenuItem = new ToolStripMenuItem(Locale.T("menu.reonline"));
            _reonlineMenuItem.Click += 重新联机源盘ToolStripMenuItem_Click;
            menuStrip1.Items.Add(_reonlineMenuItem);

            // 「远程」入口：与「校验 L2」同构（一个顶层项 = 一个对话框）。
            // 本地加速运行中置灰（三职责互斥）；远程已配对 / 运行中时可用（用户要能查看与断开）。
            _remoteMenuItem = new ToolStripMenuItem(Locale.T("menu.remote"));
            _remoteMenuItem.Click += 远程ToolStripMenuItem_Click;
            menuStrip1.Items.Add(_remoteMenuItem);

            // 「语言」入口：**直接放在菜单栏**（不塞进设置窗的犄角旮旯），切换**即时生效**并持久化。
            _zhLanguageMenuItem = new ToolStripMenuItem(Locale.T("menu.language.zh"));
            _zhLanguageMenuItem.Click += (s, e) => SwitchLanguage("zh-CN");
            _enLanguageMenuItem = new ToolStripMenuItem(Locale.T("menu.language.en"));
            _enLanguageMenuItem.Click += (s, e) => SwitchLanguage("en-US");
            _languageMenuItem = new ToolStripMenuItem(Locale.T("menu.language"));
            _languageMenuItem.DropDownItems.Add(_zhLanguageMenuItem);
            _languageMenuItem.DropDownItems.Add(_enLanguageMenuItem);
            menuStrip1.Items.Add(_languageMenuItem);

            // 托盘：加速期间关闭主窗口**不退出**，只是把窗口缩到通知区（见 OnFormClosing）；
            // 真正退出的路径只有一条——**先点「停止加速」再关窗口**（那时 IsRunning 为假，走正常关闭），
            // 所以这里不需要"退出"菜单项，只要能把窗口叫回来。
            // **只在主窗口藏起来时才有这个图标**：窗口还在屏幕上时不占通知区，叫回窗口后立刻撤掉。
            // **只会有主窗口进出托盘**——检查器是独立顶层窗口、悬浮方块归检查器自己管，都不跟着走。
            _tray = new NotifyIcon
            {
                Icon = AppIcon.Current,   // 应用图标，随系统深浅主题（启动时定，见 Theming\AppIcon.cs）
                Text = Locale.T("app.title"),
                Visible = false,   // 开关交给 OnFormClosing / RestoreFromTray：窗口在哪，图标就在哪
            };
            // 左键单击即把主窗口叫回来（右键无动作，也就不需要 ContextMenuStrip）
            _tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) RestoreFromTray();
            };

            ApplyDynamicText();   // 菜单文案 / 勾选状态按当前语言就位（状态栏首次刷新由 Load 负责）

            _statusTimer = new Timer { Interval = 1000 };
            _statusTimer.Tick += (s, e) => RefreshStatus();
            _statusTimer.Start();

            // 右下角那一行的位置是"算出来"的（见 PlaceBadgeRow）：最小化/恢复那一瞬 ClientSize 可能
            // 给出怪值（与副标题那次踩的坑同源），这里在尺寸变化后重摆一次兜住——还没显示时不动。
            ClientSizeChanged += (s, e) =>
            {
                if (lblStarHint.Visible) PlaceBadgeRow();
            };
        }

        /// <summary>
        /// 切换界面语言：即时生效（<see cref="LocalizationManager.ApplyAll"/> 重刷所有已开窗体）+ 持久化。
        /// **即时切换还需各窗体重跑状态渲染函数**，否则"已启动"这类当前句子会停留在旧语言——
        /// 主窗体的重刷在 <see cref="ApplyDynamicText"/> 里（它转调 <see cref="RefreshStatus"/>）。
        /// </summary>
        private void SwitchLanguage(string language)
        {
            Locale.SetLanguage(language);
            _config.Language = Locale.Current;
            ConfigService.Save(_config, out _);
            LocalizationManager.ApplyAll();
        }

        /// <summary>
        /// 语言变化后重刷主窗体上**不由控件 `Text` 直接承载**的文本：
        /// 菜单项（<see cref="ToolStripItem"/> 不继承 <see cref="Control"/>，摸不到）、勾选状态，
        /// 以及状态机驱动的当前句子（转调 <see cref="RefreshStatus"/>）。
        /// </summary>
        public void ApplyDynamicText()
        {
            设置ToolStripMenuItem.Text = Locale.T("menu.settings");
            检查器ToolStripMenuItem.Text = Locale.T("menu.inspector");
            _verifyL2MenuItem.Text = Locale.T("menu.verifyL2");
            _reonlineMenuItem.Text = Locale.T("menu.reonline");
            _remoteMenuItem.Text = Locale.T("menu.remote");
            _languageMenuItem.Text = Locale.T("menu.language");
            _zhLanguageMenuItem.Text = Locale.T("menu.language.zh");
            _enLanguageMenuItem.Text = Locale.T("menu.language.en");

            _zhLanguageMenuItem.Checked = Locale.Current == "zh-CN";
            _enLanguageMenuItem.Checked = Locale.Current == "en-US";

            Text = Locale.T("app.title");
            _tray.Text = Text;   // 通知区的悬停提示与窗口标题同源（切语言时一起刷）
            btnStartStop.Text = Locale.T("main.button.start");
            SetSubtitle(Locale.T("main.subtitle.local"));

            // 右下角那一行：提示语 + "按钮"的默认文字，两者都随语言变、宽度也变
            // ⇒ 若已显示就重摆一次（见 PlaceBadgeRow）
            lblStarHint.Text = Locale.T("main.github.starHint");
            lblBadgePlaceholder.Text = Locale.T("main.github.placeholder");
            if (lblStarHint.Visible) PlaceBadgeRow();

            if (IsHandleCreated) RefreshStatus();
        }

        /// <summary>
        /// 给标题与副标题装上**嵌入的**像素字体（见 <see cref="EmbeddedFont"/>）。
        ///
        /// **只有这两个控件用它**：这是 12px 点阵字体，比同字号的普通字体更难读，正文一律不用
        /// （Designer 里那两行 `new Font("Fusion Pixel …")` 已删，源头收到这里）。
        /// 字号与字重沿用 Designer 原来的值（26.25pt 粗 / 12pt 常规），所以版式不变。
        ///
        /// **刻意不碰位置**：副标题的居中归 <see cref="SetSubtitle"/> 管（它在文案真正变化时居中），
        /// 这里只换字体。
        /// </summary>
        private void ApplyTitleFont()
        {
            FontFamily family = EmbeddedFont.Family;
            labelTitle.Font = new Font(family, 26.25F, FontStyle.Bold, GraphicsUnit.Point, 254);
            labelSubtitle.Font = new Font(family, 12F, FontStyle.Regular, GraphicsUnit.Point, 254);
        }

        #region 菜单

        private void 设置ToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (_target.IsRunning || HasTargetWork)
            {
                MessageBox.Show(Locale.T("main.msg.runningCannotEdit"),
                    Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using (var settingsForm = new Settings(_config))
            {
                if (settingsForm.ShowDialog() != DialogResult.OK) return;
            }

            // 配置写入 %ProgramData%\FlyDisk\config.json；写失败必须如实报错——
            // 不能说"已保存"却什么都没写进去（用户会以为配置生效了，见 未解决的疑点.md TD-10）
            if (ConfigService.Save(_config, out string error))
            {
                Log(Locale.T("main.log.configSaved", ServiceConstants.ConfigFilePath));
            }
            else
            {
                Log(Locale.T("main.log.configSaveFailed", error));
                MessageBox.Show(
                    Locale.T("main.msg.configSaveFailed", error, ServiceConstants.ConfigFilePath),
                    Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void 检查器ToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (_inspector == null || _inspector.IsDisposed)
            {
                _inspector = new Inspector(_target);
            }
            _inspector.Show();
            _inspector.BringToFront();
        }

        /// <summary>
        /// 「校验 L2」：打开只读校验对话框（先校验、看到结果后再决定是否修复）。
        /// 目标运行中由状态栏刷新把它置灰，这里再兜一次——校验要独占 L2 容器与源盘，运行中必然抢不到锁。
        /// </summary>
        private void 校验L2ToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (_target.IsRunning || HasTargetWork)
            {
                MessageBox.Show(Locale.T("main.msg.verifyRunningCannot"),
                    Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 校验要针对**某一块具体的盘**。容器头里记着它的归属身份（型号|序列号|容量|扇区 的散列），
            // 所以候选直接收窄到"身份匹配的那块盘"——用户不必从一堆盘里去认。标记是散列、不可逆，
            // 只能枚举本地盘重算比对（见 L2Verifier.TryReadOwnerIdentity）。
            PhysicalDiskInfo? target;
            try
            {
                List<PhysicalDiskInfo> disks = PhysicalDiskHandle.Enumerate();
                if (disks.Count == 0)
                {
                    MessageBox.Show(Locale.T("main.msg.noDisks"), Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                long? owner = L2Verifier.TryReadOwnerIdentity(_config);
                if (owner.HasValue)
                {
                    List<PhysicalDiskInfo> owned = disks.Where(d => L2Verifier.Identify(d) == owner.Value).ToList();
                    if (owned.Count == 0)
                    {
                        // 容器在、但归属的那块盘不在本机：没接上 / 已在别处联机 / 远程形态（本地校验只支持本地盘）。
                        // 此时**不回退到完整清单**——本机没有任何正确的候选，让用户从盘堆里挑只会挑错。
                        MessageBox.Show(Locale.T("main.msg.verifyNoOwnerDisk"),
                            Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    disks = owned;
                }

                using var pick = new SelectDiskForm(disks, _config.PhysicalDiskIdentity,
                    l2CacheDiskNumber: PhysicalDiskHandle.GetL2CacheDiskNumber(_config.EnableSsdCache, _config.SsdCachePath));
                if (pick.ShowDialog(this) != DialogResult.OK || pick.SelectedDisk == null) return;
                target = pick.SelectedDisk;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Locale.T("main.msg.enumerateFailed", ex.Message), Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var dialog = new L2VerifyForm(_config, target.DiskNumber))
            {
                dialog.ShowDialog(this);
            }
            RefreshStatus();
        }

        /// <summary>
        /// 「重新联机源盘」：把本程序脱机的那块盘交回系统（等价于「磁盘管理 → 联机」）。
        ///
        /// 为什么要有它：停止加速后盘**刻意保持脱机**（盘归本程序管 ⇒ 跨重启的 L2 账本才敢直接采信），
        /// 以前只能让用户自己去磁盘管理点。这里复用启动时的选盘对话框，**只列脱机盘**。
        ///
        /// 代价已当面说清（对话框的提示语就写着）：联机后该盘**脱离本程序控制**，
        /// 下次加速时 L2 缓存会被判为过期而要求校验——因为它可能已被别的程序写过。
        /// </summary>
        private void 重新联机源盘ToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (_target.IsRunning || HasTargetWork)
            {
                MessageBox.Show(Locale.T("main.msg.reonlineRunningCannot"),
                    Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 联机哪一块由用户定（与启动/校验共用同一个选盘对话框与同一份清单）
            PhysicalDiskInfo? picked;
            try
            {
                List<PhysicalDiskInfo> disks = PhysicalDiskHandle.Enumerate();
                if (disks.Count == 0)
                {
                    MessageBox.Show(Locale.T("main.msg.noDisks"), Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                using var pick = new SelectDiskForm(disks, _config.PhysicalDiskIdentity, offlineOnly: true);
                if (pick.ShowDialog(this) != DialogResult.OK || pick.SelectedDisk == null) return;
                picked = pick.SelectedDisk;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Locale.T("main.msg.enumerateFailed", ex.Message), Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (_target.TryReonlineDisk(picked.DiskNumber, out string error))
            {
                Log(Locale.T("main.log.reonlineDone", picked.DiskNumber));
                Log(Locale.T("main.log.reonlineCacheWarn"));
            }
            else
            {
                Log(Locale.T("main.log.reonlineFailed", error));
                MessageBox.Show(error, Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            RefreshStatus();
        }

        #region 远程

        /// <summary>
        /// 「远程」对话框：**配对关系与监听的唯一管理入口**。
        /// 它是**一次性的**——操作做完就关，长期状态由主界面那四处可变文本显示（见 后续待办.md 第一节的 UI 定稿）。
        ///
        /// 远程整体是**实验性特性**：进门前要先让用户在"不保证可用、不保证数据安全"上明确点头，
        /// 所以下面第一件事就是那道警告（**每次都弹**，不做"不再提示"）。
        /// </summary>
        private void 远程ToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (HasTargetWork) return;
            if (!ConfirmExperimentalRemote()) return;

            using var dialog = new RemoteForm(
                _config,
                isActive: () => _peer != null || _remoteListener != null,
                statusText: () => _remoteStatus,
                canDisconnect: () => !_target.IsRunning && !HasTargetWork && !_remoteStartPending,
                onOpenServer: OpenRemoteServer,
                onPair: PairRemoteClient,
                onDisconnect: DisconnectRemote);

            dialog.ShowDialog(this);
            ConfigService.Save(_config, out _);
            RefreshStatus();
        }

        /// <summary>
        /// 「远程」的实验性特性警告。
        ///
        /// 要挡住的是两类后果，必须写明白：
        /// ① **不保证可用**——配对/传输都可能失败或卡住，它是自研协议、只经过有限的联调；
        /// ② **不保证数据安全**——通道**不加密**，而且一旦同意对端要盘，本机会把**整块物理盘脱机后交出去**，
        ///    对方在其上写什么、程序出什么缺陷，都可能落到你的数据上。
        ///
        /// 返回 false = 用户没同意，**不进入远程界面**（不是"进去但不操作"）。
        /// </summary>
        private bool ConfirmExperimentalRemote()
        {
            DialogResult answer = MessageBox.Show(
                Locale.T("main.msg.experimentalRemote"),
                Locale.T("dialog.experimental"), MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);   // 默认落在"取消"上：误按回车不该让人进到这一步

            return answer == DialogResult.OK;
        }

        /// <summary>【监听方】开放监听。**此时不脱机任何盘**——只有对端点名要某块盘、你点了同意之后才脱机</summary>
        private void OpenRemoteServer(int port)
        {
            try
            {
                _config.RemoteListenPort = port;
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start();
                _remoteListener = listener;
                _remoteStatus = Locale.T("main.remote.listening", port);

                Log(Locale.T("main.log.remoteServiceOpened", port));
                Log(Locale.T("main.log.remoteNoEncryption"));

                EnsureAccepting();
            }
            catch (Exception ex)
            {
                _remoteListener = null;
                MessageBox.Show(Locale.T("main.msg.openRemoteFailed", ex.Message), Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            RefreshStatus();
        }

        /// <summary>
        /// 【监听方】在后台**循环**等对端连上来（直到监听器被关闭）。
        ///
        /// **必须循环**：只接一次的话，"断开后再配对"就没人接了——对端 TCP 连得上（backlog 收得下），
        /// 但没人跟它握手，它会**卡在"正在配对远程服务"**上。
        /// </summary>
        private void AcceptLoop(TcpListener listener)
        {
            try
            {
                while (true)
                {
                    RemotePeer? peer;
                    try
                    {
                        // 分派规则见 RemotePeer.Accept：新建实例 / 挂到旧实例 / 返回 null（拒绝）
                        peer = RemotePeer.Accept(listener, _peer);
                    }
                    catch (Exception ex)
                    {
                        // **只有"监听器真被关掉"才是正常退出**（用户在「远程」里点了关闭）。
                        // 其余异常一律是"这一条连接是垃圾"——连上却不发 Hello 的探针、扫端口的扫描器、
                        // 握手途中被掐断。**绝不能把循环带走**：循环一死，监听 socket 还在、内核照旧完成
                        // 三次握手，于是对端"连得上却永远等不到 HelloAck"，表现为配对长时间不响应（已实际踩到）。
                        // 判据是"字段里的引用还是不是我自己"——关闭服务时会先把它摘成 null。
                        if (!ReferenceEquals(_remoteListener, listener)) return;
                        Log(Locale.T("main.log.droppedHandshake", ex.Message));
                        continue;
                    }

                    if (peer == null)
                    {
                        Invoke(new Action(() =>
                        {
                            Log(Locale.T("main.log.rejectedNewConnection"));
                            RefreshStatus();
                        }));
                        continue;
                    }

                    // 用同步 Invoke：让"认领"在下一轮 Accept 之前完成
                    Invoke(new Action(() =>
                    {
                        if (ReferenceEquals(peer, _peer))
                        {
                            // 旧实例的链路接回来了（它可能在用对方的盘，本地 target 不受影响）
                            _remoteStatus = Locale.T("main.remote.pairedWith", peer.PeerName);
                            Log(Locale.T("main.log.connectionRestored", peer.PeerName));
                        }
                        else
                        {
                            _peer?.Dispose();
                            _peer = peer;
                            HookPeer(peer);
                            _remoteStatus = Locale.T("main.remote.pairedWith", peer.PeerName);
                            Log(Locale.T("main.log.paired", peer.PeerName));
                        }
                        RefreshStatus();
                    }));
                }
            }
            finally
            {
                Interlocked.Exchange(ref _accepting, 0);
            }
        }

        /// <summary>确保监听循环在跑（开放服务时、以及断开配对之后，都要重新开始等下一个对端）</summary>
        private void EnsureAccepting()
        {
            TcpListener? listener = _remoteListener;
            if (listener == null) return;
            if (Interlocked.CompareExchange(ref _accepting, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try
                {
                    AcceptLoop(listener);
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _accepting, 0);
                    Log(Locale.T("main.log.acceptLoopCrashed", ex.Message));
                }
            });
        }

        /// <summary>给新配对的对端挂回调（两端共用同一套）</summary>
        private void HookPeer(RemotePeer peer)
        {
            peer.OnAuthorize = AuthorizeRemoteDisk;
            peer.OnStateChanged += () => { if (IsHandleCreated) BeginInvoke(new Action(RefreshStatus)); };
            peer.OnServiceEnded += OnRemoteServiceEnded;

            // **对端主动断开**（不是掉线）：清掉它、回到"等待配对"，并重新开始等下一个对端。
            // 不处理的话，本端会一直停在"已配对·等待重连"——实际再也不会有人连回来。
            peer.OnPeerLeft += () =>
            {
                if (!IsHandleCreated) return;
                BeginInvoke(new Action(() =>
                {
                    if (ReferenceEquals(_peer, peer)) _peer = null;
                    _remoteStartPending = false;
                    _remoteStatus = _remoteListener != null ? Locale.T("main.remote.listeningWaiting") : string.Empty;
                    Log(Locale.T("main.log.peerLeft", peer.PeerName));
                    EnsureAccepting();
                    RefreshStatus();
                }));
            };
        }

        /// <summary>
        /// 【服务端】全流程**唯一的人工闸门**："是否信任并同意脱机"。
        /// 由协议服务端在它的连接线程上调用，这里 marshal 回 UI 线程等用户点击
        /// （是同步 Invoke：连接线程会一直等到你点完，期间 UI 本身是响应的）。
        /// </summary>
        private bool AuthorizeRemoteDisk(PhysicalDiskInfo target, string client)
        {
            if (InvokeRequired)
            {
                return (bool)Invoke(new Func<PhysicalDiskInfo, string, bool>(AuthorizeRemoteDisk), target, client);
            }

            if (_target.IsRunning || HasTargetWork) return false;

            DialogResult answer = MessageBox.Show(
                Locale.T("main.msg.authorizeRemote", target.DiskNumber, target.Model,
                    target.SizeText, target.BytesPerSector, client),
                Locale.T("dialog.authorizeRemote"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            return answer == DialogResult.Yes;
        }

        /// <summary>【监听方】关闭远程服务（**还配对着时拒绝**，与"挂着 iSCSI 就拒绝停止"同源）</summary>
        private void CloseRemoteServer()
        {
            if (_remoteListener == null) return;

            if (_peer != null)
            {
                MessageBox.Show(
                    Locale.T("main.msg.stillPairedCannotClose", _peer.PeerName),
                    Locale.T("dialog.closeRejected"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // **先摘引用、再 Stop**：AcceptLoop 靠"字段里的引用还是不是它自己"来区分
            // "我是被用户关掉的（正常退出）"与"刚才是某条垃圾连接（记条日志继续接）"。
            // 顺序反过来的话，Stop 触发的那次异常会被当成垃圾连接，循环就在一个死监听器上空转了。
            TcpListener? listener = _remoteListener;
            _remoteListener = null;
            try { listener?.Stop(); } catch { /* 已经停了 */ }
            _remoteStatus = string.Empty;
            Log(Locale.T("main.log.remoteServiceClosed"));
            RefreshStatus();
        }

        /// <summary>【客户端】发起配对（在后台线程做网络连接，**不阻塞 UI**）</summary>
        private void PairRemoteClient(string address)
        {
            if (!TryParseAddress(address, out string host, out int port))
            {
                MessageBox.Show(Locale.T("main.msg.badAddressFormat"), Locale.T("dialog.tip"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _remoteStatus = Locale.T("main.remote.pairing", address);
            Log(Locale.T("main.log.pairingRemote", address));
            RefreshStatus();

            Task.Run(() =>
            {
                try
                {
                    RemotePeer peer = RemotePeer.Connect(host, port);
                    BeginInvoke(new Action(() =>
                    {
                        _peer = peer;
                        HookPeer(peer);
                        _remoteStatus = Locale.T("main.remote.pairedWithAddress", peer.PeerName, address);
                        Log(Locale.T("main.log.pairedWithAddress", peer.PeerName, address));
                        RefreshStatus();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _remoteStatus = string.Empty;
                        Log(Locale.T("main.log.pairFailed", ex.Message));
                        MessageBox.Show(Locale.T("main.msg.pairFailed", ex.Message), Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        RefreshStatus();
                    }));
                }
            });
        }

        private static bool TryParseAddress(string address, out string host, out int port)
        {
            host = string.Empty;
            port = 0;
            int colon = address.LastIndexOf(':');
            if (colon <= 0 || colon == address.Length - 1) return false;
            host = address[..colon].Trim();
            return int.TryParse(address[(colon + 1)..].Trim(), out port) && port > 0 && port <= 65535;
        }

        /// <summary>
        /// 【已配对】取消正在进行的"选盘 + 等对端授权"：**只放弃这次请求，配对与连接都保留**。
        /// 与「断开配对」是两回事——后者是解除连接层的动作（见 <see cref="DisconnectRemote"/>）。
        /// 之所以由主按钮承担：上一步就是这枚按钮按下去的（「启动加速」），在同一枚按钮上取消最符合直觉。
        /// </summary>
        private void CancelRemoteRequest()
        {
            _peer?.CancelPendingRequest();
            _remoteStartPending = false;
            _remoteStatus = Locale.T("main.remote.pairedWith", _peer?.PeerName);
            Log(Locale.T("main.log.requestCancelled"));
            RefreshStatus();
        }

        /// <summary>【已配对】断开配对（**只在加速停止时可用**——不能从正在跑的数据层脚下抽走连接）</summary>
        private void DisconnectRemote()
        {
            if (_target.IsRunning || HasTargetWork)
            {
                MessageBox.Show(Locale.T("main.msg.stopBeforeDisconnect"), Locale.T("dialog.tip"),
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_peer != null)
            {
                _peer.Disconnect();   // 先告诉对端"我是主动走的"，它才会立刻回到"等待配对"而不是等重连
                _peer.Dispose();
                _peer = null;
            }
            _remoteStartPending = false;
            _remoteStatus = _remoteListener != null ? Locale.T("main.remote.listeningWaiting") : string.Empty;
            Log(Locale.T("main.log.remoteDisconnected"));
            EnsureAccepting();        // 监听方：重新开始等下一个对端
            RefreshStatus();
        }

        /// <summary>
        /// 【客户端】点「启动加速」：列对端磁盘 → 弹「选择硬盘」→ 请求对端脱机 → 成功后起本地 iSCSI target。
        /// 网络往返都在后台线程，主按钮在这期间显示「取消请求」
        /// （**取消走主按钮**，因为上一步就是它按下去的，在同一枚按钮上取消最符合直觉）。
        /// </summary>
        private void StartRemoteTarget()
        {
            RemotePeer peer = _peer!;

            // **列盘也要走后台线程**：它是同步网络往返，放在 UI 线程上会把界面钉死。
            // （而且"沉默等待"意味着它可能等很久——对端进程卡死时我们不设请求超时。）
            _remoteStatus = Locale.T("main.remote.fetchingDisks");
            RefreshStatus();

            Task.Run(() =>
            {
                IReadOnlyList<RemoteDiskInfo> disks;
                try
                {
                    disks = peer.ListDisks();
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _remoteStatus = Locale.T("main.remote.pairedWith", peer.PeerName);
                        Log(Locale.T("main.log.fetchDisksFailed", ex.Message));
                        MessageBox.Show(Locale.T("main.msg.fetchDisksFailed", ex.Message), Locale.T("dialog.error"),
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                        RefreshStatus();
                    }));
                    return;
                }

                BeginInvoke(new Action(() => PickRemoteDisk(peer, disks)));
            });
        }

        /// <summary>列盘回来后（UI 线程）：弹「选择硬盘」→ 再在后台请求对端脱机</summary>
        private void PickRemoteDisk(RemotePeer peer, IReadOnlyList<RemoteDiskInfo> disks)
        {
            _remoteStatus = Locale.T("main.remote.pairedWith", peer.PeerName);
            RefreshStatus();

            if (disks.Count == 0)
            {
                MessageBox.Show(Locale.T("main.msg.peerNoDisks"), Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var pick = new SelectDiskForm(disks, _remoteDiskIdentity, peer.PeerName);
            if (pick.ShowDialog(this) != DialogResult.OK || pick.SelectedRemoteDisk == null) return;
            RemoteDiskInfo chosen = pick.SelectedRemoteDisk;

            _remoteStartPending = true;
            _remoteStatus = Locale.T("main.remote.diskSelected", chosen.DisplayName);
            Log(Locale.T("main.log.waitingServer"));   // 过程信息走日志，不占按钮文案
            RefreshStatus();

            Task.Run(() =>
            {
                try
                {
                    BlockSourceInfo info = peer.BeginService(chosen.Identity);
                    BeginInvoke(new Action(async () =>
                    {
                        try
                        {
                            await FinishRemoteStartAsync(peer, info, chosen.Identity);
                        }
                        catch (Exception ex)
                        {
                            // async void 回调：异常绝不能再往外抛（会走 Application.ThreadException）。
                            Log(Locale.T("main.log.startFailed", ex.Message));
                            MessageBox.Show(ex.Message, Locale.T("dialog.startFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }));
                }
                catch (OperationCanceledException)
                {
                    // 用户点了「取消请求」：正常路径，不该报错——配对与连接都还在
                    BeginInvoke(new Action(() =>
                    {
                        _remoteStartPending = false;
                        RefreshStatus();
                    }));
                }
                catch (Exception ex)
                {
                    BeginInvoke(new Action(() =>
                    {
                        _remoteStartPending = false;
                        _remoteStatus = Locale.T("main.remote.pairedWith", peer.PeerName);
                        Log(Locale.T("main.log.peerRejected", ex.Message));
                        MessageBox.Show(ex.Message, Locale.T("dialog.remoteStartFailed"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        RefreshStatus();
                    }));
                }
            });
        }

        /// <summary>
        /// 通知对端"我不再用这块盘了"。失败只落诊断日志——调用点处在 async void 的 UI 回调里，
        /// 异常再往外抛会直接进 Application.ThreadException，而"通知失败"本身不影响本地收尾。
        /// </summary>
        private static async Task EndRemoteServiceQuietly(RemotePeer peer)
        {
            try { await Task.Run(peer.EndService); }
            catch (Exception ex) { LogService.DebugFile($"通知对端收回盘失败（已忽略）：{ex.Message}"); }
        }

        /// <summary>
        /// 对端授权通过之后、在 UI 线程上完成的远程启动收尾。
        ///
        /// **独立成方法**是为了让上面那个 <c>BeginInvoke(async …)</c> 回调只剩一层 try/catch：
        /// async void 回调里逸出的异常会直接进 Application.ThreadException（表现为"程序出错"弹窗）。
        /// </summary>
        private async Task FinishRemoteStartAsync(RemotePeer peer, BlockSourceInfo info, string identity)
        {
            bool stillRequested = _remoteStartPending;
            _remoteStartPending = false;
            _remoteDiskIdentity = identity;
            // 选盘等授权期间仍可取消；起本地 target 后进入不可重入的启停阶段。
            if (HasTargetWork || _target.IsRunning) return;
            if (!stillRequested || !ReferenceEquals(_peer, peer))
            {
                await EndRemoteServiceQuietly(peer);
                return;
            }

            // 容量缩放确认：本地 / 远程共用同一份本地 L2，口径与本地形态完全一致（见 ConfirmL2CapacityChange）。
            if (!ConfirmL2CapacityChange())
            {
                await EndRemoteServiceQuietly(peer);
                return;
            }

            _targetOperationPending = true;
            _targetOperationStarting = true;
            RefreshStatus();
            try
            {
                if (await StartWithRemotePrompt(peer, info))
                {
                    _remoteStatus = Locale.T("main.remote.accelerating", info.Model);
                    Log(Locale.T("main.log.remoteDiskReady", info.Model, ((double)info.SizeBytes / 1024 / 1024 / 1024).ToString("F1")));
                    LogAutoMountResult(Locale.T("main.log.iscsiConnectRemoteHint"));
                }
                else
                {
                    await EndRemoteServiceQuietly(peer);
                }
            }
            finally
            {
                _targetOperationPending = false;
                RefreshStatus();
            }
        }

        /// <summary>远程形态的启动（含 L2 账本的两种"不确定"情形，处理口径见 后续待办.md 第一节）</summary>
        private async Task<bool> StartWithRemotePrompt(RemotePeer peer, BlockSourceInfo info)
        {
            // 与本地形态同一套写法：引擎返回结果，这里弹窗问用户，再带 allowL2Reset 重试
            bool allowReset = false;
            while (true)
            {
                EngineResult result = await Task.Run(() => _target.StartRemote(peer, info, allowReset));

                switch (result.Outcome)
                {
                    case EngineOutcome.Ok:
                        return true;

                    case EngineOutcome.L2Mismatch:
                    {
                        DialogResult answer = MessageBox.Show(
                            Locale.T("main.msg.l2MismatchRemote", result.Message),
                            Locale.T("dialog.l2Mismatch"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
                        if (answer != DialogResult.Yes)
                        {
                            Log(Locale.T("main.log.cancelledL2MismatchRemote"));
                            return false;
                        }

                        Log(Locale.T("main.log.l2Cleared", Locale.T("main.reason.mismatchUserReset")));
                        allowReset = true;
                        continue;
                    }

                    case EngineOutcome.L2NeedsVerify:
                    {
                        // 远程形态**不支持逐块校验**（要把整个缓存大小的数据从对端传回来，慢链路上不可行），只能清空重建
                        DialogResult answer = MessageBox.Show(
                            Locale.T("main.msg.l2NeedsVerifyRemote", result.Message),
                            Locale.T("dialog.l2NeedsHandle"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                        if (answer != DialogResult.Yes)
                        {
                            Log(Locale.T("main.log.remoteL2MustClear"));
                            return false;
                        }

                        Log(Locale.T("main.log.l2Cleared", Locale.T("main.reason.userClearRemoteL2", result.Reason)));
                        allowReset = true;
                        continue;
                    }

                    default:   // EngineOutcome.Rejected
                        Log(Locale.T("main.log.startFailed", result.Message));
                        MessageBox.Show(result.Message, Locale.T("dialog.startFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                }
            }
        }

        /// <summary>对端宣告"我没法继续提供了"（盘被拔等）：**停掉本地 target**，让上层干净地看到设备消失</summary>
        private async void OnRemoteServiceEnded(string reason)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(OnRemoteServiceEnded), reason);
                return;
            }

            Log(Locale.T("main.log.peerStoppedServing", reason));
            // 启动期间的结束通知延后到当前操作完成，不能并发驱动同一个生命周期。
            while (_targetOperationPending)
            {
                await Task.Delay(100);
                if (IsDisposed) return;
            }
            _targetOperationPending = true;
            _targetOperationStarting = false;
            RefreshStatus();
            try
            {
                if (_target.IsRunning)
                {
                    EngineResult stopped = await Task.Run(_target.Stop);
                    if (!stopped.Ok) Log(Locale.T("main.log.stopLocalFailed", stopped.Message));
                }
            }
            catch (Exception ex)
            {
                Log(Locale.T("main.log.stopLocalFailed", ex.Message));
            }
            finally { _targetOperationPending = false; }

            _remoteStartPending = false;
            _remoteStatus = Locale.T("main.remote.pairedWith", _peer?.PeerName);
            RefreshStatus();
        }

        #endregion

        #endregion

        #region 启停

        private void Form1_Load(object sender, EventArgs e)
        {
            // 启动提示：**一次 AppendText 写入、三行文字**（文本里带 \n），只产生一次 UI 刷新。
            // 内容是 Beta 免责 + 脱机说明 + 帮助指路，替代原先散成三条的启动日志。
            Log(Locale.T("main.log.startupNotice"));

            // 32 位警示（与 Program.cs 那条同源：那里写诊断日志，这里写主界面日志框）。
            // 目的：别让后续开发者把 win-x86 当成 64 位发布出去——那是 32 位，L1 池会被地址空间卡死在 ~4GB，
            // 与物理内存水位无关（性能骤降、日志被"无法分配新的 Slab"刷爆）。
            if (!Environment.Is64BitProcess)
            {
                Log(Locale.T("main.log.bitnessWarning32"));
            }

            RefreshStatus();

            // 检查更新：后台拉一次仓库里的版本号，**只在它与当前版本号不同时**提一行（见 UpdateService）。
            // 与启动日志同处一处，是为了让"提示"落在用户启动后正好能看到的位置。
            CheckForUpdates();

            // 右下角的 GitHub 徽标：同样后台下载，失败就保持隐藏（见 LoadGitHubBadge）。
            LoadGitHubBadge();
        }

        /// <summary>
        /// 启动时的静默检查更新：拉远端 csproj 里的版本号，**与当前版本号不同**才在日志区提一行；
        /// 相同、取不到、网络异常都**不输出任何东西**（见 <see cref="UpdateService"/>）。
        ///
        /// 跑在后台线程、不阻塞启动；`await` 默认捕获 UI 上下文，故回来写日志框时已在 UI 线程。
        /// </summary>
        private async void CheckForUpdates()
        {
            try
            {
                string? remote = await UpdateService.FetchRemoteVersionAsync();
                if (remote == null) return;   // 取不到：静默

                string current = UpdateService.CurrentVersion;
                if (string.Equals(remote, current, StringComparison.OrdinalIgnoreCase)) return;   // 版本一致：静默

                // 只比"是否不同"，不比大小：本地比远端新（如从源码自己编译）时也会走到这里，这没问题。
                Log(Locale.T("main.log.updateAvailable", current, remote, UpdateService.RepoUrl));
            }
            catch (Exception ex)
            {
                // async void 入口：异常不能再往外抛（会走 Application.ThreadException）。
                // 检查更新纯属锦上添花，出任何事都静默。
                LogService.DebugFile($"检查更新失败（已忽略）：{ex.Message}");
            }
        }

        /// <summary>
        /// 主页右下角那枚 GitHub 徽标（shields.io 的 star 数小图），点击跳到仓库。
        /// **URL 末尾必须带 `.png`**：shields.io 默认回的是 SVG，WinForms 的 <see cref="PictureBox"/> 显示不了。
        /// </summary>
        private const string GitHubBadgeUrl =
            "https://img.shields.io/github/stars/youke1686/FlyDisk.png?style=flat-square&logo=github&logoColor=white&color=blue";

        /// <summary>徽标下载用的客户端（与 <see cref="UpdateService"/> 那只分开：用途不同、互不影响）</summary>
        private static readonly HttpClient BadgeHttp = new() { Timeout = TimeSpan.FromSeconds(8) };

        /// <summary>
        /// 徽标的磁盘缓存（**与帮助文档同一处**：<see cref="ServiceConstants.DataDirectory"/>，
        /// 即 `%ProgramData%\FlyDisk\`）：每次启动先用它把图显示出来（离线也有图），再联网取新的、
        /// 成功就替换并覆盖写回。写不进去（目录受限等）也不影响使用，只是下次还得联网。
        /// </summary>
        private static readonly string BadgeCachePath =
            Path.Combine(ServiceConstants.DataDirectory, "github-badge.png");

        /// <summary>
        /// 装好右下角那一行：**先摆默认态（提示语 + "（点击前往）"默认文字），读到卡片再换成图**
        /// （见 <see cref="BadgeCachePath"/>）。卡片来源有两处——先本地缓存、再联网，谁先拿到用谁。
        /// 两处都没拿到就停在默认文字上：它同样可点击，所以右下角任何时候都进得去仓库。
        /// </summary>
        private async void LoadGitHubBadge()
        {
            // ① 默认态：先把"按钮"的默认文字摆出来（此刻就能点进仓库）
            ShowBadgePlaceholder();

            // ② 本地缓存里有卡片就立刻换上（同步读盘，毫秒级，不联网也能看到图）
            if (TryShowCachedBadge())
            {
                LogService.DebugFile("GitHub 徽标：已用本地缓存显示，正在联网刷新…");
            }

            // ③ 再联网取新的；成功了才替换（失败/无网就留下 ② 的缓存图或 ① 的默认文字）
            try
            {
                byte[] bytes = await BadgeHttp.GetByteArrayAsync(GitHubBadgeUrl);
                ShowBadgeImage(DecodeImage(bytes));
                TrySaveBadgeCache(bytes);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"GitHub 徽标下载失败，沿用缓存（没有则只留默认文字）：{ex.Message}");
            }
        }

        /// <summary>
        /// 显示"按钮"的默认态：一行 = 提示语 + "（点击前往）"占位文字（占的就是卡片的位置）。
        /// 卡片（本地缓存或联网图）一到就由 <see cref="ShowBadgeImage"/> 把它换掉。
        /// </summary>
        private void ShowBadgePlaceholder()
        {
            picGitHub.Visible = false;
            lblBadgePlaceholder.Visible = true;
            lblStarHint.Visible = true;
            PlaceBadgeRow();
        }

        /// <summary>把缓存里的徽标显示出来；缓存不存在或读坏了就返回 false（只落诊断日志）。</summary>
        private bool TryShowCachedBadge()
        {
            try
            {
                if (!File.Exists(BadgeCachePath)) return false;

                ShowBadgeImage(DecodeImage(File.ReadAllBytes(BadgeCachePath)));
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"GitHub 徽标缓存读取失败，已忽略：{ex.Message}");
                return false;
            }
        }

        /// <summary>把刚下到的图覆盖写回缓存；失败只落日志（下次启动就没缓存可用，仅此而已）。</summary>
        private static void TrySaveBadgeCache(byte[] bytes)
        {
            try
            {
                // 数据目录由帮助文档在启动时建过，这里再兜一次（万一它落盘失败）
                Directory.CreateDirectory(ServiceConstants.DataDirectory);
                File.WriteAllBytes(BadgeCachePath, bytes);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"GitHub 徽标缓存写入失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 解码图片。<see cref="Image.FromStream"/> 要求流在图片的**整个生命周期**内保持打开，
        /// 所以先克隆一份脱离流再放行 using——否则流一关，用它的图就废了。
        /// </summary>
        private static Image DecodeImage(byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using Image raw = Image.FromStream(stream);
            return new Bitmap(raw);
        }

        /// <summary>换上新图并摆位（旧的图要释放）；首次显示与联网刷新走过同一条路。</summary>
        private void ShowBadgeImage(Image image)
        {
            Image? previous = picGitHub.Image;
            picGitHub.Image = image;
            previous?.Dispose();

            // 卡片就位：把"（点击前往）"默认文字换掉（两者占同一个位置）
            lblBadgePlaceholder.Visible = false;
            picGitHub.Visible = true;
            lblStarHint.Visible = true;
            PlaceBadgeRow();
        }

        /// <summary>
        /// 把"提示语 + 卡片"这一行贴到内容区右下角：**行尾右贴边、上贴状态栏**，提示紧挨其左
        /// （"→"正好指向它）。行尾就是卡片图或它的默认文字"（点击前往）"——两者占同一位置。
        ///
        /// 尺寸要等图片下完 / 文字量好才知道，故位置不在 Designer 里定，而在这一行显示之后在这里算。
        /// </summary>
        private void PlaceBadgeRow()
        {
            const int margin = 10;
            const int gap = 6;

            // 行尾：有卡片图就用它，否则是"（点击前往）"默认文字
            Control tail = picGitHub.Visible ? picGitHub : lblBadgePlaceholder;

            tail.Location = new Point(
                ClientSize.Width - tail.Width - margin,
                statusStrip1.Top - tail.Height - margin);

            // 提示贴在行尾左侧、与行尾垂直居中
            lblStarHint.Location = new Point(
                tail.Left - lblStarHint.Width - gap,
                tail.Top + (tail.Height - lblStarHint.Height) / 2);
        }

        /// <summary>
        /// 点"求 star 提示"或 GitHub 徽标：用系统默认浏览器打开仓库页。打不开不是大事，只落诊断日志。
        /// </summary>
        private void OpenGitHubRepo(object? sender, EventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(UpdateService.RepoUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"打开 GitHub 仓库失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 主按钮：**三个形态共用一个按钮、就地换文案**（启动加速 / 取消请求 / 停止加速 / 关闭远程服务 …）。
        /// 按钮永远只表达"按下去会发生什么"——过程信息（如"等待服务端操作"）走日志，不占按钮文案。
        /// </summary>
        private void btnStartStop_Click(object? sender, EventArgs e)
        {
            if (HasTargetWork) return;
            // ① 已配对：**两端对等**——本端能点「启动加速」去用对方的盘
            if (_peer != null)
            {
                // 本端正在被对方用（或等对方重连）：主按钮是灰的，这里再兜一次
                if (_peer.IsServingPeer || _peer.ServiceHoldRemainSeconds > 0) return;
                if (_remoteStartPending) { CancelRemoteRequest(); return; }   // 「取消请求」
                if (_target.IsRunning) { StopTarget(); return; }           // 「停止加速」
                StartRemoteTarget();                                        // 「启动加速」
                return;
            }

            // ② 只开着监听、还没配对
            if (_remoteListener != null)
            {
                CloseRemoteServer();
                return;
            }

            // ③ 本地（现状）
            if (_target.IsRunning) StopTarget();
            else StartTarget();
        }

        private async void StartTarget()
        {
            // **每次启动都要选盘**（见 后续待办.md 第一节的"选盘时机"）：
            // 用户改过配置后可能隔几天才点启动，很容易忘了当时选的是哪块；把选择放在启动的同一刻，做到所见即所选。
            PhysicalDiskInfo? target;
            try
            {
                List<PhysicalDiskInfo> disks = PhysicalDiskHandle.Enumerate();
                if (disks.Count == 0)
                {
                    MessageBox.Show(Locale.T("main.msg.noDisks"), Locale.T("dialog.tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                using var pick = new SelectDiskForm(disks, _config.PhysicalDiskIdentity,
                    l2CacheDiskNumber: PhysicalDiskHandle.GetL2CacheDiskNumber(_config.EnableSsdCache, _config.SsdCachePath));
                if (pick.ShowDialog(this) != DialogResult.OK || pick.SelectedDisk == null) return;
                target = pick.SelectedDisk;
            }
            catch (Exception ex)
            {
                MessageBox.Show(Locale.T("main.msg.enumerateFailed", ex.Message), Locale.T("dialog.error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 本次**不启用 L2**、但缓存目录里还留着**这块盘**的 L2 账本时，先问用户
            // （此刻还没脱机、系统状态一点没动；详细理由见 ConfirmL2LedgerUnused）。
            if (!ConfirmL2LedgerUnused(target)) return;

            // 本次**启用 L2**、但上次容器容量与本次配置不一致时，先问用户"缩放（保留缓存）"还是中止
            // （与上面互斥；同样在脱机之前，用户取消时系统状态一点没动）。
            if (!ConfirmL2CapacityChange()) return;

            _targetOperationPending = true;
            _targetOperationStarting = true;
            RefreshStatus();

            try
            {
                if (await StartWithL2MismatchPrompt(target))
                {
                    // 记住这次的选择：**仅用于下次在选盘对话框里默认选中**，绝不作为启动依据
                    _config.PhysicalDiskIdentity = PhysicalDiskHandle.DescribeIdentity(target);
                    ConfigService.Save(_config, out _);

                    Log(Locale.T("main.log.targetReady", _target.ListenEndpoint, _config.TargetIqn));
                    LogAutoMountResult(Locale.T("main.log.iscsiConnectHint"));

                    // 克隆盘被系统保持脱机 / 只读（多半是判成了源盘的「冗余路径」）：按既定流程**整套重来一次**
                    // ——停止（摘 iSCSI、盘保持脱机）→ 等 1 秒 → 再启动（含同一套检查）。
                    // 一轮仍不行就停在原地、如实告诉用户（见 RecoverFromCloneDiskProblem）。
                    if (_target.CloneDiskWarning.Length > 0) await RecoverFromCloneDiskProblem(target);
                }
            }
            catch (Exception ex)
            {
                // 这是 async void 入口：异常绝不能再往外抛，否则直接走 Application.ThreadException。
                // 启动本身的失败已由 StartWithL2MismatchPrompt 转成弹窗 + false，这里兜的是它之外的部分
                // （保存配置 / 写日志 / 克隆盘恢复）。
                Log(Locale.T("main.log.startFailed", ex.Message));
                MessageBox.Show(ex.Message, Locale.T("dialog.startFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _targetOperationPending = false;
                RefreshStatus();
            }
        }

        /// <summary>
        /// 【本地形态】本次**不启用 L2**、但缓存目录里还留着**这块盘**的 L2 账本时，先问用户：
        /// 继续加速会让那份账本失效——加速期间盘会被写透传改写，账本随即与盘不一致；
        /// 而它下次被启用时可能因为"盘是脱机打开的"而被**直接采信** ⇒ 返回陈旧块。
        ///
        /// **刻意放在 UI 层、启动之前**（而不是让引擎抛异常）：这只是"看一眼 L2 文件 + 比身份 + 问用户"，
        /// 引擎没有交互能力；放在这里还能保证用户中止时系统状态一点都没动。
        /// </summary>
        /// <returns>true = 可以继续启动；false = 用户选择中止</returns>
        private bool ConfirmL2LedgerUnused(PhysicalDiskInfo target)
        {
            if (_config.EnableSsdCache) return true;   // 本次用 L2，账本归引擎自己管

            long? owner;
            try { owner = L2Verifier.TryReadOwnerIdentity(_config); }
            catch { return true; }   // 看一眼失败一律当作"没有可用的 L2"，不拦启动

            if (!owner.HasValue || owner.Value != L2Verifier.Identify(target)) return true;

            DialogResult answer = MessageBox.Show(
                Locale.T("main.msg.l2UnusedLedger", target.DiskNumber),
                Locale.T("dialog.l2UnusedLedger"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                Log(Locale.T("main.log.l2UnusedLedgerKept"));
                return false;
            }

            // 让账本失效：只改容器头的身份戳，索引原样不动 ⇒ 下次启用 L2 时判"需要校验"。
            if (SsdCacheService.InvalidateLedger(_config))
            {
                Log(Locale.T("main.log.l2UnusedLedgerInvalidated"));
            }
            return true;
        }

        /// <summary>
        /// 【容量缩放 / 夹取确认】启动前预检。两种情况都会问用户：
        /// ① **容量与上次不同**（改过配置、或可用空间变化）⇒ 说明将放大 / 缩小并保留缓存；
        /// ② **本次容量被缓存盘可用空间夹取**（配置值放不下）⇒ 额外说明"为什么留这段余量"（SSD 快满会掉速），
        ///    因为夹取很可能直接导致缩容。
        ///
        /// **放在 UI 层、启动之前**（本地与远程共用同一份本地 L2）：此刻还没脱机，用户取消时系统状态一点没动；
        /// 用户确认后引擎按新容量缩放（引擎已不再拦容量差异，见 SsdCacheService.DetectLedgerMismatch 与
        /// docs/L2容器按需增长与容量缩放_设计.md §4 D6/D7）。
        /// </summary>
        /// <returns>true = 可以继续启动；false = 用户选择中止</returns>
        private bool ConfirmL2CapacityChange()
        {
            if (!_config.EnableSsdCache) return true;                    // 本次不用 L2：没有容量可谈
            if (!SsdCacheService.LedgerExists(_config)) return true;      // 没有可保留的账本：重建即可
            if (!SsdCacheService.TryPeekContainerSlotCount(_config, out int oldSlots)) return true;

            int configuredSlots = SsdCacheService.ConfiguredSlotCountFor(_config);   // 标称（不夹取）
            int effectiveSlots = SsdCacheService.ComputeSlotCountFor(_config);       // 实际（夹取后）
            if (configuredSlots <= 0 || effectiveSlots <= 0) return true;

            bool clamped = effectiveSlots != configuredSlots;
            bool changed = effectiveSlots != oldSlots;
            if (!clamped && !changed) return true;   // 既没夹取、容量也没变：没什么可说

            string configuredText = CapacityText(configuredSlots);
            string oldText = CapacityText(oldSlots);
            string effectiveText = CapacityText(effectiveSlots);
            string reserveText = SizeText(SsdCacheService.KeepFreeBytes);

            string messageKey;
            if (clamped)
            {
                messageKey = !changed ? "main.msg.l2ClampSame"
                    : (effectiveSlots < oldSlots ? "main.msg.l2ClampShrink" : "main.msg.l2ClampGrow");
            }
            else
            {
                messageKey = effectiveSlots < oldSlots ? "main.msg.l2ResizeShrink" : "main.msg.l2ResizeGrow";
            }

            DialogResult answer = MessageBox.Show(
                Locale.T(messageKey, configuredText, oldText, effectiveText, reserveText),
                Locale.T(clamped ? "dialog.l2Clamp" : "dialog.l2Resize"), MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.Yes)
            {
                Log(Locale.T("main.log.cancelledL2Resize"));
                return false;
            }

            Log(Locale.T("main.log.l2ResizeConfirmed", oldText, effectiveText));
            return true;
        }

        /// <summary>字节 → 容量文本（1024 进制标 GiB / MiB；口径与项目"标注必须与进制一致"一致）</summary>
        private static string SizeText(long bytes)
            => bytes >= 1024L * 1024 * 1024
                ? $"{bytes / 1024.0 / 1024 / 1024:F1} GiB"
                : $"{bytes / 1024.0 / 1024:F0} MiB";

        /// <summary>槽数 → 容量文本（附槽数）</summary>
        private static string CapacityText(int slots)
            => $"{SizeText((long)slots * ServiceConstants.BlockSize)}（{slots:N0} 槽）";

        /// <summary>
        /// 启动（本地形态）。引擎把"账本状态不确定"作为**返回值**报上来，这里弹窗交给用户决定：
        /// ① 账本与当前盘/配置不匹配 ⇒ 问"清空重建？"；
        /// ② 盘启动时是**联机**、且缓存目录里有账本 ⇒ 问"现在校验？"（或清空重建）。
        /// 「清空重建」⇒ 以 <c>allowL2Reset: true</c> 再启一次；「校验」⇒ 打开校验窗口，
        /// 校验干净才带着这份 L2 继续启动（校验器已把盘脱机，引擎因此自然采信账本）；
        /// 没干净就**中止启动、不做任何补救动作**。
        /// </summary>
        /// <returns>是否已经启动</returns>
        private async Task<bool> StartWithL2MismatchPrompt(PhysicalDiskInfo target)
        {
            // 用循环重试而不是递归：用户每拍一次板，就带新的 allowL2Reset 再启一次
            bool allowReset = false;
            while (true)
            {
                EngineResult result = await Task.Run(() => _target.Start(target, allowReset));

                switch (result.Outcome)
                {
                    case EngineOutcome.Ok:
                        return true;

                    case EngineOutcome.L2Mismatch:
                    {
                        DialogResult answer = MessageBox.Show(
                            Locale.T("main.msg.l2Mismatch", result.Message),
                            Locale.T("dialog.l2Mismatch"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

                        if (answer != DialogResult.Yes)
                        {
                            Log(Locale.T("main.log.cancelledL2Mismatch"));
                            return false;
                        }

                        Log(Locale.T("main.log.l2Cleared", Locale.T("main.reason.mismatchUserReset")));
                        allowReset = true;   // 用户确认"清空重建" ⇒ 下次带 allowL2Reset 再启
                        continue;
                    }

                    case EngineOutcome.L2NeedsVerify:
                    {
                        DialogResult answer = MessageBox.Show(
                            Locale.T("main.msg.l2NeedsVerify", result.Message),
                            Locale.T("dialog.l2NeedsVerify"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                        if (answer != DialogResult.Yes)
                        {
                            Log(Locale.T("main.log.l2Cleared", Locale.T("main.reason.userDeclinedVerify", result.Reason)));
                            allowReset = true;   // 用户选了"清空重建" ⇒ 下次带 allowL2Reset 再启
                            continue;
                        }

                        // 校验窗口是模态的：它自己脱机 + 独占源盘、并抢 cache.lock（此刻引擎还没打开盘），跑完保持脱机
                        bool consistent;
                        using (var dialog = new L2VerifyForm(_config, target.DiskNumber))
                        {
                            dialog.ShowDialog(this);
                            consistent = dialog.LedgerIsConsistent;
                        }

                        if (!consistent)
                        {
                            // 校验没跑干净（取消 / 中止 / 有没修完的不一致）⇒ **中止启动，不做任何补救动作**：
                            // 不动盘的状态、不动配置、不动缓存文件（MVP 口径：用户没点的事，程序不替他做）。
                            //
                            // 校验器已轮换容器身份戳；未验完时不恢复戳，下次启动仍要求校验。
                            Log(Locale.T("main.log.abortedL2Verify"));
                            return false;
                        }

                        // 校验干净：校验器已把源盘脱机 ⇒ 引擎这次自然采信账本，**不需要 allowL2Reset**
                        Log(Locale.T("main.log.l2VerifyPassed"));
                        allowReset = false;
                        continue;
                    }

                    default:   // EngineOutcome.Rejected
                        Log(Locale.T("main.log.startFailed", result.Message));
                        MessageBox.Show(result.Message, Locale.T("dialog.startFailed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                }
            }
        }

        /// <summary>自动恢复里各步骤之间等多久（毫秒）：既让刚建立的 iSCSI 会话稳定下来，也让系统把设备栈收拾干净</summary>
        private const int CloneRetryDelayMs = 1000;

        /// <summary>自动恢复里"停止加速"最多试几次（每次之间等 <see cref="CloneRetryDelayMs"/>）</summary>
        private const int CloneRetryStopAttempts = 3;

        /// <summary>
        /// 提 issue 的落点：只在"无法加速"时由代码打开，**不把链接写进给用户看的文案**（文案里只说 GitHub 仓库）。
        /// </summary>
        private const string GitHubIssuesUrl = "https://github.com/youke1686/FlyDisk/issues/new";

        /// <summary>
        /// 克隆盘异常的**一轮自动恢复**：等 1 秒（让刚建立的 iSCSI 会话稳定）→ 停止加速全流程（摘 iSCSI、
        /// 落 L2 账本、盘保持脱机，**最多试 3 次**）→ 再等 1 秒 → 重新走一遍启动加速（含同一套克隆盘检查，
        /// 见 <see cref="TargetService.CloneDiskWarning"/>）。
        ///
        /// 成功（检查不再报）就继续加速；仍失败就停止加速、如实告知用户（见 <see cref="ReportCloneDiskUnfixable"/>）。
        /// **只重试一轮**：同一台机器、同一套时序再来一次就是同样的结果，多试只会让用户干等。
        /// 重试用**同一块盘**（不再弹选盘框）：用户刚选过，且这是程序自己发起的动作。
        ///
        /// ⚠ 为什么每处都要等 1 秒、停止还要试 3 次：刚建立起来的 iSCSI 会话**没法立刻登出**，实测第一次
        /// Stop 会被"还有 iSCSI 连接挂着"拒掉（见 <see cref="TryStopForCloneRecovery"/>）。
        /// ⚠ 全程 await 后台任务，不阻塞 UI 线程（启停已移到后台，见 <see cref="StartTarget"/>）。
        /// </summary>
        private async Task RecoverFromCloneDiskProblem(PhysicalDiskInfo target)
        {
            Log(Locale.T("main.log.cloneRetry"));

            // ① 先等 1 秒：刚建立起来的 iSCSI 会话**没法立刻登出**（实测：第一次 Stop 会因"还有连接挂着"被拒），
            //    等它稳定下来再动手。
            await Task.Delay(CloneRetryDelayMs);

            // ② 停止加速全流程（最多试 3 次）。一直停不下来就没法干净地重来，如实收尾、不再往下走。
            (bool ok, string stopError) = await TryStopForCloneRecovery();
            if (!ok)
            {
                Log(Locale.T("main.log.cloneRetryStopFailed", CloneRetryStopAttempts, stopError));
                return;
            }

            // ③ 再等 1 秒（让系统把设备栈收拾干净），然后整装重来
            await Task.Delay(CloneRetryDelayMs);

            // ④ 重新启动（含同一套克隆盘检查）；L2 两种"不确定"情形仍走原有的弹窗口径
            RefreshStatus();

            bool started = await StartWithL2MismatchPrompt(target);
            if (!started)
            {
                // 启动本身失败（L2 被取消 / 抛异常）：原有的日志与弹窗已经说清原因，这里只补一句结论
                Log(Locale.T("main.log.cloneRetryStartFailed"));
                return;
            }

            Log(Locale.T("main.log.targetReady", _target.ListenEndpoint, _config.TargetIqn));
            LogAutoMountResult(Locale.T("main.log.iscsiConnectHint"));

            if (_target.CloneDiskWarning.Length == 0)
            {
                Log(Locale.T("main.log.cloneRetryOk"));
                return;
            }

            // ⑤ 仍不行：停止加速（盘保持脱机），向用户说明并询问是否去提 issue
            Log(Locale.T("main.log.cloneRetryFailed"));

            // 先等 1 秒再停（同 ① 的道理）：刚重新启动过，那条新会话立刻登出同样会被拒
            await Task.Delay(CloneRetryDelayMs);
            // 再走"带重试的停止"（同 ② 的道理）：那条新会话也可能一下摘不干净
            (bool ok2, string finalStopError) = await TryStopForCloneRecovery();
            if (!ok2)
            {
                // 停不下来就还处于"仍在加速"的状态：日志如实写出，别让用户以为已经停了
                Log(Locale.T("main.log.stopFailed", finalStopError));
            }

            ReportCloneDiskUnfixable();
        }

        /// <summary>
        /// 停止加速（自动恢复专用）：**最多试 <see cref="CloneRetryStopAttempts"/> 次**，每次之间等 1 秒。
        ///
        /// 为什么要试多次：刚建立起来的 iSCSI 会话**没法立刻登出**（实测第一次 Stop 会被"还有 iSCSI 连接挂着"
        /// 拒掉）。<c>Disconnect</c> 本身是幂等的（登出没成功就留着会话，下次调用再试），所以这里只要按节拍
        /// 把"再试一次"做够次数，登出通常在下一次就成功了。
        /// </summary>
        /// <returns>停下来了返回 (true, 空串)；否则 (false, 最后一次错误)</returns>
        private async Task<(bool ok, string error)> TryStopForCloneRecovery()
        {
            string error = string.Empty;
            for (int attempt = 1; attempt <= CloneRetryStopAttempts; attempt++)
            {
                EngineResult stopped = await Task.Run(_target.Stop);
                if (stopped.Ok) return (true, string.Empty);

                error = stopped.Message;   // 被拒（例如会话还没登出去）⇒ 等一拍再来
                LogService.DebugFile($"克隆盘恢复：停止加速第 {attempt}/{CloneRetryStopAttempts} 次失败：{error}");
                if (attempt < CloneRetryStopAttempts) await Task.Delay(CloneRetryDelayMs);
            }
            return (false, error);
        }

        /// <summary>
        /// "无法加速"的收尾告知：弹窗说明 + 询问是否打开 GitHub 提 issue。
        /// 文案里给出恢复路径（菜单「重新联机硬盘」把盘还给系统）——此刻磁盘保持脱机，用户得知道怎么还回去。
        /// </summary>
        private void ReportCloneDiskUnfixable()
        {
            DialogResult answer = MessageBox.Show(
                Locale.T("main.msg.cloneUnfixable"),
                Locale.T("dialog.cloneUnfixable"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1);

            if (answer != DialogResult.Yes) return;

            try
            {
                Process.Start(new ProcessStartInfo(GitHubIssuesUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // 打不开浏览器不是大事：文案里已经让用户自己去仓库提
                LogService.DebugFile($"打开 GitHub issue 页面失败：{ex.Message}");
            }
        }

        private async void StopTarget()
        {
            _targetOperationPending = true;
            _targetOperationStarting = false;
            RefreshStatus();

            try
            {
                int diskNumber = _target.LocalDiskNumber;
                RemotePeer? peer = _peer;

                EngineResult stopped = await Task.Run(_target.Stop);
                if (!stopped.Ok)
                {
                    // 被拒（还有别的 iSCSI 连接挂着）：如实弹出来，否则用户只看到按钮弹回来、以为程序卡了
                    Log(Locale.T("main.log.stopFailed", stopped.Message));
                    MessageBox.Show(stopped.Message, Locale.T("dialog.stopRejected"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (peer != null)
                {
                    // **必须通知对端"我不再用这块盘了"**：它会关掉盘句柄（盘保持脱机）。
                    // 不发的话对端会一直以为你还在用，那块盘就一直被它独占着。
                    // 注意**配对与连接保留**——用户可以立刻再选一块盘，不必重新配对。
                    await Task.Run(peer.EndService);
                    _remoteStatus = Locale.T("main.remote.pairedWith", peer.PeerName);
                    Log(Locale.T("main.log.remoteStopped"));
                }
                else if (diskNumber >= 0)
                {
                    Log(Locale.T("main.log.stoppedDiskOffline", diskNumber));
                    Log(Locale.T("main.log.releaseDiskHint"));
                }
            }
            catch (Exception ex)
            {
                // 停止会被"还有 iSCSI 连接"拒绝（见 TargetService.Stop），这类拒绝要弹出来，
                // 否则用户只看到按钮弹回来、以为程序卡了
                Log(Locale.T("main.log.stopFailed", ex.Message));
                MessageBox.Show(ex.Message, Locale.T("dialog.stopRejected"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                _targetOperationPending = false;
                RefreshStatus();
            }
        }

        /// <summary>
        /// 副标题左边缘 = 标题的水平中线。
        ///
        /// **写成常量、不实时读 `ClientSize.Width / 2`**：主窗口是 `FixedSingle` + 不可最大化 ⇒ 宽度恒定；
        /// 而最小化/恢复的**那一瞬** `ClientSize` 会短暂给出怪值，读到它就会把副标题甩到一边再弹回来
        /// （实测到的跳动）。
        /// 值 = Designer 的 `ClientSize.Width` 584 / 2 —— 改窗口宽度时这里和 Designer 一起改。
        /// </summary>
        private const int SubtitleLeft = 292;

        /// <summary>
        /// 副标题三态（见 后续待办.md 第一节的主界面表与"副标题三态的依据"）：
        /// 本地加速维持现状；**配对后**（连接层已建立、还没启动加速）统一叫"远程加速器"、**不区分端**；
        /// 只有**真正开始服务**之后才按角色分成"远程服务端 / 远程客户端"。
        ///
        /// **位置：左边缘固定贴在标题的水平中线上**（见 <see cref="SubtitleLeft"/>）——副标题**刻意偏右、
        /// 不居中**，那个"——"由此正好从标题正中接出去。
        /// </summary>
        private void SetSubtitle(string text)
        {
            PlaceSubtitle();

            if (labelSubtitle.Text == text) return;
            labelSubtitle.Text = text;
        }

        /// <summary>
        /// 把副标题摆到该在的位置。**只在真的不在那儿时才写**——位置是个常量，正常情况下这句永远不成立，
        /// 于是每秒调它也不会碰控件一下（也就不会有"最小化恢复时位置跳一下"）。
        /// </summary>
        private void PlaceSubtitle()
        {
            if (labelSubtitle.Left != SubtitleLeft) labelSubtitle.Left = SubtitleLeft;
        }

        /// <summary>状态栏：每秒读一次引擎快照（单进程直连，不再有 IPC 连接状态可显示）</summary>
        private void RefreshStatus()
        {
            CacheStats stats;
            try
            {
                stats = _target.CreateSnapshot();
            }
            catch
            {
                return;   // 读快照失败就保留上一次显示
            }

            // 校验要独占 L2 容器与源盘 ⇒ 目标运行中把入口置灰（弹出对话框前还会再兜一次）
            设置ToolStripMenuItem.Enabled = !stats.IsRunning && !HasTargetWork;
            _verifyL2MenuItem.Enabled = !stats.IsRunning && !HasTargetWork;

            // 「重新联机源盘」同样只在非运行时可点：运行中那块盘是本程序的块源，联机 = 同签名双盘 + 双写
            _reonlineMenuItem.Enabled = !stats.IsRunning && !HasTargetWork;

            // 「远程」只在**本地加速运行中**置灰；远程形态下要能打开（查看状态、断开）
            _remoteMenuItem.Enabled = !HasTargetWork && !(stats.IsRunning && _peer == null);

            if (HasTargetWork)
            {
                btnStartStop.Enabled = false;
                btnStartStop.Text = Locale.T(_targetOperationPending && _targetOperationStarting
                    ? "main.button.starting" : "main.button.stopping");
                lblTargetStatus.Text = _target.IsStopping ? Locale.T("engine.stop.pending") : btnStartStop.Text;
                return;
            }

            // ① **已配对**：两端共用同一套文案（身份对等），差别只体现在"本端是被用还是用别人"
            if (_peer != null)
            {
                // 本端正在被对方用（或正在等对方重连）：主按钮灰掉——**行为完全由对方控制**
                if (_peer.IsServingPeer || _peer.ServiceHoldRemainSeconds > 0)
                {
                    SetSubtitle(Locale.T("main.subtitle.remoteServer"));
                    btnStartStop.Text = Locale.T("main.button.serving");
                    btnStartStop.Enabled = false;
                    lblTargetStatus.Text = _peer.ServiceHoldRemainSeconds > 0
                        ? Locale.T("main.status.servingPeerUsingWaiting", _peer.PeerName, _peer.ServingDisplay, _peer.ServiceHoldRemainSeconds)
                        : Locale.T("main.status.servingPeerUsing", _peer.PeerName, _peer.ServingDisplay);
                    return;
                }

                btnStartStop.Enabled = true;

                if (stats.IsRunning)
                {
                    SetSubtitle(Locale.T("main.subtitle.remoteClient"));     // 只有"真正开始用"之后才按角色分端
                    btnStartStop.Text = Locale.T("main.button.stop");
                }
                else if (_remoteStartPending)
                {
                    SetSubtitle(Locale.T("main.subtitle.remote"));
                    btnStartStop.Text = Locale.T("main.button.cancelRequest");
                }
                else
                {
                    SetSubtitle(Locale.T("main.subtitle.remote"));     // 配对后、还没启动加速：**不区分端**
                    btnStartStop.Text = Locale.T("main.button.start");
                }

                lblTargetStatus.Text = _peer.IsLinkDown ? Locale.T("main.status.linkDown") : _remoteStatus;
                return;
            }

            // ② 只开着监听、还没配对
            if (_remoteListener != null)
            {
                SetSubtitle(Locale.T("main.subtitle.remote"));
                lblTargetStatus.Text = _remoteStatus;
                btnStartStop.Text = Locale.T("main.button.closeRemote");
                btnStartStop.Enabled = true;
                return;
            }

            // ③ 本地（现状）
            SetSubtitle(Locale.T("main.subtitle.local"));
            btnStartStop.Enabled = true;
            if (stats.IsRunning)
            {
                string linkState = stats.ActiveConnections switch
                {
                    > 0 => Locale.T("main.status.linkConnected", stats.ActiveConnections),
                    0 => Locale.T("main.status.linkConnectPrompt"),
                    _ => Locale.T("main.status.linkUnknown")
                };
                lblTargetStatus.Text = Locale.T("main.status.iscsiListening", stats.ListenEndpoint, linkState);
                btnStartStop.Text = Locale.T("main.button.stop");
            }
            else
            {
                lblTargetStatus.Text = Locale.T("main.status.iscsiNotListening");
                btnStartStop.Text = Locale.T("main.button.start");
            }
        }

        #endregion

        /// <summary>
        /// 目标起来后，按**自动挂载**的结果给用户一句话：成功就说盘已自动挂上；失败就报出英文诊断
        /// （含错误码与失败步骤），再补一句该形态的手动提示（<paramref name="manualHint"/>）让用户去
        /// 「iSCSI 发起程序」自己挂。失败不拦启动（见 后续待办.md 待办 3 §9）。
        /// 最后统一补一句**位置说明**：盘不会出现在系统「网络」位置——用户常去那儿找盘，找不到会以为加速失败。
        /// </summary>
        private void LogAutoMountResult(string manualHint)
        {
            if (_target.AutoMounted)
            {
                Log(Locale.T("main.log.iscsiAutoMounted"));
            }
            else
            {
                Log(Locale.T("main.log.iscsiAutoMountFailed", _target.AutoMountError));
                Log(manualHint);
            }

            // 克隆盘检查的结论（引擎在登录后探测到"**新出现**的盘被判脱机/只读"时给出）：
            // 这种状态意味着 iSCSI 盘没能正常挂回系统——必须让用户看见，而不是只报一句"已自动挂载"。
            if (_target.CloneDiskWarning.Length > 0) Log(_target.CloneDiskWarning);

            // 本地与远程两种形态共用这一句：盘不会"搬"到系统「网络」位置（本地走的是回环 iSCSI，
            // 远程挂载后同样是一块普通本地盘），在那里找盘是找不到的。
            Log(Locale.T("main.log.diskPlacementHint"));
        }

        private void Log(string message)
        {
            if (txtLog.InvokeRequired)
            {
                // **必须异步**（BeginInvoke 而非 Invoke）：日志的调用方有 iSCSI 目标端的登录线程——
                // 它在登录握手中间回调（Target_OnAuthorizationRequest → OnInitiatorConnected → 这里），
                // 而"启动加速"是同步跑在 UI 线程上的：UI 线程正卡在 LoginIScsiTargetW 等登录应答。
                // 用同步 Invoke 会让登录线程等 UI 线程、UI 线程等登录应答 —— 双向死锁，
                // 直到发起端内部登录超时（rc=0xEFFF0012）才解开（现象：卡很久 + 看不到盘）。
                txtLog.BeginInvoke(new Action<string>(Log), message);
                return;
            }

            // 日志框是**普通 TextBox**：它只认 \r\n 为换行，单独一个 \n 会被吞掉（文字挤在一行）。
            // 而语言字典里的多行文案写作 "\n"（JSON 转义），所以在这里统一归一化到 Environment.NewLine。
            string normalized = message.Replace("\r\n", "\n").Replace('\r', '\n')
                                       .Replace("\n", Environment.NewLine);
            txtLog.AppendText($"[{DateTime.Now:HH:mm:ss}] {normalized}{Environment.NewLine}");
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 加速期间**关闭主窗口 = 缩到托盘**（不退出）：程序与加速都继续跑，窗口只是藏起来，
            // 这样"手滑关掉"再也不会把盘留在脱机状态。
            // 真正退出的路径只有一条：**先点「停止加速」，再关窗口**（那时 IsRunning 为假，走下面的正常关闭）。
            if (e.CloseReason == CloseReason.UserClosing && (_target.IsRunning || HasTargetWork))
            {
                e.Cancel = true;
                _tray.Visible = true;   // 窗口藏起来了，通知区补一个入口（窗口在屏幕上时它是隐藏的）
                Hide();   // **只藏主窗口**：检查器是独立顶层窗口、悬浮方块归检查器自己管，都不跟着走
                return;
            }

            _statusTimer.Stop();   // 先停状态节拍，避免收尾途中排队的刷新读到已释放的引擎对象

            // 关机 / 重启 / 注销：用户走不到"先点停止加速、再关窗口"那条路（Windows 直接关窗口），
            // 这里补一次收尾。发起程序登不出去时仍中止服务；只有在途 IO 已结束且 L2 无故障才提交账本，
            // 否则保留待校验状态（代价见 TargetService.TryStopForShutdown）。
            // 界面此刻正在关闭，故不写日志框；过程细节由引擎落 debug-security.log。
            if (e.CloseReason == CloseReason.WindowsShutDown)
                _target.TryStopForShutdown();

            base.OnFormClosing(e);
        }

        /// <summary>把主窗口从托盘叫回来（单击托盘图标；或第二个实例请我们到前台）</summary>
        private void RestoreFromTray()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            _tray.Visible = false;   // 窗口回到屏幕上，通知区图标随即撤掉
            Activate();
            BringToFront();
            // Activate/BringToFront 对一个"后台进程"未必真能把窗口提到最前（Windows 的前台抢占限制），
            // 再补一次 SetForegroundWindow——第二个实例已经用 AllowSetForegroundWindow 替我们预授权了。
            SetForegroundWindow(Handle);
        }

        /// <summary>
        /// 第二个实例请求"把主窗口叫到前台"时走这里（见 <see cref="Program.ActivateWindowMessage"/>）。
        /// 窗口可能正藏在托盘里（加速期间关窗口 = 缩到托盘），所以复用 <see cref="RestoreFromTray"/>。
        /// </summary>
        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == Program.ActivateWindowMessage)
            {
                RestoreFromTray();
                return;
            }
            base.WndProc(ref m);
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _peer?.Dispose();
            // 先摘引用再 Stop：否则 AcceptLoop 会把这个已死的监听器当成"垃圾连接"，
            // 在退出过程里空转刷日志（每圈一次 Invoke + 一行字符串）。
            TcpListener? listener = _remoteListener;
            _remoteListener = null;
            try { listener?.Stop(); } catch { /* 已经停了 */ }
            _tray.Dispose();   // Dispose 会一并把图标从通知区撤掉：进程退出后不该残留一个点不动的死图标
            _inspector?.Dispose();
            base.OnFormClosed(e);
        }
    }

    /// <summary>
    /// 嵌进 exe 的像素字体（Fusion Pixel 12px Proportional）。
    ///
    /// **为什么嵌**：Designer 里原先按**名字**构造字体（`new Font("Fusion Pixel 12px Proportional ", …)`），
    /// 那要求机器上装过它——换台机器就静默退回默认字体，标题的观感随环境而变。改成
    /// `EmbeddedResource` + <see cref="PrivateFontCollection"/> 在运行期装载，就没有这个依赖。
    ///
    /// **只给标题与副标题用**（见 <see cref="Form1"/> 的 ApplyTitleFont）：它是 12px 点阵字体，
    /// 正文用反而更难读。
    /// </summary>
    internal static class EmbeddedFont
    {
        /// <summary>资源名由 csproj 的 &lt;LogicalName&gt; 钉死，与文件所在目录无关</summary>
        private const string ResourceName = "FlyDisk.fusion-pixel-12px-proportional-zh_hans.ttf";

        private static readonly PrivateFontCollection Collection = new();
        private static FontFamily? _family;

        /// <summary>
        /// 嵌入字体的字族；装载失败时退回系统默认族。
        /// **绝不因为少了个字体就让窗口起不来**——标题难看是小节，起不来不是。
        /// </summary>
        public static FontFamily Family
        {
            get
            {
                if (_family != null) return _family;

                try
                {
                    using Stream? stream = typeof(EmbeddedFont).Assembly.GetManifestResourceStream(ResourceName);
                    if (stream == null) throw new InvalidOperationException($"嵌入资源缺失：{ResourceName}");

                    byte[] bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);

                    // GDI+ **不复制**这段内存，字体集合活着它就一直在 ⇒ 分配后不释放（一份，进程生存期）
                    IntPtr buffer = Marshal.AllocCoTaskMem(bytes.Length);
                    Marshal.Copy(bytes, 0, buffer, bytes.Length);
                    Collection.AddMemoryFont(buffer, bytes.Length);

                    _family = Collection.Families.Length > 0 ? Collection.Families[0] : FontFamily.GenericSansSerif;
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"嵌入字体装载失败，标题/副标题改用系统默认字体：{ex.Message}");
                    _family = FontFamily.GenericSansSerif;
                }

                return _family;
            }
        }
    }
}
