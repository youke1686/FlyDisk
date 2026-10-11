# AGENTS.md

本文件供 AI 编码代理（Agent）阅读，用于快速了解本仓库（FlyDisk）的结构、技术背景与工作规则。
在执行任何任务前，请先完整阅读本文件并严格遵守其中的规则。

> **最高优先工作规则（先计划、后动手）**：任何修改都必须**先给出书面修改计划，等待用户明确确认后再动手**。
> **不得边想边改**，**不得未经确认就改动任何文件**。计划至少写清四点：**改哪些文件、每处怎么改、为什么、影响面 / 风险**。
> **用户未确认 = 一律不动手**；计划被修改后要重新确认，不能拿"上次那份计划"当授权。
> 这条规则**优先于本文件其余所有内容**，也优先于任何"顺手改一下"的便利考虑。

***

## 一、项目概述

**FlyDisk** 是一款面向 **机械硬盘（HDD）** 的块级缓存加速器：用内存（L1）+ 另一块盘（L2，推荐 SSD）两级缓存，
加速目标机械盘上的热数据；对上层交出的仍是一块**普通本地固定盘**。

- **形态**：单进程 WinForms 程序（`FlyDisk.exe`，`requireAdministrator`），界面与引擎同程序集。
- **接管方式**：点「启动加速」后，程序把目标盘 **整盘脱机 + 独占打开**，再用**自研 iSCSI target**（协议层用现成库）
  把这块盘重新暴露给 Windows（只监听 `127.0.0.1`）。Windows 照常挂上盘上原有的 NTFS 卷、盘符回来，此后读写都先过缓存层。
- **为什么不用 WinFsp**：阶段一的 WinFsp 文件系统形态撞上驱动层硬边界（原神启动器判定"这不是正常本地卷"而灰掉按钮）。
  换成 iSCSI 块设备后，对外是 `disk.sys` 枚举出的**真实系统级磁盘**，问题消失。阶段一的代码与第三方资料已整体归档到 `废/`
  （**只读存档，不要在其中继续开发**）。
- **只做读缓存**：写路径是**写透传**（+ 写命中刷新 W1 + 块级失效），不做 WriteBack。
- **停止加速后盘保持脱机是设计，不是 bug**（盘归本程序管，跨重启的 L2 缓存才敢采信）；要还盘给系统走菜单「重新联机硬盘」。
- 项目处于 **Beta** 阶段，以 **GPL-3.0-or-later** 发布。

### 单进程内的分层（命名空间）

| 层 | 命名空间 | 主要内容 |
| --- | --- | --- |
| 界面层 | `FlyDisk` | `Form1`（启停 + 状态栏 + 菜单 + 日志 + **L2 启动前预检**）、`Settings`、`Inspector`、`RemoteForm`、`PickerForm`（通用选择框）、`L2Manage`、`L2Verify` 的 UI 部分、`HelpDocument`、`UpdateService`、`Program` |
| 引擎层 | `FlyDisk.Engine` | `TargetService`、`CachedPhysicalDisk`、`PhysicalDiskHandle`、`IBlockSource`、`CacheService`（L1）、`SsdCacheService`（L2）、`RemotePeer` / `RemoteProtocol`、`IscsiInitiator`、`LogService`、`SystemMemory`、`L2Verifier` |
| 模型层 | `FlyDisk.Models` | `DiskConfig`、`CacheStats`、`ConfigService`、`ServiceConstants` |
| 主题层 | `FlyDisk.Theming` | `ThemeManager` / `ThemePalette` / `ThemedForm` / `ThemedTabControl` / `AppIcon` / `ShortcutIcon`（浅色 / 深色） |
| 本地化 | `FlyDisk.Localization` | `Locale` / `LocalizationManager`（zh-CN / en-US） |

- 主按钮是一条链：「启动加速 → 取消请求 → 停止加速」。启动 = **校验 → 整盘脱机 → 独占打开 → 装缓存 → 起 target →
  `IscsiInitiator` 自动登录**；任一步失败会把盘恢复成打开前的样子，不把用户的盘留在"看不见"的状态。
- **用户可见的过程信息一律由界面层自己说**（主界面日志框 / 状态栏 / 弹窗）；引擎内部的异常、自检失败、降级处置只落盘诊断日志。

