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
    partial class Form1
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            btnStartStop = new Button();
            txtLog = new TextBox();
            statusStrip1 = new StatusStrip();
            lblTargetStatus = new ToolStripStatusLabel();
            menuStrip1 = new MenuStrip();
            设置ToolStripMenuItem = new ToolStripMenuItem();
            检查器ToolStripMenuItem = new ToolStripMenuItem();
            labelTitle = new Label();
            labelSubtitle = new Label();
            lblStarHint = new Label();
            lblBadgePlaceholder = new Label();
            picGitHub = new PictureBox();
            statusStrip1.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)picGitHub).BeginInit();
            menuStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // btnStartStop
            // 
            btnStartStop.Location = new Point(12, 141);
            btnStartStop.Name = "btnStartStop";
            btnStartStop.Size = new Size(560, 50);
            btnStartStop.TabIndex = 1;
            btnStartStop.Text = "启动";
            btnStartStop.UseVisualStyleBackColor = true;
            btnStartStop.Click += btnStartStop_Click;
            // 
            // txtLog
            // 
            txtLog.BackColor = Color.White;
            txtLog.ForeColor = Color.Black;
            txtLog.Location = new Point(12, 197);
            txtLog.Multiline = true;
            txtLog.Name = "txtLog";
            txtLog.ReadOnly = true;
            txtLog.ScrollBars = ScrollBars.Vertical;
            txtLog.Size = new Size(560, 143);
            txtLog.TabIndex = 2;
            // 
            // statusStrip1
            // 
            statusStrip1.Items.AddRange(new ToolStripItem[] { lblTargetStatus });
            statusStrip1.Location = new Point(0, 350);
            statusStrip1.Name = "statusStrip1";
            statusStrip1.Size = new Size(584, 22);
            statusStrip1.TabIndex = 3;
            statusStrip1.Text = "statusStrip1";
            // 
            // lblTargetStatus
            // 
            lblTargetStatus.Name = "lblTargetStatus";
            lblTargetStatus.Size = new Size(77, 17);
            lblTargetStatus.Text = "iSCSI 未监听";
            // 
            // menuStrip1
            // 
            menuStrip1.Items.AddRange(new ToolStripItem[] { 设置ToolStripMenuItem, 检查器ToolStripMenuItem });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(584, 25);
            menuStrip1.TabIndex = 4;
            menuStrip1.Text = "menuStrip1";
            // 
            // 设置ToolStripMenuItem
            // 
            设置ToolStripMenuItem.Name = "设置ToolStripMenuItem";
            设置ToolStripMenuItem.Size = new Size(44, 21);
            设置ToolStripMenuItem.Text = "设置";
            // 
            // 检查器ToolStripMenuItem
            // 
            检查器ToolStripMenuItem.Name = "检查器ToolStripMenuItem";
            检查器ToolStripMenuItem.Size = new Size(56, 21);
            检查器ToolStripMenuItem.Text = "检查器";
            // 
            // labelTitle
            // 
            labelTitle.Font = new Font("Microsoft YaHei UI", 14.25F, FontStyle.Regular, GraphicsUnit.Point, 134);
            labelTitle.Location = new Point(0, 40);
            labelTitle.Name = "labelTitle";
            labelTitle.Size = new Size(584, 52);
            labelTitle.TabIndex = 5;
            labelTitle.Text = "FlyDisk";
            labelTitle.TextAlign = ContentAlignment.MiddleCenter;
            // 
            // labelSubtitle
            // 
            labelSubtitle.AutoSize = true;
            labelSubtitle.ForeColor = Color.Gray;
            labelSubtitle.Location = new Point(292, 92);
            labelSubtitle.Name = "labelSubtitle";
            labelSubtitle.Size = new Size(146, 17);
            labelSubtitle.TabIndex = 6;
            labelSubtitle.Text = "—— 机械硬盘块级加速器";
            // 
            // lblStarHint
            // 
            lblStarHint.AutoSize = true;
            // 灰色 ⇒ 主题层按「次要文字」上色（见 ThemeManager.ResolveRole），深浅色下都是柔和的提示色。
            lblStarHint.Cursor = Cursors.Hand;
            lblStarHint.ForeColor = Color.Gray;
            lblStarHint.Name = "lblStarHint";
            lblStarHint.Text = "如果解决了你的问题，就随手给一颗star支持一下吧 →";
            // 它本身就是"进仓库"的入口（点了同样开浏览器）：即使徽标图下不到，右下角也始终点得动。
            lblStarHint.Click += OpenGitHubRepo;
            // 位置跟着徽标走（见 Form1.PlaceBadgeRow）
            lblStarHint.Visible = false;
            // 
            // lblBadgePlaceholder
            // 
            lblBadgePlaceholder.AutoSize = true;
            // 这就是那个"按钮"的默认文字：卡片（徽标图）读到之前顶在卡片的位置上，读到就换成卡片。
            // 与提示同为灰色，主题层会按「次要文字」上色。
            lblBadgePlaceholder.Cursor = Cursors.Hand;
            lblBadgePlaceholder.ForeColor = Color.Gray;
            lblBadgePlaceholder.Name = "lblBadgePlaceholder";
            lblBadgePlaceholder.Text = "（点击前往）";
            lblBadgePlaceholder.Click += OpenGitHubRepo;
            lblBadgePlaceholder.Visible = false;
            // 
            // picGitHub
            // 
            picGitHub.Cursor = Cursors.Hand;
            picGitHub.Name = "picGitHub";
            picGitHub.SizeMode = PictureBoxSizeMode.AutoSize;
            picGitHub.TabStop = false;
            // 图片是**运行时**从 shields.io 下的（见 Form1.LoadGitHubBadge）：下到之前先藏着，
            // 免得先闪一个空框；尺寸也要等拿到图才知道，位置在图片就位后由 PlaceBadgeRow 算。
            picGitHub.Visible = false;
            picGitHub.Click += OpenGitHubRepo;
            // 
            // Form1
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(584, 402);
            Controls.Add(labelSubtitle);
            Controls.Add(labelTitle);
            Controls.Add(statusStrip1);
            Controls.Add(menuStrip1);
            Controls.Add(txtLog);
            Controls.Add(btnStartStop);
            Controls.Add(picGitHub);
            Controls.Add(lblBadgePlaceholder);
            Controls.Add(lblStarHint);
            ForeColor = SystemColors.ControlText;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MainMenuStrip = menuStrip1;
            MaximizeBox = false;
            Name = "Form1";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "FlyDisk - 机械硬盘加速器";
            Load += Form1_Load;
            statusStrip1.ResumeLayout(false);
            statusStrip1.PerformLayout();
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)picGitHub).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private System.Windows.Forms.Button btnStartStop;
        private System.Windows.Forms.TextBox txtLog;
        private System.Windows.Forms.StatusStrip statusStrip1;
        private System.Windows.Forms.ToolStripStatusLabel lblTargetStatus;
        private MenuStrip menuStrip1;
        private ToolStripMenuItem 设置ToolStripMenuItem;
        private ToolStripMenuItem 检查器ToolStripMenuItem;
        private Label labelTitle;
        private Label labelSubtitle;
        private Label lblStarHint;
        private Label lblBadgePlaceholder;
        private System.Windows.Forms.PictureBox picGitHub;
    }
}
