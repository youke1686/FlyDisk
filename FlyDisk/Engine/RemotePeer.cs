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
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using FlyDisk.Localization;
using Timer = System.Threading.Timer;

namespace FlyDisk.Engine
{
    /// <summary>对端列出的一块磁盘</summary>
    public sealed class RemoteDiskInfo
    {
        /// <summary>唯一标识（有序列号时是 `型号|序列号`）——**不是盘号**（盘号只是枚举顺序）</summary>
        public string Identity { get; init; } = string.Empty;

        /// <summary>展示用名字（型号 + 容量 + 联机状态）</summary>
        public string DisplayName { get; init; } = string.Empty;

        public long SizeBytes { get; init; }
        public int BytesPerSector { get; init; }

        /// <summary>对端报告该盘是机械盘</summary>
        public bool Rotational { get; init; }
    }

    /// <summary>链路层问题（断开 / 超时 / 协议错）：调用方据此进入"沉默等待 + 重连"，而不是当介质错误往上报</summary>
    public sealed class RemoteLinkException : IOException
    {
        public RemoteLinkException(string message) : base(message) { }
    }

    /// <summary>
    /// 重连之后发现对端那块盘**已经不是原来那一块**。本地缓存里所有块号对应的都是**旧盘**的内容，
    /// 继续服务就是给上层喂错数据，所以上层必须**停掉本地 target**。
    /// </summary>
    public sealed class RemoteDeviceChangedException : IOException
    {
        public RemoteDeviceChangedException(string message) : base(message) { }
    }

    /// <summary>
    /// 远程**对端连接**——**配对之后两端完全对称**，共用这一个类型。
    ///
    /// 不对称的只有网络层：谁有公网谁 <see cref="Accept"/>，另一端 <see cref="Connect"/>。
    /// 配对成功之后：
    /// · **两端都能** <see cref="ListDisks"/> / <see cref="BeginService"/>——谁能选对方的盘，谁就成为本次的使用方；
    /// · 被选中的一端弹**唯一的人工闸门**「是否信任并同意脱机」（<see cref="OnAuthorize"/>）；
    /// · 使用方把本实例当 <see cref="IBlockSource"/> 读写；提供方在同一实例上被动响应。
    ///
    /// **同一时刻只有一方在使用盘**（MVP 一对一）：一方在用，另一方就是"提供服务中"（主按钮置灰）。
    ///
    /// 断链语义 = **沉默等待**：传输阶段**没有请求超时**（机械盘唤醒几秒、慢链路传 1MB 都正常）；
    /// 断开只由"TCP 报错"或"心跳 30 秒无字节"判定，之后在调用线程里无限重连并重发在途请求
    /// （块读写天然幂等）。上层的 SCSI 命令因此会一直挂着，直到 initiator 自己的 60 秒 hold 到期。
    /// </summary>
    public sealed class RemotePeer : IBlockSource, IDisposable
    {
        private RemoteChannel? _channel;
        private Thread? _pump;
        private volatile bool _closing;

        // ===== 建立方式（全流程唯一不对称的地方）=====
        private string _host = string.Empty;
        private int _port;
        private bool _isListener;
        private TcpListener? _listener;      // 监听方：重连时靠它继续 Accept

        // ===== 提供方：连接断开后保留服务状态 120 秒，等对方重连 =====
        private const int ServiceHoldSeconds = 120;
        private Timer? _holdTimer;
        private long _serviceExpiryTicks;

        /// <summary>重连去重：监听方可能同时从"等待重连的后台任务"与"正在跑的传输"两条路进来</summary>
        private readonly object _reconnectLock = new();

        /// <summary>**本端**发出、正在等的那个请求是否被用户取消了（只有「取消请求」会设它）</summary>
        private volatile bool _selectCancelled;

        /// <summary>
        /// **对端**取消了它刚发出的选盘请求（它发来了 <see cref="RemoteProtocol.MsgCancelSelect"/>）。
        /// 与上面那个是两回事：这个只用来让"还开着的授权框"作废，**不能影响本端的等待状态**。
        /// </summary>
        private volatile bool _peerCancelledSelect;

        /// <summary>【监听方】对端重连的新连接挂上来时置位，用于唤醒正卡在 <see cref="WaitLinkRestored"/> 里的传输线程</summary>
        private readonly ManualResetEventSlim _linkRestored = new(false);

        // ===== 单 in-flight：我发出去的请求 =====
        private readonly ManualResetEventSlim _replyEvent = new(false);
        private readonly object _replyLock = new();
        private long _cookie;
        private long _replyCookie;
        private int _replyError;
        private byte[]? _replyPayload;
        private int _expectDataLength;

        // ===== 心跳与判活（泵线程写，其它线程读）=====
        private long _lastReceived;
        private long _lastSent;
        private int _linkDown;
        private int _reconnects;

        // ===== 本端作为"提供方"时持有的盘 =====
        private PhysicalDiskHandle? _serving;
        private string _servingIdentity = string.Empty;

        // ===== 本端作为"使用方"时的那块盘 =====
        private BlockSourceInfo _servingFrom = new();
        private string _usingIdentity = string.Empty;

        private RemotePeer() { }

        /// <summary>对端机器名（Hello 里交换过来的）</summary>
        public string PeerName { get; private set; } = string.Empty;

        /// <summary>本端是监听方（有公网的那端）</summary>
        public bool IsListener => _isListener;

        /// <summary>本端是否正在使用对方的盘（此时本实例就是可用块源）</summary>
        public bool IsUsingPeer => _usingIdentity.Length > 0 && _linkDown == 0;

        /// <summary>本端是否正在把自己的盘提供给对方（对方已脱机在用）</summary>
        public bool IsServingPeer => _serving != null;

        /// <summary>链路是否已判死（正在重连中）</summary>
        public bool IsLinkDown => Volatile.Read(ref _linkDown) != 0;

