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
using System.IO;
using System.Net.Sockets;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 远程协议的常量与帧格式。**完整设计与逐条理由见 后续待办.md 第一节的「协议设计」。**
    ///
    /// 一条 TCP 连接从**配对成功**活到**用户停止**，中间只有两个阶段：配对 → 服务。
    /// 所有帧的**第一个字段都是 4 字节 magic**（大端），接收方据此分辨帧类型——
    /// 于是心跳与带外通知可以插在请求/回复之间而不破坏配对关系。
    ///
    /// 为什么自研而不直接用 NBD：官方实现是纯 Linux、`nbd-client` 的传输阶段在内核里、
    /// `nbd-server` 没有后端抽象（见第一节「为什么不用 NBD」）。但握手分两阶段、列清单、
    /// 能力位/版本号、cookie、软断开这几个结构都借自它。
    /// </summary>
    internal static class RemoteProtocol
    {
        /// <summary>协议版本：两端必须一致（不等直接拒绝，而不是解析出一堆垃圾）</summary>
        public const ushort Version = 1;

        // ===== 帧 magic（大端）=====
        public const uint MagicSession = 0x52434448;   // "RCDH" 会话消息
        public const uint MagicRequest = 0x52434451;   // "RCDQ" 传输请求
        public const uint MagicReply = 0x52434453;   // "RCDS" 传输回复
        public const uint MagicPing = 0x52434450;   // "RCDP" 心跳（无负载，双向）

        // ===== 会话消息类型（帧 magic = MagicSession）=====
        public const ushort MsgHello = 1;
        public const ushort MsgHelloAck = 2;
        public const ushort MsgListDisks = 3;
        public const ushort MsgDiskList = 4;
        public const ushort MsgSelectDisk = 5;
        public const ushort MsgSelectResult = 6;
        public const ushort MsgStopService = 7;
        public const ushort MsgError = 8;
        public const ushort MsgServiceEnded = 9;

        /// <summary>
        /// 我要断开配对了（**主动退出，不是掉线**）。收到方应回到"等待配对"，而不是停在"等待重连"。
        /// 没有它时，主动断开和网络抖动在对端看来一模一样。
        /// </summary>
        public const ushort MsgPeerLeaving = 10;

        /// <summary>
        /// 我要取消**刚发出的那个选盘请求**（典型场合：正卡在对端的授权框上不等了）。
        /// **不能复用 <see cref="MsgStopService"/>**：那条消息的含义是"关掉正在给我用的盘"，
        /// 与"作废一个还没开始服务的请求"是两件事。曾经共用一条，结果 A 一停止使用，
        /// B 收到后把"对端取消"记到了自己的取消标记上，B 下次列盘就被误判成"已取消这次请求"。
        /// </summary>
        public const ushort MsgCancelSelect = 11;

        // ===== 传输命令 =====
        public const ushort CmdRead = 0;
        public const ushort CmdWrite = 1;
        public const ushort CmdDisc = 2;
        public const ushort CmdFlush = 3;   // 预留：写路径是 WRITE_THROUGH，暂不实现

        // ===== 心跳 =====
        /// <summary>空闲多久发一个 Ping</summary>
        public const int PingIntervalMs = 5000;

        /// <summary>
        /// 多久没收到任何字节就判定链路断开。取 30 秒的理由：initiator 等 60 秒，
        /// 留一半窗口给重连；而机械盘唤醒最多几秒，差一个数量级不会误判。
        /// 注意它**只判定链路整体的死活**，绝不用于判定"某一条请求的成败"——单条请求没有超时。
        /// </summary>
        public const int DeadThresholdMs = 30000;

        /// <summary>单次传输的最大负载（与 CachedPhysicalDisk 的合并回源上限同口径）</summary>
        public const int MaxPayloadBytes = 1024 * 1024;

        /// <summary>
        /// **握手（Hello / HelloAck）的等待上限，只用于建链这一步。**
        ///
        /// 传输阶段刻意没有任何超时（沉默等待，见 <see cref="DeadThresholdMs"/>），但握手必须有一个：
        /// 一条"连上却不发 Hello"的连接（端口扫描器、健康检查探针、被中间设备截断的半开连接）
        /// 若不设上限，会把监听线程**永久钉死**在这一次读上——监听 socket 还开着，
        /// 内核照旧为后续连接完成三次握手，于是对端"连得上却永远等不到 HelloAck"，
        /// 表现为配对长时间不响应（已实际踩到）。10 秒相对一次纯本地的字节往返是 3 个数量级的余量。
        /// </summary>
        public const int HandshakeTimeoutMs = 10000;

        // ===== Reply.error 的取值 =====
        public const int ErrOk = 0;

        /// <summary>源设备读写失败。客户端收到后抛 IOException，库正好翻成 MEDIUM ERROR——
        /// 于是"远程形态下源盘读错"与"本地形态下源盘读错"在上层看来一模一样</summary>
        public const int ErrIo = 1;

        /// <summary>偏移/长度越界</summary>
        public const int ErrRange = 2;

        public const int ErrUnsupported = 3;
    }

    /// <summary>
    /// 一条连接上的帧收发原语。
    ///
    /// **发送加锁**（心跳与数据请求会并发写同一个 socket）；**接收不加锁**——
    /// 约定由**同一个线程独占读取**（客户端是泵线程，服务端是连接处理线程），
    /// 这样 `ReadExactly` 的阻塞语义才是确定的。
    /// </summary>
    internal sealed class RemoteChannel : IDisposable
    {
        private readonly Socket _socket;
        private readonly NetworkStream _stream;
        private readonly object _sendLock = new();
        private readonly byte[] _head = new byte[16];

        public RemoteChannel(Socket socket)
        {
            _socket = socket;
            // 协议明确 SHOULD 设 TCP_NODELAY：否则小包要等 ACK 才发出去（最多 200ms 的额外延迟）
            try { _socket.NoDelay = true; } catch { /* 某些栈不支持，忽略 */ }
            _stream = new NetworkStream(socket, ownsSocket: false);
        }

        public Socket Socket => _socket;

        /// <summary>在给定时间内是否可读。用于"顺带检查心跳/判活"的轮询</summary>
        public bool PollReadable(int milliseconds)
        {
            try
            {
                return _socket.Poll(milliseconds * 1000, SelectMode.SelectRead);
            }
            catch
            {
                return true;   // 出错就当作可读，让紧随其后的读把异常抛出来（统一在读取路径上处理）
            }
        }

        /// <summary>
        /// 置"读不到数据时最多等多久"（0 = 无限）。**只用于握手期间**——握手一过必须复位成 0：
        /// 传输阶段刻意不设任何请求超时（沉默等待）。超时表现为读抛 <see cref="IOException"/>
        /// （底层是 `SocketError.TimedOut`），与"对端关闭连接"走的是同一个出口。
        /// </summary>
        public void SetReadTimeout(int milliseconds)
        {
            try { _socket.ReceiveTimeout = milliseconds; } catch { /* 某些栈不支持，忽略 */ }
        }

        public void Close()
        {
            try { _socket.Shutdown(SocketShutdown.Both); } catch { /* 已经断了 */ }
            try { _socket.Close(); } catch { /* 同上 */ }
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { /* 关流失败没有补救手段 */ }
            Close();
        }

        #region 接收

        /// <summary>读一帧的第一字段（magic）。连接关闭时抛 <see cref="EndOfStreamException"/></summary>
        public uint ReadMagic()
        {
            _stream.ReadExactly(_head, 0, 4);
            return BinaryPrimitives.ReadUInt32BigEndian(_head);
        }

        public ushort ReadUInt16()
        {
            _stream.ReadExactly(_head, 0, 2);
            return BinaryPrimitives.ReadUInt16BigEndian(_head);
        }

        public int ReadInt32()
        {
            _stream.ReadExactly(_head, 0, 4);
            return BinaryPrimitives.ReadInt32BigEndian(_head);
        }

        public long ReadInt64()
        {
            _stream.ReadExactly(_head, 0, 8);
            return BinaryPrimitives.ReadInt64BigEndian(_head);
        }

        public void ReadExactly(byte[] buffer, int offset, int count) => _stream.ReadExactly(buffer, offset, count);

        #endregion

        #region 发送

        /// <summary>会话消息：`magic(4) type(2) version(2) length(4) payload`</summary>
        public void SendSession(ushort type, byte[] payload, int payloadLength)
        {
            lock (_sendLock)
            {
                Span<byte> head = stackalloc byte[12];
                BinaryPrimitives.WriteUInt32BigEndian(head[..4], RemoteProtocol.MagicSession);
                BinaryPrimitives.WriteUInt16BigEndian(head[4..6], type);
                BinaryPrimitives.WriteUInt16BigEndian(head[6..8], RemoteProtocol.Version);
                BinaryPrimitives.WriteInt32BigEndian(head[8..12], payloadLength);
                _stream.Write(head);
                if (payloadLength > 0) _stream.Write(payload, 0, payloadLength);
                _stream.Flush();
            }
        }

        /// <summary>心跳：只有一个 magic，无负载（双向）</summary>
        public void SendPing()
        {
            lock (_sendLock)
            {
                Span<byte> head = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(head, RemoteProtocol.MagicPing);
                _stream.Write(head);
                _stream.Flush();
            }
        }

        /// <summary>传输请求：`magic(4) cmd(2) flags(2) cookie(8) offset(8) length(4) [data]`，共 28 字节头</summary>
        public void SendRequest(ushort cmd, long cookie, long offset, int length, byte[]? data, int dataOffset)
        {
            lock (_sendLock)
            {
                Span<byte> head = stackalloc byte[28];
                BinaryPrimitives.WriteUInt32BigEndian(head[..4], RemoteProtocol.MagicRequest);
                BinaryPrimitives.WriteUInt16BigEndian(head[4..6], cmd);
                BinaryPrimitives.WriteUInt16BigEndian(head[6..8], 0);              // flags 预留（FUA 等）
                BinaryPrimitives.WriteInt64BigEndian(head[8..16], cookie);
                BinaryPrimitives.WriteInt64BigEndian(head[16..24], offset);
                BinaryPrimitives.WriteInt32BigEndian(head[24..28], length);
                _stream.Write(head);
                if (data != null && length > 0) _stream.Write(data, dataOffset, length);
                _stream.Flush();
            }
        }

        /// <summary>传输回复：`magic(4) cookie(8) error(4) [data]`，共 16 字节头</summary>
        public void SendReply(long cookie, int error, byte[]? data, int dataOffset, int count)
        {
            lock (_sendLock)
            {
                Span<byte> head = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(head[..4], RemoteProtocol.MagicReply);
                BinaryPrimitives.WriteInt64BigEndian(head[4..12], cookie);
                BinaryPrimitives.WriteInt32BigEndian(head[12..16], error);
                _stream.Write(head);
                if (data != null && error == RemoteProtocol.ErrOk && count > 0)
                    _stream.Write(data, dataOffset, count);
                _stream.Flush();
            }
        }

        #endregion
    }
}
