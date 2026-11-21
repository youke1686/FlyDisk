#:property TargetFramework=net10.0-windows
#:property Nullable=enable

// SMART 读取复现工具（单文件，只用 BCL，不需要额外 #:package）
// 用法：在仓库根目录执行  dotnet run smart_probe.cs
//
// 功能：
//   1. 枚举 \\.\PhysicalDriveN，打印容量、型号、序列号、BusType、RemovableMedia；
//   2. 让用户输入一个磁盘号；
//   3. 用与 FlyDisk 相同的三级回退读取 SMART：
//        IOCTL_ATA_PASS_THROUGH
//        IOCTL_SCSI_PASS_THROUGH_DIRECT + ATA PASS-THROUGH(16)
//        IOCTL_SCSI_PASS_THROUGH_DIRECT + ATA PASS-THROUGH(12)
//   4. 打印每一步的 Win32 错误 / SCSI 状态 / 校验数据，以及完整的 SMART 属性表
//      （重点看 0xF1 Total LBAs Written、0xF2 Total LBAs Read、0x0C Power Cycle）。
//
// 只读：不发任何写命令，不改磁盘内容。

using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

Console.OutputEncoding = Encoding.UTF8;
Console.WriteLine("=== FlyDisk SMART 读取复现工具 ===");
Console.WriteLine();

var disks = new System.Collections.Generic.List<(int Number, string Text)>();
for (int n = 0; n < 32; n++)
{
    using SafeFileHandle handle = Native.OpenDisk(n, write: false);
    if (handle.IsInvalid) continue;

    long length = Native.GetDiskLength(handle);
    Native.DiskDescriptor desc = Native.QueryDescriptor(handle);
    string text = $"磁盘 {n}：{FormatSize(length)}　型号「{desc.Model}」　序列号「{desc.Serial}」" +
                  $"　BusType={desc.BusType}({BusTypeName(desc.BusType)})　Removable={desc.RemovableMedia}";
    disks.Add((n, text));
    Console.WriteLine(text);
}

if (disks.Count == 0)
{
    Console.WriteLine("没有枚举到任何物理盘（需要管理员权限？）。");
    return;
}

Console.WriteLine();
Console.Write("请输入要读取 SMART 的磁盘号（例如 2）：");
string? input = Console.ReadLine();
if (!int.TryParse(input, out int diskNumber))
{
    Console.WriteLine("磁盘号无效。");
    return;
}

Console.WriteLine();
Console.WriteLine($"--- 读取 \\\\.\\PhysicalDrive{diskNumber} ---");

