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

using System.Drawing;

namespace FlyDisk.Theming
{
    /// <summary>
    /// 一套界面配色（见 深色主题.md §4）。
    ///
    /// **两份实例，字段逐个对齐**：<see cref="Light"/> 是**逐项复刻改动前的观感**（能取系统色的地方就取系统色），
    /// <see cref="Dark"/> 才是我方新增的那一套。这样"没开深色模式的机器"上的效果与本次改动前完全一致，
    /// 出问题也只可能出在深色分支。
    ///
    /// 后半段那一组 `Bar*` / `Load*` / `Speed*` 是给**自绘**用的（检查器简要页与悬浮方块）。
    /// 它们不是"换个颜色"那么简单：简要页的灰阶语义是"**越深 = 越占住**"，深色背景下必须**反过来**，
    /// 于是这一组在深色调色板里是成对翻转的（见 深色主题.md §7）。
    /// </summary>
    public sealed class ThemePalette
    {
        /// <summary>是不是深色（判定逻辑在 <see cref="ThemeManager"/>，这里只记结果）</summary>
        public bool IsDark { get; init; }

        // ===== 窗体与容器 =====
        /// <summary>窗体底色</summary>
        public Color WindowBg { get; init; }
        /// <summary>自绘画布底色（简要页整页；浅色下是纯白，与改动前一致）</summary>
        public Color CanvasBg { get; init; }
        /// <summary>输入类控件底色</summary>
        public Color InputBg { get; init; }
        /// <summary>边框（输入框 / 按钮 / 分组框描边、菜单分隔线）</summary>
        public Color Border { get; init; }

        // ===== 文字 =====
        public Color TextPrimary { get; init; }
        public Color TextSecondary { get; init; }
        public Color TextDisabled { get; init; }
        /// <summary>警告文字（远程窗"不加密"那一段）</summary>
        public Color WarningText { get; init; }
        /// <summary>错误文字（选盘窗口里"会阻止启动"的硬拦原因，行首带 ❌）</summary>
        public Color DangerText { get; init; }

        // ===== 按钮与选择 =====
        public Color ControlBg { get; init; }
        public Color ControlHover { get; init; }
        public Color SelectionBg { get; init; }
        public Color SelectionText { get; init; }
        /// <summary>菜单栏 / 状态栏底色</summary>
        public Color MenuBg { get; init; }

        // ===== 自绘：简要页的灰阶 =====
        public Color BarTrack { get; init; }
        public Color BarBorder { get; init; }
        public Color BarTick { get; init; }
        /// <summary>自绘正文（简要页的标签与读数）</summary>
        public Color BarText { get; init; }
        /// <summary>自绘次要文字（说明行、页脚）</summary>
        public Color BarSubText { get; init; }
        /// <summary>L1 命中 / 数据（浅色下最深，深色下最亮）</summary>
        public Color BarDeep { get; init; }
        /// <summary>L2 命中</summary>
        public Color BarMid { get; init; }
        /// <summary>源盘读取</summary>
        public Color BarLight { get; init; }
        /// <summary>空洞</summary>
        public Color BarHole { get; init; }
        /// <summary>可用</summary>
        public Color BarFree { get; init; }
        /// <summary>内存水位·正常</summary>
        public Color LoadNormal { get; init; }
        /// <summary>内存水位·淘汰中</summary>
        public Color LoadEvict { get; init; }
        /// <summary>内存水位·停止回填</summary>
        public Color LoadStop { get; init; }

