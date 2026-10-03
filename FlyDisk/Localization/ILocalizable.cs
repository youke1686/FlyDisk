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

using System.Collections.Generic;
using System.Windows.Forms;

namespace FlyDisk.Localization
{
    /// <summary>
    /// 可本地化的窗体（见 后续待办.md 第八节）：**半自动**——窗体显式登记自己那些"静态文本"控件，
    /// 中央语言管理器（<see cref="LocalizationManager"/>）遍历 <see cref="Application.OpenForms"/> 时靠它取表刷新。
    ///
    /// **刻意不用 `Tag` 存键**：`Tag` 是万能 `object` 槽，可能被别处占用，撞了不报错、只会静默出错。
    /// </summary>
    public interface ILocalizable
    {
        /// <summary>
        /// 本窗体的控件登记表：**文本键 → 控件们**。一条键可绑多个控件
        /// （例如各窗体的「确定」按钮共用一个键）。只放**静态文本**；动态文本走 <see cref="ApplyDynamicText"/>。
        /// </summary>
        Dictionary<string, List<Control>> TextBindings { get; }

        /// <summary>
        /// 管理器刷完登记表后调用：重刷那些**不由控件 `Text` 直接承载**的文本，例如
        /// 菜单项 / 状态栏（`ToolStripItem` 不继承 `Control`）以及状态机驱动的当前句子
        /// （如状态栏的"已启动"、主按钮文案）——后者要重跑一遍状态渲染函数，否则会停留在旧语言。
        /// </summary>
        void ApplyDynamicText();
    }
}
