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
// Copyright (c) 2022 PolarGoose。主循环结构与"先查类型、只对 File 查名字"的顺序保留；
// 过滤判据改成"句柄原始设备路径是否以某块盘的卷前缀开头"，并加了取消与进度回调。
// 许可证见 THIRD-PARTY-NOTICES.md。

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Threading;

namespace FlyDisk.Engine.HandleScan
{
    /// <summary>
    /// 一个占用者。**按"同名 + 同可执行文件路径"聚合**：同一个程序开了多个实例（浏览器、编辑器）
    /// 只占一行，PID 列成一串——否则结果里会出现十几行一模一样的进程名。
    /// </summary>
    internal sealed class OccupantInfo
    {
        public string Name = string.Empty;
        public string? ExePath;
        /// <summary>该程序的所有实例 PID</summary>
        public List<int> Pids { get; } = new();
        /// <summary>命中目标盘卷前缀的**文件**句柄总数（各实例之和）</summary>
        public int HandleCount;
        /// <summary>
        /// 该程序打开的具体文件/目录 → 句柄数（已按显示路径去重）。
        /// 同一个程序对同一个目录开两个句柄只出现一次，计数为 2。
        /// </summary>
        public Dictionary<string, int> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>一次占用扫描的结果</summary>
    internal sealed class OccupancyScanResult
    {
        public List<OccupantInfo> Processes { get; } = new();
        /// <summary>句柄能定位、但复制/查询失败的个数（受保护句柄等）</summary>
        public int UnreadableHandles;
        /// <summary>进程打不开的个数（系统进程、受保护进程）</summary>
        public int UnreadableProcesses;
        public int ScannedProcesses;
        public bool Cancelled;
        public string? Error;
    }

    /// <summary>
    /// 扫全系统句柄表，找出"谁打开了目标盘上的东西"。
    ///
    /// 判据是**句柄的原始设备路径**（`\Device\HarddiskVolumeN\…`），与盘符、挂载点无关——
    /// 这正是它比“路径前缀匹配”更可靠的原因。
    /// </summary>
    internal static class HandleInfoRetriever
    {
        /// <summary>每检查多少个句柄看一次取消标志</summary>
        private const int CancelCheckInterval = 256;