***

## 二、关键设计要点（不变量）

改动下列代码前，务必先读懂并保持这些约束。

### 2.1 目标盘与启停

- **启动校验**：块大小 4 KB / 非系统盘 / 非本程序所在盘 / **非分页文件（pagefile.sys）所在盘** / 非只读为**硬拦**；
  机械盘、可移动介质只**警告**；L2 缓存目录必须与被加速盘**不同盘**（启动即拒绝同盘，纯写放大）。
  分页文件盘判据统一走 `PhysicalDiskHandle.DescribeTargetDiskBlockReason`（整盘脱机会让内核换页失败，直接蓝屏 `0x7A`）。
- **独占**：`IOCTL_DISK_SET_DISK_ATTRIBUTES`（`persist=true`）整盘脱机 + `dwShareMode=0` 独占打开。
- **停止顺序（不可颠倒）**：**先确认没有发起程序还连着——有连接就拒绝停止**（判据 = 本机监听端口上还有 ESTABLISHED 的
  TCP 连接）→ 停 target → 关闭 LUN 后端 → 等在途命令跑完（**上限 2 秒**）→ 冻结 L2 → 落盘账本 → 关句柄（**盘保持脱机**）。
  关命令入口与在途计数必须**原子同步**；若等在途超时，**不提交本次账本**，后台等在途命令实际退出后再释放资源，
  此期间 `TargetService.IsStopping=true`，**禁止重入、改设置、校验、重新联机**。
- **关机 / 重启 / 注销**：`Form1.OnFormClosing` 会走 `TargetService.TryStopForShutdown()`。它与 `Stop()` 的**唯一区别是不做
  "有连接就拒绝停止"的守卫**——关机时 L2 账本必须落盘，即便发起程序登不出去，也照样中止 iSCSI 服务、掐断会话再落账本。
  该路径**绝不外抛**（抛出会被 `ThreadException` 兜住并弹窗、反卡关机），收尾只落 `debug-security.log`。
  运行中点「停止加速」则**绝不**这样干。
- **自动挂载**（`Engine/IscsiInitiator.cs`）：`MSiSCSI 服务在跑 → AddIScsiSendTargetPortalW → LoginIScsiTargetW`；
  **只清理本程序的会话/门户，不停止系统 MSiSCSI 服务**。两处硬约束：`IsInformationalSession = false`（为真 PnP 不枚举 LUN）、
  `IsPersistent = false`（为真会写持久登录、与本程序全权管生管死冲突）。任一步失败**不硬失败**，只记录 + 提示手动挂载。

### 2.2 L1（内存缓存）

- **非托管内存池**：`Marshal.AllocHGlobal` 直接向系统申请大块内存，绕过 GC；以 **64MB Slab** 为单位、槽位 **4KB**。
  块与扇区的对应是"`SectorsPerBlock` × `BytesPerSector`"，启动校验必须等于 4096。
- **索引**：一张 `ConcurrentDictionary<全局块号, 槽位 + 时间轮节点>`（键 = `LBA / SectorsPerBlock`）。块设备下没有文件，
  故**没有**"文件表 / `CacheEntry` / `BitArray`"（那是阶段一文件系统形态的产物）。
- **淘汰**：LRU——「时间桶 + 自排序队列」，在 `AcquireSlot` 里按需增量摘一批（O(K)，无需后台线程），另有 1 秒水位节拍主动摘最冷块。
  **L1 查找 / 提升 / 拷贝 / 失效共用 `_wheelLock`**，不得依赖宽限期保护裸指针；槽位带所属 Slab 与槽号，归还为 O(1)。
- **水位**：`EvictionThreshold`（默认 0.8，超线触发淘汰）必须小于 `StopCachingThreshold`（默认 0.9，超线只透传不缓存）。
  **Slab 归还系统**是水位能真回落的唯一办法（空闲链按 Slab 分组，整块空闲即 `NativeMemory.Free`）。
- **没有**「手动清空 / 归还 Slab」的入口；若将来要做，按「界面按钮 + 引擎方法」实现，不要恢复死代码。

### 2.3 L2（SSD 缓存）

