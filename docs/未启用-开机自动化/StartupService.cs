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
// 本文件是「开机与自动化」的参考实现：功能已从主程序摘除，这里只保留代码与设计。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using FlyDisk.Engine;
using FlyDisk.Localization;

namespace FlyDisk
{
    /// <summary>
    /// 开机自启与「开机保持脱机」的**计划任务**管理（原 AGENTS.md §2.7；该节已随功能归档移除）。
    ///
    /// **为什么不用注册表 Run 键**：本程序是 <c>requireAdministrator</c>（整盘脱机 + 独占 PhysicalDrive 必需），
    /// Run 键在用户登录时启动需要提升权限 ⇒ 每次开机都会弹一次 UAC（或被系统静默拒绝）。
    /// 计划任务可以声明"以最高权限运行"，由系统直接以提升权限拉起，**不弹 UAC**。
    ///
    /// **两条任务，各管一件事**（"无需登录也要脱机" + "登录后要看到界面"不可能由同一个进程完成：
    /// 以 SYSTEM 在无人登录时启动的进程，它的窗口不会出现在任何用户桌面上）：
    ///
    /// | 任务 | 触发器 | 身份 | 命令 | 作用 |
    /// | --- | --- | --- | --- | --- |
    /// | <see cref="KeepOfflineTaskName"/> | 开机（<c>BootTrigger</c>，延迟 1 分钟） | <c>SYSTEM</c> | `FlyDisk.exe --keep-offline-only` | 把「开机目标盘」脱机后**立刻退出**，不显示界面 |
    /// | <see cref="StartupTaskName"/> | 用户登录（<c>LogonTrigger</c>） | 创建任务时的当前用户 | `FlyDisk.exe` | 带界面启动本程序（登陆后可见），并在启动期再幂等确认一次脱机 |
    ///
    /// 两条都设 <c>MultipleInstancesPolicy = IgnoreNew</c>：登录触发时若开机那条还在跑，直接忽略，
    /// 不会出现两个实例（计划任务层面就挡住了，比在程序里加互斥体更早）。
    ///
    /// 「保持脱机」能跨进程生效的原因：脱机属性是 <c>persist = true</c> 写进系统的，
    /// 进程退出后盘仍然脱机（与「停止加速后盘保持脱机」同一机制，见 <see cref="PhysicalDiskHandle"/> 类注释）。
    /// </summary>
    internal static class StartupService
    {
        /// <summary>开机（无需登录）保持脱机的任务名（带顶级文件夹，避免与用户自建任务混在一起）</summary>
        public const string KeepOfflineTaskName = @"\FlyDisk\KeepOffline";

        /// <summary>登录后带界面启动本程序的任务名</summary>
        public const string StartupTaskName = @"\FlyDisk\AutoStart";

        /// <summary>命令行开关：只做「保持脱机」，不建界面（见 <see cref="Program"/>）</summary>
        public const string KeepOfflineOnlySwitch = "--keep-offline-only";

        /// <summary>开机任务相对系统启动的延迟：留出存储枚举 / 卷管理器就绪的时间</summary>
        private const string BootTriggerDelay = "PT1M";

        /// <summary>schtasks 的单次调用超时（毫秒）：任务计划服务没响应时不要卡住界面</summary>
        private const int SchtasksTimeoutMs = 30000;

        /// <summary>Windows 计划任务 XML 的命名空间（读写共用这一处，避免两处写错导致查询落空）</summary>
        private static readonly XNamespace TaskNs =
            "http://schemas.microsoft.com/windows/2004/02/mit/task";

        /// <summary>
        /// 【设置窗口保存时】按开关**强制**创建或删除两条任务。
        /// 失败**不吞**：返回 false 并把可读原因交给调用方弹窗（与 <see cref="ConfigService.Save"/> 同口径）。
        /// </summary>
        public static bool Apply(bool runAtStartup, bool keepOfflineOnBoot, out string error)
            => Sync(runAtStartup, keepOfflineOnBoot, force: true, out error);

        /// <summary>
        /// 【每次程序启动时】校正任务状态：开关为开而任务缺失 / 指向的不是当前 exe（exe 被移动或改名）⇒ 重建；
        /// 开关为关而任务还在 ⇒ 删除。已一致时**不调用 schtasks**，所以正常启动没有额外开销。
        /// </summary>
        public static bool Ensure(bool runAtStartup, bool keepOfflineOnBoot, out string error)
            => Sync(runAtStartup, keepOfflineOnBoot, force: false, out error);

