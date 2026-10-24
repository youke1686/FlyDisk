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
using FlyDisk.Engine;
using FlyDisk.Localization;

namespace FlyDisk
{
    /// <summary>
    /// 全程序统一的硬盘描述。**只在这一处拼盘名**，任何界面都不许再自己写
    /// `磁盘 {0}（{1}）` 之类的字符串——否则早晚长出第七种句式。
    ///
    /// 两种排版，零件完全相同（盘号 → 盘符/脱机 → 型号 → 容量）：
    /// * <see cref="Wide"/>：选择框的列表行，用全角空格分区，便于扫视；
    /// * <see cref="Full"/>：弹窗、日志、标题等正文场合，单层括号 + 逗号分隔。
    ///
    /// 三条口径：
    /// 1. **盘符槽只看 <see cref="PhysicalDiskInfo.IsOnline"/>**：脱机就写「脱机」，联机才写盘符；
    ///    联机但没有盘符（未格式化）时整段省略。加速中的源盘是脱机的，所以它显示「脱机」。
    /// 2. **型号为空统一写「未知型号」**，不再出现 `磁盘 0（）`。
    /// 3. **容量统一用 TiB/GiB/MiB 自动换档**（与检查器一致），不写 `3726.0 GiB`。
    /// </summary>
    internal static class DiskText
    {
        /// <summary>简称：`磁盘 0`。用于日志续报、引擎错误等"盘已经指认过"的场合。</summary>
        public static string Short(int diskNumber) => Locale.T("disk.short", diskNumber);

        /// <summary>型号：空串 / 全空白都回落成「未知型号」。</summary>
        public static string Model(string? model) =>
            string.IsNullOrWhiteSpace(model) ? Locale.T("disk.unknownModel") : model;

        /// <summary>硬盘容量：≥1 TiB 用 TiB、≥1 GiB 用 GiB，否则 MiB（保留 1 位小数）。</summary>
        public static string Capacity(long bytes)
        {
            double b = bytes;
            if (b >= 1024.0 * 1024 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024 * 1024)).ToString("F1") + " TiB";
            if (b >= 1024.0 * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("F1") + " GiB";
            return (b / (1024.0 * 1024)).ToString("F1") + " MiB";
        }

        /// <summary>
        /// 盘符槽的正文（不含括号）：联机有盘符 → `E:, D:`；脱机 → `脱机`；联机无盘符 → 空串。
        /// <paramref name="forceOffline"/> 用于"此刻必然脱机"的场合（停止失败、运行中的源盘），
        /// 避免拿着选盘时的旧快照把盘符又写出来。
        /// </summary>
        public static string DriveText(PhysicalDiskInfo info, bool forceOffline = false)
        {
            if (forceOffline || !info.IsOnline) return Locale.T("disk.offline");
            return info.DriveLetters.Count > 0 ? string.Join(", ", info.DriveLetters) : string.Empty;
        }

        /// <summary>窄格式：`磁盘 0（E:, D:，型号，111.8 GiB）`；脱机时盘符槽换成「脱机」。</summary>
        public static string Full(PhysicalDiskInfo info, bool forceOffline = false, bool accelerating = false) =>
            Full(info.DiskNumber, info.Model, info.SizeBytes, info.IsOnline, info.DriveLetters, forceOffline, accelerating);

        /// <summary>窄格式（字段拆开传入）：给只有零散字段的调用方（检查器、校验窗口）。</summary>
        public static string Full(int diskNumber, string? model, long sizeBytes, bool isOnline,
            IReadOnlyList<string>? driveLetters, bool forceOffline = false, bool accelerating = false)
        {
            string driveText;
            if (forceOffline || !isOnline)
            {
                driveText = Locale.T("disk.offline");
            }
            else
            {
                driveText = driveLetters is { Count: > 0 } ? string.Join(", ", driveLetters) : string.Empty;
            }

            var parts = new List<string>(4);
            if (driveText.Length > 0) parts.Add(driveText);
            if (accelerating) parts.Add(Locale.T("disk.accelerating"));
            parts.Add(Model(model));
            parts.Add(Capacity(sizeBytes));

            return Locale.T("disk.narrow", diskNumber, string.Join(Locale.T("disk.attrSeparator"), parts));
        }

        /// <summary>宽格式：选择框的列表行，`磁盘 0（E:, D:）　　型号　　（111.8 GiB）`。</summary>
        public static string Wide(PhysicalDiskInfo info)
        {
            string driveText = DriveText(info);
            string driveSlot = driveText.Length > 0 ? Locale.T("disk.driveSlot", driveText) : string.Empty;
            return Locale.T("disk.wide", info.DiskNumber, driveSlot, Model(info.Model), Capacity(info.SizeBytes));
        }

        /// <summary>
        /// 已配置但当前没接上的盘（L2 管理）：身份串是 `型号|序列号`，这里翻译成人话。
        /// </summary>
        public static string NotConnected(string identity)
        {
            int sep = identity.LastIndexOf('|');
            string model = sep >= 0 ? identity.Substring(0, sep) : identity;
            string serial = sep >= 0 ? identity.Substring(sep + 1) : string.Empty;

            string body = serial.Length > 0
                ? Locale.T("disk.modelSerial", Model(model), serial)
                : Model(model);
            return Locale.T("disk.notConnected", body);
        }
    }
}
