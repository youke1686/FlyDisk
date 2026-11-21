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

namespace FlyDisk.Models
{
    /// <summary>
    /// 一块盘的**派生快照**（程序维护，用户不应手改）：
    /// 设备身份散列（用于拼缓存目录名与匹配扫描结果）与逻辑扇区大小（盘没接上时用于估算块大小）。
    /// </summary>
    public sealed class L2DiskSnapshot
    {
        /// <summary>盘身份串（`型号|序列号`）</summary>
        public string DiskIdentity { get; set; } = string.Empty;

        /// <summary>设备身份散列（与容器头里的归属身份同一套算法）</summary>
        public long OwnerHash { get; set; }

        /// <summary>逻辑扇区大小快照（0 = 尚未记录）</summary>
        public int BytesPerSector { get; set; }

        public L2DiskSnapshot Clone() => (L2DiskSnapshot)MemberwiseClone();
    }

    /// <summary>
    /// **程序本地状态**（`%ProgramData%\FlyDisk\state.json`）：确认标记、上次选盘、设备派生快照等。
    /// 理论上用户不应手动修改；文件被删 / 改坏只会让程序重新确认一次或忘记上次选择，不损坏任何磁盘数据。
    /// </summary>
    public sealed class AppState
    {
        /// <summary>状态格式版本；缺失 / 不符 ⇒ 重置状态</summary>
        public int SchemaVersion { get; set; }

        /// <summary>是否已确认**使用前请知悉**</summary>
        public bool WarningAccepted { get; set; }

        /// <summary>
        /// 是否已确认**风险绕过开关**的风险。删除 / 关闭 <c>AllowRiskBypass</c> 后会被清零，
        /// 下次手动打开需要重新确认。
        /// </summary>
        public bool RiskBypassAccepted { get; set; }

        /// <summary>上次选择的那块物理盘的**身份串**（只用于选盘框预选）</summary>
        public string PhysicalDiskIdentity { get; set; } = string.Empty;

        /// <summary>上次「启动加速」里选中的**全部**盘身份串（只用于预选）</summary>
        public List<string> LastAcceleratedDiskIdentities { get; set; } = new();

        // ===== 旧版单盘 L2 配置（不再受支持，仅远程形态兜底）=====

        /// <summary>【旧版】是否启用 SSD 二级缓存（仅远程形态兜底）</summary>
        public bool EnableSsdCache { get; set; }

        /// <summary>【旧版】L2 缓存目录（仅远程形态兜底）</summary>
        public string SsdCachePath { get; set; } = string.Empty;

        /// <summary>【旧版】L2 容器容量上限（仅远程形态兜底）</summary>
        public long SsdCacheMaxBytes { get; set; } = L2DiskConfig.DefaultMaxBytes;

        // ===== 每盘派生快照 =====

        /// <summary>按 <see cref="L2DiskSnapshot.DiskIdentity"/> 匹配的派生快照</summary>
        public List<L2DiskSnapshot> L2DiskSnapshots { get; set; } = new();

        /// <summary>找到这块盘的派生快照；没有返回 null</summary>
        public L2DiskSnapshot? FindSnapshot(string diskIdentity)
        {
            if (string.IsNullOrEmpty(diskIdentity)) return null;
            foreach (L2DiskSnapshot snapshot in L2DiskSnapshots)
            {
                if (string.Equals(snapshot.DiskIdentity, diskIdentity, StringComparison.Ordinal)) return snapshot;
            }
            return null;
        }

        /// <summary>取得这块盘的派生快照；没有就新建一个空快照</summary>
        public L2DiskSnapshot EnsureSnapshot(string diskIdentity)
        {
            L2DiskSnapshot? snapshot = FindSnapshot(diskIdentity);
            if (snapshot != null) return snapshot;
            snapshot = new L2DiskSnapshot { DiskIdentity = diskIdentity };
            L2DiskSnapshots.Add(snapshot);
            return snapshot;
        }

        /// <summary>删除这块盘的派生快照（删除 L2 条目时用）</summary>
        public void RemoveSnapshot(string diskIdentity)
        {
            if (string.IsNullOrEmpty(diskIdentity)) return;
            L2DiskSnapshots.RemoveAll(s => string.Equals(s.DiskIdentity, diskIdentity, StringComparison.Ordinal));
        }
    }
}
