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
using System.Text.Json;

namespace FlyDisk.Models
{
    /// <summary>
    /// 配置持久化：配置文件存放在 %ProgramData%\FlyDisk\config.json。
    ///
    /// 阶段一时这个目录还需要"给 BUILTIN\Users 授修改权限"（服务以 SYSTEM 建目录、普通用户的 UI 写不进）；
    /// 单进程后整条 ACL 补齐逻辑都不再需要，随之删除。
    /// </summary>
    public static class ConfigService
    {
        private static readonly string ConfigPath = ServiceConstants.ConfigFilePath;

        public static DiskConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    return JsonSerializer.Deserialize<DiskConfig>(json) ?? new DiskConfig();
                }
            }
            catch
            {
                // 配置读不出来就用默认值（首次运行、文件被改坏都走这里）
            }
            return new DiskConfig();
        }

        /// <summary>
        /// 保存配置。**失败不吞**：返回 false 并给出原因，由调用方（设置对话框）如实报错。
        /// 此前这里吞掉一切异常，而 UI 无条件提示"配置已保存"——用户以为生效、重启后行为却没变
        /// （见 未解决的疑点.md TD-10）。
        /// </summary>
        public static bool Save(DiskConfig config, out string error)
        {
            error = string.Empty;
            try
            {
                Directory.CreateDirectory(ServiceConstants.DataDirectory);
                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
