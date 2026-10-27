# iSCSI 意外断联分析报告

> 对应 issue：<https://github.com/youke1686/FlyDisk/issues/18>  
> 场景：Win10 x64 19045 / 32 GB 内存 / 被加速盘为 USB 3.0 移动硬盘 / L1 默认 / L2 为 SSD 60 GB  
> 现象：同时发起多个文件夹的小文件移动时，大概率卡住；目标盘 100% 活动、读写 0；随后系统上新的 IO（打开文件、网络位置等）也像被挂起；iSCSI 发起程序一直显示“正在重新连接”；强制停止并重新开始加速后恢复。  
> 状态：**仅静态代码分析，未修改任何代码**。根因仍需现场日志、系统事件和设备信息确认；本报告给出已确认的代码事实、候选路径、排序和取证方法。  
> 分析依据：仓库内 `废/第三方资料/iSCSIConsole`（`ISCSI.csproj` 版本 `1.5.6.0`）与 FlyDisk 源码。

---

## 1. 结论速览

| 级别 | 结论 |
| --- | --- |
| **已确认（代码事实）** | FlyDisk 运行期没有“主动断开 iSCSI”的路径；只有用户停止、强制停止、关机、启动失败清理会停 target。 |
| **已确认（代码事实）** | 每个 iSCSI target 只有**一个**工作线程，所有 SCSI 命令严格串行；Windows 侧命令窗口为 **64**。 |
| **已确认（代码事实）** | 连接断开、登录终止旧会话、登出、`ISCSIServer.Stop` 都会调用 `ConnectionManager.ReleaseConnection`，而它会**无超时**等待在途命令（`CountdownLatch.WaitUntilZero` → `WaitHandle.WaitOne()`）。这是“一直正在重新连接”的直接代码解释。 |
| **已确认（代码事实）** | 写命令走 R2T 分片时，断线不会清理 `ISCSISession.CommandsInTransfer`；会话复用时可能造成最多约 28 条命令被永久延迟、更后面的命令乱序执行。 |
| **已确认（代码事实）** | `ISCSISession.IsFirstCmdSNPreceding` 里 `const uint commandWindow = 2 ^ 31 - 1;` 在 C# 中 `^` 是异或，实际值为 **28**，不是 `2^31-1`。这是上游库的一个明确缺陷。 |
| **已确认（代码事实）** | 源盘写使用 `FILE_FLAG_WRITE_THROUGH`，每 1 MB 一次同步 `WriteFile`；没有超时、没有 `CancelIo`、没有设备移除检测/重新打开。 |
| **高度怀疑（主因）** | USB 3.0 机械盘在“大量小文件 + 多任务并发”下写延迟急剧上升或短暂停顿 → 单工作线程队头阻塞 → Windows 发起端命令超时/重连 → 重连/登出/停止被 `WaitUntilZero` 挡住 → 表现为“一直正在重新连接”。 |
| **必须排除** | 源盘在运行期被系统重新联机 → 与克隆盘同磁盘签名/同序列号 → 克隆盘被判“冗余路径”并脱机；项目已有此类排障记录。 |
| **放大器** | L2 每块 4 KB 同步写 SSD、L1 可涨到物理内存 90%、UI 线程每秒对独占 USB 盘发 IOCTL、同步日志与断线日志风暴。 |

---

## 2. 环境与现象

### 2.1 用户环境

- 系统：Windows 10 x64 10.0.19045.5608
- 内存：32 GB
- 被加速盘：USB 3.0 移动硬盘（机械盘）
- L1：默认值（内存水位 0.8 开始淘汰 / 0.9 停止接纳）
- L2：SSD，最大 60 GB
- 其他配置：默认
- 操作：从其他盘同时发起多个文件夹移动请求到加速后的克隆盘；文件数量多、单文件小
- 结果：大概率卡住；目标盘 100% 活动、读写速度 0；iSCSI 发起程序显示“正在重新连接”且一直连不上；强制停止并重新开始加速后恢复

### 2.2 现象中需要特别区分的点

“活动时间 100%、读写 0”至少可能是三块不同的盘：

1. **USB 源盘**：设备侧停顿、SMR/桥接芯片/坏道/供电问题；
2. **L2 SSD**：60 GB 缓存容器的 4 KB 随机写把 SSD 压满，或与系统盘/分页文件竞争；
3. **iSCSI 克隆盘**：target 工作线程被源盘 IO 卡住，Windows 侧命令队列排队，任务管理器看到克隆盘 100% 活动但无完成量。

这三者对应的主因不同，现场必须确认是哪一块盘。仅凭“100% 活动、0 吞吐”不能直接归因。

---

## 3. 数据路径与关键组件

正常写路径（多个文件移动时，所有命令最终串行落到一个工作线程）：

```text
应用 / Explorer / CopyFile
        │
        ▼
克隆盘上的 NTFS（Windows 卷/文件系统）
        │  SCSI WRITE / READ / SYNCHRONIZE CACHE ...
        ▼
Windows iSCSI 发起端（MSiSCSI / iscsidsc）
        │  TCP 127.0.0.1:3260
        ▼
FlyDisk 内的 ISCSIServer（TalAloni iSCSI 1.5.6.0）
        │  每连接一个接收回调 + 一个发送线程
        ▼
ISCSITarget / VirtualSCSITarget / SCSITarget
        │  ★ 每个 target 只有一个后台工作线程，FIFO 串行执行
        ▼
CachedPhysicalDisk
        ├── L1：CacheService（非托管内存池）
        ├── L2：SsdCacheService（SSD 上的 cache.dat / index.bin）
        └── 写透传：PhysicalDiskHandle
                │  同步 WriteFile + FILE_FLAG_WRITE_THROUGH，1 MB 分块
                ▼
        USB 3.0 移动硬盘（被整盘脱机 + 独占打开的源盘）
```

关键前提：

