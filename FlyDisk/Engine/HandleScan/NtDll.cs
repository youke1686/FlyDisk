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
//
// ---------------------------------------------------------------------------
// 本文件改编自 Handle2（https://github.com/PolarGoose/Handle2），MIT License，
// Copyright (c) 2022 PolarGoose。原实现的句柄表枚举与对象名/类型查询原样保留，
// 只调整了命名空间并删掉了本项目用不到的部分。许可证见 THIRD-PARTY-NOTICES.md。

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace FlyDisk.Engine.HandleScan
{
    /// <summary>
    /// `ntdll` 的句柄表与对象查询。
    ///
    /// **安全前提**：<see cref="GetHandleName"/> 只允许对 **File 类型**的句柄调用。
    /// `NtQueryObject(ObjectNameInformation)` 遇到同步命名管道会永久阻塞在内核里，
    /// 没有任何用户态手段能中断它——所以调用方必须先 <see cref="GetHandleType"/>。
    /// </summary>
    internal static class NtDll
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public UIntPtr UniqueProcessId;
            public IntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SYSTEM_HANDLE_INFORMATION_EX
        {
            public IntPtr NumberOfHandles;
            public IntPtr Reserved;
            public SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX Handles;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UNICODE_STRING
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GENERIC_MAPPING
        {
            public int GenericRead;
            public int GenericWrite;
            public int GenericExecute;
            public int GenericAll;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct OBJECT_TYPE_INFORMATION
        {
            public UNICODE_STRING Name;
            public uint TotalNumberOfObjects;
            public uint TotalNumberOfHandles;
            public uint TotalPagedPoolUsage;
            public uint TotalNonPagedPoolUsage;
            public uint TotalNamePoolUsage;
            public uint TotalHandleTableUsage;
            public uint HighWaterNumberOfObjects;
            public uint HighWaterNumberOfHandles;
            public uint HighWaterPagedPoolUsage;
            public uint HighWaterNonPagedPoolUsage;
            public uint HighWaterNamePoolUsage;
            public uint HighWaterHandleTableUsage;
            public uint InvalidAttributes;
            public GENERIC_MAPPING GenericMapping;
            public uint ValidAccess;
            public byte SecurityRequired;
            public byte MaintainHandleCount;
            public ushort MaintainTypeList;
            public int PoolType;
            public int PagedPoolUsage;
            public int NonPagedPoolUsage;
        }

        private enum OBJECT_INFORMATION_CLASS
        {
            ObjectBasicInformation = 0,
            ObjectNameInformation = 1,
            ObjectTypeInformation = 2,
            ObjectAllTypesInformation = 3,
            ObjectHandleInformation = 4
        }

        private enum SYSTEM_INFORMATION_CLASS
        {
            SystemExtendedHandleInformation = 64
        }

        private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        private const int NT_SUCCESS = 0;

        [DllImport("ntdll.dll")]
        private static extern uint NtQuerySystemInformation(
            SYSTEM_INFORMATION_CLASS systemInformationClass,
            IntPtr systemInformation,
            int systemInformationLength,
            out int returnLength);

        [DllImport("ntdll.dll", EntryPoint = "NtQueryObject")]
        private static extern int NtQueryObject(
            IntPtr handle,
            OBJECT_INFORMATION_CLASS objectInformationClass,
            IntPtr buf,
            int bufLength,
            ref int returnLength);

        /// <summary>
        /// 取全系统句柄表。起始 32 MB，遇到 `STATUS_INFO_LENGTH_MISMATCH` 翻倍，上限 1 GB。
        /// </summary>
        internal static SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[] QuerySystemHandleInformation()
        {
            for (var bufSize = 32 * 1024 * 1024; bufSize <= 1024 * 1024 * 1024; bufSize *= 2)
            {
                IntPtr buffer = Marshal.AllocHGlobal(bufSize);
                try
                {
                    uint status = NtQuerySystemInformation(
                        SYSTEM_INFORMATION_CLASS.SystemExtendedHandleInformation, buffer, bufSize, out _);

                    if (status == NT_SUCCESS)
                    {
                        var info = Marshal.PtrToStructure<SYSTEM_HANDLE_INFORMATION_EX>(buffer);
                        return GetHandleEntries(info, buffer);
                    }

                    if (status != STATUS_INFO_LENGTH_MISMATCH)
                        throw new InvalidOperationException($"NtQuerySystemInformation failed with status 0x{status:X8}");
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            throw new InvalidOperationException("NtQuerySystemInformation buffer size is not enough");
        }

        /// <summary>句柄的类型名（`File` / `Event` / …）。对任何类型都安全。</summary>
        internal static string? GetHandleType(SafeFileHandle handle)
        {
            IntPtr buffer = QueryObjectInformation(handle, OBJECT_INFORMATION_CLASS.ObjectTypeInformation);
            if (buffer == IntPtr.Zero) return null;

            try
            {
                var info = Marshal.PtrToStructure<OBJECT_TYPE_INFORMATION>(buffer);
                return info.Name.Length > 0 ? Marshal.PtrToStringUni(info.Name.Buffer, info.Name.Length / 2) : string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>
        /// 句柄的原始对象名（`\Device\HarddiskVolume3\path\file` 之类）。
        /// **只对 File 类型调用**（见类注释里的挂起风险）。
        /// </summary>
        internal static string? GetHandleName(SafeFileHandle handle)
        {
            IntPtr buffer = QueryObjectInformation(handle, OBJECT_INFORMATION_CLASS.ObjectNameInformation);
            if (buffer == IntPtr.Zero) return null;

            try
            {
                var name = Marshal.PtrToStructure<UNICODE_STRING>(buffer);
                return name.Length > 0 ? Marshal.PtrToStringUni(name.Buffer, name.Length / 2) : null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static IntPtr QueryObjectInformation(SafeFileHandle handle, OBJECT_INFORMATION_CLASS informationClass)
        {
            int length = 0;
            NtQueryObject(handle.DangerousGetHandle(), informationClass, IntPtr.Zero, 0, ref length);
            if (length == 0) return IntPtr.Zero;

            IntPtr buffer = Marshal.AllocHGlobal(length);
            if (NtQueryObject(handle.DangerousGetHandle(), informationClass, buffer, length, ref length) != 0)
            {
                Marshal.FreeHGlobal(buffer);
                return IntPtr.Zero;
            }

            return buffer;
        }

        private static SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[] GetHandleEntries(
            SYSTEM_HANDLE_INFORMATION_EX handleInfo, IntPtr buffer)
        {
            long numberOfHandles = handleInfo.NumberOfHandles.ToInt64();
            var entries = new SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[numberOfHandles];

            long handleSize = Marshal.SizeOf<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
            var current = new IntPtr(buffer.ToInt64() + Marshal.SizeOf<SYSTEM_HANDLE_INFORMATION_EX>() - handleSize);

            for (long i = 0; i < numberOfHandles; i++)
            {
                entries[i] = Marshal.PtrToStructure<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>(current);
                current = new IntPtr(current.ToInt64() + handleSize);
            }

            return entries;
        }

        /// <summary>把句柄按所属进程分组（顺序保持句柄表的原始顺序）</summary>
        internal static List<KeyValuePair<UIntPtr, List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>>> GroupByProcess(
            SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[] entries)
        {
            var map = new Dictionary<UIntPtr, List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>>();
            var order = new List<KeyValuePair<UIntPtr, List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>>>();
            foreach (var entry in entries)
            {
                if (!map.TryGetValue(entry.UniqueProcessId, out var list))
                {
                    list = new List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>();
                    map[entry.UniqueProcessId] = list;
                    order.Add(new KeyValuePair<UIntPtr, List<SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>>(
                        entry.UniqueProcessId, list));
                }
                list.Add(entry);
            }
            return order;
        }
    }
}
