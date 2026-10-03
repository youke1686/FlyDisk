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
    /// 中央语言管理器（见 后续待办.md 第八节）：语言切换后**即时生效**的唯一入口。
    ///
    /// 做法：遍历 <see cref="Application.OpenForms"/>，对实现了 <see cref="ILocalizable"/> 的窗体，
    /// 先按登记表逐项刷 `Text`，再让它重跑自己的动态文本（菜单 / 状态栏 / 状态机句子）。
    /// </summary>
    public static class LocalizationManager
    {
        /// <summary>把所有已打开窗体刷成当前语言（即时切换；新开的窗体在构造时自己取词）</summary>
        public static void ApplyAll()
        {
            foreach (Form form in Application.OpenForms)
            {
                Apply(form);
            }
        }

        /// <summary>刷新单个窗体（登记表 + 动态文本）</summary>
        public static void Apply(Form form)
        {
            if (form is not ILocalizable localizable) return;

            foreach (KeyValuePair<string, List<Control>> pair in localizable.TextBindings)
            {
                string text = Locale.T(pair.Key);
                foreach (Control control in pair.Value)
                {
                    control.Text = text;
                }
            }

            localizable.ApplyDynamicText();
        }
    }
}
