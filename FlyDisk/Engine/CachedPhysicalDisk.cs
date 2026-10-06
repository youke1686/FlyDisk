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
using System.Threading;
using DiskAccessLibrary;
using FlyDisk.Localization;
using FlyDisk.Models;

namespace FlyDisk.Engine
{
    /// <summary>
    /// LUN 后端：把"两级缓存 + 写透传"插在 SCSI 命令与物理盘原始扇区之间。
    /// 只需实现大库 <see cref="Disk"/> 抽象类的 4 个成员（<see cref="ReadSectors"/> / <see cref="WriteSectors"/> /
    /// <see cref="BytesPerSector"/> / <see cref="Size"/>）——PDU 编解码、登录协商、SCSI 命令分发全部由 ISCSI 包负责。
    ///
    /// **读路径**：按 4KB 块逐块查缓存（L1 → L2）；未命中才回源，回源读到整块就回填。
    /// 请求可能与块边界不对齐（MBR 那 512 字节就是一个典型），此时缓存照样可用（只拷需要的那一段），
    /// 未命中时读整块、只把请求覆盖的部分拷给上层。
    ///
    /// **写路径**：直透物理盘原始扇区，然后逐块处置缓存——**整块被写请求完整覆盖**的块用写数据就地刷新
    /// （写命中刷新，W1），残缺的首尾块才作废（见 后续待办.md 的 W1 定稿）。
    /// 不做 WriteBack（延迟写回）：MVP 保持"写已落盘"，断电安全天然满足。
    /// 与阶段一"写一字节 ⇒ 整份文件缓存失效"相比，这是块设备形态带来的架构红利。
    ///
    /// 线程前提：本类的调用者是库的 target 工作线程（每个 target 一个后台线程串行执行命令），
    /// 因此 <see cref="_scratch"/> 可以复用、<see cref="PhysicalDiskHandle"/> 可以共用文件指针。
    /// </summary>
    public sealed class CachedPhysicalDisk : Disk
    {
        private const int BlockSize = ServiceConstants.BlockSize;

        /// <summary>
        /// 单条 SCSI READ/WRITE 的字节上限。协商的 MaxTransferLength 只有 32768 扇区（16MB），
        /// 这里给一个很宽的硬上限，只为挡住"异常长度把 int 搞溢出"——超了按越界拒绝，而不是崩线程。
        /// </summary>
        private const int MaxSingleRequestBytes = 256 * 1024 * 1024;

        /// <summary>
        /// 单次**合并回源**的字节上限。
        ///
        /// 逐块回源时，上层一个 64 KB 的读会变成 16 次源读——机械盘上是 16 次寻道，
        /// 远端形态下更是 16 次网络往返（见 后续待办.md 第一节的"回源粒度"）。
        /// 所以把**连续未命中的块合并成一次读**；这个上限同时约束中转缓冲的大小与单次读的延迟。
        /// </summary>
        private const int MaxMergedReadBytes = 1024 * 1024;
        private const int MaxMergedBlocks = MaxMergedReadBytes / BlockSize;

        private readonly IBlockSource _raw;
        private readonly CacheService _cache;
        private readonly byte[] _scratch = new byte[BlockSize];               // 单工作线程串行 ⇒ 可复用
        private readonly byte[] _readBuffer = new byte[MaxMergedReadBytes];   // 合并回源的中转缓冲（同上，可复用）

        private long _readCommands;
        private long _readSectors;
        private long _readFullHitCommands;
        private long _writeCommands;
        private long _writeSectors;
        private long _inFlight;
        private volatile bool _closed;
        private readonly object _lifecycleLock = new();
        private readonly ManualResetEventSlim _idle = new(true);

        public CachedPhysicalDisk(IBlockSource raw, CacheService cache)
        {
            _raw = raw;
            _cache = cache;
        }

        public override int BytesPerSector => _raw.BytesPerSector;

        public override long Size => _raw.SizeBytes;

        #region 统计