- 在 SSD 上维护一个**按需、按 Slab（64 MB = 16384 槽）整块增长**的容器（`cache.dat`）+ 一份账本（`index.bin`）：
  **只有 L2 槽位索引，只在正常关服时落盘一次**，且每次开机轮换容器身份戳（运行期零写盘）。掉电 / 强杀 / 崩溃会让身份戳对不上。
  容量**必须规整为整 Slab**；启动**不再一次性预分配整文件**（那是"大 L2 启动卡住"的根因），容器随写入按 Slab 扩展；
  扩展时用 `SetFileValidData` 免掉文件系统零填充（该卷不支持 VDL 语义 / 特权不可用 ⇒ 本会话回退纯 `SetLength`；
  扩展失败只关掉"扩容"能力、**不停用 L2**）。空闲栈按升序出槽，保证文件从头连续增长。
- **准入**：单条 **M 环（FIFO-reinsertion）+ ghost**；只有"回源读到整块"一个入口——可用槽位在保留量之上（**激进期**）就一律落位，
  不足之后（**保守期**）只收 ghost 命中的块；分界值 = `SsdConservativeThreshold`（默认 0.80，越界夹到 `[0.50, 0.99]`）。
  **L1 命中对 L2 完全静默**（两者必须正交：L1 管近期性，L2 管跨重启的长期保留）。
- **账本是否可信**由一条设备级判据决定：打开时该盘**原本就脱机**（⇒ 上次运行之后没人能写它）。
  盘原本联机、或上次未正常关服（身份戳不符）时**一律不采信**。
- ★ **职责边界（2026-10-11 约定）：拦截一律在 UI（启动前预检），引擎只做加速。**
  所有"要不要清掉 / 要不要校验 / 要不要缩放 L2 账本"的判定与询问集中在 `Form1.PrepareL2BeforeStart`
  （在**脱机之前**问，用户中止时系统状态一点没动）；"清空"= **直接删账本文件**（`L2Manage.TryClear`，
  用引擎与校验器共同持有的单实例锁做闸）。引擎因此**不再有任何"要用户先拍板"的返回值**——
  `EngineResult` 只带一个 `EngineOutcome`（`Ok` / `Rejected`）加一条给用户看的消息，不再有 `L2Mismatch` /
  `L2NeedsVerify` 这类"要 UI 再去拍板"的返回；`Start` 也没有 `allowL2Reset` 这类授权参数。
- ★ **不静默回退**：引擎装载期只有两种结局——**确实没有账本**（容器与索引都不存在）⇒ 新建空缓存；
  **其余任何不自洽**（文件不成对 / 不属于本盘 / 盘原本联机 / 身份戳不符）⇒ 抛 `L2LedgerUnusableException`
  **终止启动**。**L2 初始化失败也不再静默降级为"仅 L1"**，而是报错并引导用户到设置里关闭 L2、
  或去「L2 管理」清理。只读探测（`DetectLedgerMismatch` / `LedgerStampMismatch` / `DescribeAllL2Caches` 等）
  仍留在引擎（二进制格式知识不外泄），但它们**只输出事实**；探测本身出意外时上抛，由 UI 报错中止。
- **「L2 管理」**（`L2Manage.cs`；入口 = 设置里 L2 缓存盘右侧的「L2 管理」按钮）：扫出整机上的 L2 缓存目录
  （`<卷根>\FlyDisk.Cache`，见 `SsdCacheService.DescribeAllL2Caches`）→ 选一份（`PickerForm` 的「L2 缓存」模式）
  → 操作窗口（清空 / 打开目录 / 设为当前缓存盘）。用途是清理"用户忘了的"缓存（不自动删是设计：
  用户可能只是临时关掉 L2），也是 TODO「缓存的手动管理」（快照等）的落点。
- **启动前预检的顺序**：① 容器属于本盘吗 → ② 账本可信吗（盘原本联机 / 上次未正常关服；**远程不支持逐块校验**，
  只能清空）→ ③ 容量变化 / 被缓存盘可用空间夹取（缩 / 扩分别说明）。**校验没跑干净 ⇒ 中止启动、不做任何补救动作**。
  另外**本次不用 L2 却还留着这块盘的账本**时，仍先问一次（`ConfirmL2LedgerUnused`）：它只把身份戳改掉让账本失效，
  比清空保守——防的是用户误操作。