- 读路径：L1 → L2 → 回源；回源读到的整块会回填 L1/L2。
- 写路径：先写透传源盘，再逐块更新/失效 L1/L2。
- 所有 SCSI 命令在 target 工作线程上串行执行；读、写、元数据请求共享同一个队列。
- 运行期 L2 账本不落盘；只在正常停止时提交一次。
- 源盘保持脱机；停止加速后也不自动联机（使用 L2 的盘）。

---

## 4. 运行期断联路径总览

| 编号 | 路径 | 触发条件 | 代码位置 | 现场指纹 | 可能性 |
| --- | --- | --- | --- | --- | --- |
| A | FlyDisk 主动停 target | 用户停止 / 强制停止 / 关机 / 启动失败清理 | `TargetService.StopCore`、`ForceStopCore`、`StopForShutdownCore` | 日志有“停止”会话标记 | 本次现象不是主动停止；排除 |
| B | TCP 接收错误 | 发起端关闭连接、连接重置、socket 错误 | `ISCSIServer.ReceiveCallback` → `HandleConnectionTermination` | `会话结束：...（原因：ConnectionFailure）` | 中间结果，不是第一触发 |
| C | TCP 发送错误 | `clientSocket.Send` 抛 `SocketException` / `ObjectDisposedException` | `ISCSIServer.ProcessSendQueue` | 目标端无响应，发起端超时 | 低；通常与 B 同时发生 |
| D | 发起端命令超时 → 会话恢复/重连 | target 命令长时间不完成 / 连接长时间无响应 | Windows 侧；target 无超时机制 | 发起程序“正在重新连接”；日志反复 `New connection accepted` | **高（H1 的下游）** |
| E | 重连/登出/停止被在途命令挡住 | 任一命令仍卡在源盘 IO | `ConnectionManager.ReleaseConnection` → `WaitUntilZero` | 反复重连但不完成；停止时日志“在途命令 2 秒内未退出” | **高（与 D 组合）** |
| F | 会话复用 + 断线残留 R2T 命令 | 大写命令传输中连接断开，Windows 复用同一会话 | `ISCSISession.CommandsInTransfer` / `DelayedCommands` | 部分命令永久不完成；后续命令乱序 | 中；取决于 Windows 是否复用会话 |
| G | `commandWindow` 缺陷导致乱序 | 在途命令与后续 CmdSN 相差 > 28 | `ISCSISession.IsFirstCmdSNPreceding` | 可能表现为数据/元数据异常、NTFS 重试 | 中；正确性缺陷 |
| H | USB 源盘停顿/重置/重枚举 | 设备固件、SMR、坏道、供电、线材、UASP/BOT | `PhysicalDiskHandle` 同步写；无设备移除检测 | 源盘/克隆盘 100% 活动；Event 129/153/157 | **高（H1 的上游触发）** |
| I | 源盘被系统重新联机 → 冗余路径 | USB 重枚举后脱机属性丢失 | `TraceSourceDiskAttributes`（只诊断，无运行期守卫） | `源盘属性变化：脱机=是→否`；Event 158；`Redundant Path` | 中高；必须排除 |
| J | 工作线程未捕获异常 | `OutOfMemoryException`、`ObjectDisposedException`、其他非 IOException | `SCSITarget.ProcessCommandQueue` 无 catch | 进程退出；`crash.log`；崩溃弹窗 | 低；若进程一直活着可排除 |
| K | 缓存层死锁/无限循环 | L1/L2 锁或槽位逻辑 | `CacheService` / `SsdCacheService` | 工作线程卡住 | 低；静态检查未发现 |
| L | UI/日志/外部过滤驱动 | UI 线程 IOCTL、同步日志、杀软/VSS | `Form1.RefreshStatus`、`LogService`、系统 | 界面卡、日志写入慢、卷过滤 | 放大器；低作为第一触发 |

---

## 5. 代码级关键事实

### 5.1 一个 target 只有一个工作线程，命令严格串行

`SCSITarget` 在构造时启动一个后台线程：