        public long ReadCommandsTotal => Interlocked.Read(ref _readCommands);
        public long ReadSectorsTotal => Interlocked.Read(ref _readSectors);

        /// <summary>整条 READ 命令全部命中缓存（一个扇区都没回源）的次数</summary>
        public long ReadFullHitCommandsTotal => Interlocked.Read(ref _readFullHitCommands);

        public long WriteCommandsTotal => Interlocked.Read(ref _writeCommands);
        public long WriteSectorsTotal => Interlocked.Read(ref _writeSectors);

        /// <summary>正在执行中的 SCSI 命令数（0 或 1：命令是串行执行的）</summary>
        public bool IsIdle => Interlocked.Read(ref _inFlight) == 0;

        /// <summary>
        /// 停服序列：之后的命令一律以 <see cref="IOException"/> 拒绝。
        /// **必须是库认识的异常类型**——<c>SCSITarget</c> 的工作线程没有兜底 catch，
        /// 而它只把 <see cref="IOException"/> 与 <see cref="ArgumentOutOfRangeException"/> 翻成 SCSI 错误码；
        /// 别的异常（例如盘句柄已释放时的 ObjectDisposedException）会直接顺着那条线程把进程带走。
        /// 这一步防的是"target 停了但命令还在队列里"的尾巴：那些命令会在盘句柄关闭之后才被执行。
        /// </summary>
        public void Close()
        {
            lock (_lifecycleLock) _closed = true;
        }

        private void EnterCommand()
        {
            lock (_lifecycleLock)
            {
                if (_closed) throw new IOException(Locale.T("engine.disk.stopped"));
                _idle.Reset();
                Interlocked.Increment(ref _inFlight);
            }
        }

        private void ExitCommand()
        {
            lock (_lifecycleLock)
            {
                if (Interlocked.Decrement(ref _inFlight) == 0) _idle.Set();
            }
        }

        /// <summary>
        /// 等所有在途命令跑完（停服序列用）。停 target 之后要靠它确认"没有命令正在跑"，
        /// 才敢把 L2 账本写出去——账本必须在缓存表不再变化的那一刻序列化。
        /// </summary>
        public bool WaitForIdle(int timeoutMs)
        {
            return _idle.Wait(timeoutMs);
        }

        #endregion

        #region 读

        public override byte[] ReadSectors(long lba, int sectorCount)
        {
            EnterCommand();
            try
            {
                int bytesPerSector = BytesPerSector;
                long totalSectors = Size / bytesPerSector;
                if (lba < 0 || sectorCount <= 0 || lba > totalSectors - sectorCount)
                    throw new ArgumentOutOfRangeException(nameof(lba), $"读越界：LBA {lba} + {sectorCount} 扇区，盘共 {totalSectors} 扇区");

                long requestStart = lba * bytesPerSector;
                // 先在 long 上算清楚再判上限，最后才转 int：绝不能让它溢出（OverflowException 不在
                // 库的 catch 名单里，会顺着 target 工作线程抛出去把进程带走）
                long totalBytesLong = sectorCount * (long)bytesPerSector;
                if (totalBytesLong > MaxSingleRequestBytes)
                    throw new ArgumentOutOfRangeException(nameof(sectorCount), $"单条读请求 {totalBytesLong} 字节超出上限");
                int totalBytes = (int)totalBytesLong;
                long requestEnd = requestStart + totalBytes;

                var result = new byte[totalBytes];
                long firstBlock = requestStart / BlockSize;
                long lastBlock = (requestEnd - 1) / BlockSize;
                bool fullHit = true;

                // 连续未命中的块**攒成一段、一次源读**（见 MaxMergedReadBytes 的说明）。
                long runStart = -1;
                int runBlocks = 0;

                for (long blockIndex = firstBlock; blockIndex <= lastBlock; blockIndex++)
                {
                    // 命中：只把请求真正覆盖的那一段拷回去（边界块也一样）
                    if (_cache.TryGetBlock(blockIndex, _scratch, 0))
                    {
                        if (runBlocks > 0)
                        {
                            ReadMissedRun(runStart, runBlocks, result, requestStart, requestEnd);
                            runBlocks = 0;
                        }
                        CopyBlockToResult(_scratch, 0, blockIndex, result, requestStart, requestEnd);
                        continue;
                    }

                    fullHit = false;
                    if (runBlocks == 0) runStart = blockIndex;
                    runBlocks++;

                    // 攒不下就先把这一段发出去（中转缓冲有上限）
                    if (runBlocks >= MaxMergedBlocks)
                    {
                        ReadMissedRun(runStart, runBlocks, result, requestStart, requestEnd);
                        runBlocks = 0;
                    }
                }

                if (runBlocks > 0) ReadMissedRun(runStart, runBlocks, result, requestStart, requestEnd);

                Interlocked.Increment(ref _readCommands);
                Interlocked.Add(ref _readSectors, sectorCount);
                if (fullHit) Interlocked.Increment(ref _readFullHitCommands);
                return result;
            }
            finally
            {
                ExitCommand();
            }
        }