// 与 FlyDisk 一致：先试只读探针句柄；不行再用读写句柄重试
bool succeeded = false;
using (SafeFileHandle readOnly = Native.OpenDisk(diskNumber, write: false))
{
    if (!readOnly.IsInvalid)
    {
        Console.WriteLine("（只读句柄）");
        succeeded = SmartProbe.Run(readOnly);
    }
    else
    {
        Console.WriteLine($"只读打开失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
    }
}

if (!succeeded)
{
    using SafeFileHandle readWrite = Native.OpenDisk(diskNumber, write: true);
    if (readWrite.IsInvalid)
    {
        Console.WriteLine($"读写打开也失败，Win32 错误 {Marshal.GetLastWin32Error()}。");
        return;
    }
    Console.WriteLine("（读写句柄重试）");
    SmartProbe.Run(readWrite);
}

Console.WriteLine();
Console.WriteLine("完成。把上面的输出整段贴给开发者即可。");

static string FormatSize(long bytes)
{
    double b = bytes;
    if (b >= 1024.0 * 1024 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024 * 1024)).ToString("F1") + " TiB";
    if (b >= 1024.0 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("F1") + " GiB";
    return (b / (1024.0 * 1024)).ToString("F1") + " MiB";
}

static string BusTypeName(int busType) => busType switch
{
    1 => "SCSI",
    2 => "ATAPI",
    3 => "ATA",
    4 => "1394",
    7 => "USB",
    8 => "RAID",
    9 => "iSCSI",
    10 => "SAS",
    11 => "SATA",
    12 => "SD",
    13 => "MMC",
    14 => "Virtual",
    15 => "FileBackedVirtual",
    16 => "Spaces",
    17 => "NVMe",
    18 => "SCM",
    _ => "Unknown"
};

internal static class SmartProbe
{
    private const int SmartDataBytes = 512;
    private const int SenseBytes = 32;
    private const int TimeoutSeconds = 5;

    private const byte AtaCommandSmart = 0xB0;
    private const byte SmartFeaturesReadData = 0xD0;
    private const byte SmartLbaLow = 0x4F;
    private const byte SmartLbaMid = 0xC2;

    public static bool Run(SafeFileHandle handle)
    {
        byte[] data = new byte[SmartDataBytes];

        Console.WriteLine();
        Console.WriteLine("① IOCTL_ATA_PASS_THROUGH（直连 SATA）");
        if (TryAta(handle, data, out string ataDetail))
        {
            Console.WriteLine("   成功：" + ataDetail);
            DumpAttributes(data);
            return true;
        }
        Console.WriteLine("   失败：" + ataDetail);

        Console.WriteLine();
        Console.WriteLine("② SCSI ATA PASS-THROUGH(16)");
        if (TryScsi(handle, data, cdb16: true, out string scsi16Detail))
        {
            Console.WriteLine("   成功：" + scsi16Detail);
            DumpAttributes(data);
            return true;
        }
        Console.WriteLine("   失败：" + scsi16Detail);

        Console.WriteLine();
        Console.WriteLine("③ SCSI ATA PASS-THROUGH(12)");
        if (TryScsi(handle, data, cdb16: false, out string scsi12Detail))
        {
            Console.WriteLine("   成功：" + scsi12Detail);
            DumpAttributes(data);
            return true;
        }
        Console.WriteLine("   失败：" + scsi12Detail);

        Console.WriteLine();
        Console.WriteLine("④ IOCTL_STORAGE_PREDICT_FAILURE（部分 SATA / RAID 驱动只放行这一条）");
        if (TryPredictFailure(handle, data, out string predictDetail))
        {
            Console.WriteLine("   成功：" + predictDetail);
            DumpAttributes(data);
            return true;
        }
        Console.WriteLine("   失败：" + predictDetail);

        Console.WriteLine();
        Console.WriteLine("四种方法全部失败：这块盘 / 桥不提供 SMART READ DATA，或当前驱动不允许 ATA 直通。");
        return false;
    }

    /// <summary>
    /// IOCTL_STORAGE_PREDICT_FAILURE 的 VendorSpecific[512] 在很多 SATA / RAID 驱动上就是
    /// 原始的 SMART 属性表。它是 FILE_ANY_ACCESS，只读句柄也能调用，且没有任何副作用。
    /// </summary>
    private static bool TryPredictFailure(SafeFileHandle handle, byte[] data, out string detail)
    {
        const int size = 4 + 512;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(buffer, i, 0);

            if (!Native.DeviceIoControl(handle, Native.IOCTL_STORAGE_PREDICT_FAILURE,
                    IntPtr.Zero, 0, buffer, size, out uint returned, IntPtr.Zero))
            {
                detail = $"Win32 错误 {Marshal.GetLastWin32Error()}";
                return false;
            }

            uint predict = (uint)Marshal.ReadInt32(buffer, 0);
            byte[] vendor = new byte[512];
            Marshal.Copy(IntPtr.Add(buffer, 4), vendor, 0, 512);

            if (!LooksLikeSmartTable(vendor))
            {
                detail = $"返回 {returned} 字节，PredictFailure={predict}，但 VendorSpecific 不是 SMART 属性表";
                return false;
            }

            Buffer.BlockCopy(vendor, 0, data, 0, 512);
            detail = $"返回 {returned} 字节，PredictFailure={predict}，VendorSpecific 是 SMART 属性表";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool LooksLikeSmartTable(byte[] data)
    {
        if (data.Length < 14) return false;
        if (data[0] != 0 || data[1] != 0) return false;
        if (data[2] == 0) return false;

        int entries = 0;
        for (int offset = 2; offset + 12 <= data.Length; offset += 12)
        {
            if (data[offset] == 0) break;
            entries++;
            if (entries >= 5) return true;
        }
        return false;
    }

    private static bool TryAta(SafeFileHandle handle, byte[] data, out string detail)
    {
        int structSize = Marshal.SizeOf(typeof(Native.ATA_PASS_THROUGH_EX));
        int total = structSize + SmartDataBytes;
        IntPtr buffer = Marshal.AllocHGlobal(total);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(buffer, i, 0);

            var request = new Native.ATA_PASS_THROUGH_EX
            {
                Length = (ushort)structSize,
                AtaFlags = Native.ATA_FLAGS_DRDY_REQUIRED | Native.ATA_FLAGS_DATA_IN,
                DataTransferLength = (uint)SmartDataBytes,
                TimeOutValue = (uint)TimeoutSeconds,
                DataBufferOffset = (IntPtr)structSize,
                PreviousTaskFile = new byte[8],
                CurrentTaskFile = new byte[8]
            };
            request.CurrentTaskFile[0] = SmartFeaturesReadData;
            request.CurrentTaskFile[1] = 0x01;
            request.CurrentTaskFile[2] = SmartLbaLow;
            request.CurrentTaskFile[3] = SmartLbaMid;
            request.CurrentTaskFile[4] = 0x00;
            request.CurrentTaskFile[5] = 0xA0;
            request.CurrentTaskFile[6] = AtaCommandSmart;

            Marshal.StructureToPtr(request, buffer, false);

            if (!Native.DeviceIoControl(handle, Native.IOCTL_ATA_PASS_THROUGH, buffer, (uint)total,
                    buffer, (uint)total, out uint returned, IntPtr.Zero))
            {
                detail = $"Win32 错误 {Marshal.GetLastWin32Error()}（可用 GetLastWin32Error 对照）";
                return false;
            }

            var after = Marshal.PtrToStructure<Native.ATA_PASS_THROUGH_EX>(buffer);
            byte error = after.CurrentTaskFile[0];
            byte status6 = after.CurrentTaskFile[6];
            byte status7 = after.CurrentTaskFile[7];
            detail = $"返回 {returned} 字节，CurrentTaskFile=[{JoinBytes(after.CurrentTaskFile)}]" +
                     $"（Error=0x{error:X2}，Status[6]=0x{status6:X2}，Status[7]=0x{status7:X2}）";

            // IOCTL 成功不代表 ATA 命令成功：ERR=0x01、ABRT=0x04
            if ((status6 & 0x01) != 0 || (status7 & 0x01) != 0 || (error & 0x04) != 0)
            {
                detail += " → ATA 命令被拒绝（ERR/ABRT），继续尝试 SCSI";
                return false;
            }

            Marshal.Copy(IntPtr.Add(buffer, structSize), data, 0, SmartDataBytes);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryScsi(SafeFileHandle handle, byte[] data, bool cdb16, out string detail)
    {
        IntPtr dataBuffer = Marshal.AllocHGlobal(SmartDataBytes);
        int structSize = Marshal.SizeOf(typeof(Native.SCSI_PASS_THROUGH_DIRECT));
        IntPtr requestBuffer = Marshal.AllocHGlobal(structSize + SenseBytes);
        try
        {
            for (int i = 0; i < SmartDataBytes; i++) Marshal.WriteByte(dataBuffer, i, 0);
            for (int i = 0; i < structSize + SenseBytes; i++) Marshal.WriteByte(requestBuffer, i, 0);

            var request = new Native.SCSI_PASS_THROUGH_DIRECT
            {
                Length = (ushort)structSize,
                CdbLength = (byte)(cdb16 ? 16 : 12),
                SenseInfoLength = SenseBytes,
                DataIn = 1,
                DataTransferLength = (uint)SmartDataBytes,
                TimeOutValue = TimeoutSeconds,
                DataBuffer = dataBuffer,
                SenseInfoOffset = (uint)structSize,
                Cdb = new byte[16]
            };

            request.Cdb[0] = cdb16 ? (byte)0x85 : (byte)0xA1;
            request.Cdb[1] = 0x08;
            request.Cdb[2] = 0x90;
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

            Marshal.StructureToPtr(request, requestBuffer, false);

            if (!Native.DeviceIoControl(handle, Native.IOCTL_SCSI_PASS_THROUGH_DIRECT,
                    requestBuffer, (uint)(structSize + SenseBytes),
                    requestBuffer, (uint)(structSize + SenseBytes),
                    out uint returned, IntPtr.Zero))
            {
                detail = $"Win32 错误 {Marshal.GetLastWin32Error()}";
                return false;
            }

            var after = Marshal.PtrToStructure<Native.SCSI_PASS_THROUGH_DIRECT>(requestBuffer);
            byte[] sense = new byte[SenseBytes];
            Marshal.Copy(IntPtr.Add(requestBuffer, structSize), sense, 0, SenseBytes);
            byte senseKey = (byte)(sense[2] & 0x0F);
            byte asc = sense[12];
            byte ascq = sense[13];

            if (after.ScsiStatus != 0)
            {
                detail = $"返回 {returned} 字节，ScsiStatus=0x{after.ScsiStatus:X2}，" +
                         $"SenseKey=0x{senseKey:X2} ASC=0x{asc:X2} ASCQ=0x{ascq:X2}";
                return false;
            }

            detail = $"返回 {returned} 字节，ScsiStatus=0x00，SenseKey=0x{senseKey:X2} ASC=0x{asc:X2} ASCQ=0x{ascq:X2}";
            Marshal.Copy(dataBuffer, data, 0, SmartDataBytes);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(requestBuffer);
            Marshal.FreeHGlobal(dataBuffer);
        }
    }

    private static void DumpAttributes(byte[] data)
    {
        Console.WriteLine();
        Console.WriteLine("SMART 属性表（前 32 字节：" + JoinBytes(data.AsSpan(0, 32)) + "）");

        bool hasWritten = false;
        bool hasRead = false;
        for (int offset = 2; offset + 12 <= data.Length; offset += 12)
        {
            byte id = data[offset];
            if (id == 0) break;

            ulong raw = 0;
            for (int i = 0; i < 6; i++) raw |= (ulong)data[offset + 5 + i] << (8 * i);
            byte value = data[offset + 3];
            byte worst = data[offset + 4];

            string marker = id switch
            {
                0xF1 => "  ← Total LBAs Written",
                0xF2 => "  ← Total LBAs Read",
                0x0C => "  ← Power Cycle Count",
                _ => string.Empty
            };
            if (id == 0xF1) hasWritten = true;
            if (id == 0xF2) hasRead = true;

            Console.WriteLine($"  ID=0x{id:X2} ({id,3})  value={value,3} worst={worst,3} raw=0x{raw:X12} ({raw}){marker}");
        }

        Console.WriteLine();
        Console.WriteLine($"结论：0xF1 存在={hasWritten}，0xF2 存在={hasRead}。");
        if (!hasWritten)
        {
            Console.WriteLine("=> 这把盘不提供「累计写入量」，FlyDisk 无法用它判断‘有没有被写过’。");
        }
    }

    private static string JoinBytes(byte[] bytes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    private static string JoinBytes(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }
}

internal static class Native
{
    public const uint IOCTL_ATA_PASS_THROUGH = 0x0004D02C;
    public const uint IOCTL_SCSI_PASS_THROUGH_DIRECT = 0x0004D014;
    public const uint IOCTL_STORAGE_PREDICT_FAILURE = 0x002D0444;
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint FILE_SHARE_DELETE = 0x00000004;
    private const uint OPEN_EXISTING = 3;

    public const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
    public const ushort ATA_FLAGS_DATA_IN = 0x02;

    public static SafeFileHandle OpenDisk(int diskNumber, bool write)
    {
        SafeFileHandle handle = CreateFileW($@"\\.\PhysicalDrive{diskNumber}",
            write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return handle;
    }

    public static long GetDiskLength(SafeFileHandle handle)
    {
        byte[] buffer = new byte[8];
        IntPtr ptr = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            if (!DeviceIoControl(handle, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0,
                    ptr, (uint)buffer.Length, out _, IntPtr.Zero))
            {
                return 0;
            }
            return Marshal.ReadInt64(ptr);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    public struct DiskDescriptor
    {
        public string Model;
        public string Serial;
        public int BusType;
        public bool RemovableMedia;
    }

    public static DiskDescriptor QueryDescriptor(SafeFileHandle handle)
    {
        var result = new DiskDescriptor { Model = "?", Serial = "?" };
        const int bufferSize = 1024;
        IntPtr query = Marshal.AllocHGlobal(12);
        IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            for (int i = 0; i < 12; i++) Marshal.WriteByte(query, i, 0);
            for (int i = 0; i < bufferSize; i++) Marshal.WriteByte(buffer, i, 0);
            // PropertyId = StorageDeviceProperty(0), QueryType = PropertyStandardQuery(0)

            if (!DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, 12,
                    buffer, bufferSize, out _, IntPtr.Zero))
            {
                return result;
            }

            int vendorOffset = Marshal.ReadInt32(buffer, 12);
            int productOffset = Marshal.ReadInt32(buffer, 16);
            int serialOffset = Marshal.ReadInt32(buffer, 24);
            result.BusType = Marshal.ReadInt32(buffer, 28);
            result.RemovableMedia = Marshal.ReadByte(buffer, 10) != 0;
            result.Model = ReadAscii(buffer, vendorOffset, bufferSize) + " " + ReadAscii(buffer, productOffset, bufferSize);
            result.Model = result.Model.Trim();
            result.Serial = ReadAscii(buffer, serialOffset, bufferSize).Trim();
            return result;
        }
        catch
        {
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(query);
        }
    }

    private static string ReadAscii(IntPtr buffer, int offset, int bufferSize)
    {
        if (offset <= 0 || offset >= bufferSize) return string.Empty;
        var sb = new StringBuilder();
        for (int i = offset; i < bufferSize; i++)
        {
            byte b = Marshal.ReadByte(buffer, i);
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ATA_PASS_THROUGH_EX
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
    public struct SCSI_PASS_THROUGH_DIRECT
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode,
        IntPtr inBuffer, uint inBufferSize,
        IntPtr outBuffer, uint outBufferSize,
        out uint bytesReturned, IntPtr overlapped);
}