- [SCSITarget.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/SCSITarget/SCSITarget.cs#L34-L67)

```csharp
public SCSITarget()
{
    Thread workerThread = new Thread(ProcessCommandQueue);
    workerThread.IsBackground = true;
    workerThread.Start();
}

private void ProcessCommandQueue()
{
    while (true)
    {
        ...
        SCSIStatusCodeName status = ExecuteCommand(command.CommandBytes, command.LUN, command.Data, out responseBytes);
        command.OnCommandCompleted(status, responseBytes, command.Task);
    }
}
```

注意：`ProcessCommandQueue` **没有任何 try/catch**。`VirtualSCSITarget.Write` 只捕获 `ArgumentOutOfRangeException` 和 `IOException`；`Read` 还捕获 `CyclicRedundancyCheckException`。其他异常会离开工作线程；在 .NET 中，后台线程未捕获异常会终结进程（`Program.ReportCrash` 会写 `crash.log` 并弹窗）。

Windows 侧命令窗口：

- [ISCSIServer.Parameters.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.Parameters.cs#L13-L35)：`DefaultCommandQueueSize = 64`
- [ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L368-L428)：`MaxCmdSN = ExpCmdSN + CommandQueueSize`

含义：

- 读、写、INQUIRY、READ CAPACITY、TEST UNIT READY 全部排在同一个 FIFO；
- 一条慢命令会阻塞后面所有命令；
- 多个文件移动只是让队列更长、LBA 更随机，不会增加并发度；
- 命令的**完成延迟**可以近似为“前面所有命令耗时之和”。

FlyDisk 侧也明确依赖这个前提：

- [CachedPhysicalDisk.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CachedPhysicalDisk.cs#L41-L43)：调用者是库的 target 工作线程，每 target 一个后台线程；
- [PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L111-L118)：共用文件指针、不做加锁；
- [SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L47-L49)：L2 表不需要加锁，因为只在 target 工作线程上跑。

### 5.2 三处无超时等待：`ReleaseConnection` → `WaitUntilZero`

- [ConnectionManager.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ConnectionManager.cs#L39-L49)

```csharp
public void ReleaseConnection(ConnectionState connection)
{
    // Wait for pending I/O to complete.
    connection.RunningSCSICommands.WaitUntilZero();
    connection.SendQueue.Abort();
    SocketUtils.ReleaseSocket(connection.ClientSocket);
    ...
}
```

- [CountdownLatch.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/Utilities/Threading/CountdownLatch.cs#L52-L55)

```csharp
public void WaitUntilZero()
{
    m_waitHandle.WaitOne();
}
```

`WaitOne()` 没有超时参数。

调用点：

1. **连接断开**：[ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L280-L304)  
   `HandleConnectionTermination` → `ReleaseConnection`，在接收回调线程上阻塞。
2. **登录终止旧会话**：[ISCSIServer.Login.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.Login.cs#L137-L175)  
   `SetUpNormalSession` 发现同 initiator/ISID/target 的旧会话时，先对旧连接 `ReleaseConnection`，再 `RemoveSession`。
3. **登出**：[ISCSIServer.Logout.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.Logout.cs#L59-L68)  
   `GetLogoutResponsePDU` 对每个要关闭的连接 `WaitUntilZero`。
4. **停止服务**：[ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L179-L200)  
   `Stop` → `ResetTarget` → `ReleaseConnection`。

直接后果：

- 只要有一条命令卡在源盘 `WriteFile` 上，连接断开后的清理线程会停在那里；
- 新的登录如果在 `SetUpNormalSession` 里遇到旧会话，也会停在那里；
- 登出（包括 FlyDisk 停止时的 `_initiator.Disconnect()`）也会停在那里；
- 停止服务也会停在那里（`TargetService.StopCore` 的 `_server.Stop()` 不会返回）。

这与用户看到的现象“发起程序一直显示正在重新连接，但一直连不上”高度吻合：TCP 可能已经连上，但登录/命令完成被旧命令挡住。

### 5.3 R2T 断线残留：`CommandsInTransfer` / `DelayedCommands`

- [ISCSISession.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSISession.cs#L37-L39)

```csharp
public List<uint> CommandsInTransfer = new List<uint>();
public List<SCSICommandPDU> DelayedCommands = new List<SCSICommandPDU>();
```

- [TargetResponseHelper.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/TargetResponseHelper.cs#L20-L72)

进入 R2T 的条件：

```csharp
if (command.Write && command.DataSegmentLength < command.ExpectedDataTransferLength)
{
    ...
    session.CommandsInTransfer.Add(command.CmdSN);
    ...
}
```

只有收到最后一个 Data-Out 时才会移除：

```csharp
if (offset + request.DataSegmentLength == totalLength)
{
    connection.RemoveTransfer(...);
    session.CommandsInTransfer.Remove(transfer.Command.CmdSN);
    ...
}
```

连接断开时：

- `ConnectionManager.ReleaseConnection` 只等 `RunningSCSICommands`（已经排队/正在执行的命令）；
- 对于还在 R2T 传输中的命令，它**不在** `RunningSCSICommands` 里（`ProcessPDU` 只对 `commandsToExecute` 增加计数）；
- 因此 `ReleaseConnection` 可能很快返回，`CommandsInTransfer` 里的 CmdSN 保留下来；
- 如果会话被复用，后续命令会通过 `IsPrecedingCommandPending` 看到这个残留项，进入 `DelayedCommands`。

补充：`RemoveSession` 会丢弃整个会话对象，所以“新建会话”路径会自然清干净；问题只在“复用同一会话”时出现。

### 5.4 CmdSN 校验与 `commandWindow` 缺陷

严格顺序校验：

- [ISCSIServer.PDUProcessor.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.PDUProcessor.cs#L16-L51)

```csharp
if (state.Session.CommandNumberingStarted)
{
    if (cmdSN != state.Session.ExpCmdSN)
    {
        return false;   // 后面会“忽略这个 PDU”
    }
}
...
if (!pdu.ImmediateDelivery)
{
    state.Session.ExpCmdSN++;
}
```

`ExpCmdSN` 在命令**到达**时就前移，不是完成时。重连后如果发起端重传旧 CmdSN，会因为 `cmdSN != ExpCmdSN` 被忽略。

更重要的缺陷：

- [ISCSISession.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSISession.cs#L120-L142)

```csharp
public static bool IsFirstCmdSNPreceding(uint cmdSN1, uint cmdSN2)
{
    // ...
    const uint commandWindow = 2 ^ 31 - 1;
    ...
}
```

在 C# 中 `^` 是异或：

- `2 ^ 31` = `29`
- `29 - 1` = **`28`**

也就是说，真正的“命令窗口”只有 28，而不是 `2^31-1`（`0x7FFFFFFF`）。target 对外宣布的窗口是 64（`MaxCmdSN = ExpCmdSN + 64`），两者不一致。

影响：

- `IsPrecedingCommandPending` 只认最近 28 个 CmdSN 内的先后关系；
- 在途命令与后续命令相差超过 28 时，后续命令会被判为“没有前驱”，从而**提前执行**；
- 这违反 `PDUProcessor` 注释里写的“读请求不应在前一个写请求的数据仍在 R2T 传输时执行”；
- 如果乱序的是对同一 LBA 的写，可能造成最终落盘顺序错误；
- `GetDelayedCommandsReadyForExecution` 也可能认错“最早的在途命令”，使一部分 `DelayedCommands` 留在队列里。

对“幽灵在途命令”假设的修正：

- 之前说“所有后续命令永久卡死”**不准确**；
- 实际是：最多约 28 条紧随其后的命令可能被永久延迟，更后面的命令会绕过它乱序执行；
- 所以它既是“局部卡死”风险，也是一个独立的**正确性缺陷**；需要单独立项，但不能直接当作本次断联的第一触发点。

### 5.5 SCSI 错误处理与日志盲区

- [VirtualSCSITarget.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/SCSITarget/VirtualSCSITarget.cs#L423-L453)

`Write` 只捕获：

- `ArgumentOutOfRangeException` → LBA 越界；
- `IOException` → `MEDIUM ERROR / WRITE FAULT`。

`Read` 还捕获 `CyclicRedundancyCheckException`。

没有捕获的异常包括：

- `OutOfMemoryException`（例如 16 MB 数据/响应分配失败）；
- `ObjectDisposedException`（句柄已释放）；
- 其他 `InvalidOperationException` / `ArgumentException`。

这些异常会离开 `ProcessCommandQueue`（无 catch），导致工作线程死亡/进程退出。

日志盲区：

- `VirtualSCSITarget` 有自己的 `OnLogEntry`，会记录 “Write error: ...” 等；
- FlyDisk 的 `TargetService.RunWithSource` 只订阅了 `server.OnLogEntry`、`target.OnStandardInquiry`、`OnUnitSerialNumberInquiry`、`OnAuthorizationRequest`、`OnSessionTermination`；
- **没有订阅 `VirtualSCSITarget.OnLogEntry`**；
- 因此 USB 写失败时，`debug-security.log` 里可能看不到对应的 SCSI 错误，只能从系统事件（disk/StorPort）里找。

### 5.6 源盘写路径：同步、写穿、无超时

- [PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L477-L497)：`SetFilePointerEx` + 同步 `WriteFile`，每 1 MB 分块；
- [PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L902-L906)：`FILE_FLAG_WRITE_THROUGH`；
- [PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L111-L118)：调用方是单工作线程，共用文件指针。

后果：

- 单条 SCSI 写最大 16 MB（VPD `MaximumTransferLength = 32768 sectors`），会被拆成 16 次 1 MB 写穿；
- 每次写穿都可能触发设备缓存刷新/FUA；
- 没有 `FILE_FLAG_OVERLAPPED`，没有 `CancelSynchronousIo`，没有超时；
- 源盘一旦停顿，`WriteFile` 可以无限期阻塞，target 工作线程无法取消；
- 没有 `WM_DEVICECHANGE` / `RegisterDeviceNotification` / 设备移除后的重新打开逻辑。

### 5.7 ModeSense 上报的缓存策略

- [VirtualSCSITarget.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/SCSITarget/VirtualSCSITarget.cs#L221-L318)

```csharp
CachingParametersPage page = new CachingParametersPage();
page.RCD = true;   // Read Cache Disable
...
header.DPOFUA = true;
```

`CachingParametersPage` 的 `WCE` 默认是 `false`（写缓存未启用）。也就是说，克隆盘对 Windows 上报的是：

- 读缓存关闭（RCD = true）；
- 写缓存未启用（WCE = false）；
- 支持 DPO/FUA（DPOFUA = true）。

这会让 Windows 对克隆盘采取更保守的缓存策略，配合源盘的 `FILE_FLAG_WRITE_THROUGH`，整条链路更接近“每次写都等设备确认”。这不是断联的直接原因，但会放大延迟。

### 5.8 L2：每个回源块一次 4 KB 同步写

- [SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L960-L1061)：`Insert` / `WriteSlotFrom`
- [SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L1314-L1318)：`ReadSlot`
- [SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L1343-L1397)：`GrowToCoverSlot`
- 构造：[SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L278-L280)

```csharp
_container = new FileStream(containerPath, FileMode.OpenOrCreate,
    FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.None);
```

特点：

- 每个回源读到的 4 KB 块都会在工作线程上同步写一次容器；
- 一次 1 MB 合并回源 = 256 次 4 KB 写；一次 16 MB 读 = 4096 次；
- `FileOptions.None`：写会进系统页缓存，脏页延迟回写；项目 `TODO.md` 里也把它列为未定项；
- 容器按 64 MB Slab 增长，`SetLength` + `SetFileValidData` 都在工作线程上；
- 如果 `SetFileValidData` 不可用，每次增长可能触发 64 MB 零填充；
- 60 GB L2 的元数据（槽号数组、空闲栈、M 环、ghost 环、字典/哈希集合）估算在 1 GB 量级，字典扩容会在工作线程上产生停顿。

它不是“必然导致断联”的路径，但在 L2 与系统盘同盘、SSD 性能较弱或接近写满时，会把单条命令的延迟明显拉长。

### 5.9 L1：可涨到物理内存 90%

- [CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L830-L845)：`CanCache`
- [CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L462-L486)：`AcquireSlot`
- [CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L544-L591)：`WaterLevelTick`

特点：

- `EvictionThreshold` 默认 0.8，`StopCachingThreshold` 默认 0.9；
- 低于 0.9 时允许继续扩池，每个 Slab 64 MB；
- 32 GB 机器理论上可到约 28.8 GB；
- 水位节拍每秒最多淘汰 256 MB 并归还 Slab；
- `NativeMemory.Alloc/Free` + `GC.AddMemoryPressure` 会在工作线程/线程池上产生停顿。

如果 L1 真被填到很高，系统级内存压力会让“打开文件、网络连接”等新 IO 也变慢；但本次是“写目标盘”为主，目标盘读元数据量有限，L1 是否能填满需要现场看进程内存。

### 5.10 UI 线程每秒一次源盘 IOCTL

- [Form1.cs](file:///d:/projects/FlyDisk/FlyDisk/Form1.cs#L2254-L2260)：`RefreshStatus` 由 WinForms Timer 每秒调用；
- [TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L779-L809)：`CreateSnapshot` 调用 `TraceSourceDiskAttributes`；
- [TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L829-L851)：通过独占句柄发 `IOCTL_DISK_GET_DISK_ATTRIBUTES`。

影响：

- 这个 IOCTL 在 **UI 线程**上执行；USB 设备忙/卡住时，`DeviceIoControl` 可能排在数据请求后面，导致界面卡住；
- 每秒一次的设备属性查询也会给已经饱和的设备增加一点负载；
- 它不是 iSCSI 断联的原因，但会让“整机像被挂起”的体感更明显。

### 5.11 同步日志与会话锁

- [LogService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/LogService.cs#L68-L87)：`File.AppendAllText` 在全局锁内同步写盘；
- [ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L430-L438)：`Server_OnLogEntry` 在接收/登录线程上同步调用；
- [SessionManager.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/SessionManager.cs#L82-L97)：`RemoveSession` 持 `m_activeSessions` 锁时回调 `NotifySessionTermination`，FlyDisk 的处理是同步写日志。

影响：

- 重连期间 `New connection accepted`、`会话结束` 等日志可能形成风暴；
- 如果日志盘（通常是 C:）同时被 L2/系统盘负载压住，日志写会变慢，进而拖慢接收/登录线程；
- 它是恢复期的放大器，不是第一触发点。

---

## 6. 量化：为什么“多批小文件”容易触发

### 6.1 单工作线程的延迟放大

设单条 1 MB 写穿在 USB 盘上的耗时为 `T`：

- 一条 16 MB SCSI 写 = 16 次 `WriteFile`，耗时约 `16T`；
- Windows 最多压 64 条命令，最坏排队延迟约 `64 × 16T = 1024T`；
- 一条 4 KB 小写 = 1 次 `WriteFile`，64 条排队约 `64T`。

代入：

| 单次 1 MB 写穿耗时 T | 16 MB 命令 | 64 条 16 MB 排队 | 64 条 4 KB 小写排队 |
| --- | --- | --- | --- |
| 10 ms | 0.16 s | 10.2 s | 0.64 s |
| 60 ms | 0.96 s | 61.4 s | 3.8 s |
| 100 ms | 1.6 s | 102 s | 6.4 s |
| 1 s | 16 s | 1024 s | 64 s |

说明：

- 不需要设备“永久死掉”，只要单次写穿到几十毫秒到秒级，就有机会超过 Windows 的命令超时；
- 多批小文件让 LBA 在盘上交错，机械盘从顺序写退化为随机写 + 写穿，`T` 会显著上升；
- 单任务时写入更连续，`T` 小；多任务时随机性更强，这正是“同时移动多个文件夹”更容易触发的原因。

### 6.2 L2 的 4 KB 写放大

- 一次 1 MB 回源读 → 256 次 4 KB L2 写；
- 一次 16 MB 回源读 → 4096 次 4 KB L2 写；
- 若 SSD 4 KB 随机写耗时 0.1 ms，16 MB 读约 0.4 s；若 1 ms，约 4 s；若 10 ms，约 40 s；
- 与 USB 源盘延迟叠加后，单条读命令就可能达到几十秒。

### 6.3 内存量级（估算）

- 60 GB L2：15.7 M 槽；
- `_slotBlockIndex`（long[15.7M]）约 125 MB；
- `_freeStack` / `_mRing` / `_ghostRing` 各约 63 MB；
- `_blocksByIndex` 字典 + `_ghostSet` 哈希集合在满容量时可达数百 MB；
- 合计约 1 GB 量级；
- L1 理论上限约 28.8 GB（32 GB × 0.9）。

这些是估算，不是精确值；但足以说明 32 GB 机器上 L1+L2 元数据+系统缓存同时增长时，内存压力不可忽视。

---

## 7. 假设与排序

### H1（最高）：USB 源盘写停顿/极慢 → 单工作线程 → 发起端超时 → 重连被无限等待挡住

完整链条：

1. 多个文件移动 → 大量 4 KB/64 KB/1 MB 写，LBA 交错；
2. 单工作线程 FIFO 执行；每条写都是同步 `WriteFile` + `FILE_FLAG_WRITE_THROUGH`；
3. USB 机械盘随机写 + 写穿，单条命令耗时上升；设备侧（SMR/桥接/坏道/供电/UASP）可能短暂停顿；
4. Windows 发起端的命令超时/链路恢复计时到期，重置会话并尝试重连；
5. target 的接收回调 `HandleConnectionTermination` → `ReleaseConnection` → `WaitUntilZero`，等待仍卡在源盘上的命令；
6. 新登录 `SetUpNormalSession` 终止旧会话时也会 `ReleaseConnection` → 同一个等待；
7. 登出、停止也会等待；于是“TCP 连上了但业务不完成”，发起程序显示“正在重新连接”；
8. 强制停止最终能完成，说明那条命令最终返回了；重启加速新建 target/worker/会话，清掉等待和队列，所以恢复。

现场指纹：

- `debug-security.log` 反复出现 `New connection accepted`；
- 可能出现 `会话结束：...（原因：ConnectionFailure）`；
- 停止时出现 `在途命令 2 秒内未退出` / `仍在等待在途命令退出`；
- Inspector 的读写计数在卡住期间完全不动；
- 任务管理器里 USB 源盘或 iSCSI 克隆盘 100% 活动、0 吞吐；
- 系统事件里可能有 `disk` 129/153/157、`StorPort` 重置、`USBSTOR/UASPStor` 错误。

### H2（中高，必须排除）：源盘运行期被重新联机 → 同签名冗余路径

机制：

- 源盘以 `persist=true` 脱机；
- USB 设备在负载下重置/重新枚举，新的设备实例可能没有继承脱机属性；
- 源盘变回联机，与克隆盘同磁盘签名/同序列号；
- Windows 可能把克隆盘判为 `Redundant Path` 并脱机，或让两块盘的同签名状态导致存储栈卡顿。

现场指纹：

- `debug-security.log` 出现 `源盘属性变化：脱机=是→否`；
- 系统事件 Event 158（相同磁盘标识符）；
- 故障时 `diskpart` 的 `detail disk` 显示克隆盘 `Offline (Redundant Path)` 或 `Read-only: Yes`；
- 整机命令（包括 diskpart）明显卡顿。

项目已有记录：[iSCSI克隆盘被判冗余路径_排障记录.md](file:///d:/projects/FlyDisk/docs/iSCSI克隆盘被判冗余路径_排障记录.md#L13-L18)。

### H3（中，已修正）：会话复用 + 断线残留 R2T 命令 + `commandWindow` 缺陷

机制：

- 大写命令在 R2T 中途断线，`CommandsInTransfer` 残留；
- Windows 复用同一会话（TSIH != 0）时，残留项参与 `IsPrecedingCommandPending`；
- 由于 `commandWindow` 实际为 28，只有紧随其后的约 28 条命令会被延迟，更后面的命令会绕过它乱序执行；
- 被延迟的命令可能永远不完成，导致发起端超时/重连；
- 乱序写可能造成同一 LBA 的最终落盘顺序错误，引发 NTFS 重试/卡住。

这条不是“所有命令永久卡死”，而是“局部卡死 + 乱序”的组合；它是否发生取决于 Windows 是否复用会话，需要日志/实验确认。

### H4（放大器）：L2/SSD、L1 内存、UI IOCTL、同步日志

- L2 4 KB 同步写、容器增长、字典扩容；
- L1 可涨到 90% 内存；
- UI 线程每秒 IOCTL；
- 同步日志与会话锁；
- 这些会把单条命令延迟从“慢”推到“超时”，也会让系统整体感觉卡顿。

### H5（低/外部）：杀软、索引、VSS、Windows iSCSI 服务自身

- Defender 实时扫描、Windows Search、备份/VSS 可能在克隆盘卡住时持有过滤驱动/卷锁；
- Windows iSCSI 发起端自身的超时/重连策略不可控；
- 这类因素只能通过现场事件和对照实验排除。

### H6（低）：工作线程未捕获异常导致进程退出

- `ProcessCommandQueue` 无 catch；
- `VirtualSCSITarget.Write` 只捕获两类异常；
- 若发生 OOM/ObjectDisposed 等，进程会退出并写 `crash.log`；
- 用户描述程序仍在运行并能强制停止，所以这条可能性低；但仍应检查 `crash.log`。

---

## 8. 现场取证清单

### 8.1 FlyDisk 日志

路径：

- `%ProgramData%\FlyDisk\debug-security.log`
- `%ProgramData%\FlyDisk\debug-security.old.log`
- `%ProgramData%\FlyDisk\crash.log`（如果存在）
- `%ProgramData%\FlyDisk\config.json`（可脱敏）

要求在故障发生后、**强制停止/重启程序之前**复制；日志 8 MB 滚动，重启可能覆盖现场。

重点搜索：

| 关键字 | 含义 |
| --- | --- |
| `New connection has been accepted` | 发起端在反复建连/重连 |
| `会话结束` / `ConnectionFailure` | target 观察到连接断开 |
| `在途命令 2 秒内未退出` / `仍在等待在途命令退出` | 停止时仍有命令卡在源盘 |
| `源盘属性复核` / `源盘属性变化：脱机=是→否` | 源盘被系统重新联机 |
| `Ignoring PDU with CmdSN outside of expected range` | 重传命令被忽略（H3 相关） |
| `SSD 二级缓存运行期 IO 失败` | L2 运行期 IO 故障 |
| `容器扩展失败` / `SetFileValidData 不可用` | L2 容器增长受阻/零填充回退 |
| `CacheService：无法分配新的 Slab` | L1 扩池失败/内存压力 |
| `停止：在途命令` / `停止：仍在等待` | 停机序列等待 |

说明：

- `TraceFileLogging=true` 当前只额外放行 `Evict ` 开头的淘汰摘要，对本问题帮助很小，不建议让用户开启；
- `VirtualSCSITarget` 的 SCSI 错误不在这份日志里，需要系统事件补充。

### 8.2 Windows 系统事件

事件查看器 → Windows 日志 → 系统，筛选来源：

- `disk`
- `storport` / `storahci`
- `msiscsi`
- `USBSTOR` / `UASPStor` / `USBXHCI`
- `partmgr` / `volmgr`
- `Ntfs`

重点 Event ID：

| ID | 含义 |
| --- | --- |
| 129 | 设备重置/命令超时 |
| 153 | IO 重试 |
| 157 | 磁盘意外移除 |
| 158 | 相同磁盘标识符（冗余路径线索） |
| 51 | 分页/IO 错误 |
| 7 / 11 / 15 | 设备 IO 错误/控制器错误 |

可用命令：

```powershell
wevtutil qe System /q:"*[System[Provider[@Name='disk']]]" /c:100 /rd:true /f:text
wevtutil qe System /q:"*[System[(EventID=158)]]" /c:20 /rd:true /f:text
wevtutil qe System /q:"*[System[Provider[@Name='Microsoft-Windows-StorPort']]]" /c:100 /rd:true /f:text
wevtutil qe System /q:"*[System[Provider[@Name='msiscsi']]]" /c:100 /rd:true /f:text
```

（Provider 名称在不同系统上可能略有差异，事件查看器 GUI 筛选更稳。）

### 8.3 故障时的现场快照

1. 任务管理器 → 性能 → **所有磁盘**：确认哪块盘 100% 活动、读写 0；
2. 资源监视器 → 磁盘：看队列长度、响应时间、哪个进程；
3. 资源监视器 → 内存：Modified / Commit / 可用内存；
4. `diskpart`：
   ```text
   list disk
   select disk N
   detail disk
   attributes disk
   ```
   对源盘和克隆盘各看一次，确认是否 `Offline (Redundant Path)`、`Read-only`；
5. `iscsicli SessionList`：看会话/连接数、状态；
6. FlyDisk 检查器：读写计数是否冻结；
7. `Get-CimInstance Win32_DiskDrive`：源盘/克隆盘的 Index、Model、SerialNumber、Status；
8. `Get-PnpDevice` / 设备管理器：USB 设备是否有黄色感叹号、是否重新枚举。

### 8.4 硬件与配置

- USB 移动硬盘型号、是否 SMR、是否经过 Hub、前置/后置口、线材；
- USB 桥接芯片/UASP 还是 BOT；
- SMART：重映射扇区、待定扇区、UDMA CRC 错误；
- L2 SSD 型号、是否就是系统盘/分页文件盘、剩余空间；
- 杀软/Defender 实时保护、Windows Search、VSS/备份任务；
- `config.json` 中的 L2 配置、`L2Disks` 条目、`TraceFileLogging`。

### 8.5 给 issue 报告者的一段话

> 麻烦在故障发生后、**先不要强制停止或重启程序**，把 `%ProgramData%\FlyDisk\` 下的 `debug-security.log`、`debug-security.old.log`、`crash.log` 复制出来；再导出故障前后半小时的系统事件日志（来源 `disk` / `storport` / `msiscsi` / `USBSTOR` / `UASPStor` / `partmgr` / `Ntfs`），并截一张任务管理器里**所有磁盘**的性能图。  
> 如果方便，再告诉我：卡住时是哪块盘 100%？`diskpart` 里源盘和克隆盘分别是什么状态？USB 硬盘型号、是否经过 Hub、L2 SSD 是否就是系统盘？

---

## 9. 验证矩阵与判定树

### 9.1 对照实验

| 实验 | 目的 | 结果解释 |
| --- | --- | --- |
| 关闭该盘的 L2，重复同样多任务移动 | 排除 L2 4 KB 写/SSD 竞争 | 不再复现 → L2 是主要放大器或触发因素 |
| 换 USB 口（后置直连、不经 Hub）、换线 | 排除 USB 供电/桥接/线材 | 不再复现 → H1 硬件层 |
| 只跑 1 个移动任务 | 排除队列/随机写放大 | 单任务正常、多任务卡 → H1 队列/延迟 |
| 用 SATA 机械盘做目标（如果条件允许） | 排除 USB 栈 | SATA 正常、USB 卡 → USB 设备/驱动 |
| 目标盘从脱机状态启动 vs 从联机状态启动 | 排除冗余路径触发条件 | 联机起步必现、脱机起步正常 → H2 |
| 卡住时手动联机源盘（仅测试盘，风险高） | 复现冗余路径 | 不建议在数据盘上做；可能双写/损坏 |
| 观察 L2 容器增长日志 | 判断是否卡在 64 MB Slab 边界 | 卡顿与 `容器尺寸已调整` 同步 → L2 增长 |
| 观察进程内存 | 判断 L1 是否接近 90% | 进程内存接近 28 GB → 内存压力 |

### 9.2 判定树

| 现场观察 | 指向 |
| --- | --- |
| 卡住时 Inspector 读写计数**完全不动** | 工作线程卡在源盘 IO（H1） |
| 读写计数还在涨，但用户 IO 不动 | 发起端/Windows 或会话层（H3/H5） |
| `源盘属性变化：脱机=是→否` / Event 158 / `Redundant Path` | H2 |
| 反复 `New connection accepted` + `Ignoring PDU with CmdSN outside of expected range` | H3 |
| 关闭 L2 后不复现 | L2 放大器 |
| 换 USB 口/线后不复现 | H1 硬件 |
| 只有多任务复现、单任务正常 | H1 队列/随机写 |
| `crash.log` 存在 | H6（进程退出，另一类） |
| 任务管理器显示 L2 SSD 100%、系统盘响应慢 | L2/SSD 竞争 |
| 进程内存接近 28 GB、系统开始换页 | L1 内存压力 |

---

## 10. 若确认后的整改方向（均未实施）

> 以下只是候选方向，不是本次改动计划；按项目规则，任何代码修改都需要先给出书面计划并得到确认。

### 10.1 库级

1. **修正 `commandWindow`**：把 `2 ^ 31 - 1` 改为 `0x7FFFFFFF`（或 `uint.MaxValue / 2`），并补充 CmdSN 回绕测试。
2. **连接失败时清理会话级在途状态**：`CommandsInTransfer.Clear()`、`DelayedCommands.Clear()`，或在会话复用时重置 `ExpCmdSN`/`CommandNumberingStarted`。
3. **不要在登录/断开/登出路径无限等待**：给 `WaitUntilZero` 加超时；超时后拒绝/重建会话，而不是把登录线程一起挡住。
4. **`MaxConnections` 判断**：`Count > MaxConnections` 改为 `Count >= MaxConnections`（当前 MaxConnections=1 时会允许 2 条连接）。
5. **订阅 `VirtualSCSITarget.OnLogEntry`**：把 SCSI 读写错误写进 FlyDisk 诊断日志，避免“日志里什么都没有”。
6. **工作线程兜底 catch**：至少把非预期异常转成 SCSI 错误并记录，避免整个进程被一条异常带走。
7. **发送超时/发送队列上限**：避免发送线程永久阻塞、队列无限增长。

### 10.2 FlyDisk 侧

1. **设备移除看门狗**：监听 `WM_DEVICECHANGE`/`RegisterDeviceNotification`，USB 盘移除时让在途命令快速失败，而不是无限阻塞。
2. **命令级超时/取消**：为源盘 IO 增加超时（或可取消），至少让停止/重连不被单条命令永久挡住。
3. **降低写穿代价**：评估 USB 盘是否必须 `FILE_FLAG_WRITE_THROUGH`；或提供“性能/安全”开关；或合并小块写。
4. **L2 写合并**：为 4 KB 回源块增加一个小型内存缓冲/批量写，减少 SSD 随机写；评估 `FileOptions.WriteThrough` 与脏页回退。
5. **把 `TraceSourceDiskAttributes` 移出 UI 线程**：避免 USB 忙时卡住界面。
6. **运行期源盘守卫**：检测源盘被系统重新联机时，立即停止/告警，避免两块同签名盘同时在线（项目 TODO 已有此待办）。
7. **队列/延迟诊断**：记录每条命令的开始/结束、队列深度、等待时间，便于下次定位。
8. **L2 容量/内存提示**：在设置/检查器里显示 L1+L2 元数据的实际内存占用，避免 32 GB 机器被吃满。

### 10.3 协议/参数实验

- 将 `DefaultTime2Wait`/`DefaultTime2Retain` 从 0/0 改为 RFC 默认的 2/20，观察重连行为；
- 降低 `DefaultCommandQueueSize`（64 → 16/32），减少最坏排队延迟；
- 评估 `MaxOutstandingR2T` / `MaxBurstLength` / `FirstBurstLength` 对延迟的影响；
- 这些都需要实测，不建议直接改默认值。

---

## 11. 尚未确认的事项

1. 卡住时到底是 USB 源盘、L2 SSD 还是 iSCSI 克隆盘 100% 活动；
2. Windows 发起端实际的命令超时/重连参数（`disk` TimeOutValue、iSCSI 相关注册表项）及其在本机的取值；
3. Windows 在本次故障中是否复用了同一 iSCSI 会话（TSIH != 0），还是新建会话；
4. USB 硬盘型号、是否 SMR、桥接芯片、UASP/BOT、SMART 健康状态；
5. L2 SSD 是否就是系统盘/分页文件盘，剩余空间多少；
6. 是否有 `crash.log`，进程是否曾经退出过；
7. 故障时源盘是否短暂变回联机；
8. L2 关闭、换 USB 口、单任务等对照实验的结果。

在这些信息拿到之前，本报告只能给出“高置信度的代码事实 + 排序后的假设”，不能宣称根因已经确定。

---

## 12. 代码索引

### FlyDisk

- 启动/停止：[TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L288-L351)、[TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L571-L664)
- 连接数/停止守卫：[TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L139-L161)
- 源盘属性诊断：[TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L829-L851)
- 快照：[TargetService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/TargetService.cs#L779-L809)、[Form1.cs](file:///d:/projects/FlyDisk/FlyDisk/Form1.cs#L2254-L2260)
- 块后端：[CachedPhysicalDisk.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CachedPhysicalDisk.cs#L41-L43)、[CachedPhysicalDisk.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CachedPhysicalDisk.cs#L146-L211)、[CachedPhysicalDisk.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CachedPhysicalDisk.cs#L284-L348)
- 物理盘：[PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L111-L118)、[PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L456-L497)、[PhysicalDiskHandle.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/PhysicalDiskHandle.cs#L902-L906)
- L1：[CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L462-L486)、[CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L544-L591)、[CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L715-L806)、[CacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/CacheService.cs#L830-L845)
- L2：[SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L47-L49)、[SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L960-L1061)、[SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L1314-L1318)、[SsdCacheService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/SsdCacheService.cs#L1343-L1397)
- 日志：[LogService.cs](file:///d:/projects/FlyDisk/FlyDisk/Engine/LogService.cs#L68-L87)
- 崩溃兜底：[Program.cs](file:///d:/projects/FlyDisk/FlyDisk/Program.cs#L86-L88)、[Program.cs](file:///d:/projects/FlyDisk/FlyDisk/Program.cs#L149-L168)

### iSCSI 1.5.6.0（仓库内源码）

- 单工作线程：[SCSITarget.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/SCSITarget/SCSITarget.cs#L34-L67)
- 连接释放无超时：[ConnectionManager.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ConnectionManager.cs#L39-L49)、[CountdownLatch.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/Utilities/Threading/CountdownLatch.cs#L52-L55)
- 连接断开：[ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L280-L304)
- 登录/旧会话终止：[ISCSIServer.Login.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.Login.cs#L61-L175)
- 登出：[ISCSIServer.Logout.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.Logout.cs#L59-L68)
- 停止：[ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L179-L200)
- CmdSN 校验：[ISCSIServer.PDUProcessor.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.PDUProcessor.cs#L16-L51)
- 命令窗口/参数：[ISCSIServer.Parameters.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.Parameters.cs#L13-L35)、[DefaultParameters.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Common/DefaultParameters.cs#L15-L60)
- R2T/延迟命令：[TargetResponseHelper.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/TargetResponseHelper.cs#L20-L72)、[ISCSISession.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSISession.cs#L37-L142)
- SCSI 错误处理：[VirtualSCSITarget.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/SCSITarget/VirtualSCSITarget.cs#L221-L318)、[VirtualSCSITarget.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/SCSITarget/VirtualSCSITarget.cs#L351-L453)
- 接收缓冲：[ISCSIConnectionBuffer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Common/ISCSIConnectionBuffer.cs#L24-L130)
- 发送队列：[ISCSIServer.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/ISCSI/ISCSI.Server/ISCSIServer.cs#L368-L428)、[BlockingQueue.cs](file:///d:/projects/FlyDisk/废/第三方资料/iSCSIConsole/Utilities/Generics/BlockingQueue.cs#L18-L98)

### 项目文档

- [iSCSI克隆盘被判冗余路径_排障记录.md](file:///d:/projects/FlyDisk/docs/iSCSI克隆盘被判冗余路径_排障记录.md#L13-L18)
- [TODO.md](file:///d:/projects/FlyDisk/TODO.md#L43-L48)（L2 写盘策略、SSD 寿命）
- [TODO.md](file:///d:/projects/FlyDisk/TODO.md#L73)（冗余路径根因待查明）
- [AGENTS.md](file:///d:/projects/FlyDisk/AGENTS.md)（§2.1 启停、§2.2 L1、§2.3 L2、§2.4 写路径）

---

## 13. 一句话总结

本次最可信的链条不是“iSCSI 协议自己断了”，而是：

**USB 源盘在大量小文件随机写下变慢/停顿 → 单工作线程队头阻塞 → Windows 发起端命令超时/重连 → target 的 `ReleaseConnection` 无超时等待在途命令 → 登录/登出/停止全部被挡住 → 表现为“一直正在重新连接”；重启加速新建会话/工作线程后恢复。**

源盘重新联机/冗余路径是必须排除的竞争假设；`commandWindow=28` 与 R2T 残留命令是确认存在的库级缺陷，可能造成局部命令永久延迟和乱序写，值得单独立项。最终根因需要现场日志、系统事件和设备信息确认。
