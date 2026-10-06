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
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using FlyDisk.Engine;
using FlyDisk.Models;

namespace FlyDisk
{
    /// <summary>
    /// 随附的帮助文档（`帮助文档.html`，中英双语在同一份里）：
    /// 以**嵌入资源**随 exe 携带（`FlyDisk.help.html`，见 csproj），启动时落一份到数据目录。
    ///
    /// **为什么不直接从 exe 里读**：这份文档是给用户自己看的，得有一个真实的文件路径
    /// （双击打开、发给别人、用浏览器收藏），嵌入资源取不到这种"可交给外壳的路径"。
    ///
    /// **落盘策略**：**每次启动都重写一遍**——落盘的这份始终等于当前 exe 里嵌的那份，
    /// 程序升级后不会留下一份过期的旧文档。用户要留自己的改动就别改这份，复制出去看。
    /// </summary>
    internal static class HelpDocument
    {
        /// <summary>嵌入资源名（LogicalName，与文件位置无关）</summary>
        private const string ResourceName = "FlyDisk.help.html";

        /// <summary>落盘后的文件名（数据目录下）</summary>
        private const string FileName = "帮助文档.html";

        /// <summary>落盘后的完整路径（<c>%ProgramData%\FlyDisk\帮助文档.html</c>）</summary>
        public static string FilePath => Path.Combine(ServiceConstants.DataDirectory, FileName);

        /// <summary>
        /// 把嵌入资源里的文档写一份到数据目录（**覆盖写**：每次启动都刷新成当前版本）。
        /// **启动时调用一次**；失败只记日志，不拦启动（文档看不见是小事，起不来才是大事）。
        /// </summary>
        public static void EnsureExtracted()
        {
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(ResourceName);
                if (stream is null)
                {
                    LogService.DebugFile($"帮助文档：嵌入资源 {ResourceName} 缺失，未落盘");
                    return;
                }

                Directory.CreateDirectory(ServiceConstants.DataDirectory);
                using var file = new FileStream(FilePath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(file);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"帮助文档：落盘失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 打开文档（打开前先刷一份最新的）。返回是否成功唤起；失败由调用方提示用户（这里只记日志）。
        ///
        /// **优先交给「默认浏览器」，而不是系统默认程序**：`UseShellExecute` 走的是 `.html` 的**文件关联**，
        /// 用户若是把 `.html` 关联给了编辑器之类的程序，文档要么开错地方、要么根本打不开。
        /// 默认浏览器取自 `http` **协议**的关联（这才是"上网用的那个程序"），拿 `.html` 文件喂给它即可。
        /// 解析不出来就退回系统默认程序（原行为），两条路都失败才返回 false。
        /// </summary>
        public static bool Open()
        {
            EnsureExtracted();

            // ① 默认浏览器（绕开 .html 文件关联）
            if (TryGetDefaultBrowser(out string browser) && TryStart(browser, FilePath)) return true;

            // ② 退回系统默认程序（按 .html 关联）——原行为
            if (TryStartShell(FilePath)) return true;

            return false;
        }

        /// <summary>
        /// 从注册表解析默认浏览器的可执行文件路径。解析不出来返回 false（调用方退回系统默认程序）。
        ///
        /// 两级查：
        /// ① `HKCU\...\UrlAssociations\http\UserChoice` 的 `ProgId` ＝ 用户在「默认应用」里选的浏览器
        ///    （如 `ChromeHTML` / `MSEdgeHTM` / `FirefoxURL-…`）；
        /// ② `HKCR\&lt;ProgId&gt;\shell\open\command` ＝ 它的打开命令，从里面剥出 exe 路径。
        /// </summary>
        private static bool TryGetDefaultBrowser(out string executable)
        {
            executable = string.Empty;
            try
            {
                using RegistryKey? choice = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
                string? progId = choice?.GetValue("ProgId") as string;
                if (string.IsNullOrWhiteSpace(progId)) return false;

                using RegistryKey? commandKey = Registry.ClassesRoot.OpenSubKey($@"{progId}\shell\open\command");
                return TryParseExecutable(commandKey?.GetValue(null) as string, out executable);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"帮助文档：解析默认浏览器失败：{ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 从 shell 打开命令里剥出可执行文件路径。命令有两种写法，都要认：
        /// 带引号的 `"C:\Program Files\…\chrome.exe" -- "%1"`，和不带引号的 `C:\…\iexplore.exe %1`。
        /// 路径必须真实存在才算数（注册表里可能留着一个已经卸载的程序）。
        /// </summary>
        private static bool TryParseExecutable(string? command, out string executable)
        {
            executable = string.Empty;
            if (string.IsNullOrWhiteSpace(command)) return false;

            string text = command.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = text.IndexOf('"', 1);
                if (end <= 1) return false;
                executable = text.Substring(1, end - 1);
            }
            else
            {
                int space = text.IndexOf(' ');
                executable = space < 0 ? text : text.Substring(0, space);
            }

            return executable.Length > 0 && File.Exists(executable);
        }

        /// <summary>用指定程序打开文档；失败只记日志（是否成功由返回值表达）。</summary>
        private static bool TryStart(string executable, string filePath)
        {
            try
            {
                // ArgumentList 会自动处理带空格 / 中文的路径，不用自己拼引号；
                // UseShellExecute=false 是必须的（要以"程序 + 参数"的形式直接起动）。
                var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
                startInfo.ArgumentList.Add(filePath);
                Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"帮助文档：用默认浏览器打开失败（{executable}）：{ex.Message}");
                return false;
            }
        }

        /// <summary>退回系统默认程序（按 .html 文件关联打开）——原来的行为。</summary>
        private static bool TryStartShell(string filePath)
        {
            try
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"帮助文档：交给系统默认程序打开失败：{ex.Message}");
                return false;
            }
        }
    }
}