- **容量 / ghost 参数与本次配置不一致不再清空，改为缩放保留**：缩容**按位置截断**（保留低槽位那批块，与热度无关；
  按热度保留需要 compaction，会在启动时重新制造卡顿），扩容保留全部；**启动前由 UI 弹窗确认**（缩 / 扩分别说明），
  引擎只按"已确认"执行（确认在 UI 层，引擎不再拦）。详见 `docs/L2容器按需增长与容量缩放_设计.md`。
  容量还会被**缓存盘可用空间夹取**（`MIN_KEEP_FREE_BYTES = 1 GiB`，避免 SSD 快满时掉速；判据把 L2 已占用空间加回预算，
  否则同一份空间被扣两次）；**发生夹取时同样弹窗说明原因与后果**。
- **L2 校验 / 修复**（`L2Verify.cs`，整个功能单文件）：**绝不构造 `SsdCacheService`**（其构造器会轮换身份戳 ⇒ 账本当场作废）；
  槽位数以盘上记录为准；源盘只读句柄 + 保持脱机；修复只回写 L2 容器。入口先 `TryReadOwnerIdentity` 只读 peek 容器头归属身份，
  把候选收窄到身份匹配的那块盘（散列不可逆 ⇒ 枚举本地盘重算比对；0 匹配则拒绝）。容器长度校验已放宽为"覆盖账本最大占用槽"。
- **L2 运行期读写 IO 故障**：停用 L2、读取回源、写入失败作废受影响映射，未登记的新槽退回空闲栈；本次停止跳过账本提交，
  下一次启动要求校验或重建。源盘写请求失败可能已部分完成，必须作废整个请求涉及的 L1/L2 范围。

### 2.4 写路径

- 写直透物理盘（`FILE_FLAG_WRITE_THROUGH`，不经过系统写缓存）；写后**按块处置**被覆盖的块：整块被完整覆盖的块
  `UpdateBlock` 就地刷新已有副本（只刷已有副本、不占新槽，W1），残缺的首尾块才 `InvalidateRange` 作废。

### 2.5 远程形态（实验性）

- `RemotePeer` 是**对称的对端**（配对后两端共用同一类型），一条 TCP、两个阶段（配对 → 服务）、单 in-flight、
  心跳 5s/30s、块读写**沉默等待** + 重连后重发在途请求。协议常量与帧收发原语在 `RemoteProtocol.cs`
  （帧 magic `RCDH/RCDQ/RCDS/RCDP`，`Version=1`；**只有握手有超时** `HandshakeTimeoutMs=10s`，传输阶段刻意没有任何请求超时）。
- **重连的主动权永远在拨号方**（监听方只能守着端口等对方连回来）。重连比较必须保存并使用**重连前**的设备描述，
  不能用已被 `BeginService` 覆盖的字段。
- 监听/接受循环**必须循环且只在监听器真被关时才退出**（其余异常＝垃圾连接，记日志 `continue`，**不能**因一次垃圾连接杀掉 Accept 循环）。
  会话帧先检查长度再分配；超限写帧断开连接，避免未消耗的负载破坏后续帧解析。
- 远程大读写按协议上限 **1 MiB 分段**。入口（`RemoteForm`）**必须弹"不保证可用 / 不保证数据安全"的警告**（`OKCancel`，默认取消）。

### 2.6 诊断与日志

- `LogService`（`debug-security.log`，8MB 滚动）是**只落盘**的诊断账本，**没有**进程内展示型日志通道。
- 高频成功痕迹默认不落盘（前缀表 `LogService.TracePrefixes`），失败 / 异常 / 探针行始终落盘；
  需要完整逐笔账本时把 `config.json` 的 `TraceFileLogging` 设为 `true` 并重启（启动时界面日志会报当前模式）。

***

## 三、目录结构

