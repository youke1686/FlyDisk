# AGENTS.md

本文件供 AI 编码代理（Agent）阅读，用于快速了解本仓库的结构、技术背景与工作规则。在执行任何任务前，请先完整阅读本文件并严格遵守其中的规则。

> **当前仓库订正（2026-10-06，以下条目优先于下文的历史描述）**：
> - 唯一项目已经改名为 `FlyDisk/FlyDisk.csproj`，命名空间为 `FlyDisk`、`FlyDisk.Engine`、`FlyDisk.Models`；还包含 `Theming/` 与 `Localization/`。数据目录为 `%ProgramData%\FlyDisk\`。
> - 自动挂载由 `Engine/IscsiInitiator.cs` 调用系统 MSiSCSI 服务与 iscsidsc API；只清理本程序的会话/门户，不停止系统服务。主窗体启停在后台执行，弹窗与状态刷新回 UI 线程；启停及延后收尾期间禁止重入、改设置、校验和重新联机。
> - L1 查找、提升、拷贝与失效共用 `_wheelLock`，不得依赖宽限期保护裸指针。槽位带所属 Slab 与槽号，归还为 O(1)。内存水位读数最多复用 100 ms，水位节拍仍直接读系统；LRU / L2 M 环 + ghost 准入策略保持原样。
> - 停服：关命令入口与在途计数必须原子同步。等在途命令最多 2 秒；确认静止才冻结、提交 L2 账本、释放全部 L1 Slab、关闭本地句柄。若超时，不提交本次账本，后台等待在途命令实际退出后再释放资源；此期间 `TargetService.IsStopping=true`，不得重新启动或联机。关机遇到这种情况也必须保留待校验状态，不能承诺账本已成功提交。
> - L2 运行期读写 IO 故障：停用 L2，读取回源，写入失败作废受影响映射，未登记的新槽退回空闲栈；本次停止跳过账本提交，下一次启动要求校验或重建。源盘写请求失败可能已部分完成，必须作废整个请求涉及的 L1/L2 范围。
> - 远程大读写按协议上限 1 MiB 分段；重连比较必须保存并使用重连前的设备描述，不能用已被 `BeginService` 覆盖的字段。会话帧先检查长度再分配；超限写帧断开连接，避免未消费的负载破坏后续帧解析。远程沉默等待语义保留。
> - 当前目录的待办入口是 `TODO.md`；下文提及的 `关于内存缓存的进一步讨论.md`、`后续待办.md`、`阶段二-iSCSI块设备形态.md` 与参考目录已不在此精简仓库。不要按历史路径新建开发项目或恢复旧代码。
> - 第五章的禁止编译、运行与执行测试规则仍然适用；这些代码改动目前仅做静态检查，待用户本地验证。

***

## 一、项目概述

RamCacheDisk 是一个 **本地硬盘加速软件**：用内存（L1）+ SSD（L2）两级缓存加速机械盘上的热数据，
对上层提供一块"经过缓存的盘"，而不是把整个数据搬进内存的传统 RAMDisk。

### 当前状态（2026-09-27）：项目已转向阶段二

| | 阶段一（已归档） | 阶段二（进行中） |
| --- | --- | --- |
| 对外形态 | WinFsp 挂载的虚拟盘（缓存代理型文件系统） | 自研 iSCSI target 提供的**块设备**（本地固定盘） |
| 上层看到 | 可被设备层探针一眼认出 | 真实设备栈给出的盘（`bus=iSCSI`） |
| 代码位置 | `废\阶段一-WinFsp文件系统层\` | `RamCacheDisk\`（**单项目**：界面 + `Engine\` 引擎层） |

- **形态已实测通过（2026-09-27）**：用现成 demo 把 `J:`（整块机械盘）脱机后 export，原 NTFS 原样挂回
  （`fsutil fsinfo volumeinfo` 与真盘 **24 项逐行一致**，阶段一缺的那一串全部回来），**原神关联 + 启动成功**。
  零搬运、原数据原地跑。详见 `阶段二-iSCSI块设备形态.md` §6。
- **为什么弃用 WinFsp**：阶段一撞上**驱动层硬边界**——`src/sys/devctl.c` 只把"设备类型带 `0x8000` 位 +
  `METHOD_BUFFERED`"的请求转发到用户态，`src/sys/volinfo.c` 的卷特性位只从 `VolumeParams` 的 10 个位拼出。
  实测后果：原神启动器"关联游戏"按钮灰掉、直启 `YuanShen.exe` 卡死。完整证据链与源码级根因见
  `阶段二-iSCSI块设备形态.md` §1（该文同时记录了官方 `NTFS-Compatibility` 口径与实测的偏差）。
- **复用什么**：两级缓存的"机制"全留（非托管内存池 / LRU 时间桶 / L2 淘汰结构 / 容器与账本 / 统计 / 日志），
  **键要换**（路径 + 文件内块号 → 全局块号）；外围基本不动；宿主与文件系统层重写。§4 有逐项清单。
- **当前形态（2026-09-27，M2 代码已落地）**：**单项目**——`RamCacheDisk`（WinForms，`requireAdministrator`）
  里既有界面（`Form1`/`Settings`/`Inspector`/`RemoteForm`/`SelectDiskForm`）也有引擎（`Engine/`：`CacheService`、
  `SsdCacheService`、`LogService`、`TargetService`、`CachedPhysicalDisk`、`PhysicalDiskHandle`、`RemotePeer`、
  `RemoteProtocol`、`IBlockSource`）；`RamCacheDisk.Core` 与
  `RamCacheDisk.Service` 两个项目已整体退役到 `废\阶段一-IPC与SCM\`。**编译与实测由用户执行**（见第五章）。

### 阶段一形态（已归档，仅作背景）

核心思路：不是传统的 RAMDisk（把数据全部放进内存），而是一种 **"缓存代理型文件系统"**：

- 软件挂载一个虚拟磁盘，其目录结构与被加速的机械硬盘完全一致；

- 虚拟盘本身不存储任何实际数据，仅展示原硬盘中的文件与目录；

- 访问虚拟盘中的文件时，先查询内存缓存：

  - **命中** → 直接用内存数据响应；

  - **未命中** → 从原机械硬盘读取数据响应，并将热点块写入内存缓存；

- 原始数据始终以机械硬盘为准，断电安全，内存仅保留热点文件副本。

### 进程架构（阶段二：**单进程**）

**一个进程**：`RamCacheDisk.exe`（WinForms，管理员运行），界面与引擎同程序集。启动按钮一次性完成
"校验 → 整盘脱机 → 独占打开 → 起 iSCSI target"，停止按钮反向收尾（顺序见下）。

- **界面层（`RamCacheDisk`）**：`Form1`（启停 + 状态栏 + 菜单，**用户可见的过程信息都由它自己说**——
  主界面日志框、状态栏、弹窗）、`Settings`（iSCSI 监听端口 / 两级缓存参数）、`RemoteForm`（「远程」对话框）、
  `SelectDiskForm`（「选择硬盘」，本地/远程共用）、`Inspector`（每秒直读 `TargetService.CreateSnapshot()`）；
- **引擎层（`RamCacheDisk.Engine`）**：`TargetService`（生命周期与启动校验）、`CachedPhysicalDisk`
  （`DiskAccessLibrary.Disk` 的 4 个成员实现：缓存读 / 写透传 / 写命中刷新 + 块级失效）、`CacheService`（L1 门面）、
  `SsdCacheService`（L2）、`PhysicalDiskHandle`（脱机 / 独占 / 原始扇区 P/Invoke）、`RemotePeer` + `RemoteProtocol`
  （远程形态）、`IBlockSource`（块源抽象）、`LogService`（**只落盘**的诊断账本）；
- **模型层（`RamCacheDisk.Models`）**：`DiskConfig`（配置）、`CacheStats`（统计快照）、`ConfigService`、`ServiceConstants`。

**为什么不再拆双端**：阶段一拆"UI 进程 + LocalSystem 服务"的唯一硬理由是"让挂载的盘符对所有会话全局可见"；
而 iSCSI 盘的盘符是 `disk.sys` 枚举出的**系统级磁盘**（与插一块 USB 硬盘无异），天生对所有会话可见。
⇒ 该约束随形态一起消失，IPC 与 SCM 服务控制一并退役（见 `废\阶段一-IPC与SCM\说明.md`，M4 若要服务化再取回）。

**停止顺序（不可颠倒）**：**先确认没有发起程序还连着——有连接就拒绝停止**（2026-09-30 起）→ 停 target →
关闭 LUN 后端 → 等在途命令跑完（上限 2 秒）→ 冻结 L2 → 落盘账本 → 关句柄（**盘保持脱机**）。
拒绝的判据是"本机监听端口上还有 ESTABLISHED 的 TCP 连接"（库的会话表是 internal，见 `TargetService.GetActiveConnectionCount`）；
其中"冻结"是为了让账本成为一个不再变化的一致快照；
"不联机"是 2026-09-27 定下的口径，理由与两条兜底恢复路径见 `阶段二-iSCSI块设备形态.md` §3.6。

**关机 / 重启 / 注销**（界面层 `CloseReason.WindowsShutDown` 分支，2026-10-03 起）：用户走不到"先点停止加速、
再关窗口"那条路，故 `Form1.OnFormClosing` 会补一次 `TargetService.TryStopForShutdown()`。它与 `Stop()` 的
**唯一区别是不做"有连接就拒绝停止"的守卫**：关机时 L2 账本**必须落盘**（那是 L2 的全部价值），而落盘要求先停
target 让缓存表静止——所以即便发起程序登不出去（盘仍被占用），也照样**中止 iSCSI 服务、掐断会话**，然后落账本。
代价（已知且接受）：那块盘会在关机流程里被"意外移除"，挂着的 NTFS 可能丢掉尚未下发的写；依据是 Windows 本来
就会在关机时拆掉这块盘，这里只是把这一刻提前。运行中点「停止加速」则**绝不**这么干（用户操作、盘还要继续用）。
该路径**绝不外抛**（抛出去会被 `ThreadException` 兜住并弹窗，反卡关机），收尾过程只落 `debug-security.log`。

### 缓存设计要点

- **非托管内存池**：通过 `Marshal.AllocHGlobal` 直接向操作系统申请大块内存，完全绕过 .NET GC，避免缓存增大导致程序卡顿与内存碎片；

- **按块缓存**：内存划分为固定 **4KB Slot（插槽）**；以 **64MB Slab** 为单位向系统申请内存。
  4KB 块与设备扇区的对应关系是"配置项 `SectorsPerBlock` × `BytesPerSector`"，启动时校验必须等于 4096
  （512 字节扇区的盘填 8，4Kn 盘填 1）；

- **块号索引**：**一张 `ConcurrentDictionary<块号, 槽位+时间轮节点>`**，键是全局块号（`LBA / SectorsPerBlock`）。
  块设备下没有文件，所以没有"文件表 / `CacheEntry` / `BitArray`"这些东西（那是阶段一文件系统形态的产物）；
  写透传后的处置是**按块**的（W1，2026-09-30）：整块被写请求完整覆盖 ⇒ `UpdateBlock` 就地刷新已有的副本
  （省掉下次读的回源），残缺的首尾块才 `InvalidateRange(块号, 1)` 作废；

- **基于系统内存占用的阈值管理**：

  - `EvictionThreshold`（默认 0.8）：系统内存占用超过该比例 → 触发 LRU 淘汰；

  - `StopCachingThreshold`（默认 0.9）：系统内存占用超过该比例 → 新数据不再缓存，直接透传；

  - 安全约束：`EvictionThreshold` 必须小于 `StopCachingThreshold`，否则缓存退化为仅透传；

  - `SsdConservativeThreshold`（默认 0.80）：**L2 的**占用率水位（与上面两个同族同单位）。占用 =（数据槽 +
    空洞槽）/ 容器槽数；未达该线时"激进"（回源块一律落位），达到后转"保守"（只收 ghost 命中的块）。
    引擎内部按**可用槽位**判定，本值是它的补数（占用 80% ⇔ 可用 20%），换算见 `SsdCacheService` 构造器；
    越界夹到 `[0.50, 0.99]` 并落盘。检查器/设置里的"保守线"就是它（2026-10-01 从写死常量提为配置项）；

- **淘汰策略**：两级淘汰，判据**刻意不同**（见 `关于内存缓存的进一步讨论.md` §6/§7）：
  - **L1（内存）**：LRU——「时间桶 + 自排序队列」，在 `AcquireSlot` 里按需增量摘一批（O(K)，不需要后台线程）；
  - **L2（SSD）**：单条 **M 环（FIFO-reinsertion）+ ghost**（2026-09-30 起；原 S3-FIFO 的 S 队列已删除——
    它的"一次性访问过滤"职责与 **L1 完全重复**）。准入只有"**回源读到整块**"一个入口：可用槽位还在保留量
    之上（**激进期**）就回源块一律落位，不足之后（**保守期**）只收 ghost 命中的块——分界值见
    `SsdConservativeThreshold`。L1 命中对 L2 完全静默。
    **两者必须正交**：L1 管"近期性"（LRU），L2 管"跨重启的长期保留"（M 环 + ghost）。
    2026-09-30 之前靠"每次访问（含 L1 命中）都要走 L2 准入"去维持这条正交性，实践下来是反效果：
    L1 服务得了的块灌进 L2 只会把 M 挤空（§7.10 的实测数字），现在的做法是 L1 命中**完全不进 L2**；
- **SSD 二级缓存（L2，2026-09-26 落地，2026-09-27 改块级）**：`SsdCacheService` 在 SSD 上维护一个预分配容器
  （4KB 槽位数组）+ 一份账本；**索引只在正常关服时落盘**——索引仅在停服写一次，且每次开机都轮换容器身份戳，
  于是掉电/强杀/崩溃会让身份戳对不上（运行期零写盘）。这类"账本可疑"的情形**不再静默作废**（2026-09-30 起）：
  `Start` / `StartRemote` 返回 `EngineResult.L2NeedsVerify`，UI 弹窗让用户选「校验后保留 / 清空重建」。
  只服务读、永不对源盘写回；对 **SSD 盘**毫无意义（纯写放大），启动校验会拒绝「缓存目录与被加速盘同一块盘」。
  **账本是否可信**由一条设备级判据决定：打开时该盘**原本就脱机**（⇒ 上次运行之后没人能写它）。
  两类防不住的"离线修改"如实登记在 `阶段二-iSCSI块设备形态.md` §7-10；
- **没有**「手动清空/归还 Slab」的入口——曾有 `ClearAllCache()`/`RunSelfTest()`，因整条调用链不可达已删除。
  若将来要手动压测/释放，应按「界面按钮 + 引擎方法」实现，而不是恢复死代码。

***

## 二、目录结构

```
d:\projects\ramdisk\
├── AGENTS.md                      # 本文件（代理规则说明）
├── 关于内存缓存的进一步讨论.md       # 内存缓存核心设计文档（内存池/索引/阈值/LRU 方案）
├── 未解决的疑点.md                 # ★ 阶段二的待决账本（**2026-09-30 整册重写**：阶段一的 TD-01~22 / G1~G9 /
│                                  #   §1~§7 与偏差表已整体删除）。现在有两"待定"条 + 一条结案：
│                                  #   ① L2 出现过「单比特不一致」（1 字节/4096、孤立、二测未复现，待定性；
│                                  #     含两候选机理与下一步实验）
│                                  #   ② 环境干扰：安全软件（依旧存在，做校验/压测前先退出并对照真盘）
│                                  #   ③ **结案登记**（2026-10-01）：云服务器跑原神 → 0xBE 蓝屏，
│                                  #     验尸为米哈游反作弊驱动 `HoYoKProtect.sys` 写自己的只读页，
│                                  #     **与本项目无关**（证据链 + "Server 版不在游戏支持列表内"的 A/B 对照）
├── 阶段二-iSCSI块设备形态.md        # ★ 二阶段立项与设计：把"对外提供块设备"这一层从 WinFsp 换成
│                                  #   自研 iSCSI target（回环），换取"真实本地固定盘"的对外形态；
│                                  #   缓存引擎（L1/L2/账本/统计/UI）原样复用，见该文 §4。
│                                  #   **M1 已完成（2026-09-27）：零搬运实测通过 + 原神关联/启动成功，见 §6**
├── 后续待办.md                    # ★ 下一批待办的讨论存档（2026-09-29；**2026-10-01 大改**）：
│                                  #   ① **远程硬盘加速——已讨论到生产级定稿（含 UI 定稿）**（同一份程序两端各跑一份、
│                                  #     三职责互斥、**自研协议**而非 NBD、一条连接两个阶段（配对 → 服务）、心跳与断开判定、
│                                  #     沉默等待式断线、本地只留一段 iSCSI、主界面零新增控件；含施工批次 0~5）
│                                  #     **批次 0~5 代码已落地、处于双机实测阶段**：协议消息最终集合、
│                                  #     实测暴露并已修的缺陷（含"公网部署后配对长时间不响应"——
│                                  #     根因是**一次垃圾连接就能杀掉 Accept 循环** + 握手无超时）
│                                  #     全部收在**第一节末「实现期的修正与偏差」**（与前面各节冲突时以它为准）
│                                  #   ② L1 预读取 / L2 校验——**L2 校验/修复已讨论到施工级定稿**（先只读校验、看到结果
│                                  #     再决定修复；逐字节比对、不引散列；源盘只读句柄；实施落在单文件 L2Verify.cs）
│                                  #   ③ Windows Initiator API 自动挂载
│                                  #   ④ 盘身份改用唯一标识（**已提前并入 ① 的批次 0**）
│                                  #   ⑤ L2 结构改造（已落地，待编译验证）
│                                  #   ⑥ **账本落地优化（待讨论）**——账本丢的是"热度经验"而非数据正确性
├── repro_bigread.py               # 性能实验：大文件随机读（默认只测**被测盘**；`--compare <路径>` 才与真盘
│                                  #   并排对照。--direct 用 FILE_FLAG_NO_BUFFERING 绕过 Windows 文件缓存，
│                                  #   否则除非文件大到冲掉内核缓存，否则测不出本层价值）
├── iSCSIConsole/                  # ★ 阶段二参考：iSCSI 的 .NET 实现（PDU / 登录协商 / 虚拟 SCSI target /
│                                  #   Win32 SPTI 后端）——只读参考，不在其中开发
├── ISCSIConsole_1.5.6.1/          # ★ 同上，发布二进制版（net20/net40/net472）
├── DiskAccessLibrary/             # ★ 阶段二参考：`Disk` 抽象基类所在库（源码版：基类项目 1.6.6 /
│                                  #   Win32 项目 1.6.3，LGPL-3.0，同作者；NuGet 上 Win32 包最新也正是 1.6.3）
│                                  #   ——我们的 LUN 后端只需继承 `Disk`；**只读参考，不在其中开发**
│                                  #   （含 DiskImage/RawDiskImage/VHD/VMDK/分区表/NTFS 等，只用到 BaseClasses/Disk.cs）
├── nbd-master/                    # ★ 2026-10-01 入库的参考：NBD 官方实现（C / Linux）+ 协议文档 doc/proto.md
│                                  #   ——远程形态最终**不采用 NBD**（官方实现纯 Linux、nbd-client 的传输态在
│                                  #   内核里、nbd-server 没有后端抽象），只借它的**结构思路**（两阶段握手 /
│                                  #   服务端列清单、客户端指名 / 能力位 / cookie 关联请求回复 / 软断开）。
│                                  #   **只读参考，不在其中开发**；完整结论见 后续待办.md 第一节。
│
├── RamCacheDisk/                  # ★ 唯一项目：WinForms 管理员程序 + 引擎层（界面与引擎同程序集）
│   ├── RamCacheDisk.slnx          # 解决方案文件（只含本项目）
│   ├── RamCacheDisk.csproj        # net10.0-windows / WinForms / AllowUnsafeBlocks /
│   │                              #   ApplicationManifest=app.manifest；
│   │                              #   PackageReference: ISCSI 1.5.6 + DiskAccessLibrary.Win32 1.6.3（均 LGPL-3.0）
│   ├── app.manifest               # requireAdministrator（脱机 + 独占PhysicalDrive 必需）
│   ├── Program.cs                 # 程序入口 + 未处理异常兜底（写 crash.log 并提示恢复路径）
│   ├── Form1.cs / .Designer.cs    # 主窗体：启停 state machine（直接驱动 TargetService，或远程的 RemotePeer）、
│   │                              #   菜单 设置/检查器/校验 L2/远程，状态栏含"盘是否由本程序独占"与监听端点；
│   │                              #   主按钮一条链「启动加速 → 取消请求 → 停止加速」，**两端对等**
│   │                              #   （"提供服务中"那端置灰）；远程的 Accept 循环也在这里——**必须循环**
│   │                              #   且**只在监听器真被关时才退出**（其余异常＝垃圾连接，记日志继续接）；
│   │                              #   标题/副标题装**嵌入**的像素字体（见同文件的 `EmbeddedFont` 与
│   │                              #   csproj 里的 `EmbeddedResource`）——**正文一律不用它**
│   ├── Settings.cs / .Designer.cs # 设置对话框：iSCSI 监听端口 + 两级缓存参数（运行中锁定）；
│   │                              #   **「目标盘」区域已删**（2026-10-01）——选盘移到每次点「启动加速」时；
│   │                              #   底部左「文档」（打开落盘的帮助文档）/ 右「确定并保存」
│   ├── Inspector.cs / .Designer.cs# 缓存检查器（2026-10-01 改版）：选项卡分「简要 / 详细」两页，
│   │                              #   每秒直读 CreateSnapshot()（不再有 IPC 推送），**只渲染当前页**——
│   │                              #   简要页 = 整页自绘灰条（五根：读块去向×3 + L1 内存水位 + L2 占用，
│   │                              #             一律灰色只靠深浅区分；条上画阈值刻度/分界）；
│   │                              #   详细页 = 只读 TextBox（可选中可复制），按"结论 → 效果 → 负载 →
│   │                              #             资源 → 参考 → 口径"排序，L1/L2 共用一套骨架。
│   │                              #   同文件的 `StatText` 是两个页共用的换算与排版（口径必须一致）
│   ├── L2Verify.cs                # ★ 新（2026-09-30）：L2 校验/修复，**整个功能单文件**——
│   │                              #   `Engine.L2Verifier`（只读装载账本 → 逐字节比对 → 可选回写）+
│   │                              #   `L2VerifyForm`（UI 程序化构建，不配 designer/resx）
│   │                              #   要点：绝不构造 SsdCacheService（它的构造器会轮换身份戳 ⇒ 账本当场作废）；
│   │                              #   槽位数以盘上记录为准；源盘只读句柄 + 保持脱机；修复只回写 L2 容器；
│   │                              #   入口 = Form1 菜单「校验 L2」（运行时置灰）；入口先 `TryReadOwnerIdentity`
│   │                              #   只读 peek 容器头的归属身份，把候选收窄到**身份匹配的那块盘**
│   │                              #   （散列不可逆 ⇒ 枚举本地盘重算比对；0 匹配则拒绝，不回退整份清单）
│   ├── RemoteForm.cs              # ★ 新（2026-10-01）：菜单「远程」对话框，**只有两种形态就地切换**——
│   │                              #   空闲（单选 开放远程服务/配对远程服务 + 端口或地址 + 不加密警告 + [配对][取消]）/
│   │                              #   非空闲（状态行 + [断开][关闭]，断开只在加速停止时可用）
│   ├── SelectDiskForm.cs          # ★ 新：菜单「选择硬盘」，**本地与远程共用**（两个构造器分别吃
│   │                              #   `PhysicalDiskInfo` / `RemoteDiskInfo` + 对端名）；不能加速的盘标注并禁止选中
│   ├── HelpDocument.cs            # ★ 新（2026-10-03）：帮助文档（单文件 HTML，中英双语同在一份）随 exe 嵌入
│   │                              #   （csproj `EmbeddedResource` → LogicalName `FlyDisk.help.html`）；
│   │                              #   `EnsureExtracted()` 启动时**覆盖写**一份到数据目录（每次启动都刷新，
│   │                              #   保证与当前 exe 版本一致）；`Open()` 交给系统默认程序打开；失败只记日志
│   ├── Engine/                    # namespace: RamCacheDisk.Engine
│   │   ├── TargetService.cs       # ★ 新：生命周期（校验 → 脱机 → 独占 → 起 target；停止的固定顺序）
│   │   │                          #   + 启动校验（块 4KB / 非系统盘 / 非本程序所在盘 / 非只读 为硬拦；
│   │   │                          #     机械盘、可移动介质"只警告" / L2 不同盘）；
│   │   │                          #   两条入口 `Start(本地盘)` / `StartRemote(远端块源)`——
│   │   │                          #   **远程形态停止时完全不碰块源**（关它归 Form1 的 DisconnectRemote）
│   │   ├── CachedPhysicalDisk.cs  # ★ 新：`Disk` 实现（读走两级缓存、写透传 + 写命中刷新/块级失效）
│   │   ├── PhysicalDiskHandle.cs  # ★ 新：整盘脱机 / dwShareMode=0 独占 / 原始扇区 P/Invoke /
│   │   │                          #   设备身份（型号·固件·序列号）、搜寻惩罚、卷→盘号对照；
│   │   │                          #   **`IBlockSource` 的本地实现**（远程那份是 `RemotePeer`）
│   │   ├── IBlockSource.cs        # ★ 新（2026-10-01）：块源抽象——缓存层与 LUN 后端**只认这一个接口**，
│   │   │                          #   于是"本地盘/远端盘"对上层的差别只剩 `BlockSourceInfo`
│   │   │                          #   （型号·序列号·寻址·容量·扇区·**WasOnline**）
│   │   ├── RemotePeer.cs          # ★ 新（2026-10-01）：**对称的对端**——配对之后两端共用这一个类型
│   │   │                          #   （`Connect` 拨号 / `Accept` 监听是唯一不对称处）。一条 TCP、两个阶段、
│   │   │                          #   单 in-flight、心跳 5s/30s、块读写沉默等待 + 重连后重发在途请求；
│   │   │                          #   **重连的主动权永远在拨号方**（监听方只能守着端口等对方连回来）
│   │   ├── RemoteProtocol.cs      # ★ 新：协议常量与帧收发原语（`RemoteChannel`：发送加锁、接收由单线程独占）；
│   │   │                          #   帧 magic RCDH/RCDQ/RCDS/RCDP，`Version=1`，消息 1~11 见 后续待办.md 第一节末；
│   │   │                          #   **只有握手有超时**（`HandshakeTimeoutMs=10s`，握手完复位成无限），
│   │   │                          #   传输阶段刻意没有任何请求超时（沉默等待）
│   │   ├── CacheService.cs        # ★ 改造：L1 门面（非托管池 Slab/Slot、时间桶 LRU、水位检测）——
│   │   │                          #   索引键由"路径→该文件块表"压成**单张 块号→槽位**；失效改 InvalidateRange
│   │   ├── SsdCacheService.cs     # ★ 改造：L2（M 环 + ghost + 容器 + 身份戳）——文件表/源文件签名整段删除，
│   │   │                          #   账本改块级格式；账本可信判据 = "打开时该盘原本就脱机"
│   │   └── LogService.cs          # ★ 复用：**只落盘**的诊断账本（debug-security.log，8MB 滚动）；
│   │                              #   原「开发日志」窗口与它的进程内日志事件（OnLog/DevLog/Info）已删
│   ├── Models/                    # namespace: RamCacheDisk.Models
│   │   ├── DiskConfig.cs          # ★ 新：**物理盘身份（唯一标识，不是盘号）** / IQN / iSCSI 监听端点 /
│   │   │                          #   远程监听端口与对端地址 / SectorsPerBlock / 两级缓存参数
│   │   ├── CacheStats.cs          # 统计快照 DTO（设备信息 + 块 IO 计数 + L1/L2 计数）
│   │   ├── ConfigService.cs       # 配置持久化：%ProgramData%\RamCacheDisk\config.json
│   │   └── ServiceConstants.cs    # BlockSize(4KB) / SlabSize(64MB) / 数据目录与配置文件路径
│   └── bin/  obj/  .vs/           # 编译产物与 IDE 缓存（自动生成，禁止手工修改）
│
└── 废/                            # ★ 归档区（2026-09-27 整理）：仅存档，**不要在此继续开发**
    ├── 阶段一-WinFsp文件系统层/    # RamCacheFileSystem / FileDescriptor / FileSystemService /
    │                              #   RamCacheServiceHost / Program（阶段一的 FS 实现与服务入口）
    ├── 阶段一-IPC与SCM/            # ★ 2026-09-27 新增：RamCacheDisk.Core / RamCacheDisk.Service /
    │                              #   UI-Services（IpcClient + ServiceControlService）；
    │                              #   带 说明.md 标注"M4 拆双端时取回"
    ├── 阶段一-实验脚本/            # repro_errcode_parity / repro_flipflop / repro_reparse_guid /
    │                              #   repro_symlink_delete（均为文件系统语义面探针）
    ├── 第三方-WinFsp资料/          # winfsp 上游源码 / winfsp.wiki / winfsp-NET10.wiki /
    │                              #   winfsp_cs_wiki.md / passthrough-dotnet / fstools-master(+zip)
    ├── 无关目录/                   # pcl（Plain Craft Launcher）/ docker（LichtFeld-Studio 用）/
    │                              #   __pycache__ / ISCSIConsole_1.5.6.1.zip（重复备份）
    └── （更早的存档：初步讨论.md、关于架构混乱和冗余的讨论.md、rename_flipflop_分析存档.md、
        物理句柄生命周期_技术债与方案A-B抉择.md，以及 HelloFs / RamCacheDisk / RamCashDisk / TestApp
        等废弃实验工程）
