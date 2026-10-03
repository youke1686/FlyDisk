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
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 用 Windows 自带的 **iSCSI 发起端控制库（`iscsidsc.dll`）** 自动挂载 / 摘除本机的 iSCSI 目标。
    ///
    /// 本程序自己就是 iSCSI 目标端（<see cref="TargetService"/> 里的 TalAloni `ISCSI.Server`），
    /// 现在再加一个发起端角色，让"目标起来"之后**盘自己就回到系统视野里**，不用用户手动去
    /// 「iSCSI 发起程序」里点连接。
    ///
    /// 调用链（顺序不可颠倒）：
    ///   挂载 ① MSiSCSI 服务在跑 → ② AddIScsiSendTargetPortalW（注册门户 + 触发 SendTargets 发现）
    ///        → ③ LoginIScsiTargetW（建会话 ⇒ PnP 枚举 LUN ⇒ 盘符回来）
    ///   摘除 ① LogoutIScsiTarget（设备先从设备栈安全移除）→ ② RemoveIScsiSendTargetPortalW（注销门户）
    ///
    /// **失败不硬失败**（2026-10-02 决策）：任一步失败都只记日志 + 把可读原因交给界面提示
    /// "请手动到 iSCSI 发起程序里挂"，不抛异常、不拦启动。
    ///
    /// 已定的两处语义约束：
    /// - `IsInformationalSession = false`：**必须**。真值只建"信息会话"，PnP 不会枚举 LUN，盘符不出现。
    /// - `IsPersistent = false`：**必须**。真值会写持久登录、跨重启自动重连，和"本程序全权管生管死"冲突。
    /// </summary>
    internal sealed class IscsiInitiator
    {
        #region 常量（取自 iscsidsc.h）

        private const string ServiceName = "MSiSCSI";   // Microsoft iSCSI Initiator Service

        /// <summary>等服务启动到 Running 的上限：StartService 是异步的，返回 ≠ 已就绪</summary>
        private static readonly TimeSpan ServiceStartTimeout = TimeSpan.FromSeconds(15);

        /// <summary>过渡态（StartPending 等）等待收敛的上限；超时后仍为过渡态则改走"只等不 Start"</summary>
        private static readonly TimeSpan PendingSettleTimeout = TimeSpan.FromSeconds(5);

        private const uint ERROR_SUCCESS = 0x00000000;
        private const uint ERROR_INSUFFICIENT_BUFFER = 0x0000007A;   // 122

        // 门户注册用"任意发起程序端口"。**值不是 0**（0 会被当成"端口 0"，直接 ERROR_INVALID_PARAMETER=87）：
        // iscsidsc.h 里 ISCSI_ALL_INITIATOR_PORTS / ISCSI_ANY_INITIATOR_PORT 都是 0xFFFFFFFF（即 -1）。
        private const uint ISCSI_ALL_INITIATOR_PORTS = 0xFFFFFFFF;
        private const uint ISCSI_ANY_INITIATOR_PORT = 0xFFFFFFFF;     // 登录用

        private const uint ISCSI_LOGIN_OPTIONS_VERSION_0 = 0;
        private const uint ISCSI_NO_AUTH_TYPE = 0;                    // ISCSI_AUTH_TYPES.ISCSI_NO_AUTH_TYPE
        private const uint ISCSI_DIGEST_NONE = 0;                     // ISCSI_DIGEST_TYPES.ISCSI_DIGEST_TYPE_NONE

        // ISCSI_LOGIN_OPTIONS. InformationSpecified 的位（**以 iscsidsc.h 为准**：
        // 0x01 HEADER_DIGEST、0x02 DATA_DIGEST、0x04 MAXIMUM_CONNECTIONS、0x08 TIME_2_WAIT、
        // 0x10 TIME_2_RETAIN、0x20 USERNAME、0x40 PASSWORD、0x80 AUTH_TYPE）
        private const uint LOGIN_OPT_HEADER_DIGEST = 0x00000001;
        private const uint LOGIN_OPT_DATA_DIGEST = 0x00000002;
        private const uint LOGIN_OPT_MAXIMUM_CONNECTIONS = 0x00000004;
        private const uint LOGIN_OPT_DEFAULT_TIME_2_WAIT = 0x00000008;
        private const uint LOGIN_OPT_DEFAULT_TIME_2_RETAIN = 0x00000010;
        private const uint LOGIN_OPT_AUTH_TYPE = 0x00000080;

        private const int MAX_ISCSI_PORTAL_NAME_LEN = 256;
        private const int MAX_ISCSI_PORTAL_ADDRESS_LEN = 256;

        #endregion

        #region 结构体与 P/Invoke（照 iscsidsc.h，勿逐字照抄 后续待办.md 的旧骨架）

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ISCSI_TARGET_PORTALW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_ISCSI_PORTAL_NAME_LEN)]
            public string SymbolicName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_ISCSI_PORTAL_ADDRESS_LEN)]
            public string Address;

            public ushort Socket;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ISCSI_LOGIN_OPTIONS
        {
            public uint Version;
            public uint InformationSpecified;
            public uint LoginFlags;
            public uint AuthType;
            public uint HeaderDigest;
            public uint DataDigest;
            public uint MaximumConnections;
            public uint DefaultTime2Wait;
            public uint DefaultTime2Retain;
            public uint UsernameLength;
            public uint PasswordLength;
            public IntPtr Username;   // PUCHAR
            public IntPtr Password;   // PUCHAR
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ISCSI_UNIQUE_SESSION_ID
        {
            public ulong AdapterUnique;
            public ulong AdapterSpecific;
        }

        /// <summary>
        /// ISCSI_SESSION_INFOW。注意两个名字字段的**顺序在资料里有分歧**（Windows SDK 是
        /// TargetNodeName 在前、TargetName 在后；mingw 相反），两个都是紧邻的指针，故匹配 IQN 时
        /// **两个都看**，不赌顺序（对端目标两者通常都等于 IQN）。
        /// </summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct ISCSI_SESSION_INFOW
        {
            public ISCSI_UNIQUE_SESSION_ID SessionId;
            public IntPtr InitiatorName;
            public IntPtr TargetNodeName;
            public IntPtr TargetName;
            // ISID[6] + TSID[2]：仅用于把结构体长度/偏移对齐到 iscsidsc.h（我们不读它们），
            // 标成可空只是为了不触发 Nullable 告警；PtrToStructure 会正常填充。
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[]? Isid;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)] public byte[]? Tsid;
            public uint ConnectionCount;
            public IntPtr Connections;
        }

        [DllImport("iscsidsc.dll", CharSet = CharSet.Unicode)]
        private static extern uint AddIScsiSendTargetPortalW(string? initiatorInstance, uint initiatorPortNumber,
            ref ISCSI_LOGIN_OPTIONS loginOptions, ulong securityFlags, ref ISCSI_TARGET_PORTALW portal);

        [DllImport("iscsidsc.dll", CharSet = CharSet.Unicode)]
        private static extern uint RemoveIScsiSendTargetPortalW(string? initiatorInstance, uint initiatorPortNumber,
            ref ISCSI_TARGET_PORTALW portal);

        [DllImport("iscsidsc.dll", CharSet = CharSet.Unicode)]
        private static extern uint LoginIScsiTargetW(string targetName,
            [MarshalAs(UnmanagedType.U1)] bool isInformationalSession, string? initiatorInstance,
            uint initiatorPortNumber, ref ISCSI_TARGET_PORTALW targetPortal, ulong securityFlags,
            IntPtr mappings, ref ISCSI_LOGIN_OPTIONS loginOptions, uint keySize, IntPtr key,
            [MarshalAs(UnmanagedType.U1)] bool isPersistent,
            out ISCSI_UNIQUE_SESSION_ID uniqueSessionId, out ISCSI_UNIQUE_SESSION_ID uniqueConnectionId);

        [DllImport("iscsidsc.dll")]
        private static extern uint LogoutIScsiTarget(ref ISCSI_UNIQUE_SESSION_ID uniqueSessionId);

        [DllImport("iscsidsc.dll", CharSet = CharSet.Unicode)]
        private static extern uint GetIScsiSessionListW(ref uint bufferSize, ref uint sessionCount, IntPtr sessionInfo);

        [DllImport("iscsidsc.dll", CharSet = CharSet.Unicode)]
        private static extern uint ReportIScsiTargetsW([MarshalAs(UnmanagedType.U1)] bool forceUpdate,
            ref uint bufferSize, IntPtr buffer);

        #endregion

        #region 运行期状态

        private ISCSI_UNIQUE_SESSION_ID _session;
        private bool _hasSession;

        private ISCSI_TARGET_PORTALW _portal;
        private bool _hasPortal;

        /// <summary>当前是否有一条由本对象建立的会话（供界面/日志判断）</summary>
        public bool IsMounted => _hasSession;

        #endregion

        #region 挂载

        /// <summary>
        /// 自动挂载：<paramref name="address"/>:<paramref name="port"/> 上名为 <paramref name="iqn"/> 的目标。
        ///
        /// 成功返回 true 并把 <paramref name="detail"/> 记为关键步骤；失败返回 false，<paramref name="detail"/>
        /// 是**可读的英文诊断**（含错误码与失败的那一步），由界面负责翻译成用户提示。**不抛异常。**
        /// </summary>
        public bool Connect(string iqn, string address, int port, out string detail)
        {
            // 幂等：重试路径上可能残留上一次的会话/门户，先清干净再挂
            Disconnect();

            var steps = new List<string>();

            if (!EnsureService(out string serviceDetail))
            {
                detail = $"MSiSCSI service not ready ({serviceDetail})";
                LogService.DebugFile($"iSCSI initiator: mount aborted, {detail}");
                return false;
            }
            steps.Add($"service=ok({serviceDetail})");

            ISCSI_TARGET_PORTALW portal = BuildPortal(address, port);
            ISCSI_LOGIN_OPTIONS options = BuildLoginOptions();

            uint rc = AddIScsiSendTargetPortalW(null, ISCSI_ALL_INITIATOR_PORTS, ref options, 0, ref portal);
            steps.Add($"AddPortal=0x{rc:X8}");
            if (rc != ERROR_SUCCESS)
            {
                detail = $"AddIScsiSendTargetPortalW failed, rc=0x{rc:X8}";
                LogFailure(iqn, address, port, detail, steps);
                return false;
            }
            _portal = portal;
            _hasPortal = true;

            // 确认 IQN 已进入"已发现目标"列表（消竞态）。**非致命**：回环下几乎瞬时，探不到也照样试登录。
            bool discovered = WaitForTarget(iqn, 2000);
            steps.Add($"discovered={discovered}");

            rc = LoginIScsiTargetW(iqn, false, null, ISCSI_ANY_INITIATOR_PORT, ref portal, 0,
                IntPtr.Zero, ref options, 0, IntPtr.Zero, false, out _session, out _);
            steps.Add($"Login=0x{rc:X8}");
            if (rc != ERROR_SUCCESS)
            {
                _session = default;
                detail = $"LoginIScsiTargetW failed, rc=0x{rc:X8}";
                // 登录失败就把刚注册的门户收回，别留脏状态（Disconnect 会处理）
                Disconnect();
                LogFailure(iqn, address, port, detail, steps);
                return false;
            }

            _hasSession = true;
            detail = string.Join("; ", steps);
            LogService.DebugFile(
                $"iSCSI initiator: mounted {iqn} at {address}:{port}, session={SessionText}; {detail}");
            return true;
        }

        /// <summary>
        /// 摘除本对象建立的会话与门户（幂等；从未挂过时什么都不做，也不抛）。
        ///
        /// **登出失败时故意不忘记这条会话**：登出失败通常就是那块盘还被程序占用着（Windows 拒绝把设备
        /// 从设备栈移除）。若照样清掉 <c>_hasSession</c>，下一次「停止加速」会直接跳过登出、永不重试，
        /// 用户只能去 iSCSI 发起程序里手动断。留着它，下次 <see cref="Disconnect"/>（或
        /// <see cref="Connect"/> 开头的幂等清理）会再试一次。
        /// </summary>
        public void Disconnect()
        {
            if (_hasSession)
            {
                ISCSI_UNIQUE_SESSION_ID id = _session;
                bool loggedOut = false;
                try
                {
                    uint rc = LogoutIScsiTarget(ref id);
                    loggedOut = rc == ERROR_SUCCESS;
                    LogService.DebugFile(
                        $"iSCSI initiator: Logout rc=0x{rc:X8}, session={SessionText}");
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"iSCSI initiator: Logout threw: {ex.Message}");
                }

                if (loggedOut)
                {
                    _hasSession = false;
                    _session = default;
                }
            }

            if (_hasPortal)
            {
                ISCSI_TARGET_PORTALW portal = _portal;
                try
                {
                    uint rc = RemoveIScsiSendTargetPortalW(null, ISCSI_ALL_INITIATOR_PORTS, ref portal);
                    LogService.DebugFile(
                        $"iSCSI initiator: RemovePortal rc=0x{rc:X8}, {portal.Address}:{portal.Socket}");
                }
                catch (Exception ex)
                {
                    LogService.DebugFile($"iSCSI initiator: RemovePortal threw: {ex.Message}");
                }
                _hasPortal = false;
                _portal = default;
            }
        }

        #endregion

        #region MSiSCSI 服务（只用托管的 ServiceController）

        /// <summary>
        /// 确保 MSiSCSI 服务在跑。iscsidsc.dll 的所有函数都要经它的 RPC 端点通信，服务没起时
        /// 报错普遍含糊，所以必须先拉起来。
        ///
        /// **只走托管 <see cref="ServiceController"/>，不做裸 SCM / WMI 兜底**（2026-10-02 定）；
        /// 失败就是失败，兜底手段是让用户手动去「iSCSI 发起程序」挂。
        /// **不改服务启动类型**（那是用户机器的持久变更）。
        /// </summary>
        private static bool EnsureService(out string detail)
        {
            try
            {
                using var svc = new ServiceController(ServiceName);
                ServiceControllerStatus status = svc.Status;   // 服务不存在（1060）时这里就抛

                // 过渡态（StartPending / StopPending / …）**不能直接 Start()**：StartService 会失败，而 .NET
                // 把它包装成 InvalidOperationException("Cannot start service ...")——与"服务被禁用"是**同一条
                // 文案**，根本区分不了，却是个**误判**（它马上就会进入目标状态）。所以先把过渡态等掉再决定。
                status = WaitForPendingStateToSettle(svc, status);

                if (status == ServiceControllerStatus.Running)
                {
                    detail = "already running";
                    return true;
                }

                if (IsPendingState(status))
                {
                    // 等了一会儿还在过渡态：**只等不 Start**（Start 会抛），成败交给 WaitForStatus 判
                    svc.WaitForStatus(ServiceControllerStatus.Running, ServiceStartTimeout);
                    detail = $"already starting (still {status})";
                    return true;
                }

                svc.Start();
                // StartService 是异步的，返回 ≠ 已 Running；等它真正起来再往下走
                svc.WaitForStatus(ServiceControllerStatus.Running, ServiceStartTimeout);
                detail = $"started (was {status})";
                return true;
            }
            catch (Exception ex)
            {
                detail = DescribeException(ex);
                return false;
            }
        }

        /// <summary>是否为"过渡态"（正在启动 / 正在停止 / 正在继续 / 正在暂停）</summary>
        private static bool IsPendingState(ServiceControllerStatus status) =>
            status == ServiceControllerStatus.StartPending
            || status == ServiceControllerStatus.StopPending
            || status == ServiceControllerStatus.ContinuePending
            || status == ServiceControllerStatus.PausePending;

        /// <summary>
        /// 把过渡态等掉（最多 <see cref="PendingSettleTimeout"/>），返回稳定后的状态。
        /// 专为避开"StartPending 时调 Start() 抛异常、被误判为启动失败"。
        /// </summary>
        private static ServiceControllerStatus WaitForPendingStateToSettle(
            ServiceController svc, ServiceControllerStatus status)
        {
            long deadline = Environment.TickCount64 + (long)PendingSettleTimeout.TotalMilliseconds;
            while (IsPendingState(status) && Environment.TickCount64 < deadline)
            {
                Thread.Sleep(200);
                svc.Refresh();
                status = svc.Status;
            }
            return status;
        }

        /// <summary>只读地探一下服务是否在跑（**不启动**）：用于启动期清理，避免为清理而无谓地拉起服务</summary>
        private static bool IsServiceRunning()
        {
            try
            {
                using var svc = new ServiceController(ServiceName);
                return svc.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region 崩溃残留清理

        /// <summary>
        /// 崩溃 / 强杀后的残留清理（**幂等**，失败不拦启动）：枚举活动会话，把目标名等于
        /// <paramref name="iqn"/> 的逐个登出，再注销门户。服务没在跑时直接跳过（不为清理而拉起服务）。
        /// </summary>
        public static void CleanupStale(string iqn, string address, int port)
        {
            if (string.IsNullOrWhiteSpace(iqn)) return;

            try
            {
                if (!IsServiceRunning())
                {
                    LogService.DebugFile("iSCSI initiator: stale cleanup skipped (MSiSCSI not running)");
                    return;
                }

                int loggedOut = 0;
                foreach ((ISCSI_UNIQUE_SESSION_ID id, string target) in EnumerateSessions())
                {
                    if (!IsKnownTargetName(target, iqn)) continue;

                    ISCSI_UNIQUE_SESSION_ID sessionId = id;
                    uint rc = LogoutIScsiTarget(ref sessionId);
                    loggedOut++;
                    LogService.DebugFile($"iSCSI initiator: stale logout rc=0x{rc:X8}, target={target}");
                }

                ISCSI_TARGET_PORTALW portal = BuildPortal(address, port);
                uint removeRc = RemoveIScsiSendTargetPortalW(null, ISCSI_ALL_INITIATOR_PORTS, ref portal);
                LogService.DebugFile(
                    $"iSCSI initiator: stale cleanup done, loggedOut={loggedOut}, removePortal rc=0x{removeRc:X8}");
            }
            catch (Exception ex)
            {
                // 清理是"尽力而为"，任何失败都不该影响启动
                LogService.DebugFile($"iSCSI initiator: stale cleanup failed: {DescribeException(ex)}");
            }
        }

        /// <summary>枚举活动会话（返回 会话 id + 目标名）。失败时返回空表</summary>
        private static List<(ISCSI_UNIQUE_SESSION_ID Id, string Target)> EnumerateSessions()
        {
            var result = new List<(ISCSI_UNIQUE_SESSION_ID, string)>();

            uint bufferSize = 0;
            uint sessionCount = 0;
            uint rc = GetIScsiSessionListW(ref bufferSize, ref sessionCount, IntPtr.Zero);
            if (rc == ERROR_SUCCESS) return result;                        // 没有会话
            if (rc != ERROR_INSUFFICIENT_BUFFER || bufferSize == 0)
            {
                LogService.DebugFile($"iSCSI initiator: session list probe rc=0x{rc:X8}");
                return result;
            }

            int structSize = Marshal.SizeOf<ISCSI_SESSION_INFOW>();
            // 多留一个结构体的余量：万一托管结构体比原生的小（会算多容量），也不至于写越界捅穿堆
            IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize + structSize);
            try
            {
                sessionCount = bufferSize / (uint)structSize;
                rc = GetIScsiSessionListW(ref bufferSize, ref sessionCount, buffer);
                if (rc != ERROR_SUCCESS)
                {
                    LogService.DebugFile($"iSCSI initiator: session list fetch rc=0x{rc:X8} (struct={structSize}B)");
                    return result;
                }

                for (uint i = 0; i < sessionCount; i++)
                {
                    ISCSI_SESSION_INFOW info =
                        Marshal.PtrToStructure<ISCSI_SESSION_INFOW>(IntPtr.Add(buffer, (int)i * structSize));

                    // 两个紧邻的名字字段都看（顺序在资料里有分歧，见 ISCSI_SESSION_INFOW 注释）
                    string nodeName = ReadUni(info.TargetNodeName);
                    string targetName = ReadUni(info.TargetName);
                    result.Add((info.SessionId, targetName.Length > 0 ? targetName : nodeName));
                    if (nodeName.Length > 0 && !string.Equals(nodeName, targetName, StringComparison.OrdinalIgnoreCase))
                        result.Add((info.SessionId, nodeName));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        /// <summary>目标名是否属于我们要清理的那个 IQN</summary>
        private static bool IsKnownTargetName(string candidate, string iqn) =>
            candidate.Length > 0 && string.Equals(candidate, iqn, StringComparison.OrdinalIgnoreCase);

        private static string ReadUni(IntPtr p) =>
            p == IntPtr.Zero ? string.Empty : (Marshal.PtrToStringUni(p) ?? string.Empty);

        #endregion

        #region 辅助

        private static ISCSI_TARGET_PORTALW BuildPortal(string address, int port) => new()
        {
            SymbolicName = "FlyDisk",
            Address = address,
            Socket = (ushort)port,
        };

        /// <summary>
        /// 构造登录选项：信息位**显式**声明哪些字段有效（不置位时含义依赖注册表默认值，不可控）；
        /// 全部取"无认证 / 无摘要 / 单连接"，`SecurityFlags = 0` ⇒ 不启用 IPsec（本机回环不需要）。
        /// </summary>
        private static ISCSI_LOGIN_OPTIONS BuildLoginOptions() => new()
        {
            Version = ISCSI_LOGIN_OPTIONS_VERSION_0,
            InformationSpecified = LOGIN_OPT_AUTH_TYPE | LOGIN_OPT_HEADER_DIGEST | LOGIN_OPT_DATA_DIGEST
                                   | LOGIN_OPT_MAXIMUM_CONNECTIONS | LOGIN_OPT_DEFAULT_TIME_2_WAIT
                                   | LOGIN_OPT_DEFAULT_TIME_2_RETAIN,
            LoginFlags = 0,
            AuthType = ISCSI_NO_AUTH_TYPE,
            HeaderDigest = ISCSI_DIGEST_NONE,
            DataDigest = ISCSI_DIGEST_NONE,
            MaximumConnections = 1,
            DefaultTime2Wait = 0,
            DefaultTime2Retain = 0,
            UsernameLength = 0,
            PasswordLength = 0,
            Username = IntPtr.Zero,
            Password = IntPtr.Zero,
        };

        /// <summary>轮询"已发现目标"列表，等 <paramref name="iqn"/> 出现（消发现竞态）</summary>
        private static bool WaitForTarget(string iqn, int timeoutMs)
        {
            long deadline = Environment.TickCount64 + timeoutMs;
            bool forceUpdate = true;
            do
            {
                foreach (string target in ReportedTargets(forceUpdate))
                {
                    if (string.Equals(target, iqn, StringComparison.OrdinalIgnoreCase)) return true;
                }
                forceUpdate = false;   // 只有第一次强制刷新，之后读缓存，别把服务问爆
                Thread.Sleep(150);
            }
            while (Environment.TickCount64 < deadline);

            return false;
        }

        /// <summary>
        /// ReportIScsiTargetsW：缓冲是**双 null 结尾**的多字符串。
        ///
        /// **单位陷阱**：它的 `BufferSize` 文档写的是"列表元素数"，而 `GetIScsiSessionListW` 的同类参数
        /// 是"字节数"——两者不一致。上一版按字节理解，先探测需求、再照数分配，结果探测值（元素数，如 1）
        /// 被当成字节数 → 只分配了 1 字节，服务端往里写整条 IQN → **堆溢出（0xC0000374）**。
        /// 修法：**不猜单位**——分配足够大的固定缓冲，输入值取 `容量/2`；这样无论服务端按字节还是按
        /// 元素(WCHAR)理解，它认为可写的上限都不会超过我们实际分配的容量。
        /// </summary>
        private static List<string> ReportedTargets(bool forceUpdate)
        {
            var result = new List<string>();

            const int capacityBytes = 16 * 1024;   // 16 KiB ⇒ 最多 8192 个 WCHAR，远超任何真实目标列表
            IntPtr buffer = Marshal.AllocHGlobal(capacityBytes);
            try
            {
                // 清零：若服务端没写满，靠 null 终止读取时不会读到未初始化的垃圾
                Marshal.Copy(new byte[capacityBytes], 0, buffer, capacityBytes);

                uint size = capacityBytes / 2;     // 见上：两种单位解释下都安全
                uint rc = ReportIScsiTargetsW(forceUpdate, ref size, buffer);
                if (rc != ERROR_SUCCESS)
                {
                    LogService.DebugFile($"iSCSI initiator: ReportIScsiTargets rc=0x{rc:X8}");
                    return result;
                }

                int offset = 0;
                while (offset < capacityBytes)
                {
                    string s = ReadUni(IntPtr.Add(buffer, offset));
                    if (s.Length == 0) break;                              // 双 null 结尾
                    result.Add(s);
                    offset += (s.Length + 1) * 2;
                }
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"iSCSI initiator: ReportIScsiTargets threw: {DescribeException(ex)}");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return result;
        }

        private void LogFailure(string iqn, string address, int port, string detail, List<string> steps)
        {
            LogService.DebugFile(
                $"iSCSI initiator: mount FAILED {iqn} at {address}:{port}: {detail}; steps=[{string.Join(", ", steps)}]");
        }

        private string SessionText => $"{_session.AdapterUnique:X16}:{_session.AdapterSpecific:X16}";

        private static string DescribeException(Exception ex)
        {
            if (ex is System.ComponentModel.Win32Exception w32)
                return $"{ex.GetType().Name}(Win32 {w32.NativeErrorCode}): {w32.Message}";
            return $"{ex.GetType().Name}: {ex.Message}";
        }

        #endregion
    }
}
