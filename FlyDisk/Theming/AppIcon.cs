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
using System.Drawing;
using System.IO;
using System.Reflection;

namespace FlyDisk.Theming
{
    /// <summary>
    /// 应用图标（浅色 / 深色两枚）的**进程级单例**（见 深色主题.md）。
    ///
    /// 两枚 ico 以嵌入资源形式随 exe 携带（`FlyDisk.icon-light.ico` / `FlyDisk.icon-dark.ico`，
    /// 见 csproj）。按启动时定下的主题<see cref="ThemeManager.IsDark"/>挑一枚返回：
    /// 标题栏、任务栏、托盘都取这个 <see cref="Current"/>。
    ///
    /// **为什么做成单例**：`Icon` 是 GDI 句柄的持有者。若每个窗体各 `new` 一次，句柄会随窗体数线性增长；
    /// 这里两枚各构造一次、全程序共用，`Form.Dispose` 本来也不回收 `Form.Icon`，无需逐次释放。
    ///
    /// **与 exe 静态图标的关系**：exe 里 `<ApplicationIcon>` 嵌的那一枚是**编译期固定**的，
    /// 不随主题变——它只在"资源管理器里直接看 exe / 快捷方式尚未运行过"时被系统取用。
    /// 运行中的窗口图标走这里，能跟随主题。
    /// </summary>
    public static class AppIcon
    {
        private const string LightResource = "FlyDisk.icon-light.ico";
        private const string DarkResource = "FlyDisk.icon-dark.ico";

        // Lazy：没用到就不构造（浅色系统整个进程不会碰深色那枚）。
        private static readonly Lazy<Icon> _light = new(() => Load(dark: false));
        private static readonly Lazy<Icon> _dark = new(() => Load(dark: true));

        /// <summary>当前主题对应的应用图标（深色取 dark，浅色取 light）</summary>
        public static Icon Current => ThemeManager.IsDark ? _dark.Value : _light.Value;

        /// <summary>浅色那枚（<see cref="ShortcutIcon"/> 落盘时按主题取其一，故这里也暴露出来）</summary>
        public static Icon Light => _light.Value;

        /// <summary>深色那枚</summary>
        public static Icon Dark => _dark.Value;

        /// <summary>按主题取"该用的那一枚"（与 <see cref="Current"/> 同义，供落盘方显式表达意图）</summary>
        public static Icon For(bool dark) => dark ? _dark.Value : _light.Value;

        /// <summary>
        /// 取某一枚 ico 的**原始字节**。`ShortcutIcon` 要把图标落盘给桌面快捷方式引用，
        /// 必须原样写出（多尺寸都在里面）；`Icon.Save` 只会写出单个尺寸，所以这里单独暴露字节。
        /// 读不到返回 <c>null</c>。
        /// </summary>
        public static byte[]? ReadBytes(bool dark)
        {
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(dark ? DarkResource : LightResource);
                if (stream is null) return null;

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// 从嵌入资源读一枚 ico。**先整段读进字节再 Clone**：`Icon` 在某些实现下会持有传入流，
        /// 早关流会让图标后续操作抛异常；Clone 出的是一个自持数据的副本，与原流脱钩。
        /// 读不到（理论不该发生）时回退系统图标，绝不让程序因图标起不来。
        /// </summary>
        private static Icon Load(bool dark)
        {
            byte[]? bytes = ReadBytes(dark);
            if (bytes is null) return SystemIcons.Application;

            try
            {
                using var buffer = new MemoryStream(bytes, writable: false);
                using var icon = new Icon(buffer);
                return (Icon)icon.Clone();
            }
            catch (Exception)
            {
                return SystemIcons.Application;
            }
        }
    }
}
