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
        /// 用系统默认程序打开文档（打开前先刷一份最新的）。返回是否成功唤起；
        /// 失败由调用方提示用户（这里只记日志）。
        /// </summary>
        public static bool Open()
        {
            EnsureExtracted();
            try
            {
                Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true });
                return true;
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"帮助文档：打开失败：{ex.Message}");
                return false;
            }
        }
    }
}