```

> 带 ★ 的为项目当前的核心代码与文档；`iSCSIConsole/` 与 `ISCSIConsole_1.5.6.1/` 是**阶段二的外部参考实现**
> （第三方，只读，不在其中开发）。阶段一的 WinFsp 代码与第三方资料已全部移入 `废/`，**不要在 `废/` 里继续开发**。

***

## 三、项目详细信息

### 3.1 一个项目（三个阶段一项目）

| 项目                     | 类型                 | 说明                                                     |
| ---------------------- | ------------------ | ------------------------------------------------------ |
| `RamCacheDisk`         | WinForms（`WinExe`） | **唯一项目**：管理界面 + 引擎层（同程序集，见 §5.3 命名空间约定） |

| 公共信息   | 说明                                                                                                       |
| ------ | -------------------------------------------------------------------------------------------------------- |
| 目标框架   | .NET 10.0（`net10.0-windows`）                                                                                       |
| 语言特性   | nullable、隐式 using；`AllowUnsafeBlocks` **必需**（`CacheService` 的指针操作 + `PhysicalDiskHandle` 的原始扇区读写）                  |
| 外部依赖   | NuGet `ISCSI` 1.5.6（iSCSI 协议层）+ `DiskAccessLibrary.Win32` 1.6.3（`Disk` 基类与 Win32 助手），均 LGPL-3.0；**无 WinFsp**、无 ServiceController |
| 管理员权限  | `app.manifest` 声明 `requireAdministrator`：整盘脱机与独占打开物理盘都需要它                                                         |
| 共享数据目录 | `%ProgramData%\RamCacheDisk\`（`config.json` + `debug-security.log` + `crash.log`）                                          |
| 解决方案   | `RamCacheDisk/RamCacheDisk.slnx`（只含上述一个项目）                                                                     |
| 部署要求   | 单文件目录即可运行；服务化（若做）见 §9 M4                                                                                   |

### 3.2 单进程内的分工

- **界面层**（`RamCacheDisk`）：

  - `Form1`：启停按钮同步调用 `TargetService.Start()/Stop()`（脱机切换要几百毫秒，先把"启动中"这一帧画出来）；
    状态栏每秒直读 `CreateSnapshot()`；关闭窗口时若目标仍在运行会拦一次确认（误关会把盘留在脱机状态）；

  - `Settings`：iSCSI 监听端口 + 两级缓存参数；目标运行时锁定入口（配置只在启动那一刻读取）；
    L2 缓存盘候选会**排除被加速的那块盘**（**「目标盘」区域已删**——选盘移到每次点「启动加速」时）；

  - `RemoteForm`：菜单「远程」对话框（开放/配对/断开）——**远程整体是实验性特性**：点菜单先弹
    "不保证可用 / 不保证数据安全"的警告（`OKCancel`，默认取消），同意才进；
    `SelectDiskForm`：菜单「选择硬盘」，本地与远程共用，
    不能加速的盘标注并禁止选中，**可移动介质 / SSD 的提示也在这一屏**（比启动后打日志更及时）；

  - `Inspector`：每秒直读统计快照，分「简要」（自绘：两条读速各一个**方框** + 读块去向×3 / L1 内存水位 /
    L2 占用）与「详细」（只读文本，可复制）两页；设计定稿见 `后续待办.md` 第七节。
    **详细页每个数字前面都带表头、不做列对齐**——列对齐要求等宽字体，而检查器用的是系统默认字体
    （像素字体只给主界面标题 / 副标题）。
    读速是**UI 侧自己按秒采样差分**出来的（快照只有累计计数，没有速率）：
    `加速后读取` = `ReadSectorsTotal` 增量，`原盘读取` = 回源块数 × 4 KiB；窗口 5 秒，单位 MB/s（10⁶）。
    **两条共用同一个方框刻度**（加速后读取的历史最大值），否则两块各顶各的纯黑、看不出缓存省了多少。
    **最小化时**再冒一个 50×50 的置顶方块（`SpeedTile`，娱乐件）：左半 = 加速后读取的灰阶、
    右半 = 原盘读取的灰阶，与简要页方框同源、**同刻度**、**不加描边**；不抢焦点、可拖动、
    只建一次所以位置记得住；**点击它（不是拖动）即把检查器还原出来**。

  - **单位口径**（2026-10-01 查定，全项目统一）：**容量 = 1024 进制标 MiB/GiB/TiB，吞吐 = 10 进制标 MB/s**。
    两者本就不是一套（盘厂商标称的 TB/GB 是 10 进制，所以 1 TB 盘 = 931.5 GiB）。要守的是
    **"标注必须与进制一致"**——1024 进制的值不能写 `GB`（设置对话框那处就从 `(GB)` 改成了 `(GiB)`）。

- **引擎层**（`RamCacheDisk.Engine`）：

  - `RemotePeer` + `RemoteProtocol`：远程形态的对称对端与协议（见 `后续待办.md` 第一节末的最终消息集合）；
    `IBlockSource`：块源抽象（本地 = `PhysicalDiskHandle`，远程 = `RemotePeer`）；

  - `TargetService`：**校验 → 整盘脱机 → 独占打开 → 装缓存 → 起 target**；停止是固定顺序（见 §一 进程架构）；
    启动失败会把盘恢复成打开前的样子（不把用户的盘留在"看不见"的状态）；

  - `CachedPhysicalDisk`：`DiskAccessLibrary.Disk` 的 4 个成员实现——读按 4KB 块查两级缓存、未命中回源并回填；
    写直透物理盘 + 逐块处置被覆盖的块：**整块被完整覆盖**的块就地刷新（`UpdateBlock`，W1，只刷已有副本、
    不占新槽），残缺的首尾块才 `InvalidateRange` 作废；`Close()` 之后一律以 `IOException` 拒绝命令
    （**异常类型必须是库认识的**，否则会把 target 工作线程带走）；

  - `PhysicalDiskHandle`：整盘脱机（`IOCTL_DISK_SET_DISK_ATTRIBUTES`，`persist=true`）/ `dwShareMode=0` 独占打开 /
    原始扇区 `SetFilePointerEx + ReadFile/WriteFile`（`FILE_FLAG_WRITE_THROUGH`）/ 设备身份与"搜寻惩罚"查询 / 卷→盘号对照；

  - `CacheService`：L1 门面（非托管池 Slab/Slot、时间桶 LRU、系统内存水位（取自 `SystemMemory` 的 `GlobalMemoryStatusEx`）、两层命中与晋升、失效统一扇出）；

  - `SsdCacheService`：L2（容器 + M 环 + ghost + 身份戳；**容量上限 64 GiB**，上限的实质是常驻内存
    （索引/槽位结构约 58 B/块 ≈ 15 MiB 每 GiB 容量），故再往上只能靠改块大小或让索引落盘；**索引只在正常关服时落盘一次**；
    账本可信判据 = 打开时该盘原本就脱机，见 §3.6 修订与 `阶段二-iSCSI块设备形态.md` §7-10/11）；
    **账本可疑的两种情形都不静默作废**（2026-09-30）：①盘原本联机、②上次未正常关服（身份戳不符）
    ⇒ `EngineResult.L2NeedsVerify` ⇒ UI 弹窗「校验后保留 / 清空重建」（校验器一进窗口就轮换身份戳，
    校验干净时写回索引里的值；因此不需要额外的"已校验"标志）；
    **账本与当前盘/参数不匹配时不静默清空**（2026-09-30）：`DetectLedgerMismatch` 只读探测出原因 ⇒
    `EngineResult.L2Mismatch` ⇒ UI 弹窗问用户，选「是」才以 `Start(allowL2Reset: true)` 清空重建；
    （`SsdCacheService` 的构造函数没法返回值，仍会抛 `L2LedgerMismatchException`，由 `CacheService` 原样上抛作二次保险）
    **盘原本是联机时也不再静默作废整层**：有账本就返回 `EngineResult.L2NeedsVerify` ⇒ 用户可选"先校验再保留"
    （校验器会把盘脱机，引擎据此自然采信账本，不需要额外的"已校验"标志）或"清空重建"；
    校验没跑干净 ⇒ 中止启动、**不做任何补救动作**（注意：校验器已把源盘脱机，下次启动会因此直接采信这份没验完的账本）；
    「上次没正常关服」那类仍静默重建（每次都弹窗会让人麻木）；

  - `LogService`：**只落盘**的诊断账本（`debug-security.log`，8MB 滚动）。**没有**进程内展示型日志通道——
    用户可见的过程信息一律由界面层自己说（`Form1.Log` → 主界面日志框、状态栏、弹窗）；
    引擎内部的异常 / 自检失败 / 降级处置一律走这里落盘（`DebugFile`），供离线分析。

- **模型层**（`RamCacheDisk.Models`）：`DiskConfig`（配置 schema）、`CacheStats`（统计快照）、`ConfigService`、`ServiceConstants`。

> 阶段一的 `RamCacheFileSystem` / `FileDescriptor` / `FileSystemService` 已归档到 `废\阶段一-WinFsp文件系统层\`；
> 双进程形态（`Core` / `Service` / `IpcClient` / `ServiceControlService`）已归档到 `废\阶段一-IPC与SCM\`
> （带 `说明.md` 标注"M4 拆双端时取回"）。阶段二没有文件系统语义面（重分析点 / 命名流 / 安全描述符 / POSIX 删除改名等），
> 那是阶段一最贵的一块投入。

### 3.3 参考资料

- `阶段二-iSCSI块设备形态.md`：**当前主线的设计文档**（§1 为什么换形态、§3 M1 定案的架构、§4 复用清单、§5 逐项结论、§6 实测记录、§7 风险、§9 里程碑），做阶段二前必读；

- `关于内存缓存的进一步讨论.md`：缓存方案（L1 淘汰 §6 + L2 淘汰结构 §7）的设计依据，改 `CacheService` / `SsdCacheService` 前必读；

- `iSCSIConsole/`：iSCSI 的 .NET 参考实现（PDU 编解码、登录协商、虚拟 SCSI target、Win32 SPTI 后端），阶段二协议实现的主要参照；

- `nbd-master/`：NBD 官方实现（C / Linux）与其协议文档 `doc/proto.md`。**远程形态最终不采用它**（官方实现纯 Linux、`nbd-client` 的传输态在内核里、`nbd-server` 无后端抽象），但协议设计大量借用了它的结构思路（两阶段握手 / 列清单 / 能力位 / cookie 关联 / 软断开）——完整结论见 `后续待办.md` 第一节「为什么不用 NBD」；

- `未解决的疑点.md`：**阶段二的待决账本**（2026-09-30 整册重写，阶段一的条目已整体删除）。现在有"L2 单比特不一致（待定性）"与"安全软件的环境干扰"两条待定，外加一条**结案登记**（2026-10-01：云服务器跑原神的 0xBE 蓝屏已定性为米哈游反作弊驱动自身问题，与本项目无关）；遇到"已经知道是什么、但还没决定做不做"的问题往这里记。

### 3.4 目录中的无关内容

- `废/`：归档区（阶段一的 WinFsp 代码与实验脚本、双进程形态（IPC/SCM）、第三方资料，以及更早的废弃工程），**不要在其中继续开发**；

- `iSCSIConsole/`、`ISCSIConsole_1.5.6.1/`：第三方参考实现，只读，不修改；

- `bin/`、`obj/`、`.vs/`：自动生成产物，禁止手工编辑，也不要将其中内容当作源码分析对象。

### 3.5 对外能力位与两个"透传面"（★ 阶段一遗留：仅作证据，阶段二不适用）

> 本节内容只对阶段一的 WinFsp 形态成立，相关代码已归档到 `废\阶段一-WinFsp文件系统层\`。
> 保留在此是为了说明**为什么必须换形态**——结论文档见 `阶段二-iSCSI块设备形态.md` §1。

`RamCacheFileSystem.Init` 里的 `Host.*` 是**入场券**：内核驱动与用户态分发层据它们决定"这条请求要不要送到本文件系统"。
关掉某项时请求根本到不了用户态（日志里**一行都不会有**），排查此类问题时先回来看宣告：

| 能力位 | 作用与代价 |
| --- | --- |
| `ReparsePoints = true` | 放行 SET/GET/DELETE_REPARSE_POINT 三个 FSCTL；为假时内核态直接拒绝 |
| `NamedStreams = true` | 内核才把 `:` 当"主名:流名"解析；为假时含 `:` 的名字在内核态就地判 `STATUS_OBJECT_NAME_INVALID`（Win32 123），请求不进用户态（ADS 的入场券） |
| `SupportsPosixUnlinkRename = true` | 删除/改名走 POSIX 形态（重分析点句柄也可删） |
| `CaseSensitiveSearch = false` / `CasePreservedNames = true` | 大小写不敏感但保留原大小写——这正是 `GetNormalizedName` 存在的原因：**内核只接受"与请求名仅大小写不同"的归一化名**，形态不同（如链接被展开后的真值）只能返回 null，否则整个打开被判 `STATUS_OBJECT_NAME_INVALID` |
| `PostCleanupWhenModifiedOnly = false` / `FlushAndPurgeOnCleanup` | 物理句柄生命周期（方案 A′）依赖 Cleanup 一定到来 |
| `FileInfoTimeout = 1000` | 内核侧属性缓存的时长，按毫秒 |

**未宣告的能力位**（= 相关原语在本盘不可用，均为**有意**留空，不是遗漏）：`Control`/`DeviceControl`（⇒ 稀疏文件 `FSCTL_SET_SPARSE` 不可用；绑定层也没有 `SetSparse` 回调，要支持得把全部 FSCTL 引到用户态自行分发，见 `未解决的疑点.md` G9）、`ExtendedAttributes`（EA 创建被内核拒为 `STATUS_EAS_NOT_SUPPORTED`，与真盘不一致）、`WslFeatures`、`HardLinks`。

**卷属性面（2026-09-26 对齐源盘）**：`Init` 现在**沿用源盘**的文件系统名、卷创建时间与**卷序列号**（`DetectSourceVolumeSerial()`；此前是 WinFsp 默认的 `0x0`，而"没有序列号"会被按卷识别身份/登记安装库的软件当成异常），并宣告 `PersistentAcls = true`（真盘有"保留并加强 ACL"；权限本来就走源盘真值）。注意 `Host.*` 必须在 `Init` 里设——绑定层 `Init` 在 `FspFileSystemCreate` **之前**执行，参数一旦交给驱动就改不动了。

**驱动层硬边界（本层修不了，别在这里找）**：
1. `volinfo.c` 的卷特性位**只从 `VolumeParams` 的 10 个位拼出** ⇒ 对象标识符、稀疏文件、USN 日志、事务、配额、压缩、EFS、按文件 ID 打开、硬链接（那行**被注释掉**）在本盘**永远不会出现**，即使功能上可透传。
2. `devctl.c` 只把"设备类型带 `0x8000` 位 + `METHOD_BUFFERED`"的请求转发到用户态（`FsvolDeviceControl` 在前置检查里就回 `STATUS_INVALID_DEVICE_REQUEST`）⇒ `IOCTL_MOUNTDEV_QUERY_DEVICE_NAME`（类型 `0x4D`）与 `FSCTL_CREATE_OR_GET_OBJECT_ID` / `FSCTL_SET_SPARSE`（类型 `0x9`）**即便宣告 `DeviceControl` 也到不了我们的回调**。实测后果：原神启动器判定"这不是正常本地卷"，关联按钮灰掉并提示"请移动到其他目录重试"（ProcMon 两盘对照：该 IOCTL 真盘回 `87`、本盘回 `INVALID DEVICE REQUEST`）。

两个透传面的实现要点（它们的注释里写了逐条证据来源与真盘对照，改动前先读注释）：

1. **重分析点（符号链接 / junction）**：管理面三回调（SET/GET/DELETE）+ 解析循环数据源 `GetReparsePointByName`，
   外加 `GetSecurityByName` 的**上报义务**——名字里除末组件外还含重分析组件时须返回 `STATUS_REPARSE`
   并把**最靠左**那个组件的名字内偏移写进 `fileAttributes`。目录描述符**没有常驻物理流**，
   凡"按句柄"的访问（读写重分析数据、取真值大小写）都要退化成 `by-path` 形态并带 `FILE_FLAG_OPEN_REPARSE_POINT`
   （跟随会踩 Win11 的"DIRECTORY 链接指向文件时拒绝跟随打开"）；删除探测（`CanDelete`）同样不能跟随——
   要删的是**链接对象本身**，它自身没有子项。

2. **命名流（ADS）**：`GetStreamEntry` 逐条透传源卷的 `FileStreamInformation`（含默认数据流那条：
   源卷报 `::$DATA`，winfsp 侧约定用**空名**表示），一律不做合成——空文件恰好 1 条、空目录 0 条这类形态
   差异全部由源卷事实决定；流名归一化要连**流名部分**一起给真值大小写（内核的 FileNode 名字含流部分）；
   `DeleteNamedStreams` 只在被覆盖对象是**主数据流**时才删流（覆盖单条流只截断那一条，兄弟流必须保留）。

两个踩过的坑：
- **信息类编号有两套**：`GetFileInformationByHandleEx` 用 Win32 的 `FILE_INFO_BY_HANDLE_CLASS`
  （`FileStreamInfo=7`、`FileNameInfo=2`），NT 的 `FILE_INFORMATION_CLASS` 是另一套（`FileStreamInformation=22`、
  `FileNameInformation=9`）；用错会拿到 `ERROR_INVALID_PARAMETER(87)`。
- **错误码要"翻译"**：物理层是 Win32，winfsp 要 NTSTATUS，统一走 `NtStatusFromWin32`；
  同一现象在两侧的编码不同（如 `ERROR_SHARING_VIOLATION`、`ERROR_DIRECTORY`）——排障时先对齐"哪一层报的"。

> 变更能力位或上述语义，属**协议面/语义面变动**，必须整套重跑 winfsp-tests（见第六章），不能只看单测。

***

## 四、通信协议（UI 进程 ⇄ 服务进程）★ **阶段一遗留：已退役**

> **本章只适用于阶段一的 WinFsp 双进程形态**，相关代码（`IpcMessages` / `IpcClient` / `IpcHost` / `ConfigService` 的 ACL 补齐）
> 已归档到 `废\阶段一-IPC与SCM\`。阶段二是**单进程**：配置仍走 `config.json`，但控制与实时数据都不再跨进程——
> 启停是同一个进程里直接调 `TargetService`，统计走 `CreateSnapshot()`，日志走进程内事件。
> 保留本章是为了说明**当初为什么这么设计**（配置/控制/数据三个信息源各司其职），M4 若把 target 服务化会需要它。

三个信息源各司其职：**配置走文件、控制走 SCM（Windows 服务管理器）、实时数据走 IPC（ws-over-named-pipe）**。

### 4.1 配置下发（UI → 服务，共享文件）

- 路径：`%ProgramData%\RamCacheDisk\config.json`（常量见 `ServiceConstants`）；

- 写入方：UI 设置对话框保存时；读取方：服务 `OnStart` 时一次性读取（**启动时快照语义**，运行中修改无效）；

- 权限：共享目录由 `ConfigService.EnsureSharedDirectory()` 幂等授予 `BUILTIN\Users` Modify 权限（服务以 SYSTEM 先建目录时普通用户默认写不进）；

- UI 在服务运行中锁定设置入口，防止"改了不生效"的误解。

### 4.2 服务控制（UI ↔ SCM）

- 安装/卸载/启动/停止：UI 以 `runas` 提权调用 `sc.exe`（`create RamCacheDisk binPath= "...RamCacheDisk.Service.exe --service" start= demand`），UAC 弹窗由用户确认；

- 状态查询：`ServiceController` 每 2 秒轮询（未安装返回 null）；

- **挂载是否成功不依赖 SCM 的 Running 状态，以服务端统计推送中的** **`IsRunning`** **为端到端汇报**；SCM 状态用于启停等待态的收尾（如启动失败时服务回到 Stopped）。

### 4.3 IPC 实时通道（ws-over-named-pipe）

- **传输**：命名管道 `RamCacheDisk.Ipc`。服务端以 `NamedPipeServerStreamAcl.Create` 创建（显式 ACL 放行 `BUILTIN\Users` 读写——SYSTEM 创建的管道默认 DACL 不允许普通用户写入）；UI 以 `NamedPipeClientStream` 连接。连上后双方用 `WebSocket.CreateFromStream` 包装为标准 WebSocket，之后收发均走标准 ws 帧（协议自带 ping/pong 保活 30 秒，管道断开即异常，双方立即感知）；

- **消息格式**：JSON 文本帧信封 `{"type":"...","data":{...}}`；协议常量与 DTO 集中在 `Core/Services/IpcMessages.cs`（**两进程共用，修改时必须两端同步并一起编译验证**）；

- **连接管理**：UI 侧单例长连接，断线后每 2 秒退避重连；日志流订阅意图被记忆，重连成功后自动补发；多开 UI 各占一个管道实例，服务端支持多客户端。

### 4.4 消息清单

| 方向      | type           | data 负载                                               | 触发时机                                                      |
| ------- | -------------- | ----------------------------------------------------- | --------------------------------------------------------- |
| 服务 → UI | `stats`        | `CacheStats`                                          | 每秒一次（`IsRunning` 兼作"虚拟盘已挂载"的端到端汇报；`LastStats` 缓存供检查器即开即显） |
| 服务 → UI | `logBatch`     | `LogBatchMessage`（`OkText` + `ErrorText` 两段已按行拼接好的文本） | 攒批：0.1 秒或满 200 条，仅推给已订阅日志流的连接；队列积压上限 1000 条，超出丢最旧         |
| UI → 服务 | `setLogStream` | `SetLogStreamMessage`（`Enabled`）                      | 开发日志窗口显示/隐藏时；服务端无订阅者时**完全不计日志（零开销）**                      |

> 日志批次设计为两段整块文本而非行对象列表：前端各做一次 `AppendText` + 一次裁剪 + 一次滚动到底，把 UI 编辑操作次数降到最低。

### 4.5 日志策略

- **日志不落盘**：高频文件系统请求逐条写盘严重影响 IO 性能，且无排障价值（展示型日志）；

- UI 端滚动保留：正常日志 100 行、错误日志 5000 行（上限保护），超出丢弃最旧；

- **诊断账本（`debug-security.log`）**：`LogService.DebugFile` 落盘 `%ProgramData%\RamCacheDisk\debug-security.log`（超 8MB 滚动为 `.old`）。**逐笔高频成功痕迹默认不落盘**（前缀表见 `LogService.TracePrefixes`：`Phys*`/`Open `/`Create(file|dir)`/`CanDelete `/`Cleanup DELETE`/`Reparse SET|GET|DELETE`/`GetSecurityByName `/`Normalize `/`TryBuildExplicitOnlyAcl:`），**失败/异常/探针行始终落盘**。排障需要完整逐笔账本时，把 config.json 的 `TraceFileLogging` 设为 `true` 并重启服务（启动时界面日志会报当前模式）；

- 崩溃兜底：`Fsp.Service` 的异常与生命周期日志写入 **Windows 事件日志**（来源 RamCacheDisk），与展示型日志无关。

***

## 五、重要规则（必须严格遵守）

### 5.1 禁止编译与运行

1. **本项目禁止执行任何形式的编译/构建操作**，包括但不限于：

   - `dotnet build`、`dotnet publish`、`dotnet run`；

   - MSBuild / Visual Studio 构建；

   - 任何脚本化的构建命令。
2. **禁止运行项目、程序或可执行文件**，包括：

   - 直接运行 `bin/` 下任何已生成的 `.exe`；

   - 挂载/卸载虚拟磁盘等会产生系统级副作用的操作；

   - 通过 docker 运行任何容器。
3. **禁止执行任何测试代码**：不得自行运行单元测试、集成测试或任何验证脚本。

> 原因：本项目涉及文件系统驱动与磁盘挂载，编译产物运行会创建/卸载虚拟磁盘、改动系统挂载点，风险较高；且构建依赖本机安装的 WinFsp 与 .NET 10 SDK，环境状态不可控。

### 5.2 修改后的验证方式

- 代码修改完成后，**一律交由用户在本地自行编译验证**，代理不得代替用户执行编译；

- 需要运行或测试任何代码时，**只提供需要执行的命令与步骤说明，由用户手动执行并反馈结果**；

- 代理可做的静态检查仅限于：阅读代码、检索引用、确保修改在语法与逻辑上自洽。

### 5.3 其他工作约定

- 修改代码时遵循项目现有风格：显式 `using`、中文注释与中文 UI 文案、`#region` 分区、Win32 P/Invoke 集中放在文件尾部的 `#region Win32 API` 中；

