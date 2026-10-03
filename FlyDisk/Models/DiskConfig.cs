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
    /// 配置模型（阶段二）：被加速的对象是**一整块物理盘**（`\\.\PhysicalDriveN`），
    /// 对外以 iSCSI 块设备的形式交给上层。
    ///
    /// 与阶段一 <c>FileSystemConfig</c> 的差别：不再有源目录 / 挂载点 / 卷名（那是"文件系统形态"的产物），
    /// 换成了物理盘号、IQN、监听端点与"一个缓存块含几个逻辑扇区"。
    /// </summary>
    public class DiskConfig
    {
        // ===== 界面 =====

        /// <summary>
        /// 界面语言（支持 {zh-CN, en-US}，见 后续待办.md 第八节）。
        /// **空 = 尚未检测**：首次打开时按系统语言检测一次并写入（非中文系统 → en-US），此后以配置为准。
        /// 取值规整由 <c>FlyDisk.Localization.Locale.Normalize</c> 负责。
        /// </summary>
        public string Language { get; set; } = string.Empty;

        // ===== 目标设备（iSCSI target）=====

        /// <summary>
        /// 上次选择的那块物理盘的**身份串**（`型号|序列号`；无序列号时回落到 `型号|容量|扇区`）。
        ///
        /// **为什么不存盘号**：盘号只是**枚举顺序**，插拔顺序一变、或同时插着多块盘，
        /// 同一个 N 就可能落到另一块盘上；而我们对目标盘做的是**整盘脱机**——
        /// 选错等于把用户那块盘当场从系统里抹掉（见 后续待办.md 第四节）。
        ///
        /// 它只用于在选盘对话框里**默认选中**（仍须用户点确定），并作为"上次选的是哪块"的记忆。
        /// **粒度是整盘，不是单分区**：同一块盘上的其他卷会一起被 export，停止时也一起回来。
        /// </summary>
        public string PhysicalDiskIdentity { get; set; } = string.Empty;

        /// <summary>目标 IQN（发起程序里看到的目标名）</summary>
        public string TargetIqn { get; set; } = "iqn.flydisk:acceleration-disk";

        /// <summary>监听地址。默认只监听回环（127.0.0.1）：本程序是单机形态，不把盘暴露到网络</summary>
        public string ListenAddress { get; set; } = "127.0.0.1";

        /// <summary>监听端口（iSCSI 标准端口 3260）</summary>
        public int ListenPort { get; set; } = 3260;

        /// <summary>
        /// 一个缓存块包含几个**逻辑扇区**；块字节数 = BytesPerSector × 它。
        /// 512 字节扇区的盘填 8、4Kn 盘填 1 —— 两者都恒等于 4096。
        /// 启动时校验"块字节数 == 4096"，不满足直接拒绝启动（见 阶段二-iSCSI块设备形态.md §3.5）。
        /// </summary>
        public int SectorsPerBlock { get; set; } = 8;

        // ===== 远程形态（见 后续待办.md 第一节）=====
        // **刻意没有"角色"字段**：三职责互斥是**运行期状态**，由"谁监听 / 谁配对"协商产生，
        // 不是配置里的静态值（"配对后选择硬盘前两端对等"）。这里只有两条需要持久化的信息。

        /// <summary>
        /// 开放远程服务时的监听端口（默认 10808）。与 iSCSI 的 ListenPort 是两个独立端口：
        /// 那个是本地回环给发起程序用的，这个是给对端配对用的。
        /// </summary>
        public int RemoteListenPort { get; set; } = 10808;

        /// <summary>
        /// 上次配对的对端地址（`主机:端口`）。**只是地址输入框的记忆**，省得每次手敲——
        /// 它不等于"保持配对"，配对本身每次重来。
        /// </summary>
        public string RemotePeerAddress { get; set; } = string.Empty;

        // ===== L1 内存缓存 =====

        /// <summary>是否启用内存缓存（关闭后仅透传）</summary>
        public bool EnableMemoryCache { get; set; } = true;

        /// <summary>开始淘汰的系统内存占用比例（达到后不再扩池，改为按 LRU 回收最冷的块）</summary>
        public double EvictionThreshold { get; set; } = 0.8;

        /// <summary>停止缓存的系统内存占用比例（越过该水位后不再接纳新数据）</summary>
        public double StopCachingThreshold { get; set; } = 0.9;

        // 以下三项是 LRU 淘汰（时间桶）的参数，**当前没有 UI 控件**，只能手改 config.json；
        // 启动时会打印一条生效值日志（这些参数看不到、也改不到，只能靠日志确认）。
        // 非法值一律夹到安全区间并落警告，不让配置错误把缓存打死（见 关于内存缓存的进一步讨论.md §6.6）。

        /// <summary>淘汰时间轮窗口（秒）：超过该窗口未被命中的块归入"冷尾"，优先被淘汰</summary>
        public int EvictionWindowSeconds { get; set; } = 3600;

        /// <summary>淘汰宽限（秒）：只淘汰至少"老了"这么多秒的块，给正在读的请求让路</summary>
        public int EvictionGraceSeconds { get; set; } = 2;

        /// <summary>单次淘汰摘除的块数（按需触发，摘出的槽位由后续回填复用）</summary>
        public int EvictionBatchBlocks { get; set; } = 256;

        // ===== SSD 二级缓存（L2，见 关于内存缓存的进一步讨论.md §7）=====
        // 定位：给机械盘做「跨重启存活」的持久缓存。**对 SSD 源盘毫无意义**（纯写放大），
        // 因此启动时会拒绝「缓存目录所在的物理盘 == 被加速的物理盘」（那会让缓存读写与源数据抢同一块盘）。
        // 缓存目录不可用时降级为仅 L1 + 警告，不让配置错误把整层打死。

        /// <summary>是否启用 SSD 二级缓存（L2）</summary>
        public bool EnableSsdCache { get; set; } = false;

        /// <summary>L2 缓存目录（容器与索引都放在这里；应指向 SSD，且不能落在被加速的那块盘上）</summary>
        public string SsdCachePath { get; set; } = string.Empty;

        /// <summary>L2 容器容量上限（字节）。**必需项**：元数据约 50 B/块，不设上限会吃掉成百 MB 内存</summary>
        public long SsdCacheMaxBytes { get; set; } = 8L * 1024 * 1024 * 1024;

        /// <summary>L2 的 ghost 条目数相对 M 的倍率（默认 1.0；只存块号，无数据）</summary>
        public double SsdCacheGhostFraction { get; set; } = 1.0;

        /// <summary>
        /// L2 转入**保守**准入的占用率（默认 0.80）。"占用" =（数据槽 + 空洞槽）/ 容器槽数，
        /// 也就是"环上真正被占的份额"（空洞虽然没数据，但环位还占着、拿不回来，见 SsdCacheService 类注释）。
        ///
        /// 未达这条线时**激进**：回源块一律落位（有空闲槽就直接放，不触发淘汰）；
        /// 达到后**保守**：只收 ghost 命中的块，每次准入都要先淘汰一个旧的。
        ///
        /// **引擎内部按"可用槽位"判定**（`_freeCount < 保留量`），本值是它的补数：占用 80% ⇔ 可用 20%。
        /// 两者是同一个约束的两种说法，改这里时 SsdCacheService 里的换算要一起看。
        /// 与 L1 的 <see cref="EvictionThreshold"/> / <see cref="StopCachingThreshold"/> 同族同单位（都是占用率）。
        /// </summary>
        public double SsdConservativeThreshold { get; set; } = 0.80;

        // 注意：L2 的账本**只在正常关服时落盘一次**（运行期一次都不写），掉电/强杀/崩溃 ⇒ 整层作废重来。
        // 所以这里没有"刷盘间隔"之类的配置项，运行期也没有任何写盘开销——见 关于内存缓存的进一步讨论.md §7.6。

        /// <summary>
        /// 诊断开关：逐笔高频痕迹（如每轮淘汰的摘要）是否落盘。
        /// 默认 false —— 本盘是给机械盘做加速的，逐笔同步写日志与产品目标相反（见 未解决的疑点.md TD-14）；
        /// 失败/探针行**始终落盘**（不受本开关影响），排障时把它打开即可看到完整逐笔账本。
        /// </summary>
        public bool TraceFileLogging { get; set; } = false;
    }
}
