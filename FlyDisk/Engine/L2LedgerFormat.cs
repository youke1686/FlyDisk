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
using System.IO;
using System.Text;

namespace FlyDisk.Engine
{
    /// <summary>账本状态：干净 / 脏（停机没等到静止）/ 已失效（本次不用 L2 时主动标记）。</summary>
    public enum L2LedgerState
    {
        Clean = 0,
        Dirty = 1,
        Stale = 2
    }

    /// <summary>账本整体状态（启动前预检用）。</summary>
    public enum L2LedgerStatus
    {
        /// <summary>容器与索引都不存在 —— 首次使用</summary>
        NoLedger,

        /// <summary>格式正确、状态 Clean、戳一致</summary>
        Clean,

        /// <summary>上次没正常关服</summary>
        Dirty,

        /// <summary>本次不用 L2 时被主动标记失效</summary>
        Stale,

        /// <summary>文件缺一半 / 头部损坏 / 不属于这块盘</summary>
        Corrupt,

        /// <summary>旧版本的格式（不迁移，启动时清空 / 退出）</summary>
        Incompatible
    }

    /// <summary>源盘写入计数的附加标志</summary>
    [Flags]
    public enum L2SourceFlags
    {
        None = 0,
        /// <summary>写账本时计数确实读到了</summary>
        Valid = 1 << 0,
        /// <summary>已经证明过计数通道是活的（观察到它动过）</summary>
        Live = 1 << 1,
        /// <summary>这份记录是一份基线（可以拿来比较）</summary>
        HasBaseline = 1 << 2
    }

    /// <summary>
    /// 源盘写入计数基线（干净停止时写入账本）。
    /// 比较口径：<see cref="LbaWritten"/> 相等 ⇒ 上次停止后没人写过源盘。
    /// </summary>
    public struct L2SourceState
    {
        public long LbaWritten;
        public long LbaRead;
        public int PowerCycles;
        public L2SourceFlags Flags;
        public long StopUtcTicks;

        public readonly bool Valid => (Flags & L2SourceFlags.Valid) != 0;
        public readonly bool Live => (Flags & L2SourceFlags.Live) != 0;
        public readonly bool HasBaseline => (Flags & L2SourceFlags.HasBaseline) != 0;

        public static L2SourceState Unknown => default;
    }

    /// <summary>index.bin 的 v4 头部（128 字节）。</summary>
    public sealed class L2IndexHeader
    {
        public L2LedgerState State;
        public int BlockSize;
        public int SlotCount;
        public int MCount;
        public int GhostCount;
        public long Generation;
        public long ContainerId;
        public long DeviceIdentity;
        public L2SourceState Source;

        public byte[] ToBytes()
        {
            byte[] header = new byte[L2LedgerFormat.IndexHeaderSize];
            using (var ms = new MemoryStream(header))
            using (var w = new BinaryWriter(ms))
            {
                w.Write(L2LedgerFormat.IndexMagic);
                w.Write(L2LedgerFormat.IndexVersion);
                w.Write((int)State);
                w.Write(BlockSize);
                w.Write(SlotCount);
                w.Write(MCount);
                w.Write(GhostCount);
                w.Write(0);                       // 保留
                w.Write(Generation);
                w.Write(ContainerId);
                w.Write(DeviceIdentity);
                w.Write(Source.LbaWritten);
                w.Write(Source.LbaRead);
                w.Write(Source.PowerCycles);
                w.Write((int)Source.Flags);
                w.Write(Source.StopUtcTicks);
                w.Write(new byte[L2LedgerFormat.IndexHeaderHashOffset - 88]);
                w.Write(L2LedgerFormat.Hash(header, 0, L2LedgerFormat.IndexHeaderHashOffset));
            }
            return header;
        }

        public static bool TryRead(byte[] header, out L2IndexHeader result)
        {
            result = new L2IndexHeader();
            if (header.Length < L2LedgerFormat.IndexHeaderSize) return false;

            if (BitConverter.ToUInt32(header, 0) != L2LedgerFormat.IndexMagic) return false;
            if (BitConverter.ToInt32(header, 4) != L2LedgerFormat.IndexVersion) return false;
            if (L2LedgerFormat.Hash(header, 0, L2LedgerFormat.IndexHeaderHashOffset) !=
                BitConverter.ToUInt64(header, L2LedgerFormat.IndexHeaderHashOffset)) return false;

            using var ms = new MemoryStream(header);
            using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);
            _ = r.ReadUInt32();                    // magic
            _ = r.ReadInt32();                     // version
            int state = r.ReadInt32();
            if (state < (int)L2LedgerState.Clean || state > (int)L2LedgerState.Stale) return false;

