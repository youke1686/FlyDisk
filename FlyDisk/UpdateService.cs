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
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using FlyDisk.Engine;

namespace FlyDisk
{
    /// <summary>
    /// 检查更新：**只比版本号是否不同，不比大小**。
    ///
    /// 启动时拉一次仓库里的项目文件 `FlyDisk/FlyDisk.csproj`（raw 直链），读出其中的
    /// <c>&lt;Version&gt;</c>，与当前 exe 的版本号比对——**不同**就由调用方在主界面日志区提一行；
    /// **相同、取不到、网络异常一律完全静默**（不弹窗、不写主界面日志，只在诊断日志里留一条失败痕迹）。
    ///
    /// **为什么用 csproj 而不是 GitHub Releases API**：仓库当前还没有 Release（API 会取空），
    /// 而 csproj 里的 <c>&lt;Version&gt;</c> 本来就是升版时的唯一改动点（见 csproj 注释），
    /// 即刻可用、且不吃 API 的匿名限流。代价：分支名与文件路径被写死在这里，
    /// 将来若改默认分支或搬动项目文件，这里会**静默失效**（只影响"检查更新"，不影响程序本身）。
    /// </summary>
    internal static class UpdateService
    {
        /// <summary>
        /// 版本号的权威来源：仓库 main 分支上的项目文件（raw 直链，不走 GitHub API）。
        /// </summary>
        private const string VersionFileUrl =
            "https://raw.githubusercontent.com/youke1686/FlyDisk/main/FlyDisk/FlyDisk.csproj";

        /// <summary>发现版本不同时，日志里让用户去的地方</summary>
        public const string RepoUrl = "https://github.com/youke1686/FlyDisk";

        /// <summary>整个检查的耗时上限：拉不动就当没这回事，绝不拖住启动</summary>
        private const int TimeoutSeconds = 8;

        /// <summary>进程内单例：<see cref="HttpClient"/> 设计上就该复用（反复 new 会耗尽端口）</summary>
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(TimeoutSeconds) };
            // GitHub 要求请求带 User-Agent；raw 走的是 CDN、通常不校验，但带上更稳妥。
            client.DefaultRequestHeaders.UserAgent.ParseAdd("FlyDisk");
            return client;
        }

        /// <summary>
        /// 当前运行的版本号（<see cref="Application.ProductVersion"/> 即 csproj 的 <c>&lt;Version&gt;</c>）。
        /// **必须归一化**：.NET 8+ 默认会在后面追加 <c>+&lt;git sha&gt;</c>，那是源码修订信息、不属于版本号。
        /// </summary>
        public static string CurrentVersion => Normalize(Application.ProductVersion);

        /// <summary>
        /// 拉远端版本号；**任何失败都返回 null**（调用方据此静默）。失败只落盘诊断日志。
        /// </summary>
        public static async Task<string?> FetchRemoteVersionAsync()
        {
            try
            {
                string csproj = await Http.GetStringAsync(VersionFileUrl).ConfigureAwait(false);

                // 精确匹配 `<Version>`：`<AssemblyVersion>` / `<FileVersion>` / `<LangVersion>` 都不会命中
                // ——它们 `<` 之后的第一个字符是别的字母，不满足紧随其后的 `Version>`。
                Match match = Regex.Match(csproj, @"<Version>\s*([^<\s]+)\s*</Version>");
                if (!match.Success)
                {
                    LogService.DebugFile("检查更新：远端 csproj 里找不到 <Version>");
                    return null;
                }

                return Normalize(match.Groups[1].Value);
            }
            catch (Exception ex)
            {
                LogService.DebugFile($"检查更新：拉取远端版本失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 归一化版本号：去首尾空白、去开头的 <c>v</c>、丢掉 <c>+</c> 之后的内容。
        /// </summary>
        private static string Normalize(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return string.Empty;

            string text = version.Trim();
            if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text.Substring(1);

            int plus = text.IndexOf('+');
            if (plus >= 0) text = text.Substring(0, plus);

            return text.Trim();
        }
    }
}
