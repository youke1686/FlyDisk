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
using System.Text;
using System.Text.Json;

namespace FlyDisk.Models
{
    /// <summary>
    /// **程序本地状态**持久化：`%ProgramData%\FlyDisk\state.json`。
    ///
    /// 文件顶部固定放多行 `//` 注释（说明"程序维护、请勿手改"），读取时跳过开头的空行 / 注释行。
    /// 文件缺失 / 损坏 / 版本不符 ⇒ 重置为默认状态（重新确认一次知悉、忘记上次选择），不拦启动。
    /// </summary>
    public static class StateService
    {
        private static readonly string StatePath = ServiceConstants.StateFilePath;

        /// <summary>状态文件顶部的醒目注释（每次保存都重新写入）</summary>
        private const string CommentHeader =
@"// ================================================================
// 本文件由 FlyDisk 自动维护，保存本机状态：确认标记、上次选盘、设备快照等。
// 请不要手动修改本文件；用户配置请编辑同目录下的 config.json。
// 删除或改坏本文件只会让程序重新确认一次 / 忘记上次选择，不会损坏磁盘数据。
// ================================================================
";

        /// <summary>最近一次"状态重置"的原因（供启动流程落诊断日志；空 = 没有重置）</summary>
        public static string LastResetReason { get; private set; } = string.Empty;

        public static AppState Load()
        {
            LastResetReason = string.Empty;
            try
            {
                if (!File.Exists(StatePath)) return NewDefault();

                string json = StripLeadingComments(File.ReadAllText(StatePath));
                AppState? state = JsonSerializer.Deserialize<AppState>(json);
                if (state == null)
                {
                    return ResetToDefault("state.json 解析结果为空");
                }
                if (state.SchemaVersion != ServiceConstants.StateSchemaVersion)
                {
                    return ResetToDefault(
                        $"state.json 版本 {state.SchemaVersion} != {ServiceConstants.StateSchemaVersion}");
                }
                return state;
            }
            catch (Exception ex)
            {
                return ResetToDefault(ex.Message);
            }
        }

        /// <summary>保存状态；失败返回 false 并给出原因（调用方决定是否提示）</summary>
        public static bool Save(AppState state, out string error)
        {
            error = string.Empty;
            string tempPath = StatePath + ".tmp";
            try
            {
                Directory.CreateDirectory(ServiceConstants.DataDirectory);
                state.SchemaVersion = ServiceConstants.StateSchemaVersion;

                string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, CommentHeader + json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                if (File.Exists(StatePath)) File.Replace(tempPath, StatePath, null);
                else File.Move(tempPath, StatePath);
                return true;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* 清理失败无补救手段 */ }
                error = ex.Message;
                return false;
            }
        }

        private static AppState NewDefault()
            => new() { SchemaVersion = ServiceConstants.StateSchemaVersion };

        /// <summary>
        /// 把状态里的**每盘派生快照**灌进配置条目（<see cref="L2DiskConfig.OwnerHash"/> /
        /// <see cref="L2DiskConfig.BytesPerSector"/> 是运行期字段，不写 config.json）。
        /// </summary>
        public static void ApplySnapshots(DiskConfig config, AppState state)
        {
            foreach (L2DiskConfig entry in config.L2Disks)
            {
                L2DiskSnapshot? snapshot = state.FindSnapshot(entry.DiskIdentity);
                if (snapshot == null) continue;
                entry.OwnerHash = snapshot.OwnerHash;
                entry.BytesPerSector = snapshot.BytesPerSector;
            }
        }

        /// <summary>把配置条目里的派生数据回写到状态（保存 state.json 之前调用）</summary>
        public static void CaptureSnapshots(DiskConfig config, AppState state)
        {
            state.L2DiskSnapshots.Clear();
            foreach (L2DiskConfig entry in config.L2Disks)
            {
                if (string.IsNullOrEmpty(entry.DiskIdentity)) continue;
                if (entry.OwnerHash == 0 && entry.BytesPerSector <= 0) continue;
                state.L2DiskSnapshots.Add(new L2DiskSnapshot
                {
                    DiskIdentity = entry.DiskIdentity,
                    OwnerHash = entry.OwnerHash,
                    BytesPerSector = entry.BytesPerSector
                });
            }
        }

        private static AppState ResetToDefault(string reason)
        {
            LastResetReason = reason;
            try
            {
                if (File.Exists(StatePath)) File.Move(StatePath, StatePath + ".old", overwrite: true);
            }
            catch
            {
                // 改名失败也不拦启动：状态本来就可以用默认值继续
            }
            return NewDefault();
        }

        /// <summary>
        /// 跳过开头连续的空行与注释行（`//` / `#`），返回第一个非注释行开始的 JSON 文本。
        /// 用户在 JSON 中间写注释导致解析失败时，交给反序列化抛错 ⇒ 按"状态损坏"重置。
        /// </summary>
        private static string StripLeadingComments(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            int index = 0;
            while (index < text.Length)
            {
                int lineEnd = text.IndexOf('\n', index);
                int next = lineEnd < 0 ? text.Length : lineEnd + 1;
                string line = (lineEnd < 0 ? text.Substring(index) : text.Substring(index, lineEnd - index)).Trim();

                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) ||
                    line.StartsWith("#", StringComparison.Ordinal))
                {
                    index = next;
                    continue;
                }
                break;
            }
            return text.Substring(index);
        }
    }
}
