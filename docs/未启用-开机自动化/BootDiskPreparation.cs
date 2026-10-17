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

// ⚠ 已归档、**未参与编译**（原因、已知问题与复活前提见同目录 README.md）。
// 本文件是「开机保持脱机」的参考实现：功能已从主程序摘除，这里只保留代码与设计。

using System;
using System.Collections.Generic;
using System.Threading;
using FlyDisk.Engine;
using FlyDisk.Localization;
using FlyDisk.Models;

namespace FlyDisk
{
    /// <summary>
    /// 「开机保持脱机」的**唯一**执行处（原 AGENTS.md §2.7；该节已随功能归档移除）。
    ///
    /// 调用方有两处，必须共用这一份逻辑：
    /// - `FlyDisk.exe --keep-offline-only`：开机触发、以 SYSTEM 运行、**不建界面**（见 <see cref="StartupService"/>）；
    /// - 主界面启动时（`Form1_Load`）：登录后再幂等确认一次——开机那条可能因为"盘还没枚举出来"而失败。
    ///
    /// 口径：
    /// 1. **只处理配置里被选中的盘**（`DiskConfig.StartupDiskIdentities`），不做任何"自动挑盘"；
    /// 2. **硬拦不可绕过**：动手前必须过 <see cref="PhysicalDiskHandle.DescribeTargetDiskBlockReason"/>——
    ///    整盘脱机系统盘会把 Windows 送走，脱机本程序所在盘会把本程序送走，
    ///    脱机**分页文件所在盘**会让内核换页失败、当场蓝屏 `KERNEL_DATA_INPAGE_ERROR 0x7A`。这几条与
    ///    引擎启动校验、选盘对话框共用同一份判据，**不在这里另写一套**；
    /// 3. 幂等：盘本就是脱机就直接算成功（脱机属性是 `persist=true` 写进系统的，重启后依然生效，
    ///    所以"第二次调用"通常什么都不做）；
    /// 4. 失败不抛：盘正被占用（卷句柄没放）是常见原因，重试几次后如实报告，交给界面/诊断日志。
    ///
    /// 并发：两个调用方可能在登录瞬间同时跑（开机任务还没退、登录任务已经起来），
    /// 用全局互斥体 <see cref="GlobalGateName"/> 串行；拿不到锁说明另一条流程正在做，直接返回——
    /// 脱机是幂等的，"别人刚做过"等价于"已经做完了"。
    /// </summary>
    internal static class BootDiskPreparation
    {
        /// <summary>跨会话的互斥体名（两个调用方分属不同会话：SYSTEM 会话 + 用户会话）</summary>
        private const string GlobalGateName = @"Global\FlyDisk.KeepOffline";

        /// <summary>等锁上限：另一条流程通常几秒内做完（枚举 + 脱机 + 重试）</summary>
        private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(60);

        /// <summary>单块盘脱机的重试次数（含首次）与间隔：盘刚被"释放占用"时有几百毫秒的窗口</summary>
        private const int OfflineAttempts = 3;
        private const int OfflineRetryDelayMs = 2000;

        /// <summary>
        /// 把配置里选中的盘全部确保为**脱机**，返回逐盘结果（已是本地化文本，调用方直接显示/落盘）。
        /// </summary>
        /// <param name="config">配置（读 <see cref="DiskConfig.StartupDiskIdentities"/>）</param>
        /// <param name="anyFailed">是否有盘没能脱机（含被硬拦拦下）；调用方据此决定退出码 / 是否提示</param>
        public static List<string> Prepare(DiskConfig config, out bool anyFailed)
        {
            var lines = new List<string>();
            anyFailed = false;

            if (config.StartupDiskIdentities.Count == 0)
            {
                lines.Add(Locale.T("boot.offline.none"));
                return lines;
            }

            bool acquired;
            using var gate = new Mutex(initiallyOwned: false, GlobalGateName);
            try
            {
                acquired = gate.WaitOne(GateTimeout);
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者进程死了（强杀/断电）：互斥体归我们，继续
                acquired = true;
            }
            catch (Exception ex)
            {
                // 建不出全局互斥体（极少见）不该让功能失效：只记日志，照常往下做
                LogService.DebugFile($"保持脱机：全局互斥体不可用，继续执行（{ex.Message}）");
                acquired = true;
            }

            if (!acquired)
            {
                lines.Add(Locale.T("boot.offline.busy"));
                return lines;
            }

            try
            {
                return PrepareCore(config, lines, out anyFailed);
            }
            finally
            {
                try { gate.ReleaseMutex(); } catch { /* 未持有或已释放：忽略 */ }
            }
        }

