# 未启用功能：开机自动化（自启 / 保持脱机）

> **状态**：已从主程序**摘除接线**，本目录代码**不参与编译**（`docs/` 在 `FlyDisk/` 项目目录之外）。
> 保留目的：将来若做「开机自动加速」，这套计划任务管理与开机脱机的实现可作为参考/复用基础。
> 归档日期：2026-10-16（PR-13 整改，阶段 1）。

---

## 一、这两个文件是什么

| 文件 | 作用 |
| --- | --- |
| `StartupService.cs` | 用 **Windows 计划任务**管理两条任务：`\FlyDisk\KeepOffline`（开机触发、SYSTEM、`--keep-offline-only`）与 `\FlyDisk\AutoStart`（登录触发、当前用户、带界面启动）。含任务 XML 的生成、读回与幂等校正。 |
| `BootDiskPreparation.cs` | 「开机保持脱机」的唯一执行处：把 `DiskConfig.StartupDiskIdentities` 里选中的盘整盘脱机（`persist=true`），带硬拦、幂等与失败重试。 |

原接线点（现已从主程序摘除）：

- `Program.Main` 的 `--keep-offline-only` 分支；
- `Form1` 的 `RunStartupMaintenance` / `WaitForStartupMaintenanceAsync` 与设置保存后的 `StartupService.Apply`；
- `Settings` 的「开机与自动化」组（两个开关 + 目标盘列表 + 按盘符添加）；
- `DiskConfig` 的 `RunAtStartup` / `KeepOfflineOnBoot` / `StartupDiskIdentities`。

---

## 二、为什么停用

1. **没有「开机自动加速」时，自启的价值很低**：`AutoStart` 只是把主界面拉起来；用户仍然要自己点「启动加速」并当场选盘。省下的只是"找到程序 + 一次 UAC"，不值一整套计划任务子系统。
2. **「开机保持脱机」收益窄、代价大**：盘只要被 FlyDisk 接管过，停止后本来就保持脱机（`persist=true`），重启后依旧是脱机的 —— 开机任务在稳态下什么都不用做。它只在"盘被手动联机 / 属性丢失"时有用，作用是把"可能被其它程序占用"的窗口从"一直到用户点启动"缩短到"开机后 1 分钟"。
3. **它会破坏 L2 的核心可信判据**（见下节第 1 条），这是数据安全问题，不是小瑕疵。

---

## 三、已知问题（复活前必须先解决）

### 1. ★ 破坏 L2 可信判据（最严重）

L2 账本可信的唯一判据是"打开这块盘时它**原本就是脱机**的 ⇒ 上次停机之后没人能写它"（见 AGENTS.md §2.3 与 `SsdCacheService` 类注释第 6 条）。

而"开机保持脱机"的流程是：

```
开机 → Windows 照常挂载该盘（联机、可写）→ 约 1 分钟后任务把它脱机
     → 之后 Probe 读到"原本脱机" → 账本被当成可信 → 可能返回陈旧块
```

"开机任务不得不去脱机"这件事本身，就是"**它开机时是联机的**"的证明；那块盘在这段窗口里可能被系统（卷修复/预读/搜索/更新）或其它程序写过。**必须先作废账本**，否则会造成卷损坏级事故。

**复活前必须补**：

