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

using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace FlyDisk.Theming
{
    /// <summary>
    /// 深色主题下彻底接管"页签条带 + 页签页外那圈边距"的 TabControl（检查器在用）。
    ///
    /// **为什么要接管**：启用视觉样式时，TabControl 的这些区域由 ComCtl32 按当前系统主题绘制
    /// （TABP_PANE 的底色、未选中页签的浅色边框、条带里的空隙），这条路径**不采纳**
    /// <see cref="Control.BackColor"/>。之前试过两种绕法都不成：
    /// - `Appearance = FlatButtons`：条带变矮，留白反而更大；
    /// - 在系统画完后按 `GetTabRect` **排除**页签头再补刷：未选中页签边缘那圈浅色框落在
    ///   `GetTabRect` 之内，排除掉了就盖不住，于是只剩"未选中页签上左右有白边"。
    /// （另外，`DrawItem` 里刷整条条带也不可行：那是逐个页签回调的，会抹掉先画好的标签。）
    ///
    /// **做法**：不再试图只补"空隙"。在系统把这一帧画完之后（`WM_PAINT` 之后）：
    /// ① 把页签页以外的整个客户区刷成窗体色；② 再把每个页签头自己重画一遍（底色 + 文字）。
    /// 系统画在条带里的任何东西都被这两步盖掉，几何怎么变都不会漏。
    ///
    /// **只在深色下接管**（<see cref="TakeOver"/>）：浅色主题下这些区域本就是系统的正常造型，
    /// 不碰它才能与改动前逐像素一致。
    /// </summary>
    internal sealed class ThemedTabControl : TabControl
    {
        private const int WM_PAINT = 0x000F;

        // 下列属性只由 ThemeManager 在运行时赋值，**不进设计器序列化**：
        // 否则 WinForms 分析器报 WFO1000（属性没有配置代码序列化），且会被写进 Designer。
        /// <summary>是否接管绘制（仅深色下打开）</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal bool TakeOver { get; set; }

        /// <summary>条带与页签页外边距的底色（窗体色）</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Color StripFill { get; set; } = SystemColors.Control;

        /// <summary>选中页签的底色（画布色，与页内容连成一片）</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Color SelectedTabFill { get; set; } = SystemColors.Control;

        /// <summary>选中页签的文字色</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Color SelectedTabText { get; set; } = SystemColors.ControlText;

        /// <summary>未选中页签的文字色</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal Color TabText { get; set; } = SystemColors.ControlText;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (m.Msg == WM_PAINT && TakeOver && IsHandleCreated) PaintStrip();
        }

        /// <summary>系统画完后接管：先铺条带底色，再把页签头重画一遍</summary>
        private void PaintStrip()
        {
            Rectangle client = ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) return;

            using Graphics g = CreateGraphics();

            // 页签页那块跳过：页面是子窗口，自己会画
            Rectangle display = DisplayRectangle;
            if (display.Width > 0 && display.Height > 0) g.ExcludeClip(display);

            // ① 页签页以外整块铺底色：那圈边距、条带空隙、以及未选中页签的浅色边框全被盖掉
            using (var brush = new SolidBrush(StripFill)) g.FillRectangle(brush, client);

            // ② 页签头自己重画：底色 + 文字（系统画的那份已被上一步盖掉）
            for (int i = 0; i < TabPages.Count; i++)
            {
                Rectangle bounds = GetTabRect(i);
                if (bounds.Width <= 0 || bounds.Height <= 0) continue;

                bool selected = i == SelectedIndex;
                using (var brush = new SolidBrush(selected ? SelectedTabFill : StripFill))
                {
                    g.FillRectangle(brush, bounds);
                }

                TextRenderer.DrawText(g, TabPages[i].Text, Font, bounds,
                    selected ? SelectedTabText : TabText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
