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
    partial class Settings
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
            chkEnableCache = new CheckBox();
            lblBasicHeader = new Label();
            lblAdvancedHeader = new Label();
            lblEviction = new Label();
            trackEviction = new TrackBar();
            lblStop = new Label();
            trackStop = new TrackBar();
            btnSave = new Button();
            btnDocs = new Button();
            lblEvictionVal = new Label();
            lblStopVal = new Label();
            groupBoxCache = new GroupBox();
            groupBoxSsd = new GroupBox();
            lblL2Summary = new Label();
            btnL2Manage = new Button();
            lblMemEstimate = new Label();
            groupBoxDisk = new GroupBox();
            label2 = new Label();
            txtListenAddress = new TextBox();
            lblListenPort = new Label();
            numListenPort = new NumericUpDown();
            chkAutoIscsiLogin = new CheckBox();
            groupBoxAdvancedL1 = new GroupBox();
            lblEvictionWindow = new Label();
            numEvictionWindow = new NumericUpDown();
            lblEvictionGrace = new Label();
            numEvictionGrace = new NumericUpDown();
            lblEvictionBatch = new Label();
            numEvictionBatch = new NumericUpDown();
            groupBoxAdvancedL2 = new GroupBox();
            lblSsdConservative = new Label();
            trackSsdConservative = new TrackBar();
            lblSsdConservativeVal = new Label();
            lblGhostFraction = new Label();
            numGhostFraction = new NumericUpDown();
            groupBoxRemote = new GroupBox();
            lblRemotePort = new Label();
            numRemotePort = new NumericUpDown();
            lblRemoteAddress = new Label();
            txtRemoteAddress = new TextBox();
            ((System.ComponentModel.ISupportInitialize)trackEviction).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackStop).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numListenPort).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numEvictionWindow).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numEvictionGrace).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numEvictionBatch).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackSsdConservative).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numGhostFraction).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numRemotePort).BeginInit();
            groupBoxCache.SuspendLayout();
            groupBoxSsd.SuspendLayout();
            groupBoxDisk.SuspendLayout();
            groupBoxAdvancedL1.SuspendLayout();
            groupBoxAdvancedL2.SuspendLayout();
            groupBoxRemote.SuspendLayout();
            SuspendLayout();
            // 
            // 基础（左栏）
            // 
            // lblBasicHeader
            lblBasicHeader.AutoSize = true;
            lblBasicHeader.Font = new Font(lblBasicHeader.Font, FontStyle.Bold);
            lblBasicHeader.Location = new Point(20, 2);
            lblBasicHeader.Name = "lblBasicHeader";
            lblBasicHeader.Size = new Size(35, 17);
            lblBasicHeader.TabIndex = 0;
            lblBasicHeader.Text = "基础";
            // 
            // lblAdvancedHeader
            lblAdvancedHeader.AutoSize = true;
            lblAdvancedHeader.Font = new Font(lblAdvancedHeader.Font, FontStyle.Bold);
            lblAdvancedHeader.Location = new Point(440, 2);
            lblAdvancedHeader.Name = "lblAdvancedHeader";
            lblAdvancedHeader.Size = new Size(35, 17);
            lblAdvancedHeader.TabIndex = 1;
            lblAdvancedHeader.Text = "高级";
            // 
            // chkEnableCache
            chkEnableCache.AutoSize = true;
            chkEnableCache.Location = new Point(20, 30);
            chkEnableCache.Name = "chkEnableCache";
            chkEnableCache.Size = new Size(99, 21);
            chkEnableCache.TabIndex = 0;
            chkEnableCache.Text = "启用内存缓存";
            chkEnableCache.UseVisualStyleBackColor = true;
            chkEnableCache.CheckedChanged += chkCacheToggle_CheckedChanged;
            // 
            // lblEviction
            lblEviction.AutoSize = true;
            lblEviction.Location = new Point(20, 65);
            lblEviction.Name = "lblEviction";
            lblEviction.Size = new Size(119, 17);
            lblEviction.TabIndex = 1;
            lblEviction.Text = "开始淘汰阈值 (内存):";
            // 
            // trackEviction
            trackEviction.Location = new Point(20, 90);
            trackEviction.Maximum = 100;
            trackEviction.Name = "trackEviction";
            trackEviction.Size = new Size(300, 45);
            trackEviction.TabIndex = 2;
            trackEviction.TickFrequency = 5;
            trackEviction.Value = 80;
            trackEviction.Scroll += trackEviction_Scroll;
            // 
            // lblEvictionVal
            lblEvictionVal.AutoSize = true;
            lblEvictionVal.Location = new Point(325, 90);
            lblEvictionVal.Name = "lblEvictionVal";
            lblEvictionVal.Size = new Size(32, 17);
            lblEvictionVal.TabIndex = 3;
            lblEvictionVal.Text = "80%";
            // 
            // lblStop
            lblStop.AutoSize = true;
            lblStop.Location = new Point(20, 145);
            lblStop.Name = "lblStop";
            lblStop.Size = new Size(119, 17);
            lblStop.TabIndex = 4;
            lblStop.Text = "停止缓存阈值 (内存):";
            // 
            // trackStop
            trackStop.Location = new Point(20, 170);
            trackStop.Maximum = 100;
            trackStop.Name = "trackStop";
            trackStop.Size = new Size(300, 45);
            trackStop.TabIndex = 5;
            trackStop.TickFrequency = 5;
            trackStop.Value = 90;
            trackStop.Scroll += trackStop_Scroll;
            // 
            // lblStopVal
            lblStopVal.AutoSize = true;
            lblStopVal.Location = new Point(325, 170);
            lblStopVal.Name = "lblStopVal";
            lblStopVal.Size = new Size(32, 17);
            lblStopVal.TabIndex = 6;
            lblStopVal.Text = "90%";
            // 
            // groupBoxCache
            groupBoxCache.Controls.Add(chkEnableCache);
            groupBoxCache.Controls.Add(lblEviction);
            groupBoxCache.Controls.Add(lblEvictionVal);
            groupBoxCache.Controls.Add(trackEviction);
            groupBoxCache.Controls.Add(lblStop);
            groupBoxCache.Controls.Add(lblStopVal);
            groupBoxCache.Controls.Add(trackStop);
            groupBoxCache.Location = new Point(20, 20);
            groupBoxCache.Name = "groupBoxCache";
            groupBoxCache.Size = new Size(400, 220);
            groupBoxCache.TabIndex = 0;
            groupBoxCache.TabStop = false;
            groupBoxCache.Text = "内存缓存（L1）";
            // 
            // groupBoxSsd
            groupBoxSsd.Controls.Add(lblL2Summary);
            groupBoxSsd.Controls.Add(btnL2Manage);
            groupBoxSsd.Location = new Point(20, 250);
            groupBoxSsd.Name = "groupBoxSsd";
            groupBoxSsd.Size = new Size(400, 110);
            groupBoxSsd.TabIndex = 1;
            groupBoxSsd.TabStop = false;
            groupBoxSsd.Text = "SSD 二级缓存（L2，每盘独立配置）";
            // 
            // lblL2Summary
            lblL2Summary.AutoSize = false;
            lblL2Summary.Location = new Point(15, 32);
            lblL2Summary.Name = "lblL2Summary";
            lblL2Summary.Size = new Size(370, 40);
            lblL2Summary.TabIndex = 0;
            lblL2Summary.Text = "尚未配置 L2";
            // 
            // btnL2Manage
            btnL2Manage.Location = new Point(15, 72);
            btnL2Manage.Name = "btnL2Manage";
            btnL2Manage.Size = new Size(120, 28);
            btnL2Manage.TabIndex = 1;
            btnL2Manage.Text = "L2 管理";
            btnL2Manage.UseVisualStyleBackColor = true;
            btnL2Manage.Click += btnL2Manage_Click;
            // 
            // lblMemEstimate
            lblMemEstimate.AutoSize = false;
            lblMemEstimate.ForeColor = SystemColors.GrayText;
            lblMemEstimate.Location = new Point(20, 370);
            lblMemEstimate.Name = "lblMemEstimate";
            lblMemEstimate.Size = new Size(400, 44);
            lblMemEstimate.TabIndex = 2;
            lblMemEstimate.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // 高级（右栏）
            // 
            // groupBoxDisk
            groupBoxDisk.Controls.Add(label2);
            groupBoxDisk.Controls.Add(txtListenAddress);
            groupBoxDisk.Controls.Add(lblListenPort);
            groupBoxDisk.Controls.Add(numListenPort);
            groupBoxDisk.Controls.Add(chkAutoIscsiLogin);
            groupBoxDisk.Location = new Point(440, 20);
            groupBoxDisk.Name = "groupBoxDisk";
            groupBoxDisk.Size = new Size(400, 150);
            groupBoxDisk.TabIndex = 3;
            groupBoxDisk.TabStop = false;
            groupBoxDisk.Text = "iSCSI 端点";
            // 
            // label2
            label2.AutoSize = true;
            label2.Location = new Point(15, 32);
            label2.Name = "label2";
            label2.Size = new Size(71, 17);
            label2.TabIndex = 0;
            label2.Text = "监听地址:";
            // 
            // txtListenAddress
            txtListenAddress.Location = new Point(130, 29);
            txtListenAddress.Name = "txtListenAddress";
            txtListenAddress.Size = new Size(240, 23);
            txtListenAddress.TabIndex = 1;
            // 
            // lblListenPort
            lblListenPort.AutoSize = true;
            lblListenPort.Location = new Point(15, 67);
            lblListenPort.Name = "lblListenPort";
            lblListenPort.Size = new Size(71, 17);
            lblListenPort.TabIndex = 2;
            lblListenPort.Text = "监听端口:";
            // 
            // numListenPort
            numListenPort.Location = new Point(130, 64);
            numListenPort.Maximum = new decimal(new int[] { 65535, 0, 0, 0 });
            numListenPort.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numListenPort.Name = "numListenPort";
            numListenPort.Size = new Size(90, 23);
            numListenPort.TabIndex = 3;
            numListenPort.Value = new decimal(new int[] { 3260, 0, 0, 0 });
            // 
            // chkAutoIscsiLogin
            chkAutoIscsiLogin.AutoSize = true;
            chkAutoIscsiLogin.Location = new Point(15, 105);
            chkAutoIscsiLogin.Name = "chkAutoIscsiLogin";
            chkAutoIscsiLogin.Size = new Size(330, 21);
            chkAutoIscsiLogin.TabIndex = 4;
            chkAutoIscsiLogin.Text = "启用自动 iSCSI 登录（关闭后需手动挂载）";
            chkAutoIscsiLogin.UseVisualStyleBackColor = true;
            // 
            // groupBoxAdvancedL1
            groupBoxAdvancedL1.Controls.Add(lblEvictionWindow);
            groupBoxAdvancedL1.Controls.Add(numEvictionWindow);
            groupBoxAdvancedL1.Controls.Add(lblEvictionGrace);
            groupBoxAdvancedL1.Controls.Add(numEvictionGrace);
            groupBoxAdvancedL1.Controls.Add(lblEvictionBatch);
            groupBoxAdvancedL1.Controls.Add(numEvictionBatch);
            groupBoxAdvancedL1.Location = new Point(440, 180);
            groupBoxAdvancedL1.Name = "groupBoxAdvancedL1";
            groupBoxAdvancedL1.Size = new Size(400, 145);
            groupBoxAdvancedL1.TabIndex = 4;
            groupBoxAdvancedL1.TabStop = false;
            groupBoxAdvancedL1.Text = "L1 调参";
            // 
            // lblEvictionWindow
            lblEvictionWindow.AutoSize = true;
            lblEvictionWindow.Location = new Point(15, 32);
            lblEvictionWindow.Name = "lblEvictionWindow";
            lblEvictionWindow.Size = new Size(95, 17);
            lblEvictionWindow.TabIndex = 0;
            lblEvictionWindow.Text = "淘汰窗口 (秒):";
            // 
            // numEvictionWindow
            numEvictionWindow.Location = new Point(170, 29);
            numEvictionWindow.Maximum = new decimal(new int[] { 86400, 0, 0, 0 });
            numEvictionWindow.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numEvictionWindow.Name = "numEvictionWindow";
            numEvictionWindow.Size = new Size(100, 23);
            numEvictionWindow.TabIndex = 1;
            numEvictionWindow.Value = new decimal(new int[] { 3600, 0, 0, 0 });
            // 
            // lblEvictionGrace
            lblEvictionGrace.AutoSize = true;
            lblEvictionGrace.Location = new Point(15, 67);
            lblEvictionGrace.Name = "lblEvictionGrace";
            lblEvictionGrace.Size = new Size(95, 17);
            lblEvictionGrace.TabIndex = 2;
            lblEvictionGrace.Text = "淘汰宽限 (秒):";
            // 
            // numEvictionGrace
            numEvictionGrace.Location = new Point(170, 64);
            numEvictionGrace.Maximum = new decimal(new int[] { 3600, 0, 0, 0 });
            numEvictionGrace.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            numEvictionGrace.Name = "numEvictionGrace";
            numEvictionGrace.Size = new Size(100, 23);
            numEvictionGrace.TabIndex = 3;
            numEvictionGrace.Value = new decimal(new int[] { 2, 0, 0, 0 });
            // 
            // lblEvictionBatch
            lblEvictionBatch.AutoSize = true;
            lblEvictionBatch.Location = new Point(15, 102);
            lblEvictionBatch.Name = "lblEvictionBatch";
            lblEvictionBatch.Size = new Size(95, 17);
            lblEvictionBatch.TabIndex = 4;
            lblEvictionBatch.Text = "单次淘汰块数:";
            // 
            // numEvictionBatch
            numEvictionBatch.Location = new Point(170, 99);
            numEvictionBatch.Maximum = new decimal(new int[] { 65536, 0, 0, 0 });
            numEvictionBatch.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numEvictionBatch.Name = "numEvictionBatch";
            numEvictionBatch.Size = new Size(100, 23);
            numEvictionBatch.TabIndex = 5;
            numEvictionBatch.Value = new decimal(new int[] { 256, 0, 0, 0 });
            // 
            // groupBoxAdvancedL2
            groupBoxAdvancedL2.Controls.Add(lblSsdConservative);
            groupBoxAdvancedL2.Controls.Add(trackSsdConservative);
            groupBoxAdvancedL2.Controls.Add(lblSsdConservativeVal);
            groupBoxAdvancedL2.Controls.Add(lblGhostFraction);
            groupBoxAdvancedL2.Controls.Add(numGhostFraction);
            groupBoxAdvancedL2.Location = new Point(440, 335);
            groupBoxAdvancedL2.Name = "groupBoxAdvancedL2";
            groupBoxAdvancedL2.Size = new Size(400, 140);
            groupBoxAdvancedL2.TabIndex = 5;
            groupBoxAdvancedL2.TabStop = false;
            groupBoxAdvancedL2.Text = "L2 调参";
            // 
            // lblSsdConservative
            lblSsdConservative.AutoSize = true;
            lblSsdConservative.Location = new Point(15, 30);
            lblSsdConservative.Name = "lblSsdConservative";
            lblSsdConservative.Size = new Size(95, 17);
            lblSsdConservative.TabIndex = 0;
            lblSsdConservative.Text = "保守线 (占用率):";
            // 
            // trackSsdConservative
            trackSsdConservative.Location = new Point(15, 55);
            trackSsdConservative.Maximum = 99;
            trackSsdConservative.Minimum = 50;
            trackSsdConservative.Name = "trackSsdConservative";
            trackSsdConservative.Size = new Size(300, 45);
            trackSsdConservative.TabIndex = 1;
            trackSsdConservative.TickFrequency = 5;
            trackSsdConservative.Value = 80;
            trackSsdConservative.Scroll += trackSsdConservative_Scroll;
            // 
            // lblSsdConservativeVal
            lblSsdConservativeVal.AutoSize = true;
            lblSsdConservativeVal.Location = new Point(325, 55);
            lblSsdConservativeVal.Name = "lblSsdConservativeVal";
            lblSsdConservativeVal.Size = new Size(32, 17);
            lblSsdConservativeVal.TabIndex = 2;
            lblSsdConservativeVal.Text = "80%";
            // 
            // lblGhostFraction
            lblGhostFraction.AutoSize = true;
            lblGhostFraction.Location = new Point(15, 105);
            lblGhostFraction.Name = "lblGhostFraction";
            lblGhostFraction.Size = new Size(75, 17);
            lblGhostFraction.TabIndex = 3;
            lblGhostFraction.Text = "ghost 倍率:";
            // 
            // numGhostFraction
            numGhostFraction.DecimalPlaces = 2;
            numGhostFraction.Increment = new decimal(new int[] { 1, 0, 0, 65536 });
            numGhostFraction.Location = new Point(170, 102);
            numGhostFraction.Maximum = new decimal(new int[] { 4, 0, 0, 0 });
            numGhostFraction.Minimum = new decimal(new int[] { 0, 0, 0, 0 });
            numGhostFraction.Name = "numGhostFraction";
            numGhostFraction.Size = new Size(100, 23);
            numGhostFraction.TabIndex = 4;
            numGhostFraction.Value = new decimal(new int[] { 1, 0, 0, 0 });
            // 
            // groupBoxRemote
            groupBoxRemote.Controls.Add(lblRemotePort);
            groupBoxRemote.Controls.Add(numRemotePort);
            groupBoxRemote.Controls.Add(lblRemoteAddress);
            groupBoxRemote.Controls.Add(txtRemoteAddress);
            groupBoxRemote.Location = new Point(440, 485);
            groupBoxRemote.Name = "groupBoxRemote";
            groupBoxRemote.Size = new Size(400, 105);
            groupBoxRemote.TabIndex = 6;
            groupBoxRemote.TabStop = false;
            groupBoxRemote.Text = "远程";
            // 
            // lblRemotePort
            lblRemotePort.AutoSize = true;
            lblRemotePort.Location = new Point(15, 32);
            lblRemotePort.Name = "lblRemotePort";
            lblRemotePort.Size = new Size(71, 17);
            lblRemotePort.TabIndex = 0;
            lblRemotePort.Text = "监听端口:";
            // 
            // numRemotePort
            numRemotePort.Location = new Point(130, 29);
            numRemotePort.Maximum = new decimal(new int[] { 65535, 0, 0, 0 });
            numRemotePort.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numRemotePort.Name = "numRemotePort";
            numRemotePort.Size = new Size(90, 23);
            numRemotePort.TabIndex = 1;
            numRemotePort.Value = new decimal(new int[] { 10808, 0, 0, 0 });
            // 
            // lblRemoteAddress
            lblRemoteAddress.AutoSize = true;
            lblRemoteAddress.Location = new Point(15, 67);
            lblRemoteAddress.Name = "lblRemoteAddress";
            lblRemoteAddress.Size = new Size(71, 17);
            lblRemoteAddress.TabIndex = 2;
            lblRemoteAddress.Text = "对端地址:";
            // 
            // txtRemoteAddress
            txtRemoteAddress.Location = new Point(130, 64);
            txtRemoteAddress.Name = "txtRemoteAddress";
            txtRemoteAddress.Size = new Size(240, 23);
            txtRemoteAddress.TabIndex = 3;
            // 
            // 底部按钮
            // 
            btnDocs.Location = new Point(20, 615);
            btnDocs.Name = "btnDocs";
            btnDocs.Size = new Size(100, 35);
            btnDocs.TabIndex = 7;
            btnDocs.Text = "文档";
            btnDocs.UseVisualStyleBackColor = true;
            btnDocs.Click += btnDocs_Click;
            // 
            btnSave.Location = new Point(740, 615);
            btnSave.Name = "btnSave";
            btnSave.Size = new Size(100, 35);
            btnSave.TabIndex = 8;
            btnSave.Text = "确定并保存";
            btnSave.UseVisualStyleBackColor = true;
            btnSave.Click += btnSave_Click;
            // 
            // Settings
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(860, 670);
            Controls.Add(lblBasicHeader);
            Controls.Add(lblAdvancedHeader);
            Controls.Add(groupBoxCache);
            Controls.Add(groupBoxSsd);
            Controls.Add(lblMemEstimate);
            Controls.Add(groupBoxDisk);
            Controls.Add(groupBoxAdvancedL1);
            Controls.Add(groupBoxAdvancedL2);
            Controls.Add(groupBoxRemote);
            Controls.Add(btnDocs);
            Controls.Add(btnSave);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "Settings";
            StartPosition = FormStartPosition.CenterParent;
            Text = "软件设置";
            Load += Settings_Load;
            ((System.ComponentModel.ISupportInitialize)trackEviction).EndInit();
            ((System.ComponentModel.ISupportInitialize)trackStop).EndInit();
            ((System.ComponentModel.ISupportInitialize)numListenPort).EndInit();
            ((System.ComponentModel.ISupportInitialize)numEvictionWindow).EndInit();
            ((System.ComponentModel.ISupportInitialize)numEvictionGrace).EndInit();
            ((System.ComponentModel.ISupportInitialize)numEvictionBatch).EndInit();
            ((System.ComponentModel.ISupportInitialize)trackSsdConservative).EndInit();
            ((System.ComponentModel.ISupportInitialize)numGhostFraction).EndInit();
            ((System.ComponentModel.ISupportInitialize)numRemotePort).EndInit();
            groupBoxCache.ResumeLayout(false);
            groupBoxCache.PerformLayout();
            groupBoxSsd.ResumeLayout(false);
            groupBoxSsd.PerformLayout();
            groupBoxDisk.ResumeLayout(false);
            groupBoxDisk.PerformLayout();
            groupBoxAdvancedL1.ResumeLayout(false);
            groupBoxAdvancedL1.PerformLayout();
            groupBoxAdvancedL2.ResumeLayout(false);
            groupBoxAdvancedL2.PerformLayout();
            groupBoxRemote.ResumeLayout(false);
            groupBoxRemote.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private CheckBox chkEnableCache;
        private Label lblBasicHeader;
        private Label lblAdvancedHeader;
        private Label lblEviction;
        private TrackBar trackEviction;
        private Label lblStop;
        private TrackBar trackStop;
        private Button btnSave;
        private Button btnDocs;
        private Label lblEvictionVal;
        private Label lblStopVal;
        private GroupBox groupBoxCache;
        private GroupBox groupBoxSsd;
        private Label lblL2Summary;
        private Button btnL2Manage;
        private Label lblMemEstimate;
        private GroupBox groupBoxDisk;
        private Label label2;
        private TextBox txtListenAddress;
        private Label lblListenPort;
        private NumericUpDown numListenPort;
        private CheckBox chkAutoIscsiLogin;
        private GroupBox groupBoxAdvancedL1;
        private Label lblEvictionWindow;
        private NumericUpDown numEvictionWindow;
        private Label lblEvictionGrace;
        private NumericUpDown numEvictionGrace;
        private Label lblEvictionBatch;
        private NumericUpDown numEvictionBatch;
        private GroupBox groupBoxAdvancedL2;
        private Label lblSsdConservative;
        private TrackBar trackSsdConservative;
        private Label lblSsdConservativeVal;
        private Label lblGhostFraction;
        private NumericUpDown numGhostFraction;
        private GroupBox groupBoxRemote;
        private Label lblRemotePort;
        private NumericUpDown numRemotePort;
        private Label lblRemoteAddress;
        private TextBox txtRemoteAddress;
    }
}