- **命名空间按"层"划分（单程序集内的两段式+模型段）**：阶段一"命名空间与程序集一一对应"的前提是"跨进程契约需要物理隔离"，
  单进程后这个前提不存在了；现在同一程序集里按层分三个命名空间，**新增文件按归属选**：
  `RamCacheDisk`（界面层：窗体与 `Program`）、`RamCacheDisk.Engine`（引擎层：缓存 / target / 盘句柄 / 日志）、
  `RamCacheDisk.Models`（配置与 DTO）。**不要**再引入 `RamCacheDisk.Core.*` / `RamCacheDisk.Service.*` 这类阶段一命名空间；

- ~~修改 IPC 协议（`IpcMessages.cs`）时，UI 与服务两端必须同步更新并一起编译验证~~（★ 阶段一遗留：IPC 已退役，见第四章）；

- 更新代码后只需**关闭程序 → 重新编译 → 启动**即可生效（运行中编译会因 exe 被占用而失败；若目标正在运行，请先点「停止」——直接退程序会让盘留在脱机状态）；

- 不修改 `iSCSIConsole/` 与 `ISCSIConsole_1.5.6.1/`（第三方参考实现，只读）；`废/` 下的归档内容也不要直接改（需要时先取回）；

- 不触碰 `bin/`、`obj/`、`.vs/`、`__pycache__/` 等生成目录；

