# iSCSI 克隆盘被判「冗余路径」而脱机 —— 排障记录

> 目前不认为有普遍性，很可能只是个例，但仍需在意。
> 状态：**根因未查明；"检查 + 自动恢复"的补救措施已落地，并在用户机实测通过**（2026-10，见 §9）。
> 触发场景：本地形态（回环 iSCSI）在**目标盘联机状态下启动加速**时必现。

## 1. 现象

用户机 `SUSHI-NEOPC`，本地加速，目标盘为磁盘 0。

| 起步状态 | 加速后 | 结论 |
| --- | --- | --- |
| 磁盘 0 **联机** | 磁盘 0 在 diskpart 里显示「联机」、**盘符消失**；出现磁盘 2（克隆盘），状态 `Offline (Redundant Path)`、`Current Read-only State: Yes` | **异常** |
| 磁盘 0 **脱机** | 磁盘 2 联机，盘符正常回来 | 正常 |

- 停止加速后：磁盘 0 保持脱机，磁盘 2 消失（符合设计）。
- 异常状态下，本机几乎所有命令（含 diskpart）都**明显卡顿**，要等很久才出结果——可当作"状态异常"的体感指标。
- 曾经出现过磁盘 0 被置为**只读**（`Read-only: Yes`），导致下次启动被拒（[TargetService](../FlyDisk/Engine/TargetService.cs) 校验 `info.IsReadOnly`）。`attributes disk clear readonly` 可恢复。该只读**不是本程序设置的**（见下）。

## 2. 环境与设备

- Windows：diskpart `10.0.26100.1150`，iscsicli `10.0 内部版本 26200`。
- 磁盘 0（目标盘）：`ST1000DM003-1SB102`，926 GB，**MBR**，单分区，序列号 `Z9A2HDFJ`，`Disk ID: 7C459850`。
- 磁盘 1（系统盘）：`Fanxiang S500MQ 1TB`，931 GB，GPT。
- 磁盘 2（克隆盘，加速时出现）：`TalAloni ST1000DM003-1SB1 SCSI Disk Device`，926 GB，**`Disk ID: 7C459850`（与磁盘 0 相同）**。
- FlyDisk `0.1.0-beta`，本地形态，回环 iSCSI（`127.0.0.1:3260`，IQN `iqn.flydisk:acceleration-disk`）。

## 3. 已确证的事实

1. **只读不是本程序设的。** `DiskAccessLibrary.Win32` 的 `PhysicalDiskControl.TrySetOnlineStatus` 里
   `Attributes` 与 `AttributesMask` **只有 `DISK_ATTRIBUTE_OFFLINE` 位**，根本不碰只读位。
   （源码：`废\第三方资料\DiskAccessLibrary\DiskAccessLibrary.Win32\Utilities\PhysicalDiskControl.cs`）

2. **我们的脱机能成功，而且稳。** 只做「脱机 + 独占打开」、不建 target、不连 iSCSI 的 60 秒测试中，
   每 0.5 秒采样一次共 120 次，**全程「脱机=是」**，一次都没变。

3. **iSCSI 侧没有重复路径。** 加速中 `iscsicli SessionList` 显示：**共 1 次会话、1 条连接、1 个设备（设备号 2）**。
   所以"冗余路径"不是 iSCSI 侧多条会话造成的。

4. **两块盘的磁盘签名相同。** 磁盘 0 与磁盘 2 的 `Disk ID` 都是 `7C459850`。
   这个签名就在**扇区 0 的 0x1B8**，而克隆盘是整盘块级克隆、如实吐出源盘扇区 0 —— **重复是这套技术路线内生的**，
   与上报的设备身份无关。

5. **iSCSI 盘上报的序列号与源盘相同。** `Win32_DiskDrive` 里磁盘 0 与磁盘 2 的 `SerialNumber` 都是 `Z9A2HDFJ`。

6. **该机器的 SMAPI 不可用。** `Get-Disk` / `Get-PhysicalDisk` / `Get-Volume` **全部返回空**
   （`root\Microsoft\Windows\Storage` 无内容），而 `Win32_DiskDrive` 与 `diskpart` 正常。
   这是"用户机 vs 开发机"的一个明确环境差异，是否与本问题相关**未验证**。

