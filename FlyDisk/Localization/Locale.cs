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
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using FlyDisk.Engine;

namespace FlyDisk.Localization
{
    /// <summary>
    /// 多语言文本层（见 后续待办.md 第八节「多语言支持」）。
    ///
    /// 三层结构里的**语言字典**这一层：每个语言一份 `键 → 文字` 的 JSON（`Localization\zh-CN.json` / `en-US.json`），
    /// 以 <c>EmbeddedResource</c> **内嵌进 exe**（资源名由 csproj 的 &lt;LogicalName&gt; 钉死为
    /// `FlyDisk.&lt;语言&gt;.json`，做法同嵌入字体）——本程序没有驱动之类的复杂产物，开发者自己编译即可，
    /// 不必把语言文件散落在输出目录里。
    ///
    /// 只负责"取词"：<see cref="T(string)"/> 与带参数的 <see cref="T(string, object?[])"/>。
    /// 控件与菜单的登记与刷新是另一层，见 <see cref="LocalizationManager"/>。
    /// </summary>
    public static class Locale
    {
        /// <summary>
        /// 默认语言（源语言）：中文。缺键回退链的中间一环（当前语言 → 默认语言 → 键名）。
        /// </summary>
        public const string DefaultLanguage = "zh-CN";

        /// <summary>支持的语言集合；配置里的 <c>Language</c> 只会是其中之一</summary>
        public static readonly string[] SupportedLanguages = { "zh-CN", "en-US" };

        /// <summary>已载入的语言字典（语言 → 键值表）。**进程生存期缓存**，切换语言不重复读资源。</summary>
        private static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.Ordinal);

        private static string _current = DefaultLanguage;

        /// <summary>当前生效的语言（永远是 <see cref="SupportedLanguages"/> 中的一项）</summary>
        public static string Current => _current;

        /// <summary>
        /// 检测系统界面语言，映射到本程序支持的语言：中文系统 → zh-CN，其余一律 → en-US。
        /// **只在首次打开时调用一次**（结果写入配置，之后以配置为准）。
        /// </summary>
        public static string DetectSystemLanguage()
        {
            string twoLetter = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return twoLetter.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
        }

        /// <summary>
        /// 把任意语言值规整到支持集合：以 <c>zh</c> 开头 → zh-CN，以 <c>en</c> 开头 → en-US，其余 → 默认语言。
        /// </summary>
        public static string Normalize(string? language)
        {
            if (string.IsNullOrWhiteSpace(language)) return DefaultLanguage;
            if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
            if (language.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en-US";
            return DefaultLanguage;
        }

        /// <summary>设置当前语言（内部会规整；只改取词，不触发界面刷新——刷新由 <see cref="LocalizationManager"/> 负责）</summary>
        public static void SetLanguage(string? language) => _current = Normalize(language);

        /// <summary>
        /// 取词。**缺键不静默兜底**：当前语言 → 默认语言 → 键名本身。
        /// 直接把键名显示到界面上，方便开发者一眼发现漏翻（见 后续待办.md 第八节第 7 条）。
        /// </summary>
        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            if (Lookup(_current, key) is { } text) return text;
            if (!string.Equals(_current, DefaultLanguage, StringComparison.OrdinalIgnoreCase)
                && Lookup(DefaultLanguage, key) is { } fallback)
            {
                return fallback;
            }
            return key;
        }

        /// <summary>
        /// 带参数的取词：模板用 `string.Format` 的**索引制**占位符 `{0}`（见 后续待办.md 第八节第 5 条，
        /// 一条键 = 一整句整译，绝不拆成"前缀键 + 变量 + 后缀键"）。
        ///
        /// **数字类参数传入前请先按文化格式化**（如 `value.ToString("N0")`），否则长数字没有千位分隔。
        /// </summary>
        public static string T(string key, params object?[] args)
        {
            string template = T(key);
            return args.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, args);
        }

        private static string? Lookup(string language, string key)
        {
            Dictionary<string, string> dict = GetDictionary(language);
            return dict.TryGetValue(key, out string? value) ? value : null;
        }

        private static Dictionary<string, string> GetDictionary(string language)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(language, out Dictionary<string, string>? cached)) return cached;

                Dictionary<string, string> loaded = Load(language);
                Cache[language] = loaded;
                return loaded;
            }
        }

        private static Dictionary<string, string> Load(string language)
        {
            string resource = $"FlyDisk.{language}.json";
            try
            {
                using Stream? stream = typeof(Locale).Assembly.GetManifestResourceStream(resource);
                if (stream == null)
                {
                    LogService.DebugFile($"语言资源缺失：{resource}");
                    return new Dictionary<string, string>(StringComparer.Ordinal);
                }

                using var reader = new StreamReader(stream, Encoding.UTF8);
                string json = reader.ReadToEnd();
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                       ?? new Dictionary<string, string>(StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                // 读不出来不能让程序起不来：退化成空字典 ⇒ 界面按"缺键"规则显示键名，问题一眼可见
                LogService.DebugFile($"语言资源装载失败（{resource}）：{ex.Message}");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
        }
    }
}
