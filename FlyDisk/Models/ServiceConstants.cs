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
using System.IO;

namespace FlyDisk.Models
{
    /// <summary>
    /// 全局常量：单进程形态下就是本程序自己的名称、路径与尺寸约定。
    /// （阶段一时它叫"服务常量"、由 UI 与服务两个进程共用；单进程后只剩路径与尺寸两类事实。）
    /// </summary>
    public static class ServiceConstants
    {
        /// <summary>
        /// 缓存块大小（4KB）。**唯一定义点**：缓存引擎的 Block、块设备的 "BytesPerSector × SectorsPerBlock"
        /// 校验都必须引用它，避免"同一事实多处硬编码"（见 未解决的疑点.md TD-20）。
        /// </summary>
        public const int BlockSize = 4096;

        /// <summary>
        /// Slab（整块）大小（64MB）。**不特指某种介质**：L1 非托管内存池按它向系统申请 / 归还内存，
        /// L2 容器按它整块增长 / 截断（见 docs/L2容器按需增长与容量缩放_设计.md）。
        /// 与 <see cref="BlockSize"/> 一起作为唯一定义点（见 未解决的疑点.md TD-20）。
        /// </summary>
        public const int SlabSize = 64 * 1024 * 1024;

        /// <summary>
        /// SSD 二级缓存（L2）在缓存盘上的目录名。UI 用"缓存盘 + 该目录名"组合出 `SsdCachePath`，
        /// 引擎按该目录存放容器（cache.dat）与索引（index.bin）——见 关于内存缓存的进一步讨论.md §7.5。
        /// </summary>
        public const string SsdCacheDirectoryName = "FlyDisk.Cache";

        /// <summary>
        /// 共享数据目录（%ProgramData%\FlyDisk）：放配置文件与诊断日志。
        /// 仍在 ProgramData 而不是用户目录——它曾经是"UI 与服务两进程共享"的落点，
        /// 现在只有一个进程，但路径保持不变（避免老配置与诊断日志被遗落在别处）。
        /// </summary>
        public static readonly string DataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "FlyDisk");

        /// <summary>配置文件路径（本程序启动时读取；设置对话框保存时写入）</summary>
        public static readonly string ConfigFilePath = Path.Combine(DataDirectory, "config.json");
    }
}