        /// <param name="devicePrefixes">目标盘各卷的设备路径前缀，形如 `\Device\HarddiskVolume7\`</param>
        /// <param name="displayRoots">前缀 → 给人看的根路径（`E:\`；没有挂载点时是卷 GUID 路径）</param>
        /// <param name="onProgress">进度回调：(已检查进程数, 总进程数)</param>
        public static OccupancyScanResult FindOccupants(
            IReadOnlyList<string> devicePrefixes,
            IReadOnlyDictionary<string, string> displayRoots,
            Action<int, int>? onProgress,
            CancellationToken ct)
        {
            var result = new OccupancyScanResult();
            if (devicePrefixes.Count == 0)
            {
                result.Error = "没有定位到目标盘的卷";
                return result;
            }

            NtDll.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX[] entries;
            try
            {
                entries = NtDll.QuerySystemHandleInformation();
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }

            int selfPid = Environment.ProcessId;
            List<KeyValuePair<UIntPtr, List<NtDll.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>>> processes =
                NtDll.GroupByProcess(entries);
            int totalProcesses = processes.Count;
            onProgress?.Invoke(0, totalProcesses);

            using SafeProcessHandle currentProcess = WinApi.GetCurrentProcess();
            ushort? fileTypeIndex = null;

            // 同一个程序的多开实例合并到同一行：键 = 进程名 + 可执行文件路径
            var byProgram = new Dictionary<string, OccupantInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<UIntPtr, List<NtDll.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX>> process in processes)
            {
                if (ct.IsCancellationRequested)
                {
                    result.Cancelled = true;
                    return result;
                }

                // 跳过自己：本程序持有卷/容器的句柄，不排除的话结果里永远有自己
                ulong pid = process.Key.ToUInt64();
                if (pid == (ulong)selfPid)
                {
                    result.ScannedProcesses++;
                    onProgress?.Invoke(result.ScannedProcesses, totalProcesses);
                    continue;
                }

                using SafeProcessHandle? owner = ProcessUtils.OpenProcessToDuplicateHandle(process.Key);
                if (owner == null)
                {
                    result.UnreadableProcesses++;
                    result.ScannedProcesses++;
                    onProgress?.Invoke(result.ScannedProcesses, totalProcesses);
                    continue;
                }

                int matched = 0;
                int inspected = 0;
                var perProcessPaths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (NtDll.SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX entry in process.Value)
                {
                    if ((++inspected % CancelCheckInterval) == 0 && ct.IsCancellationRequested)
                    {
                        result.Cancelled = true;
                        return result;
                    }

                    // 类型索引一旦学到，非 File 的句柄连复制都不必做
                    if (fileTypeIndex is not null && entry.ObjectTypeIndex != fileTypeIndex.Value) continue;

                    using SafeFileHandle duplicated = WinApi.DuplicateHandle(currentProcess, owner, entry);
                    if (duplicated.IsInvalid)
                    {
                        result.UnreadableHandles++;
                        continue;
                    }

                    string? type;
                    try
                    {
                        type = NtDll.GetHandleType(duplicated);
                    }
                    catch
                    {
                        result.UnreadableHandles++;
                        continue;
                    }

                    if (type is null)
                    {
                        result.UnreadableHandles++;
                        continue;
                    }

                    // **只能从 File 句柄学这个索引**：早期版本对所有非 File 句柄也执行 `??=`，
                    // 结果第一个被扫描到的 Event/Section 句柄把索引钉死，后续 File 句柄全被跳过，
                    // 表现为"任何盘都未发现占用"。
                    if (!string.Equals(type, "File", StringComparison.Ordinal)) continue;

                    fileTypeIndex = entry.ObjectTypeIndex;

                    // 命名管道、套接字、控制台在对象管理器里同样是 "File" 类型，但名字查询
                    // `NtQueryObject(ObjectNameInformation)` 对**同步命名管道**会永久阻塞在内核里
                    // （没有任何用户态手段能中断），而且它们的名字不可能是 \Device\HarddiskVolumeN\…。
                    // 因此只保留真实的磁盘文件。上游 Handle2 在这里只靠看门狗兜底，会留下一条永不返回的线程。
                    if (WinApi.GetFileType(duplicated) != WinApi.FileType.Disk) continue;

                    string? name;
                    try
                    {
                        // ObjectNameInformation 只对 File 调：同步管道会永久挂起
                        name = NtDll.GetHandleName(duplicated);
                    }
                    catch
                    {
                        result.UnreadableHandles++;
                        continue;
                    }

                    if (name is null) continue;
                    if (!MatchesAnyPrefix(name, devicePrefixes)) continue;

                    matched++;

                    // 同一程序可能对同一个目录开多个句柄：按显示路径去重、只累计次数
                    string display = ToDisplayPath(name, displayRoots);
                    perProcessPaths[display] = perProcessPaths.TryGetValue(display, out int n) ? n + 1 : 1;
                }

                if (matched > 0)
                {
                    int numericPid = (int)pid;
                    string name = ProcessUtils.GetProcessName(numericPid) ?? "?";
                    string? exePath = ProcessUtils.GetProcessExeFullName(owner);

                    // 同名 + 同目录 = 同一个程序；PID 不同只是多开
                    string key = name + "\0" + (exePath ?? string.Empty);
                    if (!byProgram.TryGetValue(key, out OccupantInfo? info))
                    {
                        info = new OccupantInfo { Name = name, ExePath = exePath };
                        byProgram[key] = info;
                        result.Processes.Add(info);
                    }

                    info.Pids.Add(numericPid);
                    info.HandleCount += matched;
                    foreach (KeyValuePair<string, int> kv in perProcessPaths)
                    {
                        info.Paths[kv.Key] = info.Paths.TryGetValue(kv.Key, out int old) ? old + kv.Value : kv.Value;
                    }
                }

                result.ScannedProcesses++;
                onProgress?.Invoke(result.ScannedProcesses, totalProcesses);
            }

            result.Processes.Sort((a, b) => b.HandleCount.CompareTo(a.HandleCount));
            return result;
        }

        /// <summary>
        /// `\Device\HarddiskVolume7\dir\file` → `E:\dir\file`。
        ///
        /// 只做前缀替换：没有挂载点的卷，显示根就是卷 GUID 路径，仍然可读；
        /// 若一个前缀都匹配不上（理论上不会），原样返回。
        /// </summary>
        private static string ToDisplayPath(string rawName, IReadOnlyDictionary<string, string> displayRoots)
        {
            string? bestPrefix = null;
            foreach (string prefix in displayRoots.Keys)
            {
                if (!rawName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (bestPrefix is null || prefix.Length > bestPrefix.Length) bestPrefix = prefix;
            }

            if (bestPrefix is null) return rawName;
            string relative = rawName.Substring(bestPrefix.Length);
            return displayRoots[bestPrefix] + relative;
        }

        private static bool MatchesAnyPrefix(string name, IReadOnlyList<string> prefixes)
        {
            foreach (string prefix in prefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