- 涉及缓存策略变更时，先对照 `关于内存缓存的进一步讨论.md` 中的设计约束（阈值校验、4KB 对齐、非托管内存释放等）。

***

## 六、验证基线（winfsp-tests）★ 阶段一遗留

> **本章只适用于阶段一的 WinFsp 形态**（相关代码、测试工具与脚本均已归档到 `废/`）。
> 阶段二是块设备、没有文件系统语义面 ⇒ winfsp-tests / fsx / 错误码对照仪**都不适用**；
> 阶段二的验证重点是**块设备正确性（内容一致）与缓存效果**，见 `阶段二-iSCSI块设备形态.md` §8（M5）。

**阶段一基线：全量 winfsp-tests 在 `--external` 模式下全绿**——`reparse_*`（含符号链接/junction）与 `stream_*`（含 ADS 创建、覆盖、名字/流枚举）均已打通；`ea_*` / `oplock_*` / `wsl_*` 属可选测试，`notify_*` / `volpath_*` 对 `--external` 自动跳过。
> 本章只管**语义/协议面**这一条腿。另外两条腿（真盘对照仪 `repro_errcode_parity.py`、随机 I/O 一致性 `fsx`）与整体进度见 **§七**。

凡动了**能力位（3.5）或文件系统语义面**（命名流、重分析点、属性/安全描述符、创建/覆盖路径）的代码，都要重跑整套，不能只跑单个测试：