        /// <summary>
        /// 把 <paramref name="runBlocks"/> 个**连续未命中**的块一次性从源读进来：
        /// 读到真值的块写进缓存、并把请求覆盖的那一段拷进结果；源上不存在的盘尾残块补零、**不回填**。
        ///
        /// 合并的前提是"这整段在源上都是完整块"——盘容量不是 4 KB 整数倍时最后一块只有一部分在源上，
        /// 所以先按 <c>Size / BlockSize</c> 裁掉尾部，剩下的（最多一块）退回单块处理。
        /// </summary>
        private void ReadMissedRun(long runStart, int runBlocks, byte[] result, long requestStart, long requestEnd)
        {
            long wholeBlocks = Size / BlockSize;
            int readable = (int)Math.Min(runBlocks, Math.Max(0, wholeBlocks - runStart));

            if (readable > 0)
            {
                // 一次源读：本地形态 = 一次 ReadFile，远端形态 = 一次协议往返
                _raw.Read(runStart * BlockSize, _readBuffer, 0, readable * BlockSize);

                for (int i = 0; i < readable; i++)
                {
                    long blockIndex = runStart + i;
                    _cache.SetBlock(blockIndex, _readBuffer, i * BlockSize);
                    CopyBlockToResult(_readBuffer, i * BlockSize, blockIndex, result, requestStart, requestEnd);
                }
            }

            // 尾部（只可能是盘尾那一块）：补零、不回填
            for (long blockIndex = runStart + readable; blockIndex < runStart + runBlocks; blockIndex++)
            {
                ReadWholeBlockFromDisk(blockIndex, _scratch);
                CopyBlockToResult(_scratch, 0, blockIndex, result, requestStart, requestEnd);
            }
        }

        /// <summary>把一块里"请求真正覆盖的那一段"拷进结果缓冲（块与请求都可能只是部分重叠）</summary>
        private static void CopyBlockToResult(byte[] buffer, int bufferOffset, long blockIndex,
                                              byte[] result, long requestStart, long requestEnd)
        {
            long blockStart = blockIndex * BlockSize;
            long copyStart = Math.Max(blockStart, requestStart);
            long copyEnd = Math.Min(blockStart + BlockSize, requestEnd);
            if (copyEnd <= copyStart) return;
            Buffer.BlockCopy(buffer, bufferOffset + (int)(copyStart - blockStart),
                             result, (int)(copyStart - requestStart), (int)(copyEnd - copyStart));
        }

