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
using FlyDisk.Models;

namespace FlyDisk.Engine
{
    /// <summary>
    /// 诊断日志：**只落盘**（`%ProgramData%\FlyDisk\debug-security.log`）。
    ///
    /// 原「开发日志」窗口（以及喂它的那条进程内展示型日志通道 `OnLog` / `DevLog` / `Info`）已删除。
    /// 分层原则：**用户可见的过程信息一律由界面层自己说**（`Form1.Log` → 主界面日志框、状态栏、弹窗），
    /// 引擎只把诊断细节说给"落盘的账本"听。混在一条通道上的结果是逐笔细节把真正的结论淹掉——
    /// 而用户要的恰恰只是"已配对 DESKTOP-ABC"这样一句话。
    /// </summary>
    public static class LogService
    {
        #region 安全诊断文件日志

        private static readonly object _debugFileLock = new object();
        private const long DebugFileRotateBytes = 8 * 1024 * 1024;

        /// <summary>安全诊断日志文件路径（位于 %ProgramData%\FlyDisk）</summary>
        public static readonly string DebugLogFilePath = Path.Combine(ServiceConstants.DataDirectory, "debug-security.log");

        /// <summary>
        /// 逐笔高频痕迹是否落盘（由启动时按 config.json 的 TraceFileLogging 设置）。
        /// false（默认）时只写"失败/探针"行——成功路径的逐笔痕迹跳过，避免给每笔操作
        /// 追加一次同步磁盘写（见 未解决的疑点.md TD-14）。
        /// </summary>
        public static bool TraceFileLoggingEnabled { get; set; }

        /// <summary>
        /// 高频"成功路径"痕迹的前缀表：**只有**这些前缀的行受 <see cref="TraceFileLoggingEnabled"/> 控制，
        /// 其余行（失败、异常、一次性事件）始终落盘。新增痕迹点时，若它位于每笔操作的热路径上，
        /// 请把前缀登记到这里——这是"开关"作用的唯一判定处。
        ///
        /// 阶段二只有一处高频痕迹：L1 的逐轮淘汰摘要（内存吃紧时每几百块回填就打印一轮）。
        /// L2 的痕迹（账本落盘 / 载入 / 尺寸校正 / 容器重建）**全是罕见事件**，一律不登记 ⇒ 始终落盘。
        /// 判定是"纯前缀匹配"（见 未解决的疑点.md TD-14）。
        /// </summary>
        private static readonly string[] TracePrefixes =
        {
            "Evict "
        };

        /// <summary>
        /// 写入一行安全诊断日志（线程安全，单行含毫秒时间戳）。
        /// 独立于展示日志：展示日志不落盘且滚动上限小，关键结论会被高频噪音淹没，故单独落盘供离线分析；
        /// 超过 8MB 滚动为 .old。逐笔成功痕迹受 <see cref="TraceFileLoggingEnabled"/> 控制，其余始终落盘。
        /// </summary>
        public static void DebugFile(string line)
        {
            if (!TraceFileLoggingEnabled && IsTraceLine(line))
                return;
            try
            {
                lock (_debugFileLock)
                {
                    Directory.CreateDirectory(ServiceConstants.DataDirectory);
                    string oldPath = Path.Combine(ServiceConstants.DataDirectory, "debug-security.old.log");
                    if (File.Exists(DebugLogFilePath) && new FileInfo(DebugLogFilePath).Length > DebugFileRotateBytes)
                        File.Move(DebugLogFilePath, oldPath, true);
                    File.AppendAllText(DebugLogFilePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
                }
            }
            catch
            {
                // 诊断日志失败不影响块设备主流程
            }
        }

        /// <summary>该行是否属于"逐笔高频成功路径痕迹"（见 TracePrefixes）</summary>
        private static bool IsTraceLine(string line)
        {
            foreach (string prefix in TracePrefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        /// <summary>写入会话分隔标记（启动/停止时调用，便于按运行切片分析）</summary>
        public static void DebugFileSession(string marker)
        {
            DebugFile($"========== {marker} ==========");
        }

        #endregion
    }
}