        private static bool Sync(bool runAtStartup, bool keepOfflineOnBoot, bool force, out string error)
        {
            error = string.Empty;
            string exePath = CurrentExePath;

            if (!SyncOfflineTask(keepOfflineOnBoot, force, exePath, out string offlineError)) error = offlineError;
            if (!SyncStartupTask(runAtStartup, force, exePath, out string startupError))
            {
                error = error.Length == 0 ? startupError : error + Environment.NewLine + startupError;
            }
            return error.Length == 0;
        }

        /// <summary>当前 exe 的完整路径（计划任务里写的必须是它）</summary>
        private static string CurrentExePath
        {
            get
            {
                // ProcessPath 比 Assembly.Location 可靠：单文件发布（PublishSingleFile）下后者是空串
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) return path;
                return Path.Combine(AppContext.BaseDirectory, "FlyDisk.exe");
            }
        }

        private static bool SyncOfflineTask(bool wanted, bool force, string exePath, out string error)
        {
            error = string.Empty;
            bool exists = TaskExists(KeepOfflineTaskName);
            if (!wanted)
            {
                if (!exists) return true;   // 本就不该有，且没有：无需动
                return DeleteTask(KeepOfflineTaskName, out error);
            }

            if (!force && exists &&
                TaskDefinitionMatches(KeepOfflineTaskName, keepOffline: true, exePath, out _)) return true;
            return CreateTask(KeepOfflineTaskName, BuildOfflineTaskXml(exePath), out error);
        }

        private static bool SyncStartupTask(bool wanted, bool force, string exePath, out string error)
        {
            error = string.Empty;
            bool exists = TaskExists(StartupTaskName);
            if (!wanted)
            {
                if (!exists) return true;
                return DeleteTask(StartupTaskName, out error);
            }

            if (!force && exists &&
                TaskDefinitionMatches(StartupTaskName, keepOffline: false, exePath, out _)) return true;
            return CreateTask(StartupTaskName, BuildStartupTaskXml(exePath), out error);
        }

