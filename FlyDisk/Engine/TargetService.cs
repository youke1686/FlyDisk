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
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using DiskAccessLibrary;
using ISCSI.Logging;
using ISCSI.Server;
using FlyDisk.Localization;
using FlyDisk.Models;
using SCSI;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 目标生命周期：**校验 → 整盘脱机 → 独占打开 → 装缓存 → 起 iSCSI target**，以及反向的停止。
    ///
    /// 这就是阶段一的"服务宿主"在阶段二的位置——只是它不再挂文件系统，而是提供一个块设备。
    /// 单进程形态下它同时是 UI 的数据源（<see cref="CreateSnapshot"/>）。
    ///
    /// 启动顺序里有两条硬约束：
    /// 1. **先脱机再独占打开**：宿主 NTFS 必须先退场（否则两套 NTFS 同管一批扇区必然损坏），
    ///    脱机后卷被卸载，此时才拿得到 <c>dwShareMode = 0</c> 的独占句柄（见 <see cref="PhysicalDiskHandle"/>）；
    /// 2. **校验在脱机之前**：块大小、系统盘、可移动介质、是否机械盘、L2 目录是否落在同一块盘上——
    ///    这些都要在"还能读到卷/设备信息"的时候判定，且失败时系统状态一点都没动。
    ///
    /// 停止顺序：**先拒绝"还有连接在用时"的停止 → 停 target → 确认没有在途命令 → 冻结并落盘 L2 账本 →
    /// 关句柄（盘保持脱机）**。
    /// 关句柄不联机是刻意选择（盘归本程序管，见 <see cref="PhysicalDiskHandle"/> 类注释）。
    /// </summary>
    public sealed class TargetService
    {
        private readonly DiskConfig _config;

        /// <summary>本地形态的独占盘句柄（**远程形态为 null**——那边没有本地源盘要管）</summary>
        private PhysicalDiskHandle? _device;

        /// <summary>当前块源（本地 = 上面的句柄；远程 = 协议客户端）。缓存与 LUN 后端只认它</summary>
        private IBlockSource? _source;

        /// <summary>当前块源的描述：INQUIRY 上报与统计展示都取自它（**不区分形态**）</summary>
        private BlockSourceInfo? _sourceInfo;

        private CacheService? _cache;
        private CachedPhysicalDisk? _disk;
        private ISCSITarget? _target;
        private ISCSIServer? _server;
        private readonly object _lifecycleLock = new();
        private int _cleanupPending;

        /// <summary>
        /// 发起端：target 起来之后自动把盘挂回系统视野、停止时自动摘除（见 后续待办.md 待办 3）。
        /// 本程序同时是目标端与发起端，两条链各走各的（TalAloni 服务器 ↔ iscsidsc 客户端）。
        /// </summary>
        private readonly IscsiInitiator _initiator = new();

        public TargetService(DiskConfig config)
        {
            _config = config;
        }

        /// <summary>target 是否正在提供块设备</summary>
        public bool IsRunning => Volatile.Read(ref _server) != null;

        /// <summary>停服超时后仍有命令在途，后台持有资源直到命令退出；期间禁止重启或联机</summary>
        public bool IsStopping => Volatile.Read(ref _cleanupPending) != 0;

        /// <summary>
        /// 本次启动是否**自动挂载成功**（发起端会话由本程序建立）。失败时启动流程照常继续，
        /// 只是盘要用户手动去「iSCSI 发起程序」挂——界面据此决定提示文案。
        /// </summary>
        public bool AutoMounted { get; private set; }

        /// <summary>自动挂载失败时的英文诊断（含错误码与失败步骤）；成功时为空串</summary>
        public string AutoMountError { get; private set; } = string.Empty;

        public DiskConfig Config => _config;

        /// <summary>当前块源的描述（未启动时为 null）</summary>
        public BlockSourceInfo? SourceInfo => _sourceInfo;

        /// <summary>当前本地目标盘的盘号（**远程形态为 -1**；未启动时也是 -1）。仅用于日志与提示文案</summary>
        public int LocalDiskNumber => _device?.DiskNumber ?? -1;

        /// <summary>监听端点文本（如 "127.0.0.1:3260"）</summary>
        public string ListenEndpoint => $"{_config.ListenAddress}:{_config.ListenPort}";

        /// <summary>缓存引擎（未启动时为 null；Inspector 要的统计走 <see cref="CreateSnapshot"/>）</summary>
        public CacheService? Cache => _cache;

        /// <summary>
        /// 发起程序登录成功（iSCSI 连接已建立）时触发，参数为发起程序名与它所在的端点。
        /// **在库的工作线程上抛出**——订阅方（UI）自行切回自己的线程。
        /// </summary>
        public event Action<string, string>? OnInitiatorConnected;

        /// <summary>
        /// 当前仍连着本 target 的 iSCSI 连接数：<c>0</c> = 没有发起程序挂着，<c>-1</c> = 查询失败（未知）。
        /// 库的公开 API 里没有会话查询（<c>SessionManager</c> 是 internal），所以判据取"本机监听端口上还有
        /// ESTABLISHED 的 TCP 连接"——它正是"上层还挂着这块盘"的信号，且不碰库的内部实现。
        /// **未知（-1）时调用方不应拒绝**：探不到不等于没人连着，否则会把用户卡死在停不下来的状态。
        /// </summary>
        public int GetActiveConnectionCount()
        {
            try
            {
                int count = 0;
                foreach (TcpConnectionInformation connection in
                         IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections())
                {
                    if (connection.State == TcpState.Established &&
                        connection.LocalEndPoint.Port == _config.ListenPort)
                    {
                        count++;
                    }
                }
                return count;
            }
            catch (Exception ex)
            {
                LogService.DebugFile(
                    $"iSCSI 目标：查询活动 TCP 连接失败（按“未知”处理，不拦停止）：{ex.Message}");
                return -1;
            }
        }

        /// <summary>
        /// 摘掉自动挂载的会话后，等那条 TCP 连接真正从 ESTABLISHED 列表里消失（最多约 2 秒）。
        /// 不等的话，紧接着的"有连接就拒绝停止"会把自己刚摘掉的那条又数进去，把用户卡住。
        /// 未知（-1）时直接返回——探不到不等于有人连着（口径同 <see cref="GetActiveConnectionCount"/>）。
        /// </summary>
        private void WaitForAutoConnectionGone()
        {
            for (int i = 0; i < 10; i++)
            {
                if (GetActiveConnectionCount() <= 0) return;
                Thread.Sleep(200);
            }
        }

        #region 启动与停止

        /// <summary>
        /// 【本地形态】启动。校验失败或打开失败一律抛异常（由 UI 如实展示 + 记日志），
        /// 并且**失败时不改变系统状态**：盘会在异常路径上恢复成打开前的样子。
        /// </summary>
        /// <param name="target">
        /// 用户刚刚在「选择硬盘」里选定的那块盘。**每次启动都要重新选**（见 后续待办.md 第一节的"选盘时机"）——
        /// 配置里只留一个"上次的选择"用于默认选中，绝不作为启动依据。
        /// </param>
        /// <param name="allowL2Reset">
        /// 允许在"L2 账本与盘/参数不匹配"时清空重建。默认 false ⇒ 抛 <see cref="L2LedgerMismatchException"/>，
        /// UI 弹窗问过用户之后再以 true 重试（见 <see cref="SsdCacheService.DetectLedgerMismatch"/>）。
        /// </param>
        public void Start(PhysicalDiskInfo target, bool allowL2Reset = false)
        {
            lock (_lifecycleLock)
            {
                EnsureCleanupComplete();
                StartLocalCore(target, allowL2Reset);
            }
        }

        private void EnsureCleanupComplete()
        {
            if (IsStopping) throw new InvalidOperationException(Locale.T("engine.stop.pending"));
        }

        private void StartLocalCore(PhysicalDiskInfo target, bool allowL2Reset)
        {
            if (IsRunning) return;

            Validate(target, allowL2Reset);

            // 诊断开关（逐笔高频痕迹是否落盘）必须在引擎起来之前设定
            LogService.TraceFileLoggingEnabled = _config.TraceFileLogging;

            // 走到这里才动系统状态：脱机 + 独占打开
            PhysicalDiskHandle device = PhysicalDiskHandle.Open(target.DiskNumber);
            _device = device;   // 先登记：异常路径上 StopCore 要靠它把盘恢复原状
            try
            {
                LogService.DebugFileSession($"启动：磁盘 {target.DiskNumber}（{target.Model}），" +
                                            $"原状态 {(target.IsOnline ? "联机" : "脱机（接管）")}，" +
                                            $"扇区 {target.BytesPerSector} B × {_config.SectorsPerBlock} = {target.BytesPerSector * _config.SectorsPerBlock} B/块");

                RunWithSource(device, device.ToBlockSourceInfo(), allowL2Reset);
            }
            catch
            {
                // 启动失败：把刚开出来的东西全退回，并把盘恢复成打开前的样子（不把用户的盘留在"看不见"的状态）
                StopCore(restoreDeviceState: true);
                throw;
            }
        }

        /// <summary>
        /// 【远程形态】启动：块源是协议客户端（对端那块盘）。
        ///
        /// 本地那一整串启动校验（系统盘 / 程序所在盘 / 可移动介质 / 机械盘 / 缓存目录同盘）**全部不适用**——
        /// 本地根本没有源盘。取而代之的校验（协议版本、远端身份 / 容量 / 扇区）已在配对与选盘阶段完成，
        /// 见 后续待办.md 第一节的"客户端形态特有的分流"。
        /// </summary>
        public void StartRemote(IBlockSource remoteSource, BlockSourceInfo remoteInfo, bool allowL2Reset = false)
        {
            lock (_lifecycleLock)
            {
                EnsureCleanupComplete();
                StartRemoteCore(remoteSource, remoteInfo, allowL2Reset);
            }
        }

        private void StartRemoteCore(IBlockSource remoteSource, BlockSourceInfo remoteInfo, bool allowL2Reset)
        {
            if (IsRunning) return;

            LogService.TraceFileLoggingEnabled = _config.TraceFileLogging;

            try
            {
                LogService.DebugFileSession($"启动（远程）：{remoteInfo.Model}，序列号 {remoteInfo.SerialNumber}，" +
                                            $"{(double)remoteInfo.SizeBytes / 1024 / 1024 / 1024:F1} GiB，" +
                                            $"{remoteInfo.BytesPerSector} B/扇区 × {_config.SectorsPerBlock} 扇区/块");

                RunWithSource(remoteSource, remoteInfo, allowL2Reset);
            }
            catch
            {
                // 远程形态没有"盘的状态"要恢复，但要把已建起来的缓存 / LUN / target 收干净
                StopCore(restoreDeviceState: false);
                throw;
            }
        }

        /// <summary>两种形态**共同**的部分：建缓存 → 建 LUN 后端 → 起 target。它不碰块源的打开与关闭</summary>
        private void RunWithSource(IBlockSource source, BlockSourceInfo info, bool allowL2Reset)
        {
            _source = source;
            _sourceInfo = info;

            // 每次启动都重置挂载结果（重试路径上要拿到最新一次的状态）
            AutoMounted = false;
            AutoMountError = string.Empty;

            _cache = new CacheService(_config, info, allowL2Reset);
            _disk = new CachedPhysicalDisk(source, _cache);

            var target = new ISCSITarget(_config.TargetIqn, new List<Disk> { _disk });
            target.OnStandardInquiry += Target_OnStandardInquiry;
            target.OnUnitSerialNumberInquiry += Target_OnUnitSerialNumberInquiry;
            target.OnAuthorizationRequest += Target_OnAuthorizationRequest;
            target.OnSessionTermination += Target_OnSessionTermination;

            var server = new ISCSIServer();
            // 先登记所有者：Start 部分成功后抛异常时，StopCore 也能关闭监听器。
            _target = target;
            _server = server;
            server.OnLogEntry += Server_OnLogEntry;
            server.AddTarget(target);
            server.Start(new IPEndPoint(IPAddress.Parse(_config.ListenAddress), _config.ListenPort));

            // 目标已经 listen，立刻用发起端 API 把它挂回系统视野（"目标启动成功即自动挂载"）。
            // **失败不硬失败**：只记下英文诊断，界面据此提示用户手动去「iSCSI 发起程序」挂，启动照常继续。
            // 回环：目标端与发起端都在本机，门户就是本机监听地址。
            AutoMounted = _initiator.Connect(_config.TargetIqn, _config.ListenAddress, _config.ListenPort,
                out string mountError);
            AutoMountError = AutoMounted ? string.Empty : mountError;
        }

        /// <summary>
        /// 停止：**先确认没有发起程序还连着**（有则拒绝，抛异常且不动任何状态）→ target 下线 → 等在途命令结束
        /// → 冻结并落盘 L2 账本 → 关句柄。
        /// **盘保持脱机**（盘归本程序管）：想拿回原来的盘符要再启动本程序连接 iSCSI，
        /// 或调 <see cref="TryReonlineDisk"/>，或用「磁盘管理 → 联机」
        /// （后两者会让跨重启的 L2 缓存被判为过期而作废）。
        /// </summary>
        public void Stop()
        {
            lock (_lifecycleLock) StopRunningCore();
        }

        private void StopRunningCore()
        {
            if (!IsRunning) return;

            // ⓪ 先摘掉**本程序自己**自动挂载的那条会话（"停止加速即摘除"）。不摘的话，下面
            //    "有连接就拒绝停止"会把它数进去，用户就永远停不下来了；而且它正是"上层文件系统
            //    还挂着这块盘"本身，登出会让设备从设备栈安全移除，盘自然消失。
            //    摘完等 TCP 连接真正从 ESTABLISHED 列表里消失，再判有没有**别的**连接。
            _initiator.Disconnect();
            WaitForAutoConnectionGone();

            // 拒绝的理由：停止会把盘从**正在使用它的文件系统**脚下抽走（target 下线 + 关句柄 + 盘保持脱机），
            // 挂在 iSCSI 上的 NTFS 会当场收到介质错误——这跟运行中拔盘是同一件事。
            // （走到这里时上面那条自动连接已摘除，剩下的都是用户手动挂的"额外"连接。）
            int connections = GetActiveConnectionCount();
            if (connections > 0)
            {
                throw new InvalidOperationException(
                    Locale.T("main.msg.iscsiDisconnectFailed"));
            }

            StopCore(restoreDeviceState: false);
        }

        /// <summary>
        /// **关机 / 重启 / 注销**时的有序收尾（由界面层在 <c>CloseReason.WindowsShutDown</c> 分支调用）。
        ///
        /// 与 <see cref="Stop"/> 的**唯一区别**：这里**不做"有连接就拒绝停止"的守卫**。理由是——关机时
        /// L2 账本应尽量落盘，但前提是缓存表已经静止；因此即便发起程序登不出去，
        /// 也中止 iSCSI 服务、掐断会话。若在途 IO 未能结束则跳过提交，让下次启动要求校验。
        ///
        /// 代价（已知且接受）：那块盘会在关机流程里被"意外移除"，挂着的 NTFS 可能丢掉尚未下发的写。
        /// 依据是：Windows 本来就会在关机时拆掉这块盘，这里只是把这一刻提前；而运行中点「停止加速」
        /// 绝不能这么干（那是用户操作、盘还要继续用）——两处口径刻意不同，见 <see cref="Stop"/>。
        ///
        /// **绝不外抛**：抛出去会被 <c>Application.ThreadException</c> 兜住并弹窗，反而卡住关机。
        /// </summary>
        /// <returns>是否完成了收尾（<c>false</c> = 收尾过程出错，进程照常退出）</returns>
        public bool TryStopForShutdown()
        {
            lock (_lifecycleLock) return StopForShutdownCore();
        }

        private bool StopForShutdownCore()
        {
            if (!IsRunning) return !IsStopping;

            try
            {
                // 先按正常路径摘一次自动挂载的会话（登不出去也不影响后面）。结果只用于日志区分。
                _initiator.Disconnect();
                bool orderlyUnmount = !_initiator.IsMounted;

                // 无论摘没摘干净，都走完整收尾：停 target（中止服务、掐断所有会话）→ 关 LUN 后端 →
                // 等在途命令跑完 → 冻结并落 L2 账本 → 关句柄（盘保持脱机）。
                StopCore(restoreDeviceState: false);

                if (IsStopping)
                {
                    LogService.DebugFile("shutdown stop: in-flight IO did not finish; L2 ledger not committed");
                    return false;
                }
                LogService.DebugFile(orderlyUnmount
                    ? "shutdown stop: orderly stop done; healthy L2 ledger commit attempted"
                    : "shutdown stop: forced stop (initiator logout failed); healthy L2 ledger commit attempted");
                return true;
            }
            catch (Exception ex)
            {
                // 关机路径**绝不外抛**：异常会被 ThreadException 兜住并弹窗，从而卡住关机。
                LogService.DebugFile($"shutdown stop failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 把**用户选定的那块本地盘**重新联机（等价于「磁盘管理 → 联机」），让它的卷与源盘符回到系统视野。
        ///
        /// 入参由界面给出——「重新联机源盘」菜单项复用了选盘对话框，只列脱机盘（见 后续待办.md 第十二节），
        /// 所以这里不再需要"记住上次用过的盘号"。
        ///
        /// **刻意不做成"停止即联机"**：停止后保持脱机是既定口径（盘归本程序管 ⇒ 跨重启的 L2 账本才敢直接采信），
        /// 所以联机是一个**用户显式触发**的独立动作（见 <see cref="PhysicalDiskHandle.TryOnline"/>）。
        ///
        /// 联机后那份跨重启的 L2 账本会自动作废：下次启动 <see cref="PhysicalDiskHandle.Probe"/> 会读到该盘已联机
        /// （<c>WasOnline = true</c>）⇒ <c>BlockSourceInfo.FromDisk</c> 标脏 ⇒ SsdCacheService 判其过期、要求校验。
        /// 无需额外代码，界面只负责把这件事提前告诉用户。
        /// </summary>
        /// <returns>成功 <c>true</c>；失败 <c>false</c> 且 <paramref name="error"/> 给出可读原因（不抛，供界面直接展示）</returns>
        public bool TryReonlineDisk(int diskNumber, out string error)
        {
            lock (_lifecycleLock) return TryReonlineDiskCore(diskNumber, out error);
        }

        private bool TryReonlineDiskCore(int diskNumber, out string error)
        {
            if (IsStopping)
            {
                error = Locale.T("engine.stop.pending");
                return false;
            }
            // ① 正在提供块设备时**绝对不能**联机：那块盘此刻是本程序的块源，联机等于让宿主 NTFS 与
            //    iSCSI 上的克隆盘同时管一批扇区（同签名双盘 + 双写），必然损坏数据。
            //    界面已把菜单项置灰，这里是兜底。
            if (IsRunning)
            {
                error = Locale.T("main.msg.reonlineRunningCannot");
                return false;
            }

            // ② 还有 iSCSI 连接挂着 → 那块克隆盘仍在系统视野里，此时联机源盘会出现两块同签名的盘
            int connections = GetActiveConnectionCount();
            if (connections > 0)
            {
                error = Locale.T("engine.reonline.connections", connections);
                return false;
            }

            if (diskNumber < 0)
            {
                error = Locale.T("engine.reonline.noDisk");
                return false;
            }

            return PhysicalDiskHandle.TryOnline(diskNumber, out error);
        }

        private void StopCore(bool restoreDeviceState)
        {
            // ⓪ 先摘掉自动挂载（幂等）：本程序自己建的会话必须在 target 下线**之前**登出，
            //    否则盘会从正在使用它的文件系统脚下被抽走（挂着的 NTFS 会当场收到介质错误）。
            _initiator.Disconnect();

            // ① 先停 target：不再接受新命令、断开会话
            try
            {
                _server?.Stop();
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"停止 iSCSI 服务端时出错（继续收尾）：{ex.Message}");
            }

            // ①′ 立刻关上 LUN 后端：库里 target 的命令队列**没有排空手段**，队列尾巴上的命令会在
            //     下面几步（关句柄、写账本）之后才被执行。让它们撞上"目标已停止"的 IO 错误，
            //     比让它们碰到已释放的句柄（ObjectDisposedException 会把工作线程带走）安全得多。
            _disk?.Close();

            CachedPhysicalDisk? disk = _disk;
            CacheService? cache = _cache;
            PhysicalDiskHandle? device = _device;
            bool idle = disk == null || disk.WaitForIdle(2000);

            _server = null;
            _target = null;
            _disk = null;

            _cache = null;

            // ④ 最后放块源。
            // **本地形态**：关盘（并按需恢复联机）——句柄的生命周期就绑在 target 上。
            // **远程形态**：**这里不碰块源**！"停止加速"只结束 iSCSI 这一层，**配对与连接必须保留**
            // （用户要能立刻再启动、或换另一块对端盘）。协议客户端的生命周期归「断开配对」管
            // （见 Form1.DisconnectRemote）；顺手 `EndService()` 通知对端收回盘也由 Form1 负责。
            _source = null;
            _sourceInfo = null;
            _device = null;

            if (!idle)
            {
                // 既不能写可信账本，也不能释放正在被命令使用的内存/句柄。
                // 后台等待不改变远程的沉默等待语义；真正结束后才回收，期间挡住新启动。
                Volatile.Write(ref _cleanupPending, 1);
                LogService.DebugFile("等待在途 SCSI 命令超时（2 秒）；跳过 L2 账本提交，等待命令退出后释放资源");
                new Thread(() =>
                {
                    try
                    {
                        disk!.WaitForIdle(Timeout.Infinite);
                        ReleaseStoppedResources(cache, device, restoreDeviceState, persistLedger: false);
                    }
                    catch (Exception ex)
                    {
                        LogService.DebugFile($"停服后台收尾失败：{ex.Message}");
                    }
                    finally { Volatile.Write(ref _cleanupPending, 0); }
                }) { IsBackground = true, Name = "FlyDisk.StopCleanup" }.Start();
                return;
            }

            ReleaseStoppedResources(cache, device, restoreDeviceState, persistLedger: true);
        }

        private static void ReleaseStoppedResources(CacheService? cache, PhysicalDiskHandle? device,
            bool restoreDeviceState, bool persistLedger)
        {
            try
            {
                cache?.FreezeForShutdown();
                cache?.Shutdown(persistLedger);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"关闭缓存引擎时出错：{ex.Message}");
            }
            finally
            {
                if (restoreDeviceState) device?.RestoreAndClose();
                else device?.Dispose();
            }
        }

        #endregion

        #region 启动校验（全部在脱机之前做）

        private void Validate(PhysicalDiskInfo info, bool allowL2Reset)
        {
            if (info.DiskNumber < 0)
                throw new InvalidOperationException(Locale.T("engine.start.noDisk"));

            // ① 块大小必须是 4096（见 阶段二-iSCSI块设备形态.md §3.5）：块 = 扇区 × SectorsPerBlock
            int blockBytes = info.BytesPerSector * Math.Max(0, _config.SectorsPerBlock);
            if (info.BytesPerSector <= 0 || _config.SectorsPerBlock <= 0 || blockBytes != ServiceConstants.BlockSize)
            {
                int suggested = info.BytesPerSector > 0 ? ServiceConstants.BlockSize / info.BytesPerSector : 8;
                throw new InvalidOperationException(
                    Locale.T("engine.start.blockSize",
                        info.BytesPerSector, _config.SectorsPerBlock, blockBytes,
                        ServiceConstants.BlockSize, suggested));
            }

            // ② 不得是系统盘，也不得是**本程序自己所在的那块盘**：
            //    脱机是"整盘"动作，盘上所有卷会立刻消失——系统盘脱机会把 Windows 送走，
            //    程序所在盘脱机则会把本程序（以及存在盘上的配置/日志/缓存容器）一起送走。
            // 判据与「设置」对话框的提示、以及 L2 校验/修复共用一份实现（PhysicalDiskHandle.DescribeTargetDiskBlockReason）
            string blockedTarget = PhysicalDiskHandle.DescribeTargetDiskBlockReason(info.DiskNumber);
            if (blockedTarget.Length > 0)
            {
                throw new InvalidOperationException(
                    Locale.T("engine.start.blockedTarget", info.DiskNumber, blockedTarget));
            }

            int systemDisk = PhysicalDiskHandle.GetSystemDiskNumber();
            int programDisk = PhysicalDiskHandle.GetProgramDiskNumber();

            if (programDisk < 0)
            {
                LogService.DebugFile("无法确定本程序所在的是哪块物理盘（在网络上运行？），已跳过“程序所在盘”这项校验");
            }
            LogService.DebugFile($"校验基准：目标盘=磁盘 {info.DiskNumber}，系统盘=磁盘 {systemDisk}，程序所在盘=磁盘 {programDisk}");

            // ③ 可移动介质：**只警告不拦**（2026-09-27 起），提示落在「选择硬盘」对话框里
            //    （用户选它的那一刻就看到，比启动后再打一行日志更及时）——见 SelectDiskForm.BuildLocalDetail。

            // ④ 只读的盘没法做"写透传"
            if (info.IsReadOnly)
                throw new InvalidOperationException(Locale.T("engine.start.readOnly", info.DiskNumber));

            // ⑤ 机械盘检查：**只警告不拦**（2026-09-27 起）。
            //    本程序的收益来自机械盘：SSD 做块级缓存通常净亏（阶段一实测：加速 SSD 源盘只有真盘的 34%，
            //    缓存的读写反而和源数据抢同一块盘）。但要允许"拿 SSD 先跑通链路/做对照"，
            //    所以只如实提示、由用户决定——**提示落在「选择硬盘」对话框里**（见 SelectDiskForm.BuildLocalDetail）。
            if (!info.SeekPenaltyKnown)
            {
                LogService.DebugFile($"目标盘 {info.DiskNumber} 读不到“搜寻惩罚”属性，无法确认它是机械盘；仍按配置继续");
            }

            // ⑥ L2 缓存目录不能落在被加速的那块盘上（同盘 = 纯写放大；而且该盘马上要被脱机，卷会消失）
            if (_config.EnableSsdCache)
            {
                if (string.IsNullOrWhiteSpace(_config.SsdCachePath))
                {
                    throw new InvalidOperationException(Locale.T("engine.start.noSsdPath"));
                }

                string root = Path.GetPathRoot(Path.GetFullPath(_config.SsdCachePath)) ?? string.Empty;
                int cacheDisk = PhysicalDiskHandle.GetDiskNumberOfVolume(root);
                if (cacheDisk == info.DiskNumber)
                {
                    throw new InvalidOperationException(
                        Locale.T("engine.start.cacheOnTarget", _config.SsdCachePath, info.DiskNumber));
                }
                if (cacheDisk < 0)
                {
                    LogService.DebugFile($"{root} 无法对应到物理盘，已跳过“缓存目录同盘”校验");
                }

                // ⑥′ 上次留下的 L2 账本跟当前盘/配置对得上吗？对不上时**不静默清空**——那会让用户
                //     莫名其妙丢掉整层缓存。抛给 UI，由它弹窗让用户决定（见 SsdCacheService.DetectLedgerMismatch）。
                //     特意放在这里（脱机之前）：用户选"否"时，系统状态一点都没动。
                if (!allowL2Reset)
                {
                    // **联机状态必须取"此刻"的，不能沿用选盘时的快照**：`info` 是选盘那一刻抓的，
                    // 而「校验 L2」跑完会把源盘脱机——紧随其后的重试若还看旧快照，就会继续判"联机状态"，
                    // 逼用户再点一次「启动加速」。这里重探一次（与 PhysicalDiskHandle.Open 同源），
                    // 保证"状态已变、判据跟上"。
                    bool wasOnlineNow = PhysicalDiskHandle.Probe(info.DiskNumber).IsOnline;

                    string mismatch = SsdCacheService.DetectLedgerMismatch(_config, BlockSourceInfo.FromDisk(info, wasOnlineNow));
                    if (mismatch.Length > 0)
                    {
                        throw new L2LedgerMismatchException(mismatch);
                    }

                    // ⑥″ 盘**原本联机**：上次运行之后它可能被别的程序（或另一台机器）改过，L2 里那份未必还对。
                    //     不再像以前那样静默作废整层——抛给 UI 问用户："先校验再保留" 还是 "清空重建"。
                    //     用户选"校验"并跑完后，校验器会把身份戳写回自洽（且盘已被它脱机）⇒ 紧随其后的
                    //     重试里 wasOnlineNow 即为假，这条不再成立，引擎于是自然采信账本
                    //     （**不需要额外的"已校验"标志**）。
                    if (wasOnlineNow && SsdCacheService.LedgerExists(_config))
                    {
                        throw new L2NeedsVerifyException(
                            Locale.T("engine.start.l2OnlineNeedsVerify", info.DiskNumber),
                            Locale.T("engine.start.reason.online"));
                    }

                    // ⑥‴ 上次**没有正常关服**（掉电/强杀/崩溃）：容器头的身份戳与索引里的对不上。
                    //     以前这里直接静默作废整层；现在也抛给 UI —— 索引结构仍然自洽，逐块校验能把它救回来
                    //     （校验干净时会把戳写回，引擎随后即可载入）。
                    string stampMismatch = SsdCacheService.LedgerStampMismatch(_config);
                    if (stampMismatch.Length > 0)
                    {
                        throw new L2NeedsVerifyException(
                            Locale.T("engine.start.l2StampNeedsVerify", stampMismatch),
                            Locale.T("engine.start.reason.abnormalShutdown"));
                    }
                }
            }

            // ⑦ 监听地址要能解析（否则下面 IPAddress.Parse 会抛一个看不出原因的异常）
            if (!IPAddress.TryParse(_config.ListenAddress, out _))
                throw new InvalidOperationException(Locale.T("engine.start.badListenAddress", _config.ListenAddress));

            if (string.IsNullOrWhiteSpace(_config.TargetIqn))
                throw new InvalidOperationException(Locale.T("engine.start.emptyIqn"));
        }

        #endregion

        #region 统计快照

        /// <summary>给 UI 的统计快照：设备信息 + 块 IO 计数 + 两层缓存计数（只读计数器，不遍历集合）</summary>
        public CacheStats CreateSnapshot()
        {
            BlockSourceInfo? info = _sourceInfo;
            var stats = new CacheStats
            {
                Timestamp = DateTime.Now,
                IsRunning = IsRunning,
                PhysicalDiskNumber = _device?.DiskNumber ?? -1,
                DeviceModel = info?.Model ?? string.Empty,
                DeviceSerial = info?.SerialNumber ?? string.Empty,
                DeviceSizeBytes = info?.SizeBytes ?? 0,
                BytesPerSector = info?.BytesPerSector ?? 0,
                SectorsPerBlock = _config.SectorsPerBlock,
                DeviceWasOffline = info != null && !info.WasOnline,
                ActiveConnections = IsRunning ? GetActiveConnectionCount() : 0,
                TargetIqn = _config.TargetIqn,
                ListenEndpoint = ListenEndpoint,
                ReadCommandsTotal = _disk?.ReadCommandsTotal ?? 0,
                ReadSectorsTotal = _disk?.ReadSectorsTotal ?? 0,
                ReadFullHitCommandsTotal = _disk?.ReadFullHitCommandsTotal ?? 0,
                WriteCommandsTotal = _disk?.WriteCommandsTotal ?? 0,
                WriteSectorsTotal = _disk?.WriteSectorsTotal ?? 0
            };

            _cache?.FillSnapshot(stats);
            return stats;
        }

        #endregion

        #region ISCSI 事件（上报身份 + 会话日志）

        /// <summary>
        /// INQUIRY 标准数据：上报**底层盘自己的型号/固件**，而不是库默认的 "TalAloni" / "SCSI Disk 0"。
        /// 上层看到的就是这块盘的型号，属"保真"而非伪装（见 阶段二-iSCSI块设备形态.md §5.6）。
        /// 字段宽度是 SCSI 规定的（厂商 8 / 产品 16 / 固件 4），超长只能截断——真 SCSI 盘也一样受限。
        /// </summary>
        private void Target_OnStandardInquiry(object? sender, StandardInquiryEventArgs args)
        {
            BlockSourceInfo? info = _sourceInfo;
            if (info == null) return;

            if (info.VendorId.Length > 0) args.Data.VendorIdentification = Truncate(info.VendorId, 8);
            if (info.ProductId.Length > 0) args.Data.ProductIdentification = Truncate(info.ProductId, 16);
            if (info.FirmwareRevision.Length > 0) args.Data.ProductRevisionLevel = Truncate(info.FirmwareRevision, 4);

            if (info.ProductId.Length > 16)
            {
                LogService.DebugFile($"INQUIRY 产品串超出 16 字符已截断：“{info.ProductId}” → “{Truncate(info.ProductId, 16)}”");
            }
        }

        /// <summary>VPD 页 0x80：上报底层盘的真实序列号（库默认给的是 "00000000"）</summary>
        private void Target_OnUnitSerialNumberInquiry(object? sender, UnitSerialNumberInquiryEventArgs args)
        {
            BlockSourceInfo? info = _sourceInfo;
            if (info == null) return;
            if (info.SerialNumber.Length > 0) args.Page.ProductSerialNumber = Truncate(info.SerialNumber, 252);
        }

        /// <summary>发起程序登录：放行并记一笔（本机回环、无 CHAP，一律放行）。
        /// 这也是"iSCSI 连接成功"的信号，顺带抛给 UI 展示。</summary>
        private void Target_OnAuthorizationRequest(object? sender, AuthorizationRequestArgs args)
        {
            args.Accept = true;
            LogService.DebugFile($"发起程序登录：{args.InitiatorName}（{args.InitiatorEndPoint}）");
            OnInitiatorConnected?.Invoke(args.InitiatorName, Convert.ToString(args.InitiatorEndPoint) ?? string.Empty);
        }

        private void Target_OnSessionTermination(object? sender, SessionTerminationArgs args)
        {
            LogService.DebugFile($"会话结束：{args.InitiatorName}（原因：{args.Reason}）");
        }

        /// <summary>库自己的日志：只落盘 Information 及以上（Warning/Error/Critical/Information）。
        /// Verbose 及以下包含"每条 SCSI 命令"那一档，落盘会淹掉账本。</summary>
        private void Server_OnLogEntry(object? sender, LogEntry entry)
        {
            if (entry.Severity > Severity.Information) return;
            LogService.DebugFile($"[iSCSI/{entry.Severity}] {entry.Message}");
        }

        private static string Truncate(string value, int maxLength)
            => value.Length <= maxLength ? value : value.Substring(0, maxLength);

        #endregion
    }
}