- 在"需要动手脱机"的分支里，**先**在 `%ProgramData%\FlyDisk\` 写一个标记（文件名带该盘的设备身份散列），**再**脱机；
- UI 的启动前预检读到该标记 ⇒ 该盘本次 L2 账本**一律判不可信**（走既有的"校验 / 清空"询问）；
- 用户处理完（校验过 / 清空 / 本次不用 L2）⇒ 删掉标记；
- 若脱机失败或中途崩溃，标记仍然存在 ⇒ 保守地多判一次不可信（方向安全）；
- 两个调用方（SYSTEM 短命进程 + GUI 启动期）共用同一份逻辑。

### 2. 执行期判据不完整

当前只调用了 `DescribeTargetDiskBlockReason`（系统盘 / 程序所在盘 / 分页文件盘三条）。缺：

- 只读；
- **L2 缓存所在盘**（整盘脱机会让 L2 容器打不开 → 按 §2.3「不静默回退」会硬失败中止启动）；
- 无稳定身份（无序列号）。

注意：这些检查**不能只在写入配置时做**——环境会变（例如用户后来把 L2 缓存盘换成了名单里的那块），必须在**动手时**重新判一遍。建议复用选盘框的判定集合（`PickerForm.BlockReasons`）。

### 3. 弱身份可能指错盘

`ResolveDiskNumber` 对无序列号的盘曾回落到 `型号|容量|扇区`。开机路径是第一个"用存下来的身份去动手"的地方，同型号同容量的两块盘会指错。**复活后应直接拒绝无稳定身份的条目**（与主程序的加速目标硬拦一致）。

### 4. 文案与判据不符

`boot.offline.blocked` 中英都写着"（… / 只读）"，但 `DescribeTargetDiskBlockReason` 只返回三条，**"只读"根本没判**。复活时要么补判据，要么改文案。

### 5. 计划任务残留

功能摘除后，**没有任何代码会删除已经创建的任务**。升级过的机器需要手工清理：

```
schtasks /Delete /TN \FlyDisk\KeepOffline /F
schtasks /Delete /TN \FlyDisk\AutoStart /F
```

---

## 四、当前实现里的可复用部分（这次已修好）

`StartupService.cs` 在归档前刚完成以下修复（可直接参考）：

1. **写侧**：任务 XML 用 `XDocument` 构造、`XmlWriter` 落盘（`Encoding.Unicode`），**不再手工拼接字符串 / 手工 XML 转义**；
2. **读侧**：`schtasks /Query /XML` 的 stdout 收**原始字节**，用 `XDocument.Load(Stream)` 解析（编码由 XML 声明决定）；
3. **判据**：`TaskDefinitionMatches` 用**投影比较**代替"整段文本里找 exe 路径子串"：

   投影字段（只比这些）：
   - `Actions/Exec`：`Command`、`Arguments`、`WorkingDirectory`
   - `Principals/Principal[@id='Author']`：`UserId`（SID 归一）、`LogonType`、`RunLevel`
   - 触发器：`BootTrigger`（`Enabled` 缺省 true、`Delay`）/ `LogonTrigger`（`Enabled`、`UserId`）
   - `Settings`：`MultipleInstancesPolicy`、`ExecutionTimeLimit`

   **故意不比**：`RegistrationInfo`（Windows 会补 `Date`/`URI`/`SecurityDescriptor`）、其余 Settings、属性顺序与空白。
4. **超时**：两个管道并发读 + `Process.WaitForExit(timeout)`，30 秒超时**真正生效**（原实现先 `ReadToEnd` 再等，超时是死代码）；
5. **临时文件**：任务 XML 写在**当前用户临时目录**（`Path.GetTempPath()\FlyDisk`），`schtasks` 返回后立即删除（避免"提升进程从共享可写目录读文件"的 TOCTOU 坏味道，也不再在 ProgramData 留 `.xml`）。

### 实现级备忘

- 任务 XML 命名空间：`http://schemas.microsoft.com/windows/2004/02/mit/task`，读写共用一处常量；
- `UserId` 回读形态随机器可能不同（`域\用户` 或 SID）——用 `NTAccount`/`SecurityIdentifier` 互译归一；**复活时请先实测一次** `schtasks /Query /TN <名字> /XML` 的实际输出；
- 判据过严 = 每次启动都重建任务（功能无害但会反复写文件/跑 schtasks）。新增比较字段前先确认 Windows 会原样保留它。

---

## 五、复活清单（按顺序）

1. 把两个 `.cs` 复制回 `FlyDisk/`（并确认命名空间仍是 `FlyDisk`）；
2. 在 `StartupService.cs` 里补上对 `FlyDisk.Models` 的 `using`（当前归档副本不编译，引用可能已过期）；
3. `DiskConfig` 重新加回 `RunAtStartup` / `KeepOfflineOnBoot` / `StartupDiskIdentities`；
4. 恢复 `Program.Main` 的 `--keep-offline-only` 分支（`StartupService.KeepOfflineOnlySwitch`）；
5. 恢复 `Form1` 的启动期维护与设置保存后的 `Apply` 调用；
6. 恢复 `Settings` 的「开机与自动化」组（或按新 UI 设计重做）；
7. 恢复本地化键：`boot.offline.*`、`startup.task.*`、相关 `settings.*`（中英各一份）；
8. **先补第三节第 1 条的 L2 可信判据**，再恢复功能；
9. 若将来同时做「开机自动加速」，还需一套**无人值守的 L2 预检策略**（不能弹窗）：
   - 可恢复类（盘原本联机 / 上次未正常落盘 / 容量变化）→ 默认"不采信、跳过该盘 L2"，如实记日志；
   - 不可恢复类（分页文件盘 / 系统盘 / 程序盘 / 只读 / 无身份）→ 整体不启动并如实告知。

---

## 六、参考

- 本次整改的完整计划：`docs/PR13整改施工计划.md`
- L2 可信判据：`AGENTS.md` §2.3、`FlyDisk/Engine/SsdCacheService.cs` 类注释第 6 条
- iSCSI 克隆盘被判冗余路径的排障记录：`docs/iSCSI克隆盘被判冗余路径_排障记录.md`