        public int ReconnectCount => _reconnects;

        /// <summary>正在提供的盘的名字（未提供时为空）</summary>
        public string ServingDisplay => _serving?.Info.Model ?? string.Empty;

        /// <summary>
        /// 对方向本端要一块盘时的人工闸门（**在泵线程上同步调用**，实现方负责 marshal 回 UI 线程并等用户点击）。
        /// 参数：请求的盘、对端身份串；返回 true 表示同意（本端随即把它脱机独占）。
        /// </summary>
        public Func<PhysicalDiskInfo, string, bool>? OnAuthorize { get; set; }

        /// <summary>对端宣告"我没法继续提供了"（盘被拔等）。UI 订阅它去停本地 target</summary>
        public event Action<string>? OnServiceEnded;

        /// <summary>状态变化（配对 / 提供服务 / 服务结束），UI 用它刷新</summary>
        public event Action? OnStateChanged;

        /// <summary>
        /// **对端主动断开配对**（不是掉线）。UI 收到后应清掉这个对端、回到"等待配对"，而不是等重连——
        /// 后者会一直停在那儿，因为再也不会有人连回来。
        /// </summary>
        public event Action? OnPeerLeft;

        #region 建立连接（唯一不对称的地方）

        /// <summary>【拨号方】连上对端并完成配对（Hello / HelloAck），**无任何人工确认**</summary>
        public static RemotePeer Connect(string host, int port)
        {
            var peer = new RemotePeer { _host = host, _port = port, _isListener = false };
            peer.AttachChannel(new RemoteChannel(peer.DialSocket()), sendHello: true);
            return peer;
        }

        private Socket DialSocket()
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                socket.Connect(new IPEndPoint(IPAddress.Parse(_host), _port));
                return socket;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 【监听方】接受一个连接。**重连的主动权永远在拨号方**（它记着地址），
        /// 监听方这边只负责"接住"，然后按情况分派：
        ///
        /// · `existing` 为空 ⇒ 全新配对，新建实例；
        /// · `existing` 已断（它可能在用对方的盘，本地 target 正指着它）⇒ **把新连接挂到它身上**，
        ///   保住它的块源身份与已提供状态；
        /// · `existing` 活得好好的 ⇒ 拒掉这个新连接（一次只服务一个对端），返回 null。
        /// </summary>
        public static RemotePeer? Accept(TcpListener listener, RemotePeer? existing)
        {
            Socket socket = listener.AcceptSocket();

            if (existing != null && !existing._closing)
            {
                if (existing.IsLinkDown)
                {
                    try
                    {
                        existing.AttachReconnected(new RemoteChannel(socket));
                    }
                    catch
                    {
                        // 挂接失败（握手没走完就被掐断、握手超时等）：**必须让它回到"等重连"**，
                        // 否则它下次会以"已有活对端"为由把新连接永久拒掉——监听端就再也没人能连进来了。
                        try { socket.Dispose(); } catch { /* 已经断了 */ }
                        existing.MarkLinkDown();
                        throw;
                    }
                    return existing;
                }

                // 已经有一个活着的对端：拒掉新的（对方会看到连接被关）
                try { socket.Dispose(); } catch { /* 已经断了 */ }
                return null;
            }

            var peer = new RemotePeer { _isListener = true, _listener = listener };
            try
            {
                peer.AttachChannel(new RemoteChannel(socket), sendHello: false);
            }
            catch
            {
                try { socket.Dispose(); } catch { /* 已经断了 */ }
                throw;
            }
            return peer;
        }

