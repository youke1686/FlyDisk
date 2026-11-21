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
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 一次 SMART 读取的结果。**只认 0xF1（累计写入 LBA）**：读不到就是 <see cref="Valid"/>=false，
    /// 调用方按"无法判断"回退原判据。0xF2 / 0x0C 只用于活性证明与诊断。
    /// </summary>
    public readonly struct SmartCounterSnapshot
    {
        public bool Valid { get; init; }
        public ulong Written { get; init; }
        public ulong Read { get; init; }
        public int PowerCycles { get; init; }
    }

    /// <summary>
    /// 读源盘的 SMART 累计写入计数（Total LBAs Written）。
    ///
    /// 依次尝试：ATA PASS-THROUGH（直连 SATA）→ SCSI ATA PASS-THROUGH(16) → SCSI ATA PASS-THROUGH(12)
    /// （部分 USB 桥只认 12 字节）。全部失败返回 <c>Valid=false</c>，**不抛异常、不重试**。
    ///
    /// 已知边界（见 docs/L2可信判据改造_SMART写入计数与账本v4.md）：
    /// - 不是所有 USB 桥都转发 SMART；某些盘不实现 0xF1；
    /// - 桥返回静态表时"没变"会是假阴性，所以调用方还必须做**活性证明**；
    /// - 单条命令 3 秒超时，避免个别桥把启动 / 停止流程拖住。
    /// </summary>
    public static class SmartCounterReader
    {
        private const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
        private const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;

        private const int SmartDataBytes = 512;
        private const int SenseBytes = 32;
        private const int TimeoutSeconds = 3;

        private const byte AtaCommandSmart = 0xB0;
        private const byte SmartFeaturesReadData = 0xD0;
        private const byte SmartLbaLow = 0x4F;
        private const byte SmartLbaMid = 0xC2;

        private const byte AttrTotalLbaWritten = 0xF1;
        private const byte AttrTotalLbaRead = 0xF2;
        private const byte AttrPowerCycle = 0x0C;

        /// <summary>读一次；失败返回 <c>Valid=false</c>（绝大多数情况下是桥 / 盘不支持，不是错误）</summary>
        /// <param name="context">诊断日志里的来源标签（例如 <c>disk 2</c>）；为空时只写 <c>Smart</c></param>
        public static bool TryRead(SafeFileHandle handle, out SmartCounterSnapshot snapshot, string context = "")
        {
            snapshot = default;
            string tag = string.IsNullOrWhiteSpace(context) ? "Smart" : $"Smart[{context}]";

            if (handle == null || handle.IsInvalid || handle.IsClosed)
            {
                LogService.DebugFile($"{tag} handle invalid");
                return false;
            }

            byte[] data = new byte[SmartDataBytes];

            // 某个 IOCTL 成功不代表 ATA 命令成功（盘可能返回 ABRT）；数据也未必带 0xF1。
            // 所以每一级都要"命令成功 + 解析出属性"双通过，否则继续下一级。
            bool ok = TryAtaPassThrough(handle, data, out string ataDetail);
            LogService.DebugFile($"{tag} ATA: {ataDetail}");
            if (ok && TryParse(data, out snapshot))
            {
                LogParsed(tag, "ATA", snapshot);
                return true;
            }
            if (ok) LogService.DebugFile($"{tag} ATA: command ok but no usable 0xF1/0xF2");
            Array.Clear(data, 0, data.Length);

            ok = TryScsiAtaPassThrough(handle, data, cdb16: true, out string scsi16Detail);
            LogService.DebugFile($"{tag} SCSI16: {scsi16Detail}");
            if (ok && TryParse(data, out snapshot))
            {
                LogParsed(tag, "SCSI16", snapshot);
                return true;
            }
            if (ok) LogService.DebugFile($"{tag} SCSI16: command ok but no usable 0xF1/0xF2");
            Array.Clear(data, 0, data.Length);

            ok = TryScsiAtaPassThrough(handle, data, cdb16: false, out string scsi12Detail);
            LogService.DebugFile($"{tag} SCSI12: {scsi12Detail}");
            if (ok && TryParse(data, out snapshot))
            {
                LogParsed(tag, "SCSI12", snapshot);
                return true;
            }
            if (ok) LogService.DebugFile($"{tag} SCSI12: command ok but no usable 0xF1/0xF2");

            snapshot = default;
            LogService.DebugFile($"{tag} all paths failed");
            return false;
        }

        private static void LogParsed(string tag, string method, SmartCounterSnapshot snapshot)
        {
            LogService.DebugFile(
                $"{tag} {method}: parsed Written={snapshot.Written} Read={snapshot.Read} PowerCycles={snapshot.PowerCycles}");
        }

        #region ATA PASS-THROUGH（直连 SATA）

        private static bool TryAtaPassThrough(SafeFileHandle handle, byte[] data, out string detail)
        {
            int structSize = Marshal.SizeOf(typeof(ATA_PASS_THROUGH_EX));
            int total = structSize + SmartDataBytes;
            IntPtr buffer = IntPtr.Zero;
            try
            {
                buffer = Marshal.AllocHGlobal(total);
                for (int i = 0; i < total; i++) Marshal.WriteByte(buffer, i, 0);

                var request = new ATA_PASS_THROUGH_EX
                {
                    Length = (ushort)structSize,
                    AtaFlags = ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_DATA_IN,
                    DataTransferLength = (uint)SmartDataBytes,
                    TimeOutValue = (uint)TimeoutSeconds,
                    DataBufferOffset = (IntPtr)structSize,
                    PreviousTaskFile = new byte[8],
                    CurrentTaskFile = new byte[8]
                };
                request.CurrentTaskFile[0] = SmartFeaturesReadData;   // Features
                request.CurrentTaskFile[1] = 0x01;                    // Sector count
                request.CurrentTaskFile[2] = SmartLbaLow;             // LBA low
                request.CurrentTaskFile[3] = SmartLbaMid;             // LBA mid
                request.CurrentTaskFile[4] = 0x00;                    // LBA high
                request.CurrentTaskFile[5] = 0xA0;                    // Device
                request.CurrentTaskFile[6] = AtaCommandSmart;         // Command

                Marshal.StructureToPtr(request, buffer, false);
                if (!DeviceIoControl(handle, IOCTL_ATA_PASS_THROUGH, buffer, (uint)total,
                        buffer, (uint)total, out uint returned, IntPtr.Zero))
                {
                    detail = $"Win32={Marshal.GetLastWin32Error()}";
                    return false;
                }
                if (returned < (uint)total)
                {
                    detail = $"returned={returned} < {total}";
                    return false;
                }

                // IOCTL 成功 ≠ 命令成功：例如盘返回 ABRT 时，数据区是全零，直接解析会得到"没有 0xF1"。
                // 这里必须看返回的 CurrentTaskFile，失败就让调用方继续尝试 SCSI 路径。
                var after = Marshal.PtrToStructure<ATA_PASS_THROUGH_EX>(buffer);
                byte[] taskFile = after.CurrentTaskFile ?? Array.Empty<byte>();
                byte error = taskFile.Length > 0 ? taskFile[0] : (byte)0;
                byte status6 = taskFile.Length > 6 ? taskFile[6] : (byte)0;
                byte status7 = taskFile.Length > 7 ? taskFile[7] : (byte)0;
                detail = $"returned={returned}, taskfile=[{HexBytes(taskFile)}], " +
                         $"error=0x{error:X2}, status[6]=0x{status6:X2}, status[7]=0x{status7:X2}";

                if (AtaCommandFailed(after))
                {
                    detail += " (ERR/ABRT)";
                    return false;
                }

                Marshal.Copy(IntPtr.Add(buffer, structSize), data, 0, SmartDataBytes);
                detail += " (ok)";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            }
        }

        private static string HexBytes(byte[] bytes)
        {
            if (bytes.Length == 0) return string.Empty;
            var sb = new System.Text.StringBuilder(bytes.Length * 3);
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(bytes[i].ToString("X2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// ATA 命令是否失败。返回的 <c>CurrentTaskFile</c> 里索引 0 通常是 Error、索引 6 通常是 Status：
        /// ERR 位 = 0x01，ABRT = 0x04。IOCTL 本身成功但这里带 ERR，说明盘 / 驱动拒绝了 SMART 命令。
        /// </summary>
        private static bool AtaCommandFailed(ATA_PASS_THROUGH_EX after)
        {
            byte[] taskFile = after.CurrentTaskFile;
            if (taskFile == null || taskFile.Length < 8) return true;

            byte error = taskFile[0];
            byte status6 = taskFile[6];
            byte status7 = taskFile[7];
            if ((status6 & 0x01) != 0) return true;   // ERR（部分驱动把 Status 放在 [6]）
            if ((status7 & 0x01) != 0) return true;   // ERR（部分驱动把 Status 放在 [7]）
            if ((error & 0x04) != 0) return true;     // ABRT
            return false;
        }

        #endregion

        #region SCSI ATA PASS-THROUGH（USB 桥）

        private static bool TryScsiAtaPassThrough(SafeFileHandle handle, byte[] data, bool cdb16, out string detail)
        {
            IntPtr dataBuffer = IntPtr.Zero;
            IntPtr requestBuffer = IntPtr.Zero;
            int structSize = Marshal.SizeOf(typeof(SCSI_PASS_THROUGH_DIRECT));
            try
            {
                dataBuffer = Marshal.AllocHGlobal(SmartDataBytes);
                requestBuffer = Marshal.AllocHGlobal(structSize + SenseBytes);
                for (int i = 0; i < SmartDataBytes; i++) Marshal.WriteByte(dataBuffer, i, 0);
                for (int i = 0; i < structSize + SenseBytes; i++) Marshal.WriteByte(requestBuffer, i, 0);

                var request = new SCSI_PASS_THROUGH_DIRECT
                {
                    Length = (ushort)structSize,
                    CdbLength = (byte)(cdb16 ? 16 : 12),
                    SenseInfoLength = (byte)SenseBytes,
                    DataIn = 1,                         // SCSI_IO_DATA_IN
                    DataTransferLength = (uint)SmartDataBytes,
                    TimeOutValue = TimeoutSeconds,
                    DataBuffer = dataBuffer,
                    SenseInfoOffset = (uint)structSize,
                    Cdb = new byte[16]
                };
                request.Cdb[0] = cdb16 ? (byte)0x85 : (byte)0xA1;      // ATA PASS-THROUGH (16 / 12)
                request.Cdb[1] = 0x08;                                  // PIO Data-In
                request.Cdb[2] = 0x90;                                  // T_LENGTH=2, T_DIR=1
                if (cdb16)
                {
                    request.Cdb[3] = 0x00;
                    request.Cdb[4] = SmartFeaturesReadData;
                    request.Cdb[5] = 0x00;
                    request.Cdb[6] = 0x01;
                    request.Cdb[7] = 0x00;
                    request.Cdb[8] = SmartLbaLow;
                    request.Cdb[9] = 0x00;
                    request.Cdb[10] = SmartLbaMid;
                    request.Cdb[11] = 0x00;
                    request.Cdb[12] = 0x00;
                    request.Cdb[13] = 0xA0;
                    request.Cdb[14] = AtaCommandSmart;
                    request.Cdb[15] = 0x00;
                }
                else
                {
                    request.Cdb[3] = SmartFeaturesReadData;
                    request.Cdb[4] = 0x01;
                    request.Cdb[5] = SmartLbaLow;
                    request.Cdb[6] = SmartLbaMid;
                    request.Cdb[7] = 0x00;
                    request.Cdb[8] = 0xA0;
                    request.Cdb[9] = AtaCommandSmart;
                    request.Cdb[10] = 0x00;
                    request.Cdb[11] = 0x00;
                }

                // SCSI_PASS_THROUGH_DIRECT 的 SenseInfo 跟在本结构之后；把请求 + sense 放进同一块缓冲
                Marshal.StructureToPtr(request, requestBuffer, false);

                if (!DeviceIoControl(handle, IOCTL_SCSI_PASS_THROUGH_DIRECT, requestBuffer,
                        (uint)(structSize + SenseBytes), requestBuffer, (uint)(structSize + SenseBytes),
                        out uint returned, IntPtr.Zero))
                {
                    detail = $"Win32={Marshal.GetLastWin32Error()}";
                    return false;
                }

                var status = Marshal.PtrToStructure<SCSI_PASS_THROUGH_DIRECT>(requestBuffer);
                byte[] sense = new byte[SenseBytes];
                Marshal.Copy(IntPtr.Add(requestBuffer, structSize), sense, 0, SenseBytes);
                byte senseKey = (byte)(sense[2] & 0x0F);
                detail = $"returned={returned}, ScsiStatus=0x{status.ScsiStatus:X2}, " +
                         $"SenseKey=0x{senseKey:X2}, ASC=0x{sense[12]:X2}, ASCQ=0x{sense[13]:X2}";

                if (status.ScsiStatus != 0) return false;

                Marshal.Copy(dataBuffer, data, 0, SmartDataBytes);
                detail += " (ok)";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (requestBuffer != IntPtr.Zero) Marshal.FreeHGlobal(requestBuffer);
                if (dataBuffer != IntPtr.Zero) Marshal.FreeHGlobal(dataBuffer);
            }
        }

        #endregion

        #region 属性解析

        private static bool TryParse(byte[] data, out SmartCounterSnapshot snapshot)
        {
            snapshot = default;
            bool hasWritten = false;
            bool hasRead = false;
            ulong written = 0;
            ulong read = 0;
            int powerCycles = 0;

            for (int offset = 2; offset + 12 <= data.Length; offset += 12)
            {
                byte id = data[offset];
                if (id == 0) break;

                ulong raw = 0;
                for (int i = 0; i < 6; i++) raw |= (ulong)data[offset + 5 + i] << (8 * i);

                switch (id)
                {
                    case AttrTotalLbaWritten:
                        written = raw;
                        hasWritten = true;
                        break;
                    case AttrTotalLbaRead:
                        read = raw;
                        hasRead = true;
                        break;
                    case AttrPowerCycle:
                        powerCycles = (int)(raw & 0xFFFFFFFF);
                        break;
                }
            }

            // **只认 0xF1**：0xF2 / 0x0C 只作活性与诊断，缺了不影响 Valid
            if (!hasWritten) return false;
            _ = hasRead;

            snapshot = new SmartCounterSnapshot
            {
                Valid = true,
                Written = written,
                Read = read,
                PowerCycles = powerCycles
            };
            return true;
        }

        #endregion

        #region Win32 API

        private const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
        private const ushort ATA_FLAGS_DATA_IN = 0x02;

        [StructLayout(LayoutKind.Sequential)]
        private struct ATA_PASS_THROUGH_EX
        {
            public ushort Length;
            public ushort AtaFlags;
            public byte PathId;
            public byte TargetId;
            public byte Lun;
            public byte ReservedAsUchar;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public uint ReservedAsUlong;
            public IntPtr DataBufferOffset;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] PreviousTaskFile;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
            public byte[] CurrentTaskFile;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SCSI_PASS_THROUGH_DIRECT
        {
            public ushort Length;
            public byte ScsiStatus;
            public byte PathId;
            public byte TargetId;
            public byte Lun;
            public byte CdbLength;
            public byte SenseInfoLength;
            public byte DataIn;
            public uint DataTransferLength;
            public int TimeOutValue;
            public IntPtr DataBuffer;
            public uint SenseInfoOffset;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] Cdb;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            SafeFileHandle device, uint ioControlCode,
            IntPtr inBuffer, uint inBufferSize,
            IntPtr outBuffer, uint outBufferSize,
            out uint bytesReturned, IntPtr overlapped);

        #endregion
    }
}
