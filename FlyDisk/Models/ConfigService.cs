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
    /// **用户配置**持久化：`%ProgramData%\FlyDisk\config.json`。
    ///
    /// 划分标准（与 <see cref="StateService"/> 配套）：这个文件里的每一项用户都可以直接编辑；
    /// 程序维护的状态在 `state.json`。文件带 <c>SchemaVersion</c>，**不做向后兼容**：
    /// 缺失 / 不符 / 解析失败 ⇒ 把旧文件改名 `config.json.old`，写出厂默认值。
    /// </summary>
    public static class ConfigService
    {
        private static readonly string ConfigPath = ServiceConstants.ConfigFilePath;

        /// <summary>最近一次"出厂重置"的原因（供启动流程落诊断日志；空 = 没有重置）</summary>
        public static string LastResetReason { get; private set; } = string.Empty;

        public static DiskConfig Load()
        {
            LastResetReason = string.Empty;
            try
            {
                if (!File.Exists(ConfigPath)) return NewDefault();

                string json = File.ReadAllText(ConfigPath);
                DiskConfig? config = JsonSerializer.Deserialize<DiskConfig>(json);
                if (config == null)
                {
                    return ResetToDefault("config.json 解析结果为空");
                }
                if (config.SchemaVersion != ServiceConstants.ConfigSchemaVersion)
                {
                    return ResetToDefault(
                        $"config.json 版本 {config.SchemaVersion} != {ServiceConstants.ConfigSchemaVersion}");
                }
                return config;
            }
            catch (Exception ex)
            {
                // 用户手改坏了也走这里：保留旧文件、出厂重置，不把程序卡死在启动阶段
                return ResetToDefault(ex.Message);
            }
        }

        /// <summary>
        /// 保存配置。**失败不吞**：返回 false 并给出原因，由调用方如实报错。
        /// </summary>
        public static bool Save(DiskConfig config, out string error)
        {
            error = string.Empty;
            string tempPath = ConfigPath + ".tmp";
            try
            {
                Directory.CreateDirectory(ServiceConstants.DataDirectory);
                config.SchemaVersion = ServiceConstants.ConfigSchemaVersion;

                string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                if (File.Exists(ConfigPath)) File.Replace(tempPath, ConfigPath, null);
                else File.Move(tempPath, ConfigPath);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 清理失败无补救手段 */ }
                error = ex.Message;
                return false;
            }
        }

        private static DiskConfig NewDefault()
            => new() { SchemaVersion = ServiceConstants.ConfigSchemaVersion };

        private static DiskConfig ResetToDefault(string reason)
        {
            LastResetReason = reason;
            try
            {
                if (File.Exists(ConfigPath)) File.Move(ConfigPath, ConfigPath + ".old", overwrite: true);
            }
            catch
            {
                // 改名失败也不拦启动：旧文件最多继续留在原地，下次启动会再试一次
            }
            return NewDefault();
        }
    }
}