        /// <summary>
        /// 【监听方】把对端重连的新连接挂到本实例上。
        /// 走这里而不是新建实例，是为了**保住本端作为"使用方"的块源身份**——
        /// 本地 iSCSI target 正指着这个实例，换成新实例会让它永远等不到数据。
        /// </summary>
        private void AttachReconnected(RemoteChannel channel)
        {
            AttachChannel(channel, sendHello: false);

            // 本端正在用对方的盘：按同一块盘重新起服务（对端记得它在提供这块盘，会直接放行）
            if (_usingIdentity.Length > 0)
            {
                try
                {
                    BlockSourceInfo expected = _servingFrom;
                    BlockSourceInfo restored = RequestService(_usingIdentity);
                    if (!SameDevice(restored, expected))
                    {
                        _closing = true;
                        OnServiceEnded?.Invoke(Locale.T("engine.remote.peerDiskChanged"));
                        return;
                    }
                    _servingFrom = restored;
                }
                catch (Exception ex)
                {
                    _closing = true;
                    OnServiceEnded?.Invoke(Locale.T("engine.remote.restoreFailed", ex.Message));
                    return;
                }
            }

            Interlocked.Increment(ref _reconnects);
            _linkRestored.Set();          // 唤醒正卡在 WaitLinkRestored 里的传输线程
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 【监听方】链路断了以后**只能等**：本端不知道对端地址，没法拨回去。
        /// 上层监听循环接到对端重连的新连接后会调用 <see cref="AttachReconnected"/> 把这里唤醒。
        /// </summary>
        private void WaitLinkRestored()
        {
            _linkRestored.Reset();
            while (!_closing && !_linkRestored.Wait(1000)) { /* 继续等 */ }
        }

        /// <summary>
        /// 把一条连接挂到本实例上（**首次连接与重连都走这里**）。
        /// 实例的生命周期**长于任何一条 TCP 连接**——这正是"提供方跨连接保留服务状态"的基础。
        /// </summary>
        private void AttachChannel(RemoteChannel channel, bool sendHello)
        {
            // **已关闭的实例绝不能再挂新连接**：否则握手照跑、HelloAck 照回，对端以为配对成功，
            // 而本端泵线程起来后立刻因 _closing 退出 ⇒ 对端发什么都石沉大海（表现为"获取对端磁盘失败"）。
            if (_closing) throw new IOException(Locale.T("engine.remote.connectionClosed"));

            _channel = channel;
            _lastReceived = Environment.TickCount64;
            _lastSent = Environment.TickCount64;
            Volatile.Write(ref _linkDown, 0);

            // 握手是"建链"步骤，给它一个上限：一条"连上却不发 Hello"的连接不能把本线程永久钉住
            // （监听端被钉住就再也接不了新配对，而 TCP 还连得上，对端只会一直等）。
            // 握手一过**立刻复位成无限**——传输阶段刻意没有任何请求超时（沉默等待）。
            channel.SetReadTimeout(RemoteProtocol.HandshakeTimeoutMs);
            try
            {
                Handshake(channel, sendHello);
            }
            finally
            {
                channel.SetReadTimeout(0);
            }

            StartPump();
        }

        /// <summary>
        /// 交换身份与版本。**走到这里配对就完成了**（"是否信任"弹窗已删——它和"同意脱机"问的是同一件事，
        /// 全流程只保留后者那一道闸门）。
        /// </summary>
        private void Handshake(RemoteChannel channel, bool sendHello)
        {
            if (sendHello)
            {
                byte[] hello = Encoding.UTF8.GetBytes(Environment.MachineName);
                channel.SendSession(RemoteProtocol.MsgHello, hello, hello.Length);
                ushort type = ReadSessionFrame(channel, out ushort version, out byte[] payload);
                if (type != RemoteProtocol.MsgHelloAck)
                    throw new RemoteLinkException(Locale.T("engine.remote.expectHelloAck", type));
                CheckVersion(version);
                PeerName = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : Locale.T("engine.remote.peerName");
            }
            else
            {
                ushort type = ReadSessionFrame(channel, out ushort version, out byte[] payload);
                if (type != RemoteProtocol.MsgHello)
                    throw new RemoteLinkException(Locale.T("engine.remote.expectHello", type));
                CheckVersion(version);
                PeerName = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : Locale.T("engine.remote.peerName");

                byte[] mine = Encoding.UTF8.GetBytes(Environment.MachineName);
                channel.SendSession(RemoteProtocol.MsgHelloAck, mine, mine.Length);
            }
        }

        private static void CheckVersion(ushort version)
        {
            if (version != RemoteProtocol.Version)
                throw new RemoteLinkException(
                    Locale.T("engine.remote.versionMismatch", version, RemoteProtocol.Version));
        }

        /// <summary>
        /// 读一个会话帧，**途中跳过心跳帧并回一个 Ping**。
        /// 配对后对端就开始每 5 秒发心跳，而此刻本端还没有泵线程（它随配对完成才启动），
        /// 必须自己把心跳吃掉并回应，否则既会把 Ping 当成"未知帧"，也会被对端判超时。
        /// </summary>
        private static ushort ReadSessionFrame(RemoteChannel channel, out ushort version, out byte[] payload)
        {
            while (true)
            {
                uint magic = channel.ReadMagic();
                if (magic == RemoteProtocol.MagicPing)
                {
                    channel.SendPing();
                    continue;
                }
                if (magic != RemoteProtocol.MagicSession)
                    throw new RemoteLinkException(Locale.T("engine.remote.expectSessionFrame", magic));

                ushort type = channel.ReadUInt16();
                version = channel.ReadUInt16();
                int length = channel.ReadInt32();
                payload = ReadSessionPayload(channel, length);
                return type;
            }
        }

        private static byte[] ReadSessionPayload(RemoteChannel channel, int length)
        {
            if (length < 0 || length > RemoteProtocol.MaxPayloadBytes)
                throw new RemoteLinkException(Locale.T("engine.remote.invalidPayloadLength", length));
            byte[] payload = new byte[length];
            if (length > 0) channel.ReadExactly(payload, 0, length);
            return payload;
        }

        #endregion

        #region 对称能力：任一方向都能列盘 / 选盘 / 结束

        /// <summary>列出**对端**可提供的磁盘（纯枚举，不动对端任何系统状态）</summary>
        public IReadOnlyList<RemoteDiskInfo> ListDisks()
        {
            byte[] payload = SendSessionAndWait(RemoteProtocol.MsgListDisks, Array.Empty<byte>(), 0, expectDataLength: 0, cancellable: false);
            return ParseDiskList(payload);
        }

        /// <summary>
        /// 选定**对端**的一块盘并等对端用户点"同意脱机"。成功后本实例立刻变成可读写的块源。
        /// 这一步可能要等几十秒（对端的人在点确认框），调用方应在后台线程上发起。
        /// </summary>
        /// <exception cref="InvalidOperationException">对端拒绝（含原因）</exception>
        public BlockSourceInfo BeginService(string diskIdentity)
        {
            BlockSourceInfo info = RequestService(diskIdentity);
            _servingFrom = info;
            _usingIdentity = diskIdentity;
            return info;
        }

        /// <summary>协商结果先返回给调用方；重连必须验证通过后才发布新的块源描述</summary>
        private BlockSourceInfo RequestService(string diskIdentity)
        {
            _selectCancelled = false;
            byte[] name = Encoding.UTF8.GetBytes(diskIdentity);
            byte[] payload = new byte[4 + name.Length];
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), name.Length);
            name.CopyTo(payload, 4);

            byte[] reply = SendSessionAndWait(RemoteProtocol.MsgSelectDisk, payload, payload.Length, expectDataLength: 0, cancellable: true);

            int offset = 0;
            bool accepted = reply[offset++] != 0;
            if (!accepted)
            {
                string reason = ReadString(reply, ref offset);
                throw new InvalidOperationException(Locale.T("engine.remote.peerRejected", reason));
            }

