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
            lblEviction = new Label();
            trackEviction = new TrackBar();
            lblStop = new Label();
            trackStop = new TrackBar();
            btnSave = new Button();
            btnDocs = new Button();
            lblEvictionVal = new Label();
            lblStopVal = new Label();
            groupBoxDisk = new GroupBox();
            numListenPort = new NumericUpDown();
            label2 = new Label();
            groupBoxCache = new GroupBox();
            groupBoxSsd = new GroupBox();
            chkEnableSsdCache = new CheckBox();
            lblSsdDrive = new Label();
            cmbSsdCacheDrive = new ComboBox();
            lblSsdSize = new Label();
            numSsdCacheGb = new NumericUpDown();
            lblSsdConservative = new Label();
            trackSsdConservative = new TrackBar();
            lblSsdConservativeVal = new Label();
            lblMemEstimate = new Label();
            ((System.ComponentModel.ISupportInitialize)trackEviction).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackStop).BeginInit();
            ((System.ComponentModel.ISupportInitialize)trackSsdConservative).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numListenPort).BeginInit();
            ((System.ComponentModel.ISupportInitialize)numSsdCacheGb).BeginInit();
            groupBoxDisk.SuspendLayout();
            groupBoxCache.SuspendLayout();
            groupBoxSsd.SuspendLayout();
            SuspendLayout();
            // 
            // chkEnableCache
            // 
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
            // 
            lblEviction.AutoSize = true;
            lblEviction.Location = new Point(20, 65);
            lblEviction.Name = "lblEviction";
            lblEviction.Size = new Size(119, 17);
            lblEviction.TabIndex = 1;
            lblEviction.Text = "开始淘汰阈值 (内存):";
            // 
            // trackEviction
            // 
            trackEviction.Location = new Point(20, 90);
            trackEviction.Maximum = 100;
            trackEviction.Name = "trackEviction";
            trackEviction.Size = new Size(300, 45);
            trackEviction.TabIndex = 2;
            trackEviction.TickFrequency = 5;
            trackEviction.Value = 80;
            trackEviction.Scroll += trackEviction_Scroll;
            // 
            // lblStop
            // 
            lblStop.AutoSize = true;
            lblStop.Location = new Point(20, 145);
            lblStop.Name = "lblStop";
            lblStop.Size = new Size(119, 17);
            lblStop.TabIndex = 3;
            lblStop.Text = "停止缓存阈值 (内存):";
            // 
            // trackStop
            // 
            trackStop.Location = new Point(20, 170);
            trackStop.Maximum = 100;
            trackStop.Name = "trackStop";
            trackStop.Size = new Size(300, 45);
            trackStop.TabIndex = 4;
            trackStop.TickFrequency = 5;
            trackStop.Value = 90;
            trackStop.Scroll += trackStop_Scroll;
            // 
            // btnSave
            // 
            btnSave.Location = new Point(310, 618);
            btnSave.Name = "btnSave";
            btnSave.Size = new Size(100, 35);
            btnSave.TabIndex = 5;
            btnSave.Text = "确定并保存";
            btnSave.UseVisualStyleBackColor = true;
            btnSave.Click += btnSave_Click;
            // 
            // btnDocs
            // 
            // 底部左下角：与「确定并保存」同一行、分居两端（风险动作在右，轻量入口在左）
            btnDocs.Location = new Point(20, 618);
            btnDocs.Name = "btnDocs";
            btnDocs.Size = new Size(100, 35);
            btnDocs.TabIndex = 21;
            btnDocs.Text = "文档";
            btnDocs.UseVisualStyleBackColor = true;
            btnDocs.Click += btnDocs_Click;
            // 
            // lblEvictionVal
            // 
            lblEvictionVal.AutoSize = true;
            lblEvictionVal.Location = new Point(325, 90);
            lblEvictionVal.Name = "lblEvictionVal";
            lblEvictionVal.Size = new Size(32, 17);
            lblEvictionVal.TabIndex = 6;
            lblEvictionVal.Text = "80%";
            // 
            // lblStopVal
            // 
            lblStopVal.AutoSize = true;
            lblStopVal.Location = new Point(325, 170);
            lblStopVal.Name = "lblStopVal";
            lblStopVal.Size = new Size(32, 17);
            lblStopVal.TabIndex = 7;
            lblStopVal.Text = "90%";
            // 
            // groupBoxDisk
            // 
            groupBoxDisk.Controls.Add(numListenPort);
            groupBoxDisk.Controls.Add(label2);
            groupBoxDisk.Location = new Point(20, 20);
            groupBoxDisk.Name = "groupBoxDisk";
            groupBoxDisk.Size = new Size(390, 76);
            groupBoxDisk.TabIndex = 8;
            groupBoxDisk.TabStop = false;
            groupBoxDisk.Text = "iSCSI 监听端口（只监听回环，供本机发起程序连接）";
            // 
            // label2
            // 
            label2.AutoSize = true;
            label2.Location = new Point(15, 32);
            label2.Name = "label2";
            label2.Size = new Size(71, 17);
            label2.TabIndex = 3;
            label2.Text = "监听端口:";
            // 
            // numListenPort
            // 
            numListenPort.Location = new Point(110, 29);
            numListenPort.Maximum = new decimal(new int[] { 65535, 0, 0, 0 });
            numListenPort.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numListenPort.Name = "numListenPort";
            numListenPort.Size = new Size(80, 23);
            numListenPort.TabIndex = 14;
            numListenPort.Value = new decimal(new int[] { 3260, 0, 0, 0 });
            // 
            // groupBoxCache
            // 
            groupBoxCache.Controls.Add(chkEnableCache);
            groupBoxCache.Controls.Add(lblEviction);
            groupBoxCache.Controls.Add(lblStopVal);
            groupBoxCache.Controls.Add(trackEviction);
            groupBoxCache.Controls.Add(lblEvictionVal);
            groupBoxCache.Controls.Add(lblStop);
            groupBoxCache.Controls.Add(trackStop);
            groupBoxCache.Location = new Point(20, 106);
            groupBoxCache.Name = "groupBoxCache";
            groupBoxCache.Size = new Size(390, 230);
            groupBoxCache.TabIndex = 9;
            groupBoxCache.TabStop = false;
            groupBoxCache.Text = "内存缓存设置";
            // 
            // chkEnableSsdCache
            // 
            chkEnableSsdCache.AutoSize = true;
            chkEnableSsdCache.Location = new Point(20, 30);
            chkEnableSsdCache.Name = "chkEnableSsdCache";
            chkEnableSsdCache.Size = new Size(131, 21);
            chkEnableSsdCache.TabIndex = 10;
            chkEnableSsdCache.Text = "启用 SSD 二级缓存";
            chkEnableSsdCache.UseVisualStyleBackColor = true;
            chkEnableSsdCache.CheckedChanged += chkCacheToggle_CheckedChanged;
            // 
            // lblSsdDrive
            // 
            lblSsdDrive.AutoSize = true;
            lblSsdDrive.Location = new Point(15, 68);
            lblSsdDrive.Name = "lblSsdDrive";
            lblSsdDrive.Size = new Size(59, 17);
            lblSsdDrive.TabIndex = 11;
            lblSsdDrive.Text = "缓存盘:";
            // 
            // cmbSsdCacheDrive
            // 
            cmbSsdCacheDrive.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbSsdCacheDrive.FormattingEnabled = true;
            cmbSsdCacheDrive.Location = new Point(120, 65);
            cmbSsdCacheDrive.Name = "cmbSsdCacheDrive";
            cmbSsdCacheDrive.Size = new Size(150, 25);
            cmbSsdCacheDrive.TabIndex = 12;
            // 
            // lblSsdSize
            // 
            lblSsdSize.AutoSize = true;
            lblSsdSize.Location = new Point(15, 103);
            lblSsdSize.Name = "lblSsdSize";
            lblSsdSize.Size = new Size(95, 17);
            lblSsdSize.TabIndex = 13;
            lblSsdSize.Text = "容量预算(GiB):";
            // 
            // numSsdCacheGb
            // 
            numSsdCacheGb.Location = new Point(120, 100);
            // 上限 64 GiB：与引擎侧的 MAX_SLOTS（SsdCacheService.MAX_CAPACITY_BYTES = 64 GiB）保持一致，
            // 避免填了 200 GiB 却被静默夹取。**上限的实质是内存**，代价见窗口底部的「预计额外内存」提示。
            numSsdCacheGb.Maximum = new decimal(new int[] { 64, 0, 0, 0 });
            numSsdCacheGb.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            numSsdCacheGb.Name = "numSsdCacheGb";
            numSsdCacheGb.Size = new Size(80, 23);
            numSsdCacheGb.TabIndex = 14;
            numSsdCacheGb.Value = new decimal(new int[] { 8, 0, 0, 0 });
            numSsdCacheGb.ValueChanged += numSsdCacheGb_ValueChanged;
            // 
            // lblSsdConservative
            // 
            lblSsdConservative.AutoSize = true;
            lblSsdConservative.Location = new Point(15, 133);
            lblSsdConservative.Name = "lblSsdConservative";
            lblSsdConservative.Size = new Size(119, 17);
            lblSsdConservative.TabIndex = 16;
            lblSsdConservative.Text = "保守线(占用率):";
            // 
            // trackSsdConservative
            // 
            // 下界 50%：定得太低会让 L2 几乎一直保守（学不动）；上界 99% 对应引擎的夹取区间
            trackSsdConservative.Location = new Point(15, 158);
            trackSsdConservative.Maximum = 99;
            trackSsdConservative.Minimum = 50;
            trackSsdConservative.Name = "trackSsdConservative";
            trackSsdConservative.Size = new Size(300, 45);
            trackSsdConservative.TabIndex = 17;
            trackSsdConservative.TickFrequency = 5;
            trackSsdConservative.Value = 80;
            trackSsdConservative.Scroll += trackSsdConservative_Scroll;
            // 
            // lblSsdConservativeVal
            // 
            lblSsdConservativeVal.AutoSize = true;
            lblSsdConservativeVal.Location = new Point(325, 158);
            lblSsdConservativeVal.Name = "lblSsdConservativeVal";
            lblSsdConservativeVal.Size = new Size(32, 17);
            lblSsdConservativeVal.TabIndex = 18;
            lblSsdConservativeVal.Text = "80%";
            // 
            // lblMemEstimate
            // 
            // 窗口底部的"预计额外内存"提示：文本由 Settings.UpdateMemoryEstimate() 动态生成（含数字，不登记静态绑定）
            lblMemEstimate.AutoSize = false;
            lblMemEstimate.ForeColor = SystemColors.GrayText;
            lblMemEstimate.Location = new Point(20, 566);
            lblMemEstimate.Name = "lblMemEstimate";
            lblMemEstimate.Size = new Size(285, 44);
            lblMemEstimate.TabIndex = 20;
            lblMemEstimate.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // groupBoxSsd
            // 
            groupBoxSsd.Controls.Add(chkEnableSsdCache);
            groupBoxSsd.Controls.Add(lblSsdDrive);
            groupBoxSsd.Controls.Add(cmbSsdCacheDrive);
            groupBoxSsd.Controls.Add(lblSsdSize);
            groupBoxSsd.Controls.Add(numSsdCacheGb);
            groupBoxSsd.Controls.Add(lblSsdConservative);
            groupBoxSsd.Controls.Add(trackSsdConservative);
            groupBoxSsd.Controls.Add(lblSsdConservativeVal);
            groupBoxSsd.Location = new Point(20, 346);
            groupBoxSsd.Name = "groupBoxSsd";
            groupBoxSsd.Size = new Size(390, 213);
            groupBoxSsd.TabIndex = 19;
            groupBoxSsd.TabStop = false;
            groupBoxSsd.Text = "SSD 二级缓存（须与目标盘不同盘；异常终止会使整层作废）";
            // 
            // Settings
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(430, 660);
            Controls.Add(groupBoxSsd);
            Controls.Add(groupBoxCache);
            Controls.Add(groupBoxDisk);
            Controls.Add(btnDocs);
            Controls.Add(btnSave);
            Controls.Add(lblMemEstimate);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Name = "Settings";
            StartPosition = FormStartPosition.CenterParent;
            Text = "软件设置";
            Load += Settings_Load;
            ((System.ComponentModel.ISupportInitialize)trackEviction).EndInit();
            ((System.ComponentModel.ISupportInitialize)trackStop).EndInit();
            ((System.ComponentModel.ISupportInitialize)trackSsdConservative).EndInit();
            ((System.ComponentModel.ISupportInitialize)numListenPort).EndInit();
            ((System.ComponentModel.ISupportInitialize)numSsdCacheGb).EndInit();
            groupBoxDisk.ResumeLayout(false);
            groupBoxDisk.PerformLayout();
            groupBoxCache.ResumeLayout(false);
            groupBoxCache.PerformLayout();
            groupBoxSsd.ResumeLayout(false);
            groupBoxSsd.PerformLayout();
            ResumeLayout(false);
        }

        #endregion

        private CheckBox chkEnableCache;
        private Label lblEviction;
        private TrackBar trackEviction;
        private Label lblStop;
        private TrackBar trackStop;
        private Button btnSave;
        private Button btnDocs;
        private Label lblEvictionVal;
        private Label lblStopVal;
        private GroupBox groupBoxDisk;
        private Label label2;
        private NumericUpDown numListenPort;
        private GroupBox groupBoxCache;
        private GroupBox groupBoxSsd;
        private CheckBox chkEnableSsdCache;
        private Label lblSsdDrive;
        private ComboBox cmbSsdCacheDrive;
        private Label lblSsdSize;
        private NumericUpDown numSsdCacheGb;
        private Label lblSsdConservative;
        private TrackBar trackSsdConservative;
        private Label lblSsdConservativeVal;
        private Label lblMemEstimate;
    }
}
