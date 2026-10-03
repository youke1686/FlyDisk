# 第三方组件与许可证（Third-Party Notices）

本项目（FlyDisk）自身以 **GNU GPL v3 或更新**（GPL-3.0-or-later）发布（全文见仓库根目录 `LICENSE`）。分发时，除 `LICENSE` 外，还应随附本文件，以及本文件列出的第三方许可证全文（都在 `FlyDisk\Licenses\` 下）。

---

## 一、随程序分发的库

| 组件 | 版本 | 在本项目中的用途 | 许可证 | 许可证文本 |
| --- | --- | --- | --- | --- |
| ISCSI | 1.5.6 | iSCSI target，PDU 编解码，登录协商，SCSI 命令分发 | LGPL-3.0-or-later | `LGPL-3.0-or-later.txt` + `GPL-3.0.txt` |
| DiskAccessLibrary | 1.6.3 | `Disk` 等磁盘基类（`CachedPhysicalDisk` 继承 `Disk`） | LGPL-3.0-or-later | 同上 |
| DiskAccessLibrary.Win32 | 1.6.3 | `PhysicalDiskControl`：磁盘几何，联机状态，设备号，设备描述符 | LGPL-3.0-or-later | 同上 |
| System.ServiceProcess.ServiceController | 10.0.12 | 启动 MSiSCSI 服务 | MIT | `dotnet-runtime-LICENSE-MIT.txt` + `ServiceController-THIRD-PARTY-NOTICES.txt` |

**版权声明**

- ISCSI，DiskAccessLibrary，DiskAccessLibrary.Win32：Copyright © Tal Aloni 2012-2024
  - 来源：https://github.com/TalAloni/iSCSIConsole ，https://github.com/TalAloni/DiskAccessLibrary
- System.ServiceProcess.ServiceController：Copyright (c) .NET Foundation and Contributors
  - 来源：https://github.com/dotnet/dotnet

**LGPL-3.0 合规要点（前三个库）**

1. 随分发附上 LGPL-3.0 全文与 GPL-3.0 全文（LGPL-3.0 正文通过引用并入 GPL-3.0 条款），以及上方的版权声明。
2. 不得单独出售这些库。
3. 使用者应能自行替换这些库。本项目以 .NET 程序集形式引用它们（非静态链接），替换方式是换掉对应的 `ISCSI.dll`，`DiskAccessLibrary.dll`，`DiskAccessLibrary.Win32.dll`。
   注意：当前发布配置 `PublishSingleFile=true`（见 `FlyDisk\Properties\PublishProfiles\FolderProfile.pubxml`）会把这些程序集打进单个 exe，替换不便。建议随分发同时提供一个非单文件的发布包（`dotnet publish -p:PublishSingleFile=false`）。

---

## 二、随程序分发的资源

| 资源 | 用途 | 许可证 | 许可证文本 |
| --- | --- | --- | --- |
| `fusion-pixel-12px-proportional-zh_hans.ttf`（Fusion Pixel Font） | 主窗口标题与副标题的像素字体，嵌入 exe | SIL OFL 1.1（字体本体），MIT（工具链） | `fusion-pixel-font-LICENSE-OFL.txt` + `fusion-pixel-font-LICENSE-MIT.txt` |

**版权声明**：Copyright (c) 2022, TakWolf（https://takwolf.com ，https://github.com/TakWolf/fusion-pixel-font）

**OFL 1.1 要点**

1. 随分发保留版权声明与许可证全文。
2. 不得单独出售字体本身；随本软件分发（含随软件收费）是允许的。
3. 若修改了字体（子集化，调整字模等），衍生字体必须继续以 OFL 发布。本字体**未声明 Reserved Font Name**，故名称使用不受额外限制。
4. OFL 只约束字体及其衍生字体，不约束本项目的源代码，也不约束用该字体渲染出的内容。

---

## 三、.NET 运行时

当前发布配置为框架依赖发布（`SelfContained=false`），运行时由用户机器提供，**不随本程序分发**，因此无需随附运行时的许可证。

若日后改为自包含发布（`SelfContained=true`）或自带运行时的单文件发布，需追加随附 `dotnet/runtime` 的 `LICENSE.TXT`（MIT）与 `THIRD-PARTY-NOTICES.TXT`。

---

## 四、仅构建期使用，不随程序分发

| 工具 | 用途 |
| --- | --- |
| Inkscape | 把 `FlyDisk\icons\` 下的 `大/中/小图标-浅/深.svg` 转成同目录 `light`、`dark` 子目录里的 PNG（见 `convert-icons.bat`） |
| ImageMagick | 把上述 PNG 合成为 `FlyDisk\icons\icon-light.ico`，`FlyDisk\icons\icon-dark.ico`（见 `pack-icons.bat`） |

这些工具只用于生成资源，其产物（ICO/PNG）不构成对工具的衍生，故不必列入分发声明；此处登记仅为说明构建链。

---

## 五、许可证文本清单

| 文件 | 覆盖对象 |
| --- | --- |
| `LGPL-3.0-or-later.txt` | ISCSI，DiskAccessLibrary，DiskAccessLibrary.Win32 |
| `GPL-3.0.txt` | 上者的引用条款（LGPL-3.0 并入 GPL-3.0） |
| `dotnet-runtime-LICENSE-MIT.txt` | System.ServiceProcess.ServiceController（.NET 运行时的 MIT 许可） |
| `ServiceController-THIRD-PARTY-NOTICES.txt` | .NET 运行时所用第三方组件声明（随该程序集分发） |
| `fusion-pixel-font-LICENSE-OFL.txt` | Fusion Pixel Font 字体本体 |
| `fusion-pixel-font-LICENSE-MIT.txt` | Fusion Pixel Font 工具链 |

---

## 六、本项目自身的许可证

本项目（FlyDisk）以 **GNU GPL v3 或更新**（GPL-3.0-or-later）发布，全文见仓库根目录 `LICENSE`，每个源文件顶部亦带有版权与授权声明。

**与第三方许可证的兼容性复核**

- LGPL-3.0-or-later 组件（ISCSI，DiskAccessLibrary，DiskAccessLibrary.Win32）：LGPLv3 明确允许与 GPLv3 作品组合，方向兼容；仍须满足第一节的声明与可替换要求。
- MIT 组件（System.ServiceProcess.ServiceController）：MIT 与 GPLv3 兼容，保留版权与许可声明即可。
- Fusion Pixel Font（SIL OFL 1.1）：字体以资源形式随程序分发，字体本身继续适用 OFL，不因宿主程序改用 GPLv3 而改变；OFL 亦明确不限制作该字体渲染出的内容与宿主软件代码。

上述第三方代码并未并入本项目自身的 GPL 授权范围，各自许可证互不影响。