```cmd
rem 1) 清空被测目录（源盘＝本盘的同一份数据，先 cd 出被删目录，否则 cmd 自身句柄会让 rmdir 失败）
cd /d C:\
rmdir /s /q D:\wt-src
mkdir D:\wt-src

rem 2) 从被测目录执行：--external 以 cwd 判定被测卷（同目录还有 winfsp-tests-x64.exe，任选其一）
cd /d X:\wt-src
"C:\Program Files (x86)\WinFsp\bin\winfsp-tests-x86.exe" --external --resilient
```

- **跑之前必须清目录**：测试被断言中断时不会自我清理，残留的 `dir1` / `file1` / 链接等会让下一轮在 `CreateDirectoryW` 上撞 `ERROR_ALREADY_EXISTS`（`err=c0000035:183`）——这是**假红**，与代码无关，先清理再复跑；
- **跑之前先做环境体检**（否则会拿到第二类"假红"）：
  - ① 确认测试产物**非空**——本机曾出现 `winfsp-tests-x64.exe` 为 **0 字节**（cmd 里直接报"拒绝访问。"），此时改用 `winfsp-tests-x86.exe` 或重装 WinFsp；
  - ② **退出第三方安全软件**——本机实测 360 的"主动防御"会拦截 `FSCTL_SET_REPARSE_POINT`，表现为 `reparse_*` 全部在 `reparse-test.c:65` 报 `err=c0000022:5`（**真盘 `D:\wt-src` 上同样红**），退出后两盘全绿；