7. **刚建立起来的 iSCSI 会话没法立刻登出。** 自动恢复里第一次 `_target.Stop()` 被拒，报的就是
   `main.msg.iscsiDisconnectFailed`（"iSCSI断开连接失败…"）——即 `Disconnect()` 的 `LogoutIScsiTarget`
   没成功、连接仍在，于是 `Stop()` 里"有连接就拒绝停止"那道守卫把它拦下。
   **隔 1 秒再试、并把停止试满 3 次**即可摘干净（见 §9.2）。

## 4. 已证伪的假设

| 假设 | 实验 | 结果 |
| --- | --- | --- |
| **多路径**：iSCSI 侧存在重复会话/门户 | `iscsicli SessionList` | **否**，只有 1 条会话 |
| **时序/沉降**：脱机刚做完，系统尚未把该盘当成非活动设备 | 脱机后插 15 秒等待再登录 iSCSI | **否**，无任何变化 |
| **`persist`**：`persist: true` 只记属性、不当场改变当前状态 | 把脱机改成 `persist: false` | **否**，无任何变化 |
| **设备身份（VPD 页 0x80）** | 给上报的序列号加 `FLYDISK-` 前缀 | **试验无效**：`Win32_DiskDrive` 仍显示 `Z9A2HDFJ`，说明该字段未被 Windows 采用，或已被 PnP 缓存 |

## 5. 未解之谜

用户判断（**目前最贴合全部观测**）：**"我们执行过一次 联机→脱机 转换"本身就是触发条件**。
即磁盘"曾经在本机联机过"这段历史，让后到的克隆盘被判为它的冗余路径。

未验证的是机制：Windows 依据什么判定"这是同一设备的另一条路径"。
候选是**磁盘签名（扇区 0 数据）**或某个**被缓存的 PnP 设备身份**，但两个实验都没能把它们分开。

## 6. 观测工具的经验（可复用）

1. **我们独占某盘期间，diskpart 的 `Status` 列不可信。** 实测：本程序脱机后 `list disk` 仍显示该盘
   「联机」，同一时刻 `detail disk` 里该盘的卷**没有盘符、`Fs` 列为空**；重启程序后用「重新联机硬盘」
   能正常联机、盘符回来——说明它**确实处在脱机状态**，是 diskpart 的显示不可信
   （它拿不到状态时显示默认值「联机」，那些"卡半天"就是它在等这个拿不到的回答）。

2. **可用通道**：`diskpart`（能读脱机原因，但 Status 列有上述坑）；`Win32_DiskDrive`（型号/序列号/盘在不在，
   但不给脱机标志）；`Win32_LogicalDisk`（盘符在不在，最原始也最真）；程序内 `IOCTL_DISK_GET_DISK_ATTRIBUTES`
   （读的是**属性位**，与系统视角可能不一致）。

3. **不可用通道**：本机 `Get-Disk` / `Get-PhysicalDisk` / `Get-Volume` 全空。

4. **`diskpart` 自身脱机可行**：`online disk` → `offline disk` 均成功，隔 30 秒再查仍是 `Offline`，只读全程 `No`。

## 7. 复现与取证命令

复现：磁盘 0 先联机，点「启动加速」。

```
echo list disk | diskpart
select disk 0
detail disk
attributes disk
select disk 2
detail disk
attributes disk
exit
```

```
iscsicli SessionList
```

```
powershell -NoProfile -Command "Get-CimInstance Win32_DiskDrive | Format-Table Index,Model,SerialNumber -AutoSize"
powershell -NoProfile -Command "Get-CimInstance Win32_LogicalDisk | Format-Table DeviceID,DriveType,VolumeName -AutoSize"
```

```
wevtutil qe System /q:"*[System[Provider[@Name='disk']]]" /c:40 /rd:true /f:text
```

最后一条用于找 **Event ID 158**（"Disk N has the same disk identifiers as one or more disks connected to the system"），
**尚未执行**，是后续继续排查时的第一站——它会让 Windows 亲口说明"标识符重复"是否成立，以及用的是哪个标识符。

## 8. 后续可选补救方案（当时列的候选）

> 其中"只告知"与"自动恢复"已按 §9 落地；本节保留当时的可行性分析，供继续排查时参考。