        private static List<string> PrepareCore(DiskConfig config, List<string> lines, out bool anyFailed)
        {
            anyFailed = false;

            List<PhysicalDiskInfo> disks;
            try
            {
                disks = PhysicalDiskHandle.Enumerate();
            }
            catch (Exception ex)
            {
                anyFailed = true;
                lines.Add(Locale.T("boot.offline.enumerateFailed", ex.Message));
                LogService.DebugFile($"保持脱机：枚举物理盘失败：{ex.Message}");
                return lines;
            }

            lines.Add(Locale.T("boot.offline.headline", config.StartupDiskIdentities.Count));

            foreach (string identity in config.StartupDiskIdentities)
            {
                int diskNumber = PhysicalDiskHandle.ResolveDiskNumber(disks, identity);
                if (diskNumber < 0)
                {
                    anyFailed = true;
                    lines.Add(Locale.T("boot.offline.missing", identity));
                    continue;
                }

                // ① 硬拦：与选盘对话框 / 引擎启动校验 / L2 校验共用同一份判据（不另写一套）
                string blocked = PhysicalDiskHandle.DescribeTargetDiskBlockReason(diskNumber);
                if (blocked.Length > 0)
                {
                    anyFailed = true;
                    lines.Add(Locale.T("boot.offline.blocked", diskNumber, blocked));
                    LogService.DebugFile($"保持脱机：磁盘 {diskNumber} 被硬拦，拒绝脱机 —— {blocked}");
                    continue;
                }

                // ② 已经脱机就不用动（persist 属性自上次运行起一直有效）
                if (PhysicalDiskHandle.TryGetAttributes(diskNumber, out bool offline, out _) && offline)
                {
                    lines.Add(Locale.T("boot.offline.already", DescribeDisk(disks, diskNumber)));
                    continue;
                }

                // ③ 脱机（失败重试：盘可能正被某个程序短暂占用）
                string model = DescribeDisk(disks, diskNumber);
                bool ok = false;
                string error = string.Empty;
                for (int attempt = 1; attempt <= OfflineAttempts && !ok; attempt++)
                {
                    ok = PhysicalDiskHandle.TryOffline(diskNumber, out error);
                    if (!ok && attempt < OfflineAttempts) Thread.Sleep(OfflineRetryDelayMs);
                }

                if (ok)
                {
                    lines.Add(Locale.T("boot.offline.done", model));
                    LogService.DebugFile($"保持脱机：磁盘 {diskNumber}（{model}）已保持脱机");
                }
                else
                {
                    anyFailed = true;
                    lines.Add(Locale.T("boot.offline.failed", model, error));
                    LogService.DebugFile($"保持脱机：磁盘 {diskNumber}（{model}）脱机失败：{error}");
                }
            }

            // 有失败就给一句"下一步怎么办"：脱机失败最常见的原因是盘正被别的程序读写
            if (anyFailed) lines.Add(Locale.T("boot.offline.failedHint"));
            lines.Add(anyFailed ? Locale.T("boot.offline.tailFailed") : Locale.T("boot.offline.tailOk"));
            return lines;
        }

        /// <summary>展示用："磁盘 N（型号）"；型号取不到时只留盘号</summary>
        private static string DescribeDisk(IReadOnlyList<PhysicalDiskInfo> disks, int diskNumber)
        {
            foreach (PhysicalDiskInfo info in disks)
            {
                if (info.DiskNumber != diskNumber) continue;
                return info.Model.Length > 0
                    ? Locale.T("boot.offline.diskWithModel", diskNumber, info.Model)
                    : Locale.T("boot.offline.diskOnly", diskNumber);
            }
            return Locale.T("boot.offline.diskOnly", diskNumber);
        }
    }
}