- **单测过滤**：测试名直接写在命令末尾（支持前缀与通配），如 `... --external --resilient reparse_symlink_relative_test`；
- **对照真盘**：同一命令在 `D:\wt-src`（或任意 NTFS 目录）下跑即"真盘基线"，两盘**不一致**才有讨论价值；差异条目别静默放过（阶段一那份偏差表已随清账删除，现在直接记进 `未解决的疑点.md`）；
- **`reparse_*` 报 `c0000022:5` 时先对照真盘**：真盘同命令也红 ⇒ 是环境拦截（拦截"创建重分析点"是勒索防护的常见做法），**不要去改三个 FSCTL 回调**；详细判读流程见 `未解决的疑点.md` 的「环境干扰：安全软件」一节；
  - ⚠ **判据订正（2026-09-26）**：早先还要求"日志里没有 `Reparse SET` 行"作为佐证，该条**已失效**——
    `Reparse SET` 在 `LogService.TracePrefixes` 里，而 `IsTraceLine` 是**纯前缀匹配**，
    **失败行同样以该前缀开头、同样被过滤**；默认（`TraceFileLogging=false`）下无论成败都不落盘。
    要用这条判据，必须先把 `TraceFileLogging` 设为 `true` 并重启服务（此时它是有效证据：没有该行 ⇒ 请求没进用户态）。