用户提出的方向：**iSCSI 连接之后检查一下 iSCSI 盘是否脱机**。可行性见下。

### 8.1 识别克隆盘

`_initiator.Connect(...)` 成功之后，克隆盘才出现在系统视野里。要拿到它的盘号，两条路：

- **对比法（无需新增 P/Invoke）**：连接前后各调一次 `PhysicalDiskHandle.Enumerate()`，
  新增的、容量与源盘相同的那一块即克隆盘。（注意：**不能**用序列号区分——两者序列号相同。）
- **精确法**：给 `iscsicli` 的 `ReportIScsiDevicesW` 加 P/Invoke，直接拿到 `ISCSI_DEVICE_ON_SESSION.DeviceNumber`。
  `IscsiInitiator` 里已经在用同族的 `GetIScsiSessionListW` / `ReportIScsiTargetsW`，加这个不算离经叛道。

### 8.2 读取状态

对**克隆盘**开一个普通探针句柄（`FileShare.ReadWrite`，我们没有独占它）读
`PhysicalDiskControl.GetOnlineStatus` 的脱机/只读两项。这条读数不受"我们独占源盘"影响。

要点：

- **必须带重试**：设备枚举有延迟，刚登录完可能还看不到，建议几百毫秒一次、总计若干秒。
- 判定要**用系统视角**（外部 `diskpart` / `Win32_*` 与程序内读数若不一致，以系统视角为准——
  源盘那次"程序内说脱机、diskpart 说联机"的教训就在于此）。

### 8.3 处理

- **只告知（低风险，确定可行）**：检测到克隆盘脱机时，不再打印"已自动挂载"，
  而是明确报出"系统把克隆盘判为源盘的冗余路径并保持脱机"，并给出人工恢复步骤
  （磁盘管理里对克隆盘点「联机」，或 `diskpart` → `select disk 2` → `online disk`）。
- **尝试自动联机（需实测）**：直接对克隆盘调 `PhysicalDiskHandle.TryOnline(diskNumber, out error)`。
  API 层面没问题，但 Windows 是否接受对"冗余路径"判定下的盘联机**未知**，需在用户机实测；
  若被系统再次脱机，则退回"只告知"。

风险说明：源盘此刻由本程序独占并保持脱机，克隆盘是唯一在线副本，因此**把克隆盘联机不会造成双写**。
但反过来（源盘与克隆盘同时在线）才是危险状态，这也是下面这条守卫值得做的原因。

### 8.4 顺带建议的守卫（与本问题根因无关，但补的是数据安全缺口）

启动完成后复核**源盘**在系统视角下是否仍为脱机。现在这道防线只防"用户手动联机"
（`TargetService.TryReonlineDisk`），对系统主动改变没有兜底。一旦出现"源盘与克隆盘同时在线"，
就是两块同签名盘对同一批扇区双写。

## 9. 已落地的补救措施（2026-10，实测通过）

方向取 §8 的"只告知 + 自动恢复"，**不尝试自动联机克隆盘**（那条风险更高，仍是待办）。

### 9.1 克隆盘检查（每次启动加速都做）

`TargetService` 新增 `#region 克隆盘检查（诊断）`：

- **识别**：登录前后各扫一次**能被探测到的盘号**，**差集**里新出现的那块就是克隆盘。
  - 扫描**刻意不用** `PhysicalDiskHandle.Enumerate()`：它内部 `GetPhysicalDiskIndexList` 要给每块盘取设备号，
    而在本程序**独占持有源盘**期间那个句柄开不出来（共享冲突 ⇒ 取号抛异常），异常会掀翻整份清单，
    "前后各枚举一次"这条路就永远失败。改为逐号 `PhysicalDiskHandle.Probe(n)`（`0..31`）、失败即跳过——
    独占中的源盘自然落在两侧清单之外，不影响取差集。
  - 刻意**不按容量 / 序列号筛选**：克隆盘这两项都与源盘相同（见 §3），筛选只会引入"把真克隆盘筛掉"的静默失败。
- **判定**：对差集里的盘 `Probe` 一次，读它的脱机 / 只读；**被判脱机**（典型 `Redundant Path`）或**被置只读**即为异常。
- **等设备出现**：登录后它通常几十毫秒就进系统视野（日志里紧跟其后的 INQUIRY 就是它）；最多等 3 秒、250 毫秒一轮。
- **产出**：`TargetService.CloneDiskWarning`（已本地化的整句），由界面打到主页日志。