            result.State = (L2LedgerState)state;
            result.BlockSize = r.ReadInt32();
            result.SlotCount = r.ReadInt32();
            result.MCount = r.ReadInt32();
            result.GhostCount = r.ReadInt32();
            _ = r.ReadInt32();                     // 保留
            result.Generation = r.ReadInt64();
            result.ContainerId = r.ReadInt64();
            result.DeviceIdentity = r.ReadInt64();
            result.Source.LbaWritten = r.ReadInt64();
            result.Source.LbaRead = r.ReadInt64();
            result.Source.PowerCycles = r.ReadInt32();
            result.Source.Flags = (L2SourceFlags)r.ReadInt32();
            result.Source.StopUtcTicks = r.ReadInt64();
            return true;
        }
    }

    /// <summary>cache.dat 的 v3 头部（写在第一个块的头部，其余补零）。</summary>
    public sealed class L2ContainerHeader
    {
        public long ContainerId;
        public int SlotCount;
        public int BlockSize;
        public long DeviceIdentity;

        public void Write(byte[] block)
        {
            Array.Clear(block, 0, block.Length);
            using var ms = new MemoryStream(block);
            using var w = new BinaryWriter(ms);
            w.Write(L2LedgerFormat.ContainerMagic);
            w.Write(L2LedgerFormat.ContainerVersion);
            w.Write(ContainerId);
            w.Write(SlotCount);
            w.Write(BlockSize);
            w.Write(DeviceIdentity);
            w.Write(L2LedgerFormat.Hash(block, 0, L2LedgerFormat.ContainerHashLen));
        }

        public static bool TryRead(byte[] buffer, out L2ContainerHeader result)
        {
            result = new L2ContainerHeader();
            if (buffer.Length < L2LedgerFormat.ContainerHashLen + 8) return false;

            if (BitConverter.ToUInt32(buffer, 0) != L2LedgerFormat.ContainerMagic) return false;
            if (BitConverter.ToInt32(buffer, 4) != L2LedgerFormat.ContainerVersion) return false;
            if (L2LedgerFormat.Hash(buffer, 0, L2LedgerFormat.ContainerHashLen) !=
                BitConverter.ToUInt64(buffer, L2LedgerFormat.ContainerHashLen)) return false;

            result.ContainerId = BitConverter.ToInt64(buffer, 8);
            result.SlotCount = BitConverter.ToInt32(buffer, 16);
            result.BlockSize = BitConverter.ToInt32(buffer, 20);
            result.DeviceIdentity = BitConverter.ToInt64(buffer, 24);
            return true;
        }
    }

    /// <summary>账本格式常量与只读探测结果。</summary>
    public static class L2LedgerFormat
    {
        public const uint ContainerMagic = 0x44434453;   // "SDCD"
        public const int ContainerVersion = 3;
        public const int ContainerHashLen = 32;

        public const uint IndexMagic = 0x31434453;       // "SDC1"
        public const int IndexVersion = 4;
        public const int IndexHeaderSize = 128;
        public const int IndexHeaderHashOffset = 120;

        public const int ContainerHeaderPeekBytes = 4096;

        /// <summary>FNV-1a 64：够用的完整性校验（不引入新依赖）</summary>
        public static ulong Hash(byte[] data, int offset, int count)
        {
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < count; i++)
            {
                h ^= data[offset + i];
                h *= 1099511628211UL;
            }
            return h;
        }
    }

    /// <summary>启动前 / 管理界面的只读探测结果。</summary>
    public sealed class L2LedgerProbe
    {
        public L2LedgerStatus Status { get; init; }
        public int ContainerVersion { get; init; }
        public int IndexVersion { get; init; }
        public int StoredSlotCount { get; init; }
        public int StoredBlockBytes { get; init; }
        public long OwnerIdentity { get; init; }
        public long ContainerId { get; init; }
        public L2SourceState Source { get; init; }
    }
}