        /// <summary>
        /// 从源读**一整块**到 <paramref name="buffer"/>——只用于**盘尾残块**（容量不是 4 KB 整数倍时的最后一块）。
        /// 返回 false 表示"这不是源上那一块的真值"（补了零），调用方据此不回填缓存——
        /// 回填一块掺了零的数据会让后续读到错内容。常规回源走 <see cref="ReadMissedRun"/>（合并读）。
        /// </summary>
        private bool ReadWholeBlockFromDisk(long blockIndex, byte[] buffer)
        {
            long offset = blockIndex * BlockSize;
            long remaining = Size - offset;
            if (remaining >= BlockSize)
            {
                _raw.Read(offset, buffer, 0, BlockSize);
                return true;
            }

            // 盘容量不是 4KB 整数倍时，最后那一块只有一部分在盘上：多出来的部分补零、且不回填
            Array.Clear(buffer);
            if (remaining <= 0) return false;
            _raw.Read(offset, buffer, 0, (int)remaining);
            return false;
        }

        #endregion

        #region 写

        public override void WriteSectors(long lba, byte[] data)
        {
            EnterCommand();
            try
            {
                int bytesPerSector = BytesPerSector;
                if (data.Length == 0) return;
                if (data.Length % bytesPerSector != 0)
                    throw new IOException($"写长度 {data.Length} 字节不是扇区大小 {bytesPerSector} 的整数倍");

                long sectorCount = data.Length / bytesPerSector;
                long totalSectors = Size / bytesPerSector;
                if (lba < 0 || lba > totalSectors - sectorCount)
                    throw new ArgumentOutOfRangeException(nameof(lba), $"写越界：LBA {lba} + {sectorCount} 扇区，盘共 {totalSectors} 扇区");
                if (data.Length > MaxSingleRequestBytes)
                    throw new ArgumentOutOfRangeException(nameof(data), $"单条写请求 {data.Length} 字节超出上限");

                long offset = lba * bytesPerSector;
                long firstBlock = offset / BlockSize;
                long lastBlock = (offset + data.Length - 1) / BlockSize;

                // ① 先落盘（写透传，不经缓存）
                try
                {
                    _raw.Write(offset, data, 0, data.Length);
                }
                catch
                {
                    // 源写失败也可能已完成部分扇区（分段写/远程回复丢失）。
                    // 无法确认哪些字节变过，整个请求范围都必须作废，然后报告原错误。
                    // 作废自身若再抛（例如缓存已停服），绝不能把源写失败的原始异常盖掉——那才是上层要看到的错误码。
                    try { _cache.InvalidateRange(firstBlock, lastBlock - firstBlock + 1); }
                    catch (Exception ex) { LogService.DebugFile($"源写失败后作废缓存范围又出错（已忽略，保留源写错误）：{ex.Message}"); }
                    throw;
                }

                // ② 逐块处置被覆盖到的块。**必须在写盘之后**：刷新以"源盘已是新数据"为前提；
                //    若反过来，中间这段窗口里别的读可能把"写之前的旧块"又读回来。
                //    刷新与作废是同一位置的两个选择，两者都保证缓存里不留陈旧副本，区别只在下次读要不要回源：
                //    · 整块被完整覆盖 ⇒ 写数据就是盘上这一块的新值，直接刷进缓存（W1，省掉下次读的回源）；
                //    · 只被覆盖一部分（写请求未必块对齐，MBR 那 512 字节就是）⇒ 块内其余字节还是盘上旧值，
                //      拿写数据当整块填进去就是脏数据，因此退回作废。
                for (long blockIndex = firstBlock; blockIndex <= lastBlock; blockIndex++)
                {
                    long blockStart = blockIndex * BlockSize;
                    long copyStart = Math.Max(blockStart, offset);
                    long copyEnd = Math.Min(blockStart + BlockSize, offset + data.Length);
                    if (copyStart == blockStart && copyEnd == blockStart + BlockSize)
                    {
                        _cache.UpdateBlock(blockIndex, data, (int)(copyStart - offset));
                    }
                    else
                    {
                        _cache.InvalidateRange(blockIndex, 1);
                    }
                }

                Interlocked.Increment(ref _writeCommands);
                Interlocked.Add(ref _writeSectors, sectorCount);
            }
            finally
            {
                ExitCommand();
            }
        }

        #endregion
    }
}
