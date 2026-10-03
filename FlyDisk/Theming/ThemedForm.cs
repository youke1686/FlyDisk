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
using System.Windows.Forms;

namespace FlyDisk.Theming
{
    /// <summary>
    /// 参与换肤的窗体基类（见 深色主题.md §4）。全部窗体都从它派生，于是"上色"只写一遍：
    /// 句柄建好 / 加载完成时各刷一次（标题栏要句柄，所以不能只靠构造函数）。
    ///
    /// **深浅色只在启动时读一次系统设置**（`Program.Main` 里的 <see cref="ThemeManager.Initialize"/>）：
    /// 运行中改系统深浅色**不跟随**——那需要在 WndProc 里收 WM_SETTINGCHANGE，本轮刻意不做。
    /// 因此调色板在整个进程生命周期内是固定的。
    ///
    /// **保留公开无参构造**：VS 设计器要能实例化基类/派生窗体，所以不能只留带参构造。
    /// </summary>
    public class ThemedForm : Form
    {
        /// <summary>
        /// 构造时就把应用图标挂上：标题栏左上角与任务栏按钮都取 <see cref="Form.Icon"/>。
        /// 深浅主题在启动时已定（<see cref="ThemeManager.Initialize"/> 早于任何窗体构造），
        /// 所以这里直接按当前主题取那一枚即可。**保留公开无参构造**：VS 设计器要能实例化。
        /// </summary>
        public ThemedForm()
        {
            Icon = AppIcon.Current;
        }

        /// <summary>
        /// 句柄建好就上色：DWM 的深色标题栏必须此时设置（晚了会看见标题栏闪一下白）。
        /// </summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ThemeManager.Apply(this);
        }

        /// <summary>
        /// 加载时再刷一次。句柄创建可能早于全部子控件就位，这里兜住；
        /// 反复上色是**幂等**的（角色的判定结果与事件挂载都有一次性状态记录），所以多刷无妨。
        /// </summary>
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ThemeManager.Apply(this);
        }
    }
}
