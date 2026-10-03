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

namespace FlyDisk
{
    partial class Inspector
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            tabPages = new Theming.ThemedTabControl();
            pageBrief = new TabPage();
            briefView = new BriefView();
            pageDetail = new TabPage();
            textBox1 = new TextBox();
            chkTopMost = new CheckBox();
            tabPages.SuspendLayout();
            pageBrief.SuspendLayout();
            pageDetail.SuspendLayout();
            SuspendLayout();
            // 
            // tabPages
            // 
            // 两页只渲染"当前选中的那一页"（切换时再渲一次），见 Inspector.Render
            tabPages.Controls.Add(pageBrief);
            tabPages.Controls.Add(pageDetail);
            tabPages.Dock = DockStyle.Fill;
            tabPages.Location = new Point(0, 0);
            tabPages.Name = "tabPages";
            tabPages.SelectedIndex = 0;
            tabPages.Size = new Size(800, 450);
            tabPages.TabIndex = 0;
            // 
            // pageBrief
            // 
            // 简要页整页自绘：系统 ProgressBar 做不出"条上带阈值刻度 / 条内分三段"（见 BriefView 类注释）
            pageBrief.Controls.Add(briefView);
            pageBrief.Location = new Point(4, 25);
            pageBrief.Name = "pageBrief";
            pageBrief.Size = new Size(792, 421);
            pageBrief.TabIndex = 0;
            pageBrief.Text = "简要";
            pageBrief.UseVisualStyleBackColor = true;
            // 
            // briefView
            // 
            briefView.Dock = DockStyle.Fill;
            briefView.Location = new Point(0, 0);
            briefView.Name = "briefView";
            briefView.Size = new Size(792, 421);
            briefView.TabIndex = 0;
            // 
            // pageDetail
            // 
            // 用只读 TextBox 而不是 Label：Label 的文本**不可选中、不可复制**（用户要把统计贴出来时抓瞎）。
            pageDetail.Controls.Add(textBox1);
            pageDetail.Location = new Point(4, 25);
            pageDetail.Name = "pageDetail";
            pageDetail.Padding = new Padding(3);
            pageDetail.Size = new Size(792, 421);
            pageDetail.TabIndex = 1;
            pageDetail.Text = "详细";
            pageDetail.UseVisualStyleBackColor = true;
            // 
            // textBox1
            // 
            // 统计文本很长；不换行 + 双向滚动，行宽不会随窗口变化而折行
            textBox1.Dock = DockStyle.Fill;
            textBox1.Location = new Point(3, 3);
            textBox1.Multiline = true;
            textBox1.Name = "textBox1";
            textBox1.ReadOnly = true;
            textBox1.ScrollBars = ScrollBars.Both;
            textBox1.Size = new Size(786, 415);
            textBox1.TabIndex = 0;
            textBox1.WordWrap = false;
            // 
            // chkTopMost
            // 
            // 置顶开关贴在窗口底部：它管的是**整个窗口**，所以不能放进任何一页里（两页都要看得见）
            chkTopMost.AutoSize = true;
            chkTopMost.Dock = DockStyle.Bottom;
            chkTopMost.Location = new Point(0, 429);
            chkTopMost.Name = "chkTopMost";
            chkTopMost.Padding = new Padding(10, 3, 0, 3);
            chkTopMost.Size = new Size(800, 21);
            chkTopMost.TabIndex = 1;
            chkTopMost.Text = "窗口置顶";
            chkTopMost.UseVisualStyleBackColor = true;
            chkTopMost.CheckedChanged += chkTopMost_CheckedChanged;
            // 
            // Inspector
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            // **先加"边"控件、后加 Fill**：docking 按 Controls 集合的顺序处理，Fill 放最后才吃剩下的那块
            Controls.Add(chkTopMost);
            Controls.Add(tabPages);
            Name = "Inspector";
            Text = "检查器";
            tabPages.ResumeLayout(false);
            pageBrief.ResumeLayout(false);
            pageDetail.ResumeLayout(false);
            pageDetail.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private Theming.ThemedTabControl tabPages;
        private TabPage pageBrief;
        private TabPage pageDetail;
        private BriefView briefView;
        private TextBox textBox1;
        private CheckBox chkTopMost;
    }
}