- **排障顺序**：先看 `%ProgramData%\RamCacheDisk\debug-security.log`（每个回调的逐条账本：入参、判定分支、错误码），再对照 `winfsp/tst/winfsp-tests/*.c` 的断言与 `winfsp/src/{sys,dll}` 的对应分支。**"日志里一行都没有"本身就是决定性证据**——说明请求没到用户态（能力位没开，或内核态校验已拒绝）；
- **确认跑的是新产物**：核对服务实际加载的 `RamCacheDisk.Service.dll` 时间戳与进程启动时刻。流程固定为"停服务 → 编译 → 启服务"，运行中编译会因 exe 被占用而失败。

***

## 七、当前进度与基线汇总

### 7.0 阶段二（2026-09-27）

- **M1 已完成**（调研定稿 + 前置验证实测，详见 `阶段二-iSCSI块设备形态.md`）：
  - **协议层不自研**：直接用 NuGet `ISCSI` 1.5.6（+ 依赖 `DiskAccessLibrary`，均 LGPL-3.0、含 `netstandard2.0`），
    我们只需实现 `Disk` 抽象类的 4 个成员（`ReadSectors` / `WriteSectors` / `BytesPerSector` / `Size`）；
  - **形态实测通过**（零搬运、现成 demo、只读挂载）：`J:`（磁盘 2，ST1000DM010，931 GB 机械盘）脱机 → export →
    手动连接，**原 NTFS 原样挂回**、数据完好；`fsutil fsinfo volumeinfo` 与真盘 **24 项逐行一致**
    （阶段一缺的对象标识符 / 稀疏文件 / USN / 事务 / 配额 / 压缩 / EFS / 按文件 ID 打开 / 硬链接**全部回来**）；
    **原神关联成功 + 启动成功**——阶段一被挡住的两步同时打通；
  - **定案**：后端「直通为主、镜像先打通」；`BytesPerSector` **跟随源盘** + 配置项 `SectorsPerBlock`（块 = 扇区 × SPB = 4096）；
    索引键改为 `Dictionary<块号, 槽号>` + 现有 LRU 链（去掉 `BitArray` / 文件表 / 源文件签名）；
    写路径 = 读写挂载 + 只读缓存 + **写透传** + `InvalidateRange` 块级失效（不做 WriteBack）；
    独占 = 整盘脱机 + `ShareMode.None` 独占句柄；MVP = **管理员运行的 WinForms 单进程**
  （复用阶段一 UI 窗体、布局保留逻辑重写；不做 Windows 服务、不做开机自启、不再走 IPC，见该文 §3.7）。
