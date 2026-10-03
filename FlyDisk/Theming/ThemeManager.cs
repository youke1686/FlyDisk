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
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FlyDisk.Theming
{
    /// <summary>
    /// 自绘件在换肤后"自己收尾"（见 深色主题.md §7）。
    ///
    /// 递归上色只按类型刷 <c>BackColor</c> / <c>ForeColor</c>，管不了自绘内容
    /// （检查器简要页整页是画出来的、悬浮方块是左右两块灰阶）。这类控件实现本接口，
    /// 由 <see cref="ThemeManager"/> 在上色时回调一次，自己去取调色板、重建画笔、重绘。
    /// </summary>
    public interface IThemedSurface
    {
        void ApplyTheme(ThemePalette palette);
    }

    /// <summary>
    /// 深浅色主题管理器（见 深色主题.md）。
    ///
    /// **为什么自研而不是用 .NET 10 的 `Application.SetColorMode`**：那条路只在 Windows 11 才有深色，
    /// 本机是 Windows 10 22H2，调了会静默退回浅色。自研反而更可控，也不必再关心系统版本。
    ///
    /// 两件事：
    /// ① **判定**——启动时读一次注册表 + 高对比度检查，得出该用哪套调色板（<see cref="Initialize"/>）；
    /// ② **上色**——递归遍历窗体控件树按类型刷色（<see cref="Apply"/>），并把自绘件交给
    ///    <see cref="IThemedSurface"/> 收尾。
    ///
    /// **不做运行中跟随**：系统改深浅色要重启本程序才生效（需求就是"启动时读一下系统设置"）。
    /// 于是调色板在进程生命周期内固定，省掉了收 WM_SETTINGCHANGE 那一整套。
    ///
    /// **浅色一律"恢复"而不是"不动"**：深色下为按钮 / 输入框 / 页签做的是**结构性**改造
    /// （扁平化、改边框样式、改自绘），切回浅色必须显式还原，否则会停在深色的造型上。
    /// 浅色取值全部取自系统色 ⇒ 观感与本次改动前逐像素一致（见 深色主题.md §9）。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>系统"应用使用浅色 / 深色"的开关所在位置</summary>
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>DWM 深色标题栏属性（Win10 20H1+ 与 Win11 都是 20；更早的预览版是 19）</summary>
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaUseImmersiveDarkModeLegacy = 19;

        /// <summary>不参与换肤的控件（连同子树）。主界面那块黑底绿字的日志框就是它</summary>
        private static readonly HashSet<Control> Excluded = new();

        /// <summary>每个控件的一次性状态（角色判定结果 / 事件是否已挂 / 原始边框样式）</summary>
        private static readonly ConditionalWeakTable<Control, ControlState> States = new();

        /// <summary>全局 ToolStrip 渲染器：颜色表每次取色都现读 <see cref="Palette"/>，所以只需装一次</summary>
        private static readonly ToolStripRenderer StripRenderer = new ThemeToolStripRenderer();

        private static ThemePalette _palette = ThemePalette.Light;

        /// <summary>当前生效的调色板</summary>
        public static ThemePalette Palette => _palette;

        /// <summary>当前是不是深色</summary>
        public static bool IsDark => _palette.IsDark;

        private enum LabelRole
        {
            Primary,
            Secondary,
            Warning,
        }

        private sealed class ControlState
        {
            /// <summary>语义角色是否已判定（只在**首次**上色时按原始前景色判一次）</summary>
            public bool RoleResolved;
            public LabelRole Role;

            /// <summary>自绘事件是否已挂（反复上色不能重复挂）</summary>
            public bool DrawHooked;

            /// <summary>EnabledChanged 是否已挂</summary>
            public bool EnabledHooked;

            /// <summary>原始边框样式（深色下改成 FixedSingle，切回浅色要还原）</summary>
            public bool BorderStyleSaved;
            public BorderStyle OriginalBorderStyle;
        }

        /// <summary>
        /// 定下初始调色板（启动时读一次系统设置）。
        /// **必须在任何窗体构造之前调用**（`Program.Main` 里），
        /// 否则第一个窗体拿到的还是默认的浅色调色板。
        ///
        /// **只在深色下装渲染器**：浅色沿用系统默认那个，菜单 / 状态栏的观感才与改动前完全一致
        /// （自己装一个 ProfessionalColorTable 会把下拉底色、选中高亮都换成我方取值，浅色下就是无谓的改动）。
        /// </summary>
        public static void Initialize()
        {
            _palette = DetectIsDark() ? ThemePalette.Dark : ThemePalette.Light;
            if (_palette.IsDark) ToolStripManager.Renderer = StripRenderer;
        }

        /// <summary>把一个控件排除在换肤之外（连同它的子树）。要在该控件首次上色之前调用</summary>
        public static void Exclude(Control control) => Excluded.Add(control);

        /// <summary>上色一个窗体及其全部子控件，并处理标题栏</summary>
        public static void Apply(Form form)
        {
            ApplyControl(form, _palette);
            ApplyTitleBar(form);
            form.Invalidate(true);
        }

        // ===== 判定 =====

        /// <summary>
        /// 系统当前是不是深色。
        /// **高对比度一律当浅色**：辅助功能主题的优先级高于深浅色，硬刷会把可读性搞坏
        /// （官方给 <c>SetColorMode</c> 写的限制里也有这一条）。
        /// </summary>
        private static bool DetectIsDark()
        {
            try
            {
                if (SystemInformation.HighContrast) return false;

                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                // 值缺失（老系统）⇒ 视为浅色
                return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
            }
            catch (Exception)
            {
                // 注册表读不到（策略限制等）不能让程序起不来：当浅色处理
                return false;
            }
        }

        // ===== 上色 =====

        private static void ApplyControl(Control control, ThemePalette p)
        {
            if (Excluded.Contains(control)) return;   // 连同子树一起放过

            switch (control)
            {
                case Form form:
                    form.BackColor = p.WindowBg;
                    form.ForeColor = p.TextPrimary;
                    break;

                case TabControl tabs:
                    StyleTabControl(tabs, p);
                    break;

                case TabPage page:
                    StyleTabPage(page, p);
                    break;

                case ToolStrip strip:
                    StyleToolStrip(strip, p);
                    break;

                case Button button:
                    StyleButton(button, p);
                    break;

                // NumericUpDown 内嵌的那个 TextBox 自带边框会与外壳叠成双层：
                // 深色下抹掉内边框，只留外壳那一条。浅色下**碰都不碰**（不动边框样式）。
                case TextBox inner when inner.Parent is UpDownBase:
                    inner.BackColor = p.IsDark ? p.InputBg : SystemColors.Window;
                    inner.ForeColor = p.IsDark ? p.TextPrimary : SystemColors.WindowText;
                    if (p.IsDark) inner.BorderStyle = BorderStyle.None;
                    break;

                case TextBox text:
                    StyleTextInput(text, p);
                    break;

                case RichTextBox rich:
                    StyleTextInput(rich, p);
                    break;

                case NumericUpDown numeric:
                    StyleTextInput(numeric, p);
                    break;

                case ListBox list:
                    StyleListBox(list, p);
                    break;

                case ComboBox combo:
                    StyleComboBox(combo, p);
                    break;

                case CheckBox check:
                    check.FlatStyle = FlatStyle.Standard;
                    check.BackColor = Color.Transparent;
                    check.ForeColor = EnabledText(p, check.Enabled);
                    EnsureEnabledHook(check);
                    break;

                case RadioButton radio:
                    radio.FlatStyle = FlatStyle.Standard;
                    radio.BackColor = Color.Transparent;
                    radio.ForeColor = EnabledText(p, radio.Enabled);
                    EnsureEnabledHook(radio);
                    break;

                case GroupBox group:
                    group.BackColor = Color.Transparent;
                    group.ForeColor = p.TextPrimary;   // 分组标题与正文同级（浅色下就是原来的黑色）
                    break;

                case Label label:
                    label.BackColor = Color.Transparent;
                    label.ForeColor = RoleText(p, ResolveRole(label));
                    break;

                case TrackBar track:
                    // 滑块与轨道是系统自绘，改不动；这里只把四周底色刷掉（见 深色主题.md §8）
                    track.BackColor = p.WindowBg;
                    break;

                case ProgressBar:
                    // 系统自绘，改不动（见 深色主题.md §8）
                    break;

                case Panel panel:
                    panel.BackColor = p.WindowBg;
                    panel.ForeColor = p.TextPrimary;
                    break;

                case Control other:
                    other.BackColor = p.WindowBg;
                    other.ForeColor = p.TextPrimary;
                    break;
            }

            if (control is IThemedSurface surface) surface.ApplyTheme(p);

            foreach (Control child in control.Controls) ApplyControl(child, p);
        }

        private static void StyleTabControl(TabControl tabs, ThemePalette p)
        {
            tabs.BackColor = p.IsDark ? p.WindowBg : SystemColors.Control;

            // 页签条带与页签页外那圈边距由系统按主题绘制，BackColor 刷不动、排除法也盖不全
            // （未选中页签边缘那圈浅色框落在 GetTabRect 之内）。
            // 深色下整条交给 ThemedTabControl 在系统画完之后接管重画（见该类注释）；浅色下不碰。
            if (tabs is ThemedTabControl themed)
            {
                themed.StripFill = p.WindowBg;
                themed.SelectedTabFill = p.CanvasBg;
                themed.SelectedTabText = p.TextPrimary;
                themed.TabText = p.TextSecondary;
                themed.TakeOver = p.IsDark;
            }

            // 页签头既然由 ThemedTabControl 自己重画，就不需要 owner-draw 了
            // （owner-draw 是逐页签回调，在里头刷条带会抹掉先画好的标签）。
            tabs.DrawMode = TabDrawMode.Normal;
            tabs.Appearance = TabAppearance.Normal;
        }

        private static void StyleTabPage(TabPage page, ThemePalette p)
        {
            if (p.IsDark)
            {
                page.UseVisualStyleBackColor = false;
                page.BackColor = p.CanvasBg;
                page.ForeColor = p.TextPrimary;
            }
            else
            {
                page.UseVisualStyleBackColor = true;
            }
        }

        private static void StyleToolStrip(ToolStrip strip, ThemePalette p)
        {
            strip.BackColor = p.MenuBg;
            strip.ForeColor = p.TextPrimary;   // 下拉菜单里的项由渲染器 + 下面这轮逐项刷色兜住

            foreach (ToolStripItem item in strip.Items) StyleToolItem(item, p);
        }

        private static void StyleToolItem(ToolStripItem item, ThemePalette p)
        {
            item.ForeColor = p.TextPrimary;
            if (item is ToolStripMenuItem menu)
            {
                foreach (ToolStripItem child in menu.DropDownItems) StyleToolItem(child, p);
            }
        }

        /// <summary>
        /// 按钮：深色下扁平化 + 自定三态底色；浅色下**交还视觉样式**。
        /// 浅色若不还原，切回浅色后会停在深色的扁平造型上——这是"恢复"而不是"不动"的原因。
        /// </summary>
        private static void StyleButton(Button button, ThemePalette p)
        {
            if (p.IsDark)
            {
                button.FlatStyle = FlatStyle.Flat;
                button.UseVisualStyleBackColor = false;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = p.Border;
                button.FlatAppearance.MouseOverBackColor = p.ControlHover;
                button.FlatAppearance.MouseDownBackColor = p.SelectionBg;
                button.BackColor = p.ControlBg;
                button.ForeColor = EnabledText(p, button.Enabled);
                EnsureEnabledHook(button);
            }
            else
            {
                button.FlatStyle = FlatStyle.Standard;
                button.UseVisualStyleBackColor = true;
                button.BackColor = SystemColors.Control;   // 走视觉样式时被忽略，写回系统值只是兜底
                button.ForeColor = SystemColors.ControlText;
            }
        }

        /// <summary>输入类控件：深色下换成单线边框 + 深底；浅色下还原成原来那个边框样式</summary>
        private static void StyleTextInput(Control input, ThemePalette p)
        {
            ControlState state = StateOf(input);
            if (!state.BorderStyleSaved)
            {
                state.OriginalBorderStyle = ReadBorderStyle(input);
                state.BorderStyleSaved = true;
            }

            WriteBorderStyle(input, p.IsDark ? BorderStyle.FixedSingle : state.OriginalBorderStyle);
            input.BackColor = p.IsDark ? p.InputBg : SystemColors.Window;
            input.ForeColor = p.IsDark ? p.TextPrimary : SystemColors.WindowText;
        }

        // BorderStyle 在 TextBoxBase 与 UpDownBase 上各有一份，Control 上没有，只能分派着读写
        private static BorderStyle ReadBorderStyle(Control input) => input switch
        {
            TextBoxBase text => text.BorderStyle,
            UpDownBase upDown => upDown.BorderStyle,
            _ => BorderStyle.Fixed3D,
        };

        private static void WriteBorderStyle(Control input, BorderStyle style)
        {
            if (input is TextBoxBase text) text.BorderStyle = style;
            else if (input is UpDownBase upDown) upDown.BorderStyle = style;
        }

        private static void StyleListBox(ListBox list, ThemePalette p)
        {
            ControlState state = StateOf(list);
            if (!state.BorderStyleSaved)
            {
                state.OriginalBorderStyle = list.BorderStyle;
                state.BorderStyleSaved = true;
            }

            list.BorderStyle = p.IsDark ? BorderStyle.FixedSingle : state.OriginalBorderStyle;
            list.BackColor = p.IsDark ? p.InputBg : SystemColors.Window;
            list.ForeColor = p.IsDark ? p.TextPrimary : SystemColors.WindowText;

            if (p.IsDark)
            {
                if (!state.DrawHooked)
                {
                    state.DrawHooked = true;
                    list.DrawItem += OnDrawListBoxItem;
                }
                list.DrawMode = DrawMode.OwnerDrawFixed;
            }
            else
            {
                list.DrawMode = DrawMode.Normal;
            }
        }

        private static void StyleComboBox(ComboBox combo, ThemePalette p)
        {
            combo.BackColor = p.IsDark ? p.InputBg : SystemColors.Window;
            combo.ForeColor = p.IsDark ? p.TextPrimary : SystemColors.WindowText;

            if (!p.IsDark)
            {
                combo.FlatStyle = FlatStyle.Standard;
                if (combo.DropDownStyle == ComboBoxStyle.DropDownList) combo.DrawMode = DrawMode.Normal;
                return;
            }

            combo.FlatStyle = FlatStyle.Flat;

            // 只有 DropDownList 需要自绘下拉列表（可编辑样式自带一个系统文本框，情况不同）
            if (combo.DropDownStyle != ComboBoxStyle.DropDownList) return;

            ControlState state = StateOf(combo);
            if (!state.DrawHooked)
            {
                state.DrawHooked = true;
                combo.DrawItem += OnDrawComboItem;
            }
            combo.DrawMode = DrawMode.OwnerDrawFixed;
        }

        /// <summary>禁用态用灰字，启用态用正文色</summary>
        private static Color EnabledText(ThemePalette p, bool enabled)
            => enabled ? p.TextPrimary : p.TextDisabled;

        /// <summary>按语义角色取文字色</summary>
        private static Color RoleText(ThemePalette p, LabelRole role) => role switch
        {
            LabelRole.Secondary => p.TextSecondary,
            LabelRole.Warning => p.WarningText,
            _ => p.TextPrimary,
        };

        /// <summary>
        /// 判一次标签的语义角色（主要 / 次要 / 警告）。
        ///
        /// **只在首次上色时判**：那时窗体刚构造完，`ForeColor` 还是 Designer / 构造函数里写的原始值
        /// （`Color.Gray`、`SystemColors.GrayText`、`(160,80,0)`），判得准；之后再上色就只剩下
        /// 上一套调色板的颜色了，所以结果要缓存下来。
        /// </summary>
        private static LabelRole ResolveRole(Label label)
        {
            ControlState state = StateOf(label);
            if (state.RoleResolved) return state.Role;

            Color original = label.ForeColor;
            state.Role = IsWarningColor(original) ? LabelRole.Warning
                       : IsSecondaryColor(original) ? LabelRole.Secondary
                       : LabelRole.Primary;
            state.RoleResolved = true;
            return state.Role;
        }

        private static bool IsSecondaryColor(Color c)
            => c == Color.Gray
            || c == Color.DimGray
            || c == SystemColors.GrayText
            || c == SystemColors.ControlDark
            || c == SystemColors.ControlDarkDark;

        /// <summary>橙 / 棕系（只命中远程窗那条"不加密"警告）</summary>
        private static bool IsWarningColor(Color c) => c.B < 80 && c.R > 100 && c.G < c.R;

        private static ControlState StateOf(Control control)
        {
            if (!States.TryGetValue(control, out ControlState? state))
            {
                state = new ControlState();
                States.Add(control, state);
            }
            return state;
        }

        /// <summary>挂"启用态变了"的回调：禁用态的底色/字色与启用态不同，不重刷会看不出被禁用</summary>
        private static void EnsureEnabledHook(Control control)
        {
            ControlState state = StateOf(control);
            if (state.EnabledHooked) return;
            state.EnabledHooked = true;

            control.EnabledChanged += (s, e) =>
            {
                if (s is Control c && !Excluded.Contains(c)) ApplyControl(c, _palette);
            };
        }

        // ===== 自绘：列表 / 下拉列表 =====

        // 页签头不在这里自绘：条带与页签页外的边距必须整体接管才不会留白，
        // 那件事交给 ThemedTabControl 做（见该类注释），owner-draw 这条路已弃。

        private static void OnDrawListBoxItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ListBox list || e.Index < 0 || e.Index >= list.Items.Count) return;
            ThemePalette p = Palette;
            bool selected = (e.State & DrawItemState.Selected) != 0;

            using (var background = new SolidBrush(selected ? p.SelectionBg : p.InputBg))
            {
                e.Graphics.FillRectangle(background, e.Bounds);
            }

            TextRenderer.DrawText(e.Graphics, list.Items[e.Index]?.ToString() ?? string.Empty, list.Font,
                e.Bounds, selected ? p.SelectionText : p.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);
        }

        private static void OnDrawComboItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ComboBox combo) return;
            ThemePalette p = Palette;
            bool selected = (e.State & DrawItemState.Selected) != 0;

            using (var background = new SolidBrush(selected ? p.SelectionBg : p.InputBg))
            {
                e.Graphics.FillRectangle(background, e.Bounds);
            }

            // 可编辑样式的编辑框那一段 e.Index 为 -1，没有文字要画
            if (e.Index < 0) return;

            TextRenderer.DrawText(e.Graphics, combo.Items[e.Index]?.ToString() ?? string.Empty, combo.Font,
                e.Bounds, selected ? p.SelectionText : p.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix
                | TextFormatFlags.EndEllipsis);
        }

        // ===== 标题栏 =====

        /// <summary>
        /// 让 DWM 用深色画标题栏。不调这一手的话，深色窗体顶着一整条惨白的标题栏。
        /// 属性号在 Win10 20H1+ 与 Win11 都是 20（更早的预览版是 19），失败时回退试一次。
        /// 取不到 dwmapi（Server Core 之类）时静默放过——标题栏颜色不值得让程序起不来。
        /// </summary>
        private static void ApplyTitleBar(Form form)
        {
            if (!form.IsHandleCreated) return;

            try
            {
                int dark = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(form.Handle, DwmwaUseImmersiveDarkModeLegacy, ref dark, sizeof(int));
                }
            }
            catch (Exception)
            {
                // 见上：不影响功能
            }
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // ===== ToolStrip 渲染器 =====

        /// <summary>
        /// 颜色表**每次取色都现读** <see cref="Palette"/>：渲染器只装一次，换肤不用重装，
        /// 也不会漏掉那些还没创建的菜单。
        /// </summary>
        private sealed class ThemeColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Palette.MenuBg;
            public override Color MenuStripGradientBegin => Palette.MenuBg;
            public override Color MenuStripGradientEnd => Palette.MenuBg;
            public override Color MenuItemSelected => Palette.ControlHover;
            public override Color MenuItemSelectedGradientBegin => Palette.ControlHover;
            public override Color MenuItemSelectedGradientEnd => Palette.ControlHover;
            public override Color MenuItemPressedGradientBegin => Palette.MenuBg;
            public override Color MenuItemPressedGradientEnd => Palette.MenuBg;
            public override Color MenuItemBorder => Palette.Border;
            public override Color MenuBorder => Palette.Border;
            public override Color SeparatorDark => Palette.Border;
            public override Color SeparatorLight => Palette.Border;
            public override Color StatusStripGradientBegin => Palette.MenuBg;
            public override Color StatusStripGradientEnd => Palette.MenuBg;
            public override Color ToolStripBorder => Palette.Border;
            public override Color ToolStripGradientBegin => Palette.MenuBg;
            public override Color ToolStripGradientMiddle => Palette.MenuBg;
            public override Color ToolStripGradientEnd => Palette.MenuBg;
            public override Color ImageMarginGradientBegin => Palette.MenuBg;
            public override Color ImageMarginGradientMiddle => Palette.MenuBg;
            public override Color ImageMarginGradientEnd => Palette.MenuBg;
            public override Color ImageMarginRevealedGradientBegin => Palette.MenuBg;
            public override Color ImageMarginRevealedGradientMiddle => Palette.MenuBg;
            public override Color ImageMarginRevealedGradientEnd => Palette.MenuBg;
            public override Color OverflowButtonGradientBegin => Palette.MenuBg;
            public override Color OverflowButtonGradientMiddle => Palette.MenuBg;
            public override Color OverflowButtonGradientEnd => Palette.MenuBg;
            public override Color ButtonSelectedHighlight => Palette.ControlHover;
            public override Color ButtonSelectedBorder => Palette.Border;
            public override Color ButtonSelectedGradientBegin => Palette.ControlHover;
            public override Color ButtonSelectedGradientEnd => Palette.ControlHover;
            public override Color ButtonPressedHighlight => Palette.SelectionBg;
            public override Color ButtonPressedBorder => Palette.Border;
            public override Color ButtonPressedGradientBegin => Palette.SelectionBg;
            public override Color ButtonPressedGradientEnd => Palette.SelectionBg;
            public override Color ButtonCheckedHighlight => Palette.SelectionBg;
            public override Color ButtonCheckedGradientBegin => Palette.SelectionBg;
            public override Color ButtonCheckedGradientEnd => Palette.SelectionBg;
            public override Color CheckBackground => Palette.SelectionBg;
            public override Color CheckSelectedBackground => Palette.SelectionBg;
            public override Color CheckPressedBackground => Palette.SelectionBg;
            public override Color GripDark => Palette.Border;
            public override Color GripLight => Palette.Border;
        }

        /// <summary>
        /// 深色下的勾选标记系统画得太淡（菜单里那两个语言项就是勾选态），自己画一个。
        /// </summary>
        private sealed class ThemeToolStripRenderer : ToolStripProfessionalRenderer
        {
            public ThemeToolStripRenderer() : base(new ThemeColorTable())
            {
                RoundedEdges = false;
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                ThemePalette p = Palette;
                Rectangle bounds = e.ImageRectangle;

                using (var background = new SolidBrush(p.SelectionBg))
                {
                    e.Graphics.FillRectangle(background, bounds);
                }

                TextRenderer.DrawText(e.Graphics, "✓", e.Item.Font, bounds, p.SelectionText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