```
d:\projects\FlyDisk\
├── AGENTS.md                      # 本文件
├── README.md                      # 面向用户的项目说明（docs/README_en.md 为英文版）
├── TODO.md                        # ★ 唯一待办入口：现状 / 待施工（功能、远程、其他）
├── THIRD-PARTY-NOTICES.md         # 第三方组件与许可证清单
├── repro_bigread.py               # 性能实验：大文件随机读（默认只测被测盘；--compare <路径> 才与真盘并排对照；
│                                  #   --direct 用 FILE_FLAG_NO_BUFFERING 绕过 Windows 文件缓存）
├── 原理演示.html                   # 原理演示页
├── docs/
│   ├── README_en.md               # README 英文版
│   ├── res/                       # README 用到的截图
│   ├── L2容器按需增长与容量缩放_设计.md     # ★ L2 按需按 Slab 增长 + 容量缩放的设计与决策（D1–D9）
│   └── iSCSI克隆盘被判冗余路径_排障记录.md   # ★ 已知缺陷的排障记录（根因待查明，见 TODO）
├── FlyDisk/                       # ★ 唯一项目：WinForms 管理员程序 + 引擎层
│   ├── FlyDisk.slnx / FlyDisk.csproj
│   ├── app.manifest               # requireAdministrator（脱机 + 独占 PhysicalDrive 必需）
│   ├── Program.cs                 # 入口 + 未处理异常兜底
│   ├── Form1.cs / Settings.cs / Inspector.cs / RemoteForm.cs / PickerForm.cs / L2Manage.cs
│   ├── L2Verify.cs                # L2 校验/修复（Engine.L2Verifier + L2VerifyForm，单文件）
│   ├── HelpDocument.cs / 帮助文档.html   # 帮助文档（单文件双语，嵌入 exe + 每次启动覆盖落盘）
│   ├── UpdateService.cs           # 检查更新（只比 csproj 里的 <Version> 是否不同）
│   ├── Engine/                    # namespace FlyDisk.Engine
│   ├── Models/                    # namespace FlyDisk.Models
│   ├── Theming/                   # 浅色 / 深色主题
│   ├── Localization/              # zh-CN.json / en-US.json（键按「模块.用途」命名）
│   ├── Licenses/                  # 随程序分发的许可证全文
│   ├── icons/                     # 应用图标（浅/深，ico + png + 源 svg）
│   └── bin/ obj/ .vs/             # 生成产物与 IDE 缓存（禁止手工修改）
└── 废/                            # ★ 归档区：仅存档，不要在此继续开发
    ├── 阶段一-WinFsp文件系统层/     # 阶段一的文件系统实现
    ├── 阶段一-IPC与SCM/            # 阶段一双进程形态（Core / Service / IpcClient / ServiceControlService）
    ├── winfsp-native/ WinFsp.Native.Tests/ HelloFs/   # WinFsp 原生层实验与示例
    ├── 第三方-WinFsp资料/           # winfsp 上游源码 / wiki / fstools-master（fsx 等）
    ├── 第三方资料/                  # iSCSIConsole / DiskAccessLibrary 参考实现
    ├── 无关目录/                    # pcl / docker / ISCSIConsole 备份等
    ├── 阶段二-iSCSI块设备形态.md     # 阶段二立项与设计文档（历史）
    ├── 关于内存缓存的进一步讨论.md    # 缓存方案（L1 淘汰 / L2 淘汰结构）设计依据
    ├── 后续待办.md / 未解决的疑点.md / 深色主题.md / ...
    └── RamCacheDisk/ RamCashDisk/ TestApp/ 等废弃实验工程
```

> **注意**：`废/` 下的文档（`阶段二-iSCSI块设备形态.md`、`关于内存缓存的进一步讨论.md`、`后续待办.md`、`未解决的疑点.md` 等）
> 曾长期放在仓库根目录，源码注释里仍有对它们的引用；它们现在**已归档**。当前活跃文档只有 `README.md`、`TODO.md`
> 与 `docs/` 下的设计 / 排障记录。

> **注意**：`废/` 下的 `第三方资料` 目录，尽管标记为废，但依旧很有价值，标记为废只是避免上传到GitHub中带来不必要的麻烦，并不是说它已经没有价值了。

***

## 四、重要规则（必须严格遵守）

### 4.1 禁止编译与运行