        /// <summary>
        /// 开机任务：<c>SYSTEM</c> + 开机后 1 分钟 + 只跑 <c>--keep-offline-only</c>。
        /// 用 SYSTEM 是"无需登录"的必要条件；该分支不建窗体，所以不会出现"窗口在会话 0 里没人看得见"的问题。
        /// Principal 必须显式写 <c>ServiceAccount</c>：缺省是 <c>InteractiveToken</c>，对 <c>S-1-5-18</c> 无效、任务起不来。
        /// </summary>
        private static XDocument BuildOfflineTaskXml(string exePath) => new(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(TaskNs + "Task",
                new XAttribute("version", "1.2"),
                new XElement(TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Author", "FlyDisk"),
                    new XElement(TaskNs + "Description", "FlyDisk：开机把选中的硬盘保持脱机（无需登录；做完即退出）")),
                new XElement(TaskNs + "Triggers",
                    new XElement(TaskNs + "BootTrigger",
                        new XElement(TaskNs + "Enabled", "true"),
                        new XElement(TaskNs + "Delay", BootTriggerDelay))),
                new XElement(TaskNs + "Principals",
                    new XElement(TaskNs + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNs + "UserId", "S-1-5-18"),
                        new XElement(TaskNs + "LogonType", "ServiceAccount"),
                        new XElement(TaskNs + "RunLevel", "HighestAvailable"))),
                BuildCommonSettings("PT1H"),
                new XElement(TaskNs + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(TaskNs + "Exec",
                        new XElement(TaskNs + "Command", exePath),
                        new XElement(TaskNs + "Arguments", KeepOfflineOnlySwitch),
                        new XElement(TaskNs + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? string.Empty)))));

        /// <summary>两条任务共用的 Settings 段（只差 ExecutionTimeLimit：开机任务是有限时，登录任务是无限时）</summary>
        private static XElement BuildCommonSettings(string executionTimeLimit) => new(TaskNs + "Settings",
            new XElement(TaskNs + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(TaskNs + "DisallowStartIfOnBatteries", "false"),
            new XElement(TaskNs + "StopIfGoingOnBatteries", "false"),
            new XElement(TaskNs + "AllowHardTerminate", "true"),
            new XElement(TaskNs + "StartWhenAvailable", "true"),
            new XElement(TaskNs + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(TaskNs + "IdleSettings",
                new XElement(TaskNs + "StopOnIdleEnd", "false"),
                new XElement(TaskNs + "RestartOnIdle", "false")),
            new XElement(TaskNs + "AllowStartOnDemand", "true"),
            new XElement(TaskNs + "Enabled", "true"),
            new XElement(TaskNs + "Hidden", "false"),
            new XElement(TaskNs + "RunOnlyIfIdle", "false"),
            new XElement(TaskNs + "WakeToRun", "false"),
            new XElement(TaskNs + "ExecutionTimeLimit", executionTimeLimit),
            new XElement(TaskNs + "Priority", "7"));

        /// <summary>
        /// 登录任务：<c>InteractiveToken</c> + 当前用户 + 最高权限 ⇒ 登录即起、界面正常显示、不弹 UAC。
        /// 这里不写 <c>UserId</c> 的 SID 而是写"域\用户名"，因为任务要跑在**用户自己的会话**里。
        /// </summary>
        private static XDocument BuildStartupTaskXml(string exePath) => new(
            new XDeclaration("1.0", "utf-16", null),
            new XElement(TaskNs + "Task",
                new XAttribute("version", "1.2"),
                new XElement(TaskNs + "RegistrationInfo",
                    new XElement(TaskNs + "Author", "FlyDisk"),
                    new XElement(TaskNs + "Description", "FlyDisk：登录后带界面启动本程序（最高权限，无 UAC 提示）")),
                new XElement(TaskNs + "Triggers",
                    new XElement(TaskNs + "LogonTrigger",
                        new XElement(TaskNs + "Enabled", "true"),
                        new XElement(TaskNs + "UserId", CurrentUserName))),
                new XElement(TaskNs + "Principals",
                    new XElement(TaskNs + "Principal",
                        new XAttribute("id", "Author"),
                        new XElement(TaskNs + "UserId", CurrentUserName),
                        new XElement(TaskNs + "LogonType", "InteractiveToken"),
                        new XElement(TaskNs + "RunLevel", "HighestAvailable"))),
                BuildCommonSettings("PT0S"),
                new XElement(TaskNs + "Actions",
                    new XAttribute("Context", "Author"),
                    new XElement(TaskNs + "Exec",
                        new XElement(TaskNs + "Command", exePath),
                        new XElement(TaskNs + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? string.Empty)))));

        /// <summary>当前用户名（`域\用户`）；取不到时用 <c>Environment.UserName</c>（任务计划也接受纯用户名）</summary>
        private static string CurrentUserName
        {
            get
            {
                string domain = Environment.UserDomainName;
                string user = Environment.UserName;
                return string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
            }
        }

        private static bool TaskExists(string taskName)
        {
            int rc = RunSchtasks(new[] { "/Query", "/TN", taskName }, out _, out _);
            return rc == 0;
        }

        /// <summary>
        /// 已存在的任务定义是否与"我们期望的定义"一致（启动期幂等校正的判据）。
        ///
        /// 判据不是"任务里有没有当前 exe 路径"这种文本子串匹配，而是**投影比较**：
        /// 只取真正影响行为的字段（动作 / 身份 / 触发器关键项 / 关键 Settings），
        /// 与同一生成函数产出的期望定义逐项比对；这样"用户手工改了触发器或参数"也能被发现。
        ///
        /// 读侧走**原始字节**（<see cref="XDocument.Load(Stream)"/>）：`schtasks /Query /XML` 的输出编码
        /// 由 XML 声明决定，先解码成 string 会在中文路径上烂掉。
        /// 任何解析失败都判**不一致**（保守方向：重建）。
        /// </summary>
        private static bool TaskDefinitionMatches(string taskName, bool keepOffline, string exePath, out string reason)
        {
            reason = string.Empty;

            int rc = RunSchtasks(new[] { "/Query", "/TN", taskName, "/XML" }, out byte[] bytes, out _);
            if (rc != 0)
            {
                reason = $"查询任务失败（rc={rc}）";
                return false;
            }

            XDocument actual;
            try
            {
                using var stream = new MemoryStream(bytes);
                actual = XDocument.Load(stream);
            }
            catch (Exception ex)
            {
                // 解析失败 ⇒ 无法确认一致 ⇒ 重建（保守方向）
                reason = $"任务 XML 解析失败：{ex.Message}";
                return false;
            }

            XDocument expected = keepOffline ? BuildOfflineTaskXml(exePath) : BuildStartupTaskXml(exePath);
            string actualText = ProjectTaskDefinition(actual, keepOffline);
            string expectedText = ProjectTaskDefinition(expected, keepOffline);
            if (string.Equals(actualText, expectedText, StringComparison.Ordinal)) return true;

            reason = "任务定义与期望不一致";
            return false;
        }

        /// <summary>
        /// 把任务定义投影成一段**规范化文本**（只含我们真正在意、且 Windows 会原样保留的字段）。
        ///
        /// **故意不投影**：RegistrationInfo（Windows 会补 Date/URI/SecurityDescriptor 等）、
        /// 其余 Settings 子项、属性顺序与空白 —— 这些一比就永远不相等，等于每次启动都重建。
        /// </summary>
        private static string ProjectTaskDefinition(XDocument doc, bool keepOffline)
        {
            XElement root = doc.Root ?? new XElement(TaskNs + "Task");
            XElement? exec = root.Element(TaskNs + "Actions")?.Elements(TaskNs + "Exec").FirstOrDefault();
            XElement? principal = root.Element(TaskNs + "Principals")?.Elements(TaskNs + "Principal")
                .FirstOrDefault(p => string.Equals((string?)p.Attribute("id"), "Author", StringComparison.OrdinalIgnoreCase));
            XElement? settings = root.Element(TaskNs + "Settings");
            XElement? trigger = keepOffline
                ? root.Element(TaskNs + "Triggers")?.Element(TaskNs + "BootTrigger")
                : root.Element(TaskNs + "Triggers")?.Element(TaskNs + "LogonTrigger");

            var lines = new List<string>
            {
                "command=" + Value(exec?.Element(TaskNs + "Command")),
                "arguments=" + Value(exec?.Element(TaskNs + "Arguments")),
                "workingDirectory=" + Value(exec?.Element(TaskNs + "WorkingDirectory")),
                "principal.userId=" + NormalizeUser(Value(principal?.Element(TaskNs + "UserId"))),
                "principal.logonType=" + Value(principal?.Element(TaskNs + "LogonType")),
                "principal.runLevel=" + Value(principal?.Element(TaskNs + "RunLevel")),
                "trigger=" + (trigger == null ? "(missing)" : keepOffline ? "boot" : "logon"),
                "settings.multipleInstancesPolicy=" + Value(settings?.Element(TaskNs + "MultipleInstancesPolicy")),
                "settings.executionTimeLimit=" + Value(settings?.Element(TaskNs + "ExecutionTimeLimit")),
            };

            if (trigger != null)
            {
                lines.Add("trigger.enabled=" + Value(trigger.Element(TaskNs + "Enabled"), "true"));
                lines.Add(keepOffline
                    ? "trigger.delay=" + Value(trigger.Element(TaskNs + "Delay"))
                    : "trigger.userId=" + NormalizeUser(Value(trigger.Element(TaskNs + "UserId"))));
            }

            return string.Join("\n", lines);
        }

        /// <summary>取元素的文本（缺失时用 <paramref name="missing"/>），统一 Trim</summary>
        private static string Value(XElement? element, string missing = "")
            => element == null ? missing : (element.Value ?? string.Empty).Trim();

        /// <summary>
        /// 用户标识归一：Windows 回读时可能把"域\用户"规范化成 SID，两者都翻译成 SID 再比。
        /// 翻译失败（域不可达 / 格式特殊）就退回原样比较，绝不让判据抛出去。
        /// </summary>
        private static string NormalizeUser(string raw)
        {
            raw = (raw ?? string.Empty).Trim();
            if (raw.Length == 0) return string.Empty;

            try
            {
                SecurityIdentifier sid = raw.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                    ? new SecurityIdentifier(raw)
                    : (SecurityIdentifier)new NTAccount(raw).Translate(typeof(SecurityIdentifier));
                return sid.Value;
            }
            catch
            {
                return raw;
            }
        }

        private static bool CreateTask(string taskName, XDocument definition, out string error)
        {
            error = string.Empty;

            try
            {
                // **写在当前用户的临时目录**，而不是 %ProgramData%：
                // 提升权限的进程"从共享可写目录读一个文件来执行"是安全上公认的敏感模式
                //（ProgramData 配置劫持 / TOCTOU）；用户临时目录对其他账户不可写。
                string directory = Path.Combine(Path.GetTempPath(), "FlyDisk");
                Directory.CreateDirectory(directory);
                string file = Path.Combine(directory, taskName.TrimStart('\\').Replace('\\', '_') + ".xml");

                try
                {
                    // 声明的 encoding 与落盘编码都来自 XmlWriter（UTF-16 + BOM），二者自动一致
                    var writerSettings = new XmlWriterSettings { Encoding = Encoding.Unicode, Indent = true };
                    using (var writer = XmlWriter.Create(file, writerSettings)) definition.Save(writer);

                    int rc = RunSchtasks(new[] { "/Create", "/TN", taskName, "/XML", file, "/F" },
                        out _, out string stderr);
                    if (rc == 0) return true;

                    error = Locale.T("startup.task.createFailed", taskName, rc, stderr.Trim());
                    LogService.DebugFile($"计划任务创建失败：{taskName} rc={rc} {stderr.Trim()}");
                    return false;
                }
                finally
                {
                    // schtasks 已经读完这个文件了；用完即删，别留临时垃圾
                    try { File.Delete(file); } catch { /* 删不掉无害 */ }
                }
            }
            catch (Exception ex)
            {
                error = Locale.T("startup.task.createFailed", taskName, -1, ex.Message);
                LogService.DebugFile($"计划任务创建异常：{taskName} {ex.Message}");
                return false;
            }
        }

        private static bool DeleteTask(string taskName, out string error)
        {
            error = string.Empty;
            int rc = RunSchtasks(new[] { "/Delete", "/TN", taskName, "/F" }, out _, out string stderr);
            if (rc == 0 || !TaskExists(taskName)) return true;   // 已经没了也算成功（幂等）

            error = Locale.T("startup.task.deleteFailed", taskName, rc, stderr.Trim());
            LogService.DebugFile($"计划任务删除失败：{taskName} rc={rc} {stderr.Trim()}");
            return false;
        }

        /// <summary>
        /// 调一次 schtasks.exe，返回退出码（启动失败/超时返回 -1）。输出走重定向，不弹黑窗。
        ///
        /// **stdout 收原始字节、stderr 收文本，两者并发读**：
        /// · 收字节是为了让 <c>/Query /XML</c> 的内容交给 XML 解析器按声明判编码
        ///   （先解码成 string 会在中文路径上烂掉）；
        /// · 并发读是为了消除"先读完 stdout 再读 stderr"的管道缓冲区互等；
        /// · 只有并发读之后，<see cref="Process.WaitForExit(int)"/> 的超时才真正生效
        ///   （原先先 <c>ReadToEnd</c> 再等，超时分支是死代码，schtasks 卡住时会无限阻塞）。
        /// </summary>
        private static int RunSchtasks(IReadOnlyList<string> arguments, out byte[] stdout, out string stderr)
        {
            stdout = Array.Empty<byte>();
            stderr = string.Empty;
            try
            {
                var startInfo = new ProcessStartInfo("schtasks.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

                using Process? process = Process.Start(startInfo);
                if (process == null) return -1;

                Task<byte[]> stdoutTask = Task.Run(async () =>
                {
                    using var buffer = new MemoryStream();
                    await process.StandardOutput.BaseStream.CopyToAsync(buffer).ConfigureAwait(false);
                    return buffer.ToArray();
                });
                Task<string> stderrTask = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit(SchtasksTimeoutMs))
                {
                    try { process.Kill(entireProcessTree: true); } catch { /* 杀不掉只能作罢 */ }
                    try { process.WaitForExit(2000); } catch { /* 忽略 */ }
                    // 给两个读取任务一个短暂收尾：Kill 之后管道会关闭，它们通常会自然结束
                    try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 2000); } catch { /* 读取失败/超时都丢弃 */ }
                    LogService.DebugFile($"schtasks 超时（{SchtasksTimeoutMs} ms）：{string.Join(' ', arguments)}");
                    return -1;
                }

                stdout = stdoutTask.GetAwaiter().GetResult();
                stderr = stderrTask.GetAwaiter().GetResult();
                return process.ExitCode;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"schtasks 调用失败：{string.Join(' ', arguments)} ⇒ {ex.Message}");
                return -1;
            }
        }
    }
}