已知盲区：**上次崩溃残留**的克隆盘在登录前就已在系统里，不落在差集里 ⇒ 本检查不报
（那种情形由启动期的 `IscsiInitiator.CleanupStale` 清理）。

### 9.2 自动恢复（一轮）

`Form1.RecoverFromCloneDiskProblem` + `TryStopForCloneRecovery`，**只接在本地加速**这条路
（远程借盘那条涉及对端协调，未动）：

```
启动加速 → 检查到克隆盘异常（主页日志打出 ⚠ 与磁盘号）
  → 等 1 秒（让刚建立的 iSCSI 会话稳定，见 §3 第 7 条）
  → 停止加速全流程（摘 iSCSI、落 L2 账本、盘保持脱机）      【最多 3 次，每次之间 1 秒】
  → 再等 1 秒
  → 重新启动加速（同一块盘，不再弹选盘框；含同一套检查）
       ├─ 检查过了 → 「重启后克隆盘已正常，加速继续。」
       └─ 仍异常 → 等 1 秒 + 停止加速（同样最多 3 次）→ ⚠ 告知 + 弹窗
```

**为什么这样就能修好（关键）**：重试时的启动是**从脱机状态起步**的——那正是 §1 里"正常"的那一列。
也就是说，这套恢复等价于把"联机起步"（必现异常）**自动转换成"脱机起步"（已知正常）**，
用户不必自己去联机 / 脱机折腾。第一次启动之所以仍会报错，是因为它恰好就是从联机起步的。

**但根因仍不明**（见 §5）：这里只是绕开了触发条件，并不是把它修好了。

最终告知：`main.msg.cloneUnfixable` + `dialog.cloneUnfixable`——说明无法加速、给出磁盘恢复路径
（菜单「重新联机硬盘」）、引导到 **GitHub 仓库**提 issue（**文案里不写链接**；点「是」由
`Form1.GitHubIssuesUrl` 打开 issue 页面）。

### 9.3 顺带改掉的文案

- `main.log.diskPlacementHint`（启动提示）改为："加速后的硬盘照旧在「此电脑」中打开使用，
  如果一切正常，使用体验应该是无感的。"
- 新增文案键（中英各一份）：`engine.start.cloneDiskOffline` / `engine.start.cloneDiskReadOnly`、
  `main.log.cloneRetry` / `cloneRetryOk` / `cloneRetryFailed` / `cloneRetryStartFailed` / `cloneRetryStopFailed`、
  `main.msg.cloneUnfixable`、`dialog.cloneUnfixable`。

### 9.4 实测

用户机实测：**流程走通，用户无需手动干预即可完成加速**（原话："极大的方便了用户的使用"）。

### 9.5 仍未做的

- **根因未查明**（§5、§6）。
- §8.3 的"尝试自动联机克隆盘"、§8.4 的运行期源盘守卫，都还是待办。

## 10. 代码里的诊断相关代码（现状）

以下**被动诊断**只在读数变化时写 `%ProgramData%\FlyDisk\debug-security.log`，不改变任何行为；
§9 落地后它们已是**常驻功能**（不是临时实验），若要精简可从这里入手：

- `PhysicalDiskHandle.TryGetCurrentAttributes()`：经已持有的独占句柄复读该盘属性。
- `TargetService.TraceSourceDiskAttributes()` 及四个调用点：登录前基线 / 登录完成后 / 每秒快照（`CreateSnapshot`）/ 按停止前。

§9 落地的功能代码（**保留**，勿当实验删掉）：

- `TargetService.CloneDiskWarning` + `CheckCloneDisk` / `InspectNewDisk` / `ScanProbedDiskNumbers`
- `Form1.RecoverFromCloneDiskProblem` / `TryStopForCloneRecovery` / `ReportCloneDiskUnfixable`

曾经加入、**已全部撤除**的临时实验代码：只脱机不连 iSCSI 的诊断入口、登录前 15 秒等待、VPD 页 0x80 序列号前缀、`persist: false`。
（`persist` 已改回 `true`。）