        /// <summary>
        /// 浅色：能取系统色的都取系统色，取值与改动前逐项一致。
        /// </summary>
        public static ThemePalette Light { get; } = new()
        {
            IsDark = false,

            WindowBg = SystemColors.Control,
            CanvasBg = Color.White,
            InputBg = SystemColors.Window,
            Border = SystemColors.ControlDark,

            TextPrimary = SystemColors.ControlText,
            TextSecondary = SystemColors.GrayText,
            TextDisabled = SystemColors.GrayText,
            WarningText = Color.FromArgb(160, 80, 0),
            DangerText = Color.FromArgb(0xC0, 0x28, 0x28),

            ControlBg = SystemColors.Control,
            ControlHover = SystemColors.ControlLight,
            SelectionBg = SystemColors.Highlight,
            SelectionText = SystemColors.HighlightText,
            MenuBg = SystemColors.Control,

            BarTrack = Color.FromArgb(0xF0, 0xF0, 0xF0),
            BarBorder = Color.FromArgb(0xA0, 0xA0, 0xA0),
            BarTick = Color.FromArgb(0x00, 0x00, 0x00),
            BarText = Color.FromArgb(0x1A, 0x1A, 0x1A),
            BarSubText = Color.FromArgb(0x6E, 0x6E, 0x6E),
            BarDeep = Color.FromArgb(0x4A, 0x4A, 0x4A),
            BarMid = Color.FromArgb(0x8C, 0x8C, 0x8C),
            BarLight = Color.FromArgb(0xC8, 0xC8, 0xC8),
            BarHole = Color.FromArgb(0xB4, 0xB4, 0xB4),
            BarFree = Color.FromArgb(0xE0, 0xE0, 0xE0),
            LoadNormal = Color.FromArgb(0x8C, 0x8C, 0x8C),
            LoadEvict = Color.FromArgb(0x5A, 0x5A, 0x5A),
            LoadStop = Color.FromArgb(0x2E, 0x2E, 0x2E),
        };

        /// <summary>
        /// 深色：一整套中性灰，不引入强调色（与检查器"一律灰色、只靠深浅区分"的既有口径一致）。
        /// 灰阶相对浅色**成对翻转**：浅色越深越"占住"，深色越亮越"占住"。
        /// </summary>
        public static ThemePalette Dark { get; } = new()
        {
            IsDark = true,

            WindowBg = Color.FromArgb(0x1E, 0x1E, 0x1E),
            CanvasBg = Color.FromArgb(0x1E, 0x1E, 0x1E),
            InputBg = Color.FromArgb(0x2D, 0x2D, 0x30),
            Border = Color.FromArgb(0x3F, 0x3F, 0x46),

            TextPrimary = Color.FromArgb(0xE6, 0xE6, 0xE6),
            TextSecondary = Color.FromArgb(0x9A, 0x9A, 0x9A),
            TextDisabled = Color.FromArgb(0x6A, 0x6A, 0x6A),
            WarningText = Color.FromArgb(0xE0, 0xA0, 0x50),
            DangerText = Color.FromArgb(0xF0, 0x6B, 0x6B),

            ControlBg = Color.FromArgb(0x33, 0x33, 0x37),
            ControlHover = Color.FromArgb(0x3E, 0x3E, 0x42),
            SelectionBg = Color.FromArgb(0x26, 0x4F, 0x78),
            SelectionText = Color.FromArgb(0xFF, 0xFF, 0xFF),
            MenuBg = Color.FromArgb(0x2D, 0x2D, 0x30),

            BarTrack = Color.FromArgb(0x2A, 0x2A, 0x2A),
            BarBorder = Color.FromArgb(0x5A, 0x5A, 0x5A),
            BarTick = Color.FromArgb(0xE6, 0xE6, 0xE6),
            BarText = Color.FromArgb(0xE6, 0xE6, 0xE6),
            BarSubText = Color.FromArgb(0x9A, 0x9A, 0x9A),
            BarDeep = Color.FromArgb(0xD8, 0xD8, 0xD8),
            BarMid = Color.FromArgb(0xA0, 0xA0, 0xA0),
            BarLight = Color.FromArgb(0x6E, 0x6E, 0x6E),
            BarHole = Color.FromArgb(0x7A, 0x7A, 0x7A),
            BarFree = Color.FromArgb(0x3A, 0x3A, 0x3A),
            LoadNormal = Color.FromArgb(0xA0, 0xA0, 0xA0),
            LoadEvict = Color.FromArgb(0xC4, 0xC4, 0xC4),
            LoadStop = Color.FromArgb(0xEC, 0xEC, 0xEC),
        };
    }
}