1. **禁止执行任何形式的编译 / 构建操作**：`dotnet build` / `publish` / `run`、MSBuild、Visual Studio 构建、任何脚本化构建命令。
2. **禁止运行项目、程序或可执行文件**：包括直接运行 `bin/` 下已生成的 `.exe`、挂载/卸载磁盘等系统级副作用操作、通过 docker 运行容器。
3. **禁止执行任何测试代码**：不得自行运行单元测试、集成测试或任何验证脚本。

> 原因：本项目涉及磁盘脱机 / 独占与挂载，编译产物运行会改动系统挂载点，风险较高。

### 4.2 修改后的验证方式

- 代码修改完成后，**一律交由用户在本地自行编译验证**，代理不得代替用户编译；
- 需要运行或测试任何代码时，**只提供命令与步骤说明，由用户手动执行并反馈结果**；
  - 我们的工作流程是代理修改，用户验证并反馈，代理再修改，用户再验证并反馈，如此往复；
  - 所以并不需要代理急于一次性完成所有修改并且确保无误，可以进行多次的实验反馈。
- 代理可做的静态检查仅限于：阅读代码、检索引用、确保修改在语法与逻辑上自洽。

### 4.3 代码与文档约定

- 遵循项目现有风格：显式 `using`、中文注释与中文 UI 文案、`#region` 分区、Win32 P/Invoke 集中放在文件尾部的 `#region Win32 API` 中；
  每个源文件顶部带版权与授权声明。
- **命名空间按"层"划分**：新增文件按归属选 `FlyDisk`（界面）/ `FlyDisk.Engine`（引擎）/ `FlyDisk.Models`（配置与 DTO）/
  `FlyDisk.Theming` / `FlyDisk.Localization`。**不要**再引入 `RamCacheDisk.*` 这类阶段一命名空间。
- **本地化**：用户可见文本一律走 `Locale.T(key)`，中英各一份 JSON，键按「模块.用途」命名，改动需两份同步。
- **帮助文档**：`帮助文档.html` 是单文件双语，改中文段落必须同步改英文段落；它嵌在 exe 里，每次启动覆盖写一份到 `%ProgramData%\FlyDisk\`。
- **改代码同步更新本文件**；新的讨论与决策写进 `TODO.md`，结论收敛到 `TODO.md`。
- 不修改 `废/` 下的归档内容（需要时先取回）；不触碰 `bin/`、`obj/`、`.vs/`、`__pycache__/` 等生成目录。
- 更新代码后只需**关闭程序 → 重新编译 → 启动**即可生效（运行中编译会因 exe 被占用而失败；若目标正在运行，请先点「停止加速」
  ——直接退程序会让盘留在脱机状态）。

### 4.4 单位与口径

- **容量 = 1024 进制标 MiB / GiB / TiB，吞吐 = 10 进制标 MB/s**。要守的是**"标注必须与进制一致"**
  ——1024 进制的值不能写 `GB`（设置对话框那处已从 `(GB)` 改为 `(GiB)`）。
- 检查器里读速是 **UI 侧按秒采样差分**出来的（快照只有累计计数）：`加速后读取` = `ReadSectorsTotal` 增量，
  `原盘读取` = 回源块数 × 4 KiB；窗口 5 秒，单位 MB/s（10⁶）。

***

## 五、环境与依赖

| 项 | 说明 |
| --- | --- |
| 目标框架 | .NET 10.0（`net10.0-windows`） |
| 语言特性 | nullable、隐式 using；`AllowUnsafeBlocks` **必需**（`CacheService` 的指针操作 + `PhysicalDiskHandle` 的原始扇区读写） |
| 外部依赖 | NuGet `ISCSI` 1.5.6（iSCSI 协议层）+ `DiskAccessLibrary.Win32` 1.6.3（`Disk` 基类与 Win32 助手），均 LGPL-3.0；`System.ServiceProcess.ServiceController` 10.0.12（拉起 MSiSCSI 服务） |
| 管理员权限 | `app.manifest` 声明 `requireAdministrator` |
| 数据目录 | `%ProgramData%\FlyDisk\`：`config.json` + `debug-security.log` + `crash.log` + 落盘的帮助文档；L2 容器与索引在其下的 `FlyDisk.Cache\`（`cache.dat` / `index.bin`） |
| 解决方案 | `FlyDisk/FlyDisk.slnx`（只含一个项目） |
