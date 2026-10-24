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

namespace FlyDisk.Engine
{
    /// <summary>
    /// 本次停止时 L2 账本的提交方式。
    ///
    /// * <see cref="None"/>  —— 不提交（没有 L2、或本来就没启用）；
    /// * <see cref="Clean"/> —— 在途命令已全部结束，写出**可信**账本（会先 `FlushFileBuffers`）；
    /// * <see cref="Dirty"/> —— 2 秒内没等到静止，仍然写出一份**近似**索引，但把身份戳写成与容器不符，
    ///   下次启动必然要求校验（见 docs/停机改造与占用查询_施工计划.md §5）。
    /// </summary>
    public enum LedgerCommit
    {
        None = 0,
        Clean = 1,
        Dirty = 2,
    }
}