- **M2 代码已落地（2026-09-27）**，待用户本地编译 + 实测：
  - **单进程化重构完成**：`Core` / `Service` 两个项目 + UI 的 `Services/`（IpcClient / ServiceControlService）
    整体退役到 `废\阶段一-IPC与SCM\`（带 `说明.md`）；引擎三件套搬进 `RamCacheDisk/Engine/` 并改造；
    窗体保留布局、逻辑重写（直连引擎）；`csproj` 换成 `ISCSI 1.5.6` + `DiskAccessLibrary.Win32 1.6.3`、
    开 `AllowUnsafeBlocks`、挂 `app.manifest`（requireAdministrator）；`slnx` 只留一个项目；
  - **索引键改造完成**：L1 从"路径 → 该文件块表"压成**单张 `块号 → 槽位`**（`Invalidate(path)` → `InvalidateRange(块区间)`）；
    L2 删掉文件表与源文件签名、账本改块级格式（`INDEX_VERSION = 2`）；
  - **新增三件**：`TargetService`（生命周期 + 启动校验）、`CachedPhysicalDisk`（`Disk` 实现：缓存读 / 写透传 / 块级失效）、
    `PhysicalDiskHandle`（整盘脱机 / 独占 / 原始扇区 P/Invoke / 设备身份 / 搜寻惩罚 / 卷→盘号）；
  - **两处新定案**（详见 `阶段二-iSCSI块设备形态.md` §3.6 修订与 §7-10/11）：
    ① **停服不再自动联机**（盘归本程序管）——这是"跨重启 L2 账本可信"的唯一判据来源；
    ② L2 账本可信判据 = 打开时该盘**原本就脱机**；并如实登记两类防不住的"离线修改"。
  - **写入路径用 `FILE_FLAG_WRITE_THROUGH`**：写不经过系统写缓存，让"写已落盘"尽量成立
    （磁盘自身的易失写缓存不承诺，见 §3.4）。
- **M2 实测通过（2026-09-27）**：`winfsp-tests --external` **78 条全绿**、`fsx` `A-OK`、
  `repro_bigread --direct` 内容确认全过（随机 72 KiB 冷 15.8 → 热 **269 MiB/s**；顺序 1 MiB 热 **687 MiB/s**）；
  可写挂载已补测。**注意目标盘是 U 盘**，机械盘对照留 M3。热态瓶颈已量化：每命令固定 ~220 µs + 搬运 1.19 µs/KiB
  （渐近 ~820 MiB/s），源于库的单线程串行执行队列，**对 HDD 加速定位不构成瓶颈**（详见设计文档 §5.5/§6.6/§7-4）。
- **L1 淘汰已按语义修好（2026-09-27）**：原先"开始淘汰"名不副实（淘汰只挂在 `AcquireSlot` 上 ⇒ 全程命中就永不回收，
  实测阈值 44%/负载 47% 时 512 MiB 纹丝不动）。现补上**水位节拍**（`System.Threading.Timer` 每 1 秒，越线即主动摘最冷的块）
  + **Slab 归还系统**（空闲链按 Slab 分组 ⇒ 整块空闲即 `NativeMemory.Free`）——这是水位能真回落的唯一办法
  （只回收块不改提交量）。检查器新增三行：`水位触发`/`已归还系统`/`已分配 Slab`。
  完整订正与复审（含锁序、自检的在途窗口）见 `关于内存缓存的进一步讨论.md` §2.2/§6.7/§6.9/§6.10。
- **下一步（M2 收尾 → M3）**：用户本地编译 + 验证这次 L1 改动（见下）→ 冒烟回归（连接 + 随机读 + `fsx`）
  → M3 换**机械盘**做冷/热对照（§5.5 的性能模型已给出预期）。
  **验证 L1 淘汰是否真生效的步骤**：把 `EvictionThreshold` 设到**系统基线之上**，重启程序 →
  用 `repro_bigread.py --seq --direct --no-verify --size-mb 512` 灌满缓存（检查器看"已分配 Slab"增长）→
  **停掉程序、把阈值改到基线之下、再启动**（配置是启动时快照，运行中改无效）→ 重新灌一遍并盯检查器三行：
  `水位触发` 开始涨 → `累计淘汰` 增长 → **`已归还系统` 增长、`已分配 Slab` 与"系统内存负载"回落**。
  注意顺序：先淘汰、后归还（不是同一拍），且阈值低于基线时缓存会退化成"最近活跃热子集"（这是语义正确的）。

### 7.1 阶段一：三条验证腿（当时都绿；工具与脚本现已归档到 `废/`）

> 下表里的脚本与工具均已归档：四个语义面脚本在 `废\阶段一-实验脚本\`，
> `fsx.exe` 在 `废\第三方-WinFsp资料\fstools-master\src\fsx\`。

| 腿 | 覆盖什么 | 怎么跑 | 当前结果 |
| --- | --- | --- | --- |
| **语义 / 协议面** | 每个回调的正确语义与错误码 | 见第六章 | 78 条全绿 `--- COMPLETE ---`；真盘 `D:`/`J:` 与加速盘 `X:`/`Y:` **四组同绿** |
| **真盘并排对照** | 错误码 / 语义面与 NTFS 的等价性 | `python repro_errcode_parity.py all <真盘根> <虚拟盘根>` | **19 一致 / 0 差异**（P3 稀疏文件属能力缺口，不计差异） |
| **随机 I/O 一致性** | 写 / 读 / mmap 读写 / truncate 的内容与大小 | 见下方 fsx 命令 | 两盘均 `All operations - 5293 - completed A-OK!`，且操作轨迹**逐行一致** |

```cmd
rem fsx：真盘基线与经虚拟盘各跑一遍，**同参数同 seed** 才可比
cd /d D:\projects\ramdisk\fstools-master\src\fsx
fsx.exe -N 5000 -F 64m -o 256k -c 1000 -C -S 12345 D:\fsx-src\fsx.dat
fsx.exe -N 5000 -F 64m -o 256k -c 1000 -C -S 12345 X:\fsx-src\fsx.dat
```

fsx 使用上的两个坑（都是 vendored 工具的既有问题，不是本项目代码）：

- **不要用 `-P`**：它按"fname 是相对路径"的老用法拼接，传绝对路径会拼出 `<dir>/D:\...\x.dat.fsxgood` 这类非法路径。
  不给 `-P` 时 `.fsxlog` / `.fsxgood` 自动落在 **fname 旁边**（正确用法就是本页命令这样）。
- **`-y` 不能加**：usage 文本里列着，但 getopt 串里没有它 ⇒ `illegal option -- y`。这是该工具**文档与实现漂移**，
  别照帮助文本抄参数（已逐个核对，只有 `-y` 缺失）。

失败时：用**同一个 `-S <seed>`** 复现，并 `fc /b <文件> <文件>.fsxgood` 比对（fsx 会打印 `Correct content saved for comparison`）。

### 7.2 技术债与疑点：阶段一已清零

- 阶段一的 TD-01～TD-22 与四条疑点**全部落地并验证**；逐轮证据链随条目一并归档到 `废\` 与 git 历史
  （`未解决的疑点.md` 已于 2026-09-30 整册重写为只管阶段二）。

### 7.3 性能实测基线（`repro_bigread.py`）

512 MiB 文件、1000 次随机读、平均 71.8 KiB/次；下表取 **`--direct` 口径**（`FILE_FLAG_NO_BUFFERING` 绕过
Windows 文件缓存——不加 `--direct` 时 512 MiB 会被内核缓存整个吃掉，**测不出本层价值**）：

| 通道 | 冷（第 1 遍） | 热（第 2/3 遍） | 相对真盘（热） |
| --- | --- | --- | --- |
| `J:` 真盘（机械） | 5.3 MiB/s | 5.3 MiB/s | — |
| `Y:` 加速 `J:` | 5.2 MiB/s | **1004.6 MiB/s** | **冷→热 194×** |
| `D:` 真盘（SSD） | 2810 MiB/s | 2674 MiB/s | — |
| `X:` 加速 `D:` | 236.8 MiB/s | 969.5 MiB/s | 仅 34%（**净亏**） |

四条结论：

1. **命中路径 ≈ 70 µs/次（≈1 GiB/s 天花板）**，且与后端盘无关（`X:` 969.5 vs `Y:` 1004.6）⇒ 瓶颈全在本层
   用户态路径（winfsp 往返 + 按 4 KB 块逐块拷贝），这也解释了 X:/Y: 比真盘"慢 3 秒"的元数据开销。
2. **未命中回填 ≈ 300 µs/次**：机械盘上被寻道（13 ms）淹没，SSD 上暴露成约 15 倍惩罚。
3. **对 SSD 后端本层是净亏**（只有真盘的 34%）。收益只在"后端是机械盘"或"工作集远超内存、OS 缓存被冲掉"
   时才成立——**评估本层必须把工作集做到内存装不下**，512 MiB 是玩具规模。
4. **本盘几乎吃不到 Windows 文件缓存**：buffered 与 `--direct` 的差只有 11%（热）/ 27%（冷）；
   若内核缓存生效，热读应跳到 3 GiB/s 量级。**待查**，也是唯一能直接提升 ~3× 吞吐的优化入口。

### 7.4 有意留空 / 尚未排期

- **有意留空**（各自括号里就是理由；下面这几条都是阶段一形态的，随形态废弃只作背景）：G1（绕盘写入不失效，需设计决策）、
  G3（已核实无行为差异）、G4（EA/WSL/HardLinks/DeviceControl 能力位——宣告即意味着承诺实现）、
  G5（`ReparsePointsAccessCheck`，收紧需独立决策）、G6（`FileInfoTimeout=1000`，调参需性能基线）、
  G9（`SetSparse`——绑定层根本没这个回调）；两条**协议限制**（`Overwrite` 无 SD 参数、流枚举无错误出口）；
  `FileIdInfo`（class 18）回 `87`（winfsp 驱动缺口，本层无法修）。
- **尚未排期**：
  - **自动淘汰（L1 LRU）已上线**（2026-09-26，§6 的「时间桶 + 自排序队列」；TD-12 结案），
    `EvictionThreshold` 现在真的会被执行；**仍没有**「手动清空/归还 Slab」的入口（Slab 归还属 §6.9 阶段 2）。
  - **SSD 二级缓存（L2）已上线**（2026-09-26，§7；2026-09-30 改造为"单条 M 环 + ghost"），
    功能验证（正常关服后的持久性、异常终止后的校验恢复、填充期→稳态的学习速度）待补。
  - 缓存管理面：绝对内存额度、按路径/扩展名选缓存对象、pin/预热——均未实现。
  - 压力测试补面：多进程**并发**（fsx 是单进程单文件，需多开实例 / 多开窗口）、目录级操作
    （原属 fstorture，但它 `pthread` + `fork` 不可移植）。

