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
    /// 一块盘的 L2 配置（阶段 3 起）：每块盘自己的 L2 卷与容量上限。
    /// 同一条目且 <see cref="Enabled"/> = 这块盘参与 L2；停用只表示"本次不用 L2"，缓存文件保留。
    /// `CacheVolume` 为空或 `MaxBytes &lt;= 0` = 配置不完整，启动时会被拒绝
    /// （「L2 管理」允许先留空保存，但绝不静默当成"不启用"）。
    ///
    /// **只放用户可以手改的东西**：派生数据（设备身份散列、扇区快照）放在 <see cref="AppState"/>。
    /// </summary>
    public sealed class L2DiskConfig
    {
        /// <summary>盘身份串（`型号|序列号`）——与 <c>PhysicalDiskHandle.DescribeIdentity</c> 同口径</summary>
        public string DiskIdentity { get; set; } = string.Empty;

        /// <summary>
        /// 是否启用 L2。默认 true（旧 config.json 没有这个字段时保持原有行为）；
        /// false = 只用 L1，缓存文件与账本保留，但本次加速会让账本失效（启动前会询问）。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>L2 缓存卷（存盘符，如 "D:"；空 = 不启用）</summary>
        public string CacheVolume { get; set; } = string.Empty;

        /// <summary>该盘的 L2 容量上限（字节）</summary>
        public long MaxBytes { get; set; } = DefaultMaxBytes;

        /// <summary>默认上限：8 GiB（回收条目时容器头读不出槽数就用它）</summary>
        public const long DefaultMaxBytes = 8L * 1024 * 1024 * 1024;

        /// <summary>
        /// 一个缓存块包含几个**逻辑扇区**（块字节数 = 该盘扇区大小 × 它）。
        /// **0 = 未设置**：按最小块（`ceil(4096 / 扇区)` 个扇区）处理——旧配置没有这个字段时即为此值，
        /// 512 扇区盘等价于 8、4Kn 盘等价于 1，与旧行为一致。
        /// 该值在「L2 管理」里维护：存在可识别容器后只读（块大小锁定）。
        /// </summary>
        public int SectorsPerBlock { get; set; }

        /// <summary>
        /// **运行期派生数据**（不写 config.json；由 <see cref="StateService"/> 从 state.json 灌入）：
        /// 设备身份散列，用于拼缓存目录名与匹配扫描结果。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public long OwnerHash { get; set; }

        /// <summary>
        /// **运行期派生数据**（不写 config.json；由 <see cref="StateService"/> 从 state.json 灌入）：
        /// 逻辑扇区大小快照，盘没接上时用于估算块大小。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int BytesPerSector { get; set; }

        /// <summary>逐字段深拷贝（全部是字符串 / 值类型，浅拷贝即深拷贝）</summary>
        public L2DiskConfig Clone() => (L2DiskConfig)MemberwiseClone();

        /// <summary>找到这块盘的配置条目；没有返回 null</summary>
        public static L2DiskConfig? Find(List<L2DiskConfig> list, string diskIdentity)
        {
            if (string.IsNullOrEmpty(diskIdentity)) return null;
            foreach (L2DiskConfig entry in list)
            {
                if (string.Equals(entry.DiskIdentity, diskIdentity, StringComparison.Ordinal)) return entry;
            }
            return null;
        }
    }

    /// <summary>
    /// **用户可手动修改**的配置（`%ProgramData%\FlyDisk\config.json`）。
    ///
    /// 划分标准：这个文件里的每一项，用户都可以直接编辑；程序维护的"上次选了哪块盘 / 确认状态 /
    /// 设备派生快照"一律不在这里，见 <see cref="AppState"/>。
    /// 文件带 schema 版本；版本不符会直接出厂重置（不迁移，Beta 阶段口径）。
    /// </summary>
    public class DiskConfig
    {
        /// <summary>配置格式版本；缺失 / 不符 ⇒ 出厂重置（见 <see cref="ConfigService"/>）</summary>
        public int SchemaVersion { get; set; }

        /// <summary>浅拷贝（字符串 / 值类型安全；列表按引用共享）。会话副本与 L2 校验都用它。</summary>
        public DiskConfig Clone() => (DiskConfig)MemberwiseClone();

        // ===== 运行期（不落盘）=====

        /// <summary>
        /// **运行期**块几何（不落盘）。由 `Form1.BuildSessionConfig` 按"该盘的 L2 条目 + 源盘扇区"算好后
        /// 随会话副本一起交给引擎与启动前预检；远程形态由引擎按最小块自行推导，可能为 null。
        /// </summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public BlockGeometry? RuntimeGeometry { get; set; }

        /// <summary>运行期：本次会话是否使用 L2（由每盘 L2 条目 / 远程旧字段解析后填入）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool EnableSsdCache { get; set; }

        /// <summary>运行期：本次会话的 L2 缓存目录</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public string SsdCachePath { get; set; } = string.Empty;

        /// <summary>运行期：本次会话的 L2 容量上限（字节）</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public long SsdCacheMaxBytes { get; set; } = L2DiskConfig.DefaultMaxBytes;

        // ===== 界面 =====

        /// <summary>
        /// 界面语言（支持 {zh-CN, en-US}）。空 = 尚未检测：
        /// 首次打开时按系统语言检测一次并写入（非中文系统 → en-US），此后以配置为准。
        /// </summary>
        public string Language { get; set; } = string.Empty;

        /// <summary>
        /// 是否在启动加速时用 iSCSI 发起端**自动登录**目标。
        /// 关闭后：引擎照常起 target / 建 LUN，但不做自动登录，由用户手动挂载。
        /// </summary>
        public bool EnableAutoIscsiLogin { get; set; } = true;

        /// <summary>
        /// **隐藏开关**：是否在"账本不可信"的提示里额外显示「无视风险启动」。
        ///
        /// 默认 false，界面**不提供**打开入口——必须手动编辑 config.json 才能开启；
        /// 开启后首次启动还会弹一次风险确认（见 <c>RiskBypassForm</c>）。
        /// 只对"盘启动时联机 / 计数变化 / 计数不可读"生效，不覆盖"上次没正常关服"。
        /// </summary>
        public bool AllowRiskBypass { get; set; }

        // ===== 目标设备（iSCSI target）=====

        /// <summary>目标 IQN（发起程序里看到的目标名）</summary>
        public string TargetIqn { get; set; } = "iqn.flydisk:acceleration-disk";

        /// <summary>监听地址。默认只监听回环（127.0.0.1）：本程序是单机形态，不把盘暴露到网络</summary>
        public string ListenAddress { get; set; } = "127.0.0.1";

        /// <summary>监听端口（iSCSI 标准端口 3260）</summary>
        public int ListenPort { get; set; } = 3260;

        // ===== 远程形态 =====

        /// <summary>开放远程服务时的监听端口（默认 10808）。与 iSCSI 的 ListenPort 是两个独立端口</summary>
        public int RemoteListenPort { get; set; } = 10808;

        /// <summary>上次配对的对端地址（`主机:端口`）——只是地址输入框的记忆</summary>
        public string RemotePeerAddress { get; set; } = string.Empty;

        // ===== L1 内存缓存 =====

        /// <summary>是否启用内存缓存（关闭后仅透传）</summary>
        public bool EnableMemoryCache { get; set; } = true;

        /// <summary>开始淘汰的系统内存占用比例（达到后不再扩池，改为按 LRU 回收最冷的块）</summary>
        public double EvictionThreshold { get; set; } = 0.8;

        /// <summary>停止缓存的系统内存占用比例（越过该水位后不再接纳新数据）</summary>
        public double StopCachingThreshold { get; set; } = 0.9;

        /// <summary>淘汰时间轮窗口（秒）：超过该窗口未被命中的块归入"冷尾"，优先被淘汰</summary>
        public int EvictionWindowSeconds { get; set; } = 3600;

        /// <summary>淘汰宽限（秒）：只淘汰至少"老了"这么多秒的块，给正在读的请求让路</summary>
        public int EvictionGraceSeconds { get; set; } = 2;

        /// <summary>单次淘汰摘除的块数（按需触发，摘出的槽位由后续回填复用）</summary>
        public int EvictionBatchBlocks { get; set; } = 256;

        // ===== SSD 二级缓存（L2）=====

        /// <summary>
        /// **每块盘各自的 L2 配置**：盘身份 + 该盘选的 L2 卷 + 容量上限 + 每块扇区数。
        /// 没有条目的盘 = 不启用 L2；条目存在但 `CacheVolume` 为空（或 `MaxBytes &lt;= 0`）= **配置不完整**，
        /// 启动前统一拦截，不会静默跳过。
        /// </summary>
        public List<L2DiskConfig> L2Disks { get; set; } = new();

        /// <summary>L2 的 ghost 条目数相对 M 的倍率（默认 1.0；只存块号，无数据）</summary>
        public double SsdCacheGhostFraction { get; set; } = 1.0;

        /// <summary>
        /// L2 转入**保守**准入的占用率（默认 0.80）。"占用" =（数据槽 + 空洞槽）/ 容器槽数。
        /// 未达这条线时**激进**：回源块一律落位；达到后**保守**：只收 ghost 命中的块。
        /// </summary>
        public double SsdConservativeThreshold { get; set; } = 0.80;

        // ===== 诊断 =====

        /// <summary>
        /// 诊断开关：逐笔高频痕迹是否落盘。默认 false；失败 / 探针行始终落盘。
        /// </summary>
        public bool TraceFileLogging { get; set; } = false;
    }
}