            return new BlockSourceInfo
            {
                Model = ReadString(reply, ref offset),
                SerialNumber = ReadString(reply, ref offset),
                VendorId = ReadString(reply, ref offset),
                ProductId = ReadString(reply, ref offset),
                FirmwareRevision = ReadString(reply, ref offset),
                SizeBytes = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(offset)),
                BytesPerSector = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(offset + 8)),
                WasOnline = reply[offset + 12] != 0,
            };
        }

        /// <summary>
        /// 结束"本端使用对端盘"这件事：对端会关掉盘句柄（**保持脱机**），本端回到"已配对"。
        /// **连接与配对都保留**，可以立刻再选一块。
        /// </summary>
        public void EndService()
        {
            _usingIdentity = string.Empty;
            try { SendSessionNoWait(RemoteProtocol.MsgStopService); }
            catch (Exception ex) { LogService.DebugFile($"通知对端结束服务失败（连接可能已断）：{ex.Message}"); }
        }

        /// <summary>
        /// 取消"我发出的、正在等回复的那个请求"（典型场景：正卡在等对端点授权）。
        ///
        /// **只中断等待，不动连接**——配对与连接都保留，用户可以立刻重来。
        /// 同时**通知对端作废这次请求**：对端可能还开着授权框，不通知的话它点下"同意"就会把盘脱机出去，
        /// 而这边已经不要了，那块盘会白白消失一段时间。
        /// </summary>
        public void CancelPendingRequest()
        {
            _selectCancelled = true;
            try { SendSessionNoWait(RemoteProtocol.MsgCancelSelect); } catch { /* 连接已断，无所谓 */ }
            lock (_replyLock) { _replyEvent.Set(); }
        }

        /// <summary>
        /// 断开连接（配对一并作废；下次连上要重新配对）。
        /// **先告诉对端"我是主动走的"**——否则对端只能靠 TCP / 心跳感知，还会一律按"等重连"处理，
        /// 它那边就会停在"已配对·等待重连"很久（实际再也不会有人连回来）。
        /// </summary>
        public void Disconnect()
        {
            try { SendSessionNoWait(RemoteProtocol.MsgPeerLeaving); } catch { /* 已经断了 */ }
            _closing = true;
            _channel?.Close();
        }

        #endregion

        #region IBlockSource：读写（沉默等待 + 重连 + 重发）

        public int BytesPerSector => _servingFrom.BytesPerSector;
        public long SizeBytes => _servingFrom.SizeBytes;

        /// <summary>本端正在使用的对端盘的描述（缓存层要的那几项）</summary>
        public BlockSourceInfo UsingInfo => _servingFrom;

        public void Read(long byteOffset, byte[] buffer, int bufferOffset, int count)
            => TransferChunks(RemoteProtocol.CmdRead, byteOffset, buffer, bufferOffset, count);

        public void Write(long byteOffset, byte[] data, int dataOffset, int count)
            => TransferChunks(RemoteProtocol.CmdWrite, byteOffset, data, dataOffset, count);

        /// <summary>块源接受大请求，协议帧仍保持最多 1 MiB；每一段都等确认再发下一段</summary>
        private void TransferChunks(ushort cmd, long byteOffset, byte[] buffer, int bufferOffset, int count)
        {
            if (byteOffset < 0 || count < 0 || byteOffset > SizeBytes - count ||
                bufferOffset < 0 || bufferOffset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            int done = 0;
            while (done < count)
            {
                int chunk = Math.Min(count - done, RemoteProtocol.MaxPayloadBytes);
                if (cmd == RemoteProtocol.CmdWrite)
                    Transfer(cmd, byteOffset + done, buffer, bufferOffset + done, chunk, null, 0);
                else
                    Transfer(cmd, byteOffset + done, null, 0, chunk, buffer, bufferOffset + done);
                done += chunk;
            }
        }

        /// <summary>
        /// 传输的公共路径：发请求 → 等回复 → 链路失败就（重连后）整条重来。
        /// **这就是"沉默等待"的落点**：只要用户没点停止，这个循环就一直重试；
        /// 上层的 SCSI 命令会一直挂着，直到 initiator 自己的 60 秒 hold 窗口到期。
        /// </summary>
        private void Transfer(ushort cmd, long offset, byte[]? data, int dataOffset, int length,
                              byte[]? receive, int receiveOffset)
        {
            while (true)
            {
                if (_closing) throw new IOException(Locale.T("engine.remote.diskClosed"));

                try
                {
                    RemoteChannel channel = _channel ?? throw new RemoteLinkException(Locale.T("engine.remote.notConnected"));
                    if (Volatile.Read(ref _linkDown) != 0) throw new RemoteLinkException(Locale.T("engine.remote.linkDown"));

                    long cookie = Interlocked.Increment(ref _cookie);
                    lock (_replyLock)
                    {
                        _replyEvent.Reset();
                        _replyCookie = 0;
                        _replyError = RemoteProtocol.ErrOk;
                        _replyPayload = null;
                        _expectDataLength = receive != null ? length : 0;
                    }

                    channel.SendRequest(cmd, cookie, offset, length, data, dataOffset);
                    Volatile.Write(ref _lastSent, Environment.TickCount64);

                    WaitReply(cookie);

                    if (_replyError != RemoteProtocol.ErrOk) throw MapError(_replyError);
                    if (receive != null && _replyPayload != null)
                        Buffer.BlockCopy(_replyPayload, 0, receive, receiveOffset, length);
                    return;
                }
                catch (RemoteLinkException)
                {
                    if (_closing) throw new IOException(Locale.T("engine.remote.diskClosed"));

                    // **重连的主动权永远在拨号方**（它记着对方的地址）。监听方拨不回去，
                    // 只能等上层把对端重连上来的新连接挂到本实例上（见 AttachReconnected）。
                    if (_isListener) WaitLinkRestored();
                    else ReconnectWithBackoff();
                }
            }
        }

        private void WaitReply(long cookie)
        {
            WaitReplyEvent();

            if (_replyCookie != 0 && _replyCookie != cookie)
                throw new RemoteLinkException(Locale.T("engine.remote.replyMismatch"));
        }

        /// <summary>对端错误码 → 本地异常。复用 .NET 自己的类型，库里会翻成对应的 SCSI 错误码</summary>
        private static Exception MapError(int error) => error switch
        {
            RemoteProtocol.ErrIo => new IOException(Locale.T("engine.remote.ioMediaError")),
            RemoteProtocol.ErrRange => new ArgumentOutOfRangeException(nameof(error), Locale.T("engine.remote.ioRange")),
            RemoteProtocol.ErrUnsupported => new IOException(Locale.T("engine.remote.ioUnsupported")),
            _ => new IOException(Locale.T("engine.remote.ioErrorCode", error))
        };

        private void ReconnectWithBackoff()
        {
            // 去重：拿不到就直接返回（调用方那一侧会继续重试，不会漏）
            if (!Monitor.TryEnter(_reconnectLock)) return;
            try
            {
                int[] delays = { 1000, 2000, 4000, 8000, 30000 };
                int attempt = 0;

                while (!_closing)
                {
                    Thread.Sleep(delays[Math.Min(attempt, delays.Length - 1)]);
                    attempt++;
                    if (_closing) return;

                    try
                    {
                        Reconnect();
                        return;
                    }
                    catch (RemoteDeviceChangedException)
                    {
                        throw;
                    }
                    catch
                    {
                        // 连不上就继续退避（对端可能正在重启）——这正是"沉默等待"期望的行为
                    }
                }
            }
            finally
            {
                Monitor.Exit(_reconnectLock);
            }
        }

        /// <summary>
        /// 重连 = 重建 TCP + 重跑配对 +（如果本端正在用对方的盘）按同一块盘重新起服务。
        ///
        /// **两端方式不同**：拨号方重新拨过去；监听方只能"**继续 Accept，等对方连回来**"
        /// ——它是被动挨连的那一端，既不知道对方地址、也不该反过来拨。
        /// 因为是挂在**同一个实例**上，所以本端作为提供方持有的 `_serving` 不会因换连接而丢失。
        /// </summary>
        private void Reconnect()
        {
            try { _channel?.Dispose(); } catch { /* 已经断了 */ }
            _channel = null;

            // **只有拨号方走这里**：监听方永远不会主动"拨回去"（它在 Transfer 里改走 WaitLinkRestored）
            AttachChannel(new RemoteChannel(DialSocket()), sendHello: true);

            // 本端在用对方的盘：按同一块盘重新起服务。对端记得"我正在提供这块盘"，会直接放行、不弹第二次授权
            if (_usingIdentity.Length > 0)
            {
                BlockSourceInfo expected = _servingFrom;
                BlockSourceInfo restored = RequestService(_usingIdentity);
                if (!SameDevice(restored, expected))
                {
                    _closing = true;
                    OnServiceEnded?.Invoke(Locale.T("engine.remote.peerDiskChanged"));
                    throw new RemoteDeviceChangedException(
                        Locale.T("engine.remote.deviceChanged", expected.Model, restored.Model));
                }
                _servingFrom = restored;
            }

            Interlocked.Increment(ref _reconnects);
            OnStateChanged?.Invoke();
        }

        private static bool SameDevice(BlockSourceInfo a, BlockSourceInfo b)
            => a.Model == b.Model && a.SerialNumber == b.SerialNumber &&
               a.SizeBytes == b.SizeBytes && a.BytesPerSector == b.BytesPerSector;

        #endregion

        #region 泵线程：收帧 / 心跳 / 分发

        private void StartPump()
        {
            _pump = new Thread(PumpLoop) { IsBackground = true, Name = "RemotePeer.Pump" };
            _pump.Start();
        }

        private void PumpLoop()
        {
            RemoteChannel? channel = _channel;
            try
            {
                while (!_closing && channel != null)
                {
                    if (!channel.PollReadable(500))
                    {
                        long now = Environment.TickCount64;
                        if (now - Volatile.Read(ref _lastSent) > RemoteProtocol.PingIntervalMs)
                        {
                            channel.SendPing();
                            Volatile.Write(ref _lastSent, now);
                        }
                        if (now - Volatile.Read(ref _lastReceived) > RemoteProtocol.DeadThresholdMs)
                        {
                            MarkLinkDown();
                            return;
                        }
                        continue;
                    }

                    uint magic = channel.ReadMagic();
                    Volatile.Write(ref _lastReceived, Environment.TickCount64);

                    if (magic == RemoteProtocol.MagicReply) HandleReply(channel);
                    else if (magic == RemoteProtocol.MagicPing) { /* 已刷新 _lastReceived */ }
                    else if (magic == RemoteProtocol.MagicSession) HandleSession(channel);
                    else if (magic == RemoteProtocol.MagicRequest) HandleIncomingRequest(channel);
                    else throw new RemoteLinkException(Locale.T("engine.remote.unknownFrame", magic));
                }
            }
            catch (Exception ex)
            {
                if (!_closing) LogService.DebugFile($"远程连接结束：{ex.Message}");
                MarkLinkDown();
            }
        }

        private void HandleReply(RemoteChannel channel)
        {
            long cookie = channel.ReadInt64();
            int error = channel.ReadInt32();

            byte[]? data = null;
            if (error == RemoteProtocol.ErrOk)
            {
                int length = Volatile.Read(ref _expectDataLength);
                if (length > 0)
                {
                    data = new byte[length];
                    channel.ReadExactly(data, 0, length);
                }
            }

            lock (_replyLock)
            {
                _replyCookie = cookie;
                _replyError = error;
                _replyPayload = data;
                _replyEvent.Set();
            }
        }

        private void HandleSession(RemoteChannel channel)
        {
            ushort type = channel.ReadUInt16();
            channel.ReadUInt16();
            int length = channel.ReadInt32();
            byte[] payload = ReadSessionPayload(channel, length);

            switch (type)
            {
                // ===== 对端对我的请求 =====
                case RemoteProtocol.MsgListDisks:
                    SendDiskList(channel);
                    break;

                case RemoteProtocol.MsgSelectDisk:
                    // **授权必须放到后台线程**：用户可能盯着"是否信任并同意脱机"想十几秒甚至更久，
                    // 期间泵线程要继续跑心跳——否则对端 30 秒收不到我们的任何字节就会把连接判死
                    // （弹窗还挂着，链路已经没了）。
                    byte[] request = payload;
                    ThreadPool.QueueUserWorkItem(_ => RunAuthorizeAndReply(channel, request));
                    break;

                case RemoteProtocol.MsgStopService:
                    // 对端不再用本机的盘了：关掉盘句柄（**保持脱机**），连接与配对都保留。
                    // 注意这里**不碰** _peerCancelledSelect——"停止使用"与"取消请求"是两件事（见 MsgCancelSelect）。
                    CloseServing();
                    LogService.DebugFile("远程对端：对端已停止使用本机的盘（服务结束，连接与配对保留）");
                    OnStateChanged?.Invoke();
                    break;

                case RemoteProtocol.MsgCancelSelect:
                    // 对端不等它那个选盘请求了：让"可能还开着的授权框"作废。
                    // 此刻本端还没开始服务（_serving 为 null），所以没有句柄要关。
                    _peerCancelledSelect = true;
                    LogService.DebugFile("远程对端：对端已取消它的用盘请求");
                    break;

                case RemoteProtocol.MsgError:
                    DeliverSessionReply(0, RemoteProtocol.ErrIo, Encoding.UTF8.GetBytes(Locale.T("engine.remote.peerReturnedError")));
                    break;

                // ===== 我对对端的请求的回复 =====
                case RemoteProtocol.MsgDiskList:
                case RemoteProtocol.MsgSelectResult:
                    DeliverSessionReply(0, RemoteProtocol.ErrOk, payload);
                    break;

                case RemoteProtocol.MsgPeerLeaving:
                    LogService.DebugFile($"远程对端：对端（{PeerName}）已主动断开配对");
                    _usingIdentity = string.Empty;
                    OnPeerLeft?.Invoke();
                    MarkLinkDown(expectReconnect: false);
                    break;

                case RemoteProtocol.MsgServiceEnded:
                    {
                        string reason = payload.Length > 0 ? Encoding.UTF8.GetString(payload) : Locale.T("engine.remote.peerStoppedServing");
                        _usingIdentity = string.Empty;
                        MarkLinkDown();
                        OnServiceEnded?.Invoke(reason);
                        break;
                    }

                default:
                    LogService.DebugFile($"远程对端：收到未知的会话消息类型 {type}，忽略");
                    break;
            }
        }

        private void HandleIncomingRequest(RemoteChannel channel)
        {
            ushort cmd = channel.ReadUInt16();
            channel.ReadUInt16();
            long cookie = channel.ReadInt64();
            long offset = channel.ReadInt64();
            int length = channel.ReadInt32();

            byte[]? data = null;
            if (cmd == RemoteProtocol.CmdWrite)
            {
                if (length <= 0 || length > RemoteProtocol.MaxPayloadBytes)
                {
                    // 无法安全跳过一个超限写负载：继续解析会把剩余数据误认成下一帧。
                    throw new RemoteLinkException(Locale.T("engine.remote.invalidPayloadLength", length));
                }
                data = new byte[length];
                channel.ReadExactly(data, 0, length);
            }

            if (cmd == RemoteProtocol.CmdDisc) { MarkLinkDown(); return; }

            PhysicalDiskHandle? device = _serving;
            if (device == null)
            {
                channel.SendReply(cookie, RemoteProtocol.ErrIo, null, 0, 0);
                return;
            }

            try
            {
                if (cmd == RemoteProtocol.CmdRead)
                {
                    if (length <= 0 || length > RemoteProtocol.MaxPayloadBytes ||
                        offset < 0 || offset + length > device.SizeBytes)
                    {
                        channel.SendReply(cookie, RemoteProtocol.ErrRange, null, 0, 0);
                        return;
                    }

                    byte[] buffer = new byte[length];
                    device.Read(offset, buffer, 0, length);
                    channel.SendReply(cookie, RemoteProtocol.ErrOk, buffer, 0, length);
                    return;
                }

                if (cmd == RemoteProtocol.CmdWrite)
                {
                    if (offset < 0 || data == null || offset + data.Length > device.SizeBytes)
                    {
                        channel.SendReply(cookie, RemoteProtocol.ErrRange, null, 0, 0);
                        return;
                    }

                    device.Write(offset, data, 0, data.Length);
                    channel.SendReply(cookie, RemoteProtocol.ErrOk, null, 0, 0);
                    return;
                }

                channel.SendReply(cookie, RemoteProtocol.ErrUnsupported, null, 0, 0);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"远程对端（{PeerName}）：源盘 IO 失败：{ex.Message}");
                channel.SendReply(cookie, RemoteProtocol.ErrIo, null, 0, 0);
            }
        }

        private void MarkLinkDown(bool expectReconnect = true)
        {
            Volatile.Write(ref _linkDown, 1);
            _channel?.Close();
            lock (_replyLock) { _replyEvent.Set(); }

            // 本端正在提供盘：**保留服务状态 120 秒**等对方重连。
            // 立刻清掉的话，对方重连就得重新弹授权，"60 秒内无感恢复"就落空了。
            if (_serving != null && _serviceExpiryTicks == 0)
            {
                _serviceExpiryTicks = Environment.TickCount64 + ServiceHoldSeconds * 1000L;
                _holdTimer ??= new Timer(_ => CheckServiceHold(), null, 5000, 5000);
            }

            // 注意：**监听方不自己 Accept**。Accept 统一归上层的监听循环（Form1.AcceptLoop），
            // 否则两边会抢同一个 TcpListener，把连接接得七零八落。
            // 已知残留：监听方"正在用对方盘"时断线，重连后上层会换成新实例，
            // 而本地 target 还指着旧实例——这个场景当前需要用户手动重来一次（待修）。
            OnStateChanged?.Invoke();
        }

        private void CheckServiceHold()
        {
            if (_serving == null || !IsLinkDown) return;
            if (_serviceExpiryTicks == 0 || Environment.TickCount64 < _serviceExpiryTicks) return;

            LogService.DebugFile($"远程对端：等待重连超时（{ServiceHoldSeconds} 秒），关闭对「{_serving.Info.Model}」的提供（该盘保持脱机）");
            CloseServing();
            _serviceExpiryTicks = 0;
            OnStateChanged?.Invoke();
        }

        /// <summary>提供方等待对方重连的剩余秒数（0 = 没在等）</summary>
        public int ServiceHoldRemainSeconds
        {
            get
            {
                if (_serving == null || !IsLinkDown || _serviceExpiryTicks == 0) return 0;
                return (int)Math.Max(0, (_serviceExpiryTicks - Environment.TickCount64 + 999) / 1000);
            }
        }

        #endregion

        #region 作为提供方：列盘 / 授权 / 脱机

        private void SendDiskList(RemoteChannel channel)
        {
            List<PhysicalDiskInfo> disks;
            try
            {
                disks = PhysicalDiskHandle.Enumerate();
            }
            catch (Exception ex)
            {
                byte[] err = Encoding.UTF8.GetBytes($"枚举物理盘失败：{ex.Message}");
                channel.SendSession(RemoteProtocol.MsgError, err, err.Length);
                return;
            }

            using var ms = new MemoryStream();
            WriteInt32(ms, disks.Count);
            foreach (PhysicalDiskInfo info in disks)
            {
                WriteString(ms, PhysicalDiskHandle.DescribeIdentity(info));
                WriteString(ms, $"{info.Model}（{info.SizeText}，{(info.IsOnline ? "联机" : "脱机")}）");
                WriteInt64(ms, info.SizeBytes);
                WriteInt32(ms, info.BytesPerSector);
                ms.WriteByte((byte)(info.SeekPenaltyKnown && info.IncursSeekPenalty ? 1 : 0));
            }

            byte[] payload = ms.ToArray();
            channel.SendSession(RemoteProtocol.MsgDiskList, payload, payload.Length);
        }

        /// <summary>
        /// 在**后台线程**上走"人工闸门"，再把结果回给对端。
        /// 放到后台是必须的：弹窗可能被晾十几秒甚至更久，泵线程不能因此停摆（否则心跳断、对端判死）。
        /// 发帧本身是加锁的，所以可以安全地从这里发。
        /// </summary>
        private void RunAuthorizeAndReply(RemoteChannel channel, byte[] payload)
        {
            try
            {
                HandleSelectDisk(channel, payload);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"远程对端：处理对端的用盘请求时出错：{ex.Message}");
            }
        }

        /// <summary>对方向我要一块盘：走**全流程唯一的人工闸门**，同意后才脱机独占</summary>
        private void HandleSelectDisk(RemoteChannel channel, byte[] payload)
        {
            _peerCancelledSelect = false;     // 新请求进来：清掉上一次的取消标记

            int offset = 0;
            string identity = ReadString(payload, ref offset);

            // 对方重连：本端仍保留着"正在提供这块盘" ⇒ 直接放行，不再弹第二次授权
            if (_serving != null && _servingIdentity == identity)
            {
                SendSelectResult(channel, _serving, null);
                OnStateChanged?.Invoke();
                return;
            }

            PhysicalDiskInfo? target = null;
            try
            {
                foreach (PhysicalDiskInfo info in PhysicalDiskHandle.Enumerate())
                {
                    if (PhysicalDiskHandle.DescribeIdentity(info) == identity) { target = info; break; }
                }
            }
            catch (Exception ex)
            {
                SendSelectResult(channel, null, $"枚举物理盘失败：{ex.Message}");
                return;
            }

            if (target == null)
            {
                SendSelectResult(channel, null, "本机已经找不到那块磁盘（可能被拔出或更换）。");
                return;
            }

            bool allowed = OnAuthorize?.Invoke(target, $"{PeerName}（{_host}）") ?? false;

            // 用户在弹窗上停留期间对端可能已经断开：**这时绝不能把盘脱机出去**——
            // 没人用还占着它（还得等 120 秒才自动收回），本机那块盘白白消失一段时间。
            if (IsLinkDown)
            {
                LogService.DebugFile($"远程对端：已放弃把磁盘 {target.DiskNumber} 提供出去 — 对端连接在确认期间断开了");
                return;
            }

            if (_peerCancelledSelect)
            {
                LogService.DebugFile($"远程对端：已放弃把磁盘 {target.DiskNumber} 提供出去 — 对端取消了这次请求");
                return;
            }

            if (!allowed)
            {
                LogService.DebugFile($"远程对端：已拒绝对端使用磁盘 {target.DiskNumber}（{target.Model}）");
                SendSelectResult(channel, null, Locale.T("engine.remote.peerUserRefused"));
                return;
            }

            try
            {
                _serving = PhysicalDiskHandle.Open(target.DiskNumber);
                _servingIdentity = identity;
            }
            catch (Exception ex)
            {
                SendSelectResult(channel, null, $"无法使该磁盘脱机独占：{ex.Message}");
                return;
            }

            LogService.DebugFile(
                $"远程对端：已同意把磁盘 {target.DiskNumber}（{target.Model}）整盘脱机并独占");

            SendSelectResult(channel, _serving, null);
            OnStateChanged?.Invoke();
        }

        private static void SendSelectResult(RemoteChannel channel, PhysicalDiskHandle? device, string? reason)
        {
            using var ms = new MemoryStream();
            if (device == null)
            {
                ms.WriteByte(0);
                WriteString(ms, reason ?? Locale.T("engine.remote.unknownReason"));
            }
            else
            {
                ms.WriteByte(1);
                BlockSourceInfo info = device.ToBlockSourceInfo();
                WriteString(ms, info.Model);
                WriteString(ms, info.SerialNumber);
                WriteString(ms, info.VendorId);
                WriteString(ms, info.ProductId);
                WriteString(ms, info.FirmwareRevision);
                WriteInt64(ms, info.SizeBytes);
                WriteInt32(ms, info.BytesPerSector);
                ms.WriteByte((byte)(info.WasOnline ? 1 : 0));
            }

            byte[] payload = ms.ToArray();
            channel.SendSession(RemoteProtocol.MsgSelectResult, payload, payload.Length);
        }

        /// <summary>关闭正在提供的盘（**保持脱机**——盘归本程序管）</summary>
        private void CloseServing()
        {
            PhysicalDiskHandle? device = _serving;
            _serving = null;
            _servingIdentity = string.Empty;
            if (device == null) return;

            try { device.Dispose(); }
            catch (Exception ex) { LogService.DebugFile($"远程对端：关闭被提供磁盘的句柄时出错：{ex.Message}"); }
        }

        #endregion

        #region 主动发请求

        /// <param name="cancellable">
        /// 这一次等待是否响应「取消请求」。只有"等对端授权"那种可能等很久、界面上有取消按钮的请求才为真；
        /// 列盘之类的快请求若也响应它，上一次遗留的取消标记就会误伤到这一次上（已实际踩到）。
        /// </param>
        private byte[] SendSessionAndWait(ushort type, byte[] payload, int length, int expectDataLength, bool cancellable)
        {
            RemoteChannel channel = _channel ?? throw new RemoteLinkException(Locale.T("engine.remote.notConnected"));

            lock (_replyLock)
            {
                _replyEvent.Reset();
                _replyPayload = null;
                _replyError = RemoteProtocol.ErrOk;
            }
            _expectDataLength = expectDataLength;

            channel.SendSession(type, payload, length);
            Volatile.Write(ref _lastSent, Environment.TickCount64);

            WaitReplyEvent();

            if (cancellable && _selectCancelled)
            {
                _selectCancelled = false;
                throw new OperationCanceledException(Locale.T("engine.remote.requestCancelled"));
            }

            if (_replyError != RemoteProtocol.ErrOk) throw MapError(_replyError);
            return _replyPayload ?? Array.Empty<byte>();
        }

        /// <summary>
        /// 等一次回复。**注意 `_replyEvent` 也可能被 <see cref="MarkLinkDown"/> 唤醒**——
        /// 那种情况下并没有真的收到回复，必须按链路断开处理；否则会拿着一个空 payload 去解析，
        /// 表现成"索引超出数组范围"之类的诡异错误。
        /// </summary>
        private void WaitReplyEvent()
        {
            while (!_replyEvent.Wait(200))
            {
                if (Volatile.Read(ref _linkDown) != 0)
                    throw new RemoteLinkException(Locale.T("engine.remote.linkDownDetail"));
                if (_closing) throw new IOException(Locale.T("engine.remote.connectionClosed"));
            }

            // 事件已置位，但可能是被 MarkLinkDown 唤醒的：再确认一次
            if (Volatile.Read(ref _linkDown) != 0)
                throw new RemoteLinkException(Locale.T("engine.remote.linkDownDetail"));
        }

        private void SendSessionNoWait(ushort type)
            => _channel?.SendSession(type, Array.Empty<byte>(), 0);

        private void DeliverSessionReply(long cookie, int error, byte[] payload)
        {
            lock (_replyLock)
            {
                _replyCookie = cookie;
                _replyError = error;
                _replyPayload = payload;
                _replyEvent.Set();
            }
        }

        #endregion

        #region 解析与编码（与对端一一对应）

        private static List<RemoteDiskInfo> ParseDiskList(byte[] payload)
        {
            var list = new List<RemoteDiskInfo>();
            if (payload.Length < 4) return list;

            int offset = 0;
            int count = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset));
            offset += 4;

            for (int i = 0; i < count && offset < payload.Length; i++)
            {
                string identity = ReadString(payload, ref offset);
                string display = ReadString(payload, ref offset);
                long size = BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(offset));
                offset += 8;
                int sector = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset));
                offset += 4;
                bool rotational = payload[offset++] != 0;

                list.Add(new RemoteDiskInfo
                {
                    Identity = identity,
                    DisplayName = display,
                    SizeBytes = size,
                    BytesPerSector = sector,
                    Rotational = rotational
                });
            }
            return list;
        }

        private static void WriteInt32(Stream s, int value)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(b, value);
            s.Write(b);
        }

        private static void WriteInt64(Stream s, long value)
        {
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(b, value);
            s.Write(b);
        }

        private static void WriteString(Stream s, string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            WriteInt32(s, bytes.Length);
            s.Write(bytes, 0, bytes.Length);
        }

        private static string ReadString(byte[] payload, ref int offset)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset));
            offset += 4;
            string value = Encoding.UTF8.GetString(payload, offset, length);
            offset += length;
            return value;
        }

        #endregion

        public void Dispose()
        {
            // 也在这一步告诉对端"我是主动走的"（正常关程序同样应当让它立刻回到"等待配对"）
            try { SendSessionNoWait(RemoteProtocol.MsgPeerLeaving); } catch { /* 已经断了 */ }
            _closing = true;
            _holdTimer?.Dispose();
            _holdTimer = null;
            _channel?.Close();
            _pump?.Join(1000);
            _channel?.Dispose();
            CloseServing();
            _replyEvent.Dispose();
            _linkRestored.Dispose();
        }
    }
}
