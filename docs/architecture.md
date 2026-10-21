# 架构决策记录 — 阶段 0：环境与架构验证

- 状态：已确认（阶段 0 完成，阶段 1 骨架已建立）
- 日期：2026-10-20
- 适用范围：本轮仅覆盖环境验证、后端路线选择、项目骨架。DLNA 协议与完整 UI 尚未实现。

本文件记录的都是本机**实际执行并观察到的结果**。凡是无法在本环境中验证的内容，均单独标注为"未验证"，不做推断性结论。

---

## 1. 环境验证结果

| 项目 | 实测值 | 验证方式 |
| --- | --- | --- |
| 操作系统 | Windows 11 专业版，Build 26300 | `Get-CimInstance Win32_OperatingSystem` |
| 进程架构 | win-x64 | `dotnet --info` |
| .NET SDK | 10.0.402（同时安装 3.1.426、7.0.410） | `dotnet --list-sdks` |
| .NET 运行时 | Microsoft.NETCore.App 10.0.13 | `dotnet --info` |
| Windows SDK | 仅 10.0.28000.0 | `ls "C:\Program Files (x86)\Windows Kits\10\Include"` |
| Visual Studio / Build Tools | **未安装** | `C:\Program Files\Microsoft Visual Studio` 不存在 |
| winapp CLI | 0.7.1 | `winapp --version` |
| WinUI 模板包 | Microsoft.WindowsAppSDK.WinUI.CSharp.Templates **0.0.7-alpha** | `dotnet new list winui` |
| Windows App Runtime | 2.5.1.0（x64/x86）已安装，另有 1.0–1.8 | `Get-AppxPackage *WindowsAppRuntime*` |

结论：在没有 Visual Studio 的机器上，WinUI 3 依然可以通过 .NET SDK + NuGet 构建工具链完成构建与运行，因为
`Microsoft.Windows.SDK.BuildTools` / `Microsoft.WindowsAppSDK` 以 NuGet 形式提供 MSBuild 目标与工具。
但**模板包版本为 0.0.7-alpha**，属于预发布模板；AGENTS.md 要求记录该事实，见 §6 风险项。

### 1.1 已验证的 winapp 子命令

`winapp --help` 实际列出：`init`、`new`、`restore`、`update`、`package`、`sign`、`az-sign`、`cert`、`manifest`、
`embed-identity`、`create-external-catalog`、`create-debug-identity`、`store`、`tool`、`get-winapp-path`、`run`、
`unregister`、`target`、`find-api`、`find-ui`、`ui`。

`winapp new --list` 实际可用模板：`winui`、`winui-lib`、`winui-mvvm`、`winui-navview`、`winui-tabview`、
`winui-unittest`，以及实验性 Reactor 模板（`reactor`、`reactor-mvu`、`reactor-navview`、`reactor-tabview`）。

本轮实际使用并成功的命令只有：`winapp new`、`winapp run`、`winapp unregister`、`winapp get-winapp-path`。
未使用任何未经帮助文档确认的参数。

---

## 2. `winapp new --template winui-mvvm --name DlnRenderer --output .` 无输出问题诊断

### 2.1 结论（根因）

**不是卡死，也不是模板损坏，而是"输出目录非空"的前置检查拒绝了命令，且错误信息只写到 stderr。**

- 退出码：`2`
- stdout：仅一行 `⚠ Non-interactive environment detected. Using default values.`
- stderr：`❌ Output directory '<cwd>' is not empty. Use --force to scaffold into it anyway.`

仓库根目录本来就存在 `AGENTS.md`，因此 `--output .` 必然命中该检查。若调用方（脚本 / Agent / 管道）
只捕获 stdout 或只看退出码，就会表现为"运行后无任何输出"。

### 2.2 复现矩阵（本机实测）

| 场景 | 命令 | 退出码 | 结果 |
| --- | --- | --- | --- |
| 非空目录（含 AGENTS.md 或日志文件） | `winapp new --template winui-mvvm -n DlnRenderer -o .` | 2 | stderr 报"目录非空"，未生成任何文件 |
| 真正空目录 | 同上，输出重定向到目录外 | 0 | 正常生成 WinUI MVVM 工程 |
| 非空目录 + `--force` | 加 `--force` | 0 | 正常生成，**且不删除原有文件**（`AGENTS.md` 内容保持不变） |
| 子目录输出 | `-n DlnRenderer -o src/DlnRenderer.App` | 0 | 直接在该目录生成工程 |

补充观察（重要，用于避坑）：即使目录里只有 shell 重定向产生的 `out.log`，也会被判为非空。
因此不要把 stdout/stderr 重定向到待生成目录内。

另外 `winapp new` 在非交互环境会打印 `⚠ Non-interactive environment detected`，若模板包需要更新，
它可能尝试交互确认；本轮实测未出现交互等待，模板已是最新（0.0.7-alpha）。

### 2.3 本轮采用的做法

不复用 `--output .`，而是把 App 工程生成到子目录：

```powershell
winapp new --template winui-mvvm --name DlnRenderer --output src/DlnRenderer.App
```

`--name DlnRenderer`（而非 `DlnRenderer.App`）是有意为之：模板会把带点的名称转义成
`RootNamespace = DlnRenderer_App`，实测会污染所有命名空间。保持 `DlnRenderer` 可获得干净的
`namespace DlnRenderer`，而工程文件位于 `src/DlnRenderer.App/DlnRenderer.csproj`。

---

## 3. 兼容性核实

### 3.1 .NET SDK / WinUI 3 / Windows App SDK

`winapp new` 生成的工程使用：

- `TargetFramework`：`net10.0-windows10.0.26100.0`
- `TargetPlatformMinVersion`：`10.0.17763.0`（Windows 10 1809）
- `Platforms`：`x86;x64;ARM64`，`RuntimeIdentifier` 默认取构建进程架构
- `UseWinUI=true`、`EnableMsixTooling=true`、`WinUISDKReferences=false`

NuGet 实际解析版本（`dotnet list package --include-transitive`）：

| 包 | 解析版本 | 许可证 |
| --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.5.1 | MIT（nuspec 内含 license.txt） |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Windows SDK 许可条款（非 OSI 开源） |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.7.1 | Microsoft 许可条款 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |

结论：Windows 11 26300 + .NET 10.0.402 + WASDK 2.5.1 组合可构建、可启动（见 §7）。
Windows 10 1809 为声明的最低版本，但**本机无法实测**，属于未验证项。

模板生成的工程使用 `Version="*"` 浮动版本。阶段 1.5 已改为 `Directory.Packages.props` 集中固定
上述已验证版本，App 另提交 `packages.lock.json`（见 §10.4）。

### 3.2 libmpv

本机实测（不是推断）：

- Scoop 安装的 `mpv` 0.41.0 是 **静态单文件构建**（`mpv.exe` 57 MB），**不包含** `libmpv-2.dll`，不能用于嵌入。
- 另装有 Scoop `mpv-dev` 包：`libmpv-2.dll`，116 MiB，来源
  `zhongfly/mpv-winbuild` 的 `20261019-git-6a262d11f6`（对应 mpv v0.41.0-1224-g6a262d11f）。
- PE 头 `Machine = 0x8664` → **x64**。
- 用最小 P/Invoke 探针实际加载该 DLL：`mpv_create` 成功、`mpv_initialize` 返回 `0`，
  并能读出 `mpv-version` 与 `mpv-configuration` 属性 → **在 .NET 10 x64 进程中可正常加载和初始化**。
- 其 `mpv-configuration` 中**不含** `-Dgpl=false` → 这是 **GPL 构建**（mpv 默认启用 GPL 特性）。
- 官方 `mpv.io` 明确说明：Windows 二进制包除注明者外均为**第三方非官方构建**；LGPL 构建需要单独的
  `-lgpl` 产物或自行以 `-Dgpl=false` 编译。
- 同一上游提供 `aarch64` 变体，因此 ARM64 路线可行，但**未在本机验证**。

对项目的影响（关键）：当前可用的 libmpv 是 GPLv2+。若项目以非 GPL 许可证发布，
直接链接该 DLL 会让整个应用受 GPL 约束。阶段 1.5 已用该构建完成真实播放验证，
但**仅限本地开发、不分发**；许可证选择与义务见 §10.1。

候选绑定库：`HanumanInstitute.LibMpv` 0.10.1，MIT，只是 P/Invoke 封装，原生 DLL 仍需自行提供。
**未采用**：本项目只用约 10 个 C 函数，自写互操作面更小且无需额外依赖（已实际播放验证）。

---

## 4. DLNA 后端：复用 vs 自研

### 4.1 调研限制（必须说明）

本机 DNS 把 `github.com` 解析到保留地址 `198.18.0.99`，无法访问 GitHub 源码与许可证文件。
因此：

- 无法直接核实 AGENTS.md 中提到的 **MiniCast** 的源码结构、许可证与维护状态。按公开搜索，能确认存在名为
  "minicast" 的 DLNA renderer 项目（有其他项目自述"inspired by Macast and minicast"），但**本次未能取得其
  仓库与许可证原文**，故不将其作为已核实的复用方案。
- 可用于交叉印证的同类开源实现是 **Macast**（Python + mpv 的 DLNA Media Renderer）：
  PyPI 元数据实测 `license = GPL3`，版本 0.65.post1。

该限制不影响结论方向，但影响证据强度：**"复用某开源 renderer 后端"这一路线的许可证风险是系统性的**，
因为此类项目普遍是 GPL（Macast 已核实为 GPL-3.0；mpv 本身的 GPL 构建也是同一类问题）。

### 4.2 对比

| 维度 | A. 复用现成 renderer 后端 | B. C# 自研 DLNA 后端 |
| --- | --- | --- |
| 协议覆盖 | 起点较高，通常已包含 SSDP、AVTransport、RenderingControl | 需自行实现，但可精确控制支持范围 |
| 与本项目进程模型的契合 | 差。Macast/minicast 一类实现把"协议 + 托盘 UI + 播放"耦合在同一进程，剥离 UI 需改源码 | 好。协议层天然可放进独立 Service 进程 |
| 许可证 | 高风险：已核实的同类实现为 GPL-3.0；若再链接 GPL libmpv，整体必然 GPL | 可控：仅受 libmpv 构建方式约束（可选 LGPL） |
| Windows 部署 | 需要携带 Python 运行时或改造为原生进程，与 WinUI 3 打包（MSIX）协调困难 | 单一 .NET 运行时，与 WinUI 3 同栈，MSIX 打包自然 |
| 维护成本 | 上游停更 / 与上游分叉后长期维护；跨语言调试成本高 | 全部自有代码，测试可控，但需自行跟进 UPnP 规范细节 |
| 与 AGENTS.md 约束的一致性 | 与"禁止把 Python 托盘程序套壳"的定位冲突风险高 | 一致 |

### 4.3 决策

**采用方案 B：使用 C# / .NET 在 `DlnRenderer.Dlna` 中独立实现 DLNA Renderer 协议层**，理由：

1. 无法在本环境核实候选复用项目的许可证与可剥离性，而"未验证即复用"违反 AGENTS.md 的调研要求。
2. 已核实的同类实现（Macast）为 GPL-3.0，与本项目希望保持的宽松许可、以及未来可能的闭源分发不兼容。
3. 本项目要求协议后端与 UI、播放器解耦并独立进程运行；自研后端从一开始就按该边界设计，避免后期拆进程。
4. 自研范围可控：首版只需 AVTransport + RenderingControl + ConnectionManager 的能力声明子集。

**协议覆盖面（首版目标，尚未实现）**：SSDP 发现与公告、设备/服务描述、SOAP 处理、DIDL-Lite 解析、
AVTransport（`SetAVTransportURI`、`GetTransportInfo`、`GetPositionInfo`、`Play`、`Pause`、`Stop`、`Seek`、
`GetMediaInfo`）、RenderingControl（`GetVolume`/`SetVolume`/`GetMute`/`SetMute`）、ConnectionManager 能力声明。

**明确不承诺**：DLNA 认证合规。`Next`/`Previous`/`SetNextAVTransportURI` 在播放队列设计确定前一律返回
"不支持"错误，不返回虚假成功。

---

## 5. 模块划分与本轮实际落地

目标结构与实际创建的差异（AGENTS.md 允许按真实依赖拆分，避免空项目）：

| 目标项目 | 本轮状态 | 原因 |
| --- | --- | --- |
| `DlnRenderer.App` | **已创建**（winapp `winui-mvvm` 模板） | 阶段 0 交付物：可构建、可运行的 WinUI 3 工程 |
| `DlnRenderer.Core` | **已创建**（含真实代码 + 单元测试） | 播放状态模型是所有层的地基，先落地 |
| `DlnRenderer.Core.Tests` | **已创建**（43 个测试） | 验证播放状态转换规则 |
| `DlnRenderer.Player` | **已创建**（阶段 1.5） | libmpv P/Invoke、播放器生命周期、状态映射、Win32 视频子窗口 |
| `DlnRenderer.Player.Tests` | **已创建**（23 个测试） | 状态映射、节流规则、原生播放/错误/顺序加载/释放 |
| `DlnRenderer.Dlna` | **已创建**（阶段 3） | SSDP、HTTP、SOAP、AVTransport、ConnectionManager、DIDL、SCPD |
| `DlnRenderer.Dlna.Tests` | **已创建**（222 个测试） | 协议解析、Fault 码、回环 HTTP 集成、网卡选择 |
| `DlnRenderer.Service` | **已创建**（阶段 3） | 独立后台进程：播放会话、DLNA 宿主、IPC 服务端、原生视频窗口 |
| `DlnRenderer.Service.Tests` | **已创建**（115 个测试） | 真实 Named Pipe：命令、事件、错误、断连、协议违规 |

依赖方向（已建立）：`App → Core`；`Service → Core, Dlna, Player`；`Dlna → Core`；`Player → Core`。
`Core` 不反向依赖任何上层，且不引用 WinUI 或 native 代码。

Core 当前内容：

- `Models/PlaybackState.cs`：`Stopped`/`Playing`/`Paused`/`Buffering`/`Transitioning`/`Error`
- `Models/PlaybackStatus.cs`：单一状态源（含位置、时长、音量、静音、错误）
- `Models/MediaInfo.cs`：DIDL-Lite 解析后的媒体元数据（字段均可缺省）
- `Interfaces/IMediaPlayer.cs`：播放器抽象，隔离 libmpv
- `State/PlaybackStateRules.cs` + `State/PlaybackStateMachine.cs`：状态转换规则与守卫

设计取舍：合法转换表显式列出；同状态重复转换视为幂等空操作（控制端常重复下发 Play/Pause；
若拒绝会产生虚假 DLNA 错误）；`Error` 不允许直接回到 `Playing`，必须先 `Transitioning`（重新加载）或 `Stopped`。

---

## 6. 依赖与许可证

| 组件 | 版本 | 许可证 | 来源 / 义务 |
| --- | --- | --- | --- |
| .NET SDK / Runtime | 10.0.402 / 10.0.13 | MIT | Microsoft |
| Windows App SDK | 2.5.1 | MIT | nuspec 内含 `license.txt` |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Windows SDK 许可条款 | 非 OSI 开源；仅作构建工具，注意再分发条款 |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.7.1 | Microsoft 许可条款 | 提供 `dotnet run` 包身份支持 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | 模板自带；当前仅示例 ViewModel 使用 |
| xunit | 2.9.3 | Apache-2.0 | 测试项目 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | 测试项目 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | 测试项目 |
| coverlet.collector | 6.0.4 | MIT | 测试项目 |
| libmpv（zhongfly 构建） | 20261019-git-6a262d11f6（mpv 0.41.0-1224） | **GPL-2.0-or-later（实测确认）** | 本地开发依赖，**不入库、不随包分发**；见 §10.1 |
| mpv 客户端头文件 `client.h` | 随上面的构建提供 | ISC | 仅头文件 API 声明；P/Invoke 按此手写 |
| HanumanInstitute.LibMpv | 0.10.1（候选） | MIT | P/Invoke 封装；未采用（自写更小的接口面） |

发布前必须补齐：`mpv`/`libmpv` 的完整对应源码与构建信息（尤其 GPL 情况下）、Windows SDK 工具的再分发声明、
以及第三方 notices 文件。当前仓库**尚未**包含 `THIRD-PARTY-NOTICES`，属阶段 5 待办。

---

## 7. 实测结果（可复现命令）

```powershell
dotnet restore DlnRenderer.sln
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
winapp run src/DlnRenderer.App -c Debug --detach
```

| 步骤 | 结果 |
| --- | --- |
| `dotnet restore` / `--locked-mode` | 均成功（锁文件仅用于 App，理由见 §10.4） |
| `dotnet build DlnRenderer.sln -c Debug` | 成功，0 错误，0 警告（模板示例 ViewModel 已被真实播放器 UI 替换） |
| 输出架构 | `src/DlnRenderer.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/` |
| `dotnet test` | 通过 163 / 失败 0（Core 25 + Player 15 + Dlna 110 + Service 13） |
| `winapp run --detach` | 成功启动，窗口可交互（详见 §10.6 与 §11） |

构建过程中修复过一个真实缺陷：`PlaybackStateRules` 原先在检查枚举有效性之前先判定"同状态即合法"，
导致未定义值 `(PlaybackState)42` 自比较时被放行；已调整顺序，由单元测试覆盖。

---

## 8. 已知问题与风险

1. **libmpv 许可证未定案（阻断分发，不阻断开发）**：现有可用二进制均为 GPL。项目尚未声明整体许可证；
   在做出决定前，播放器只作本地开发验证，不随包分发。详见 §10.1。
2. **ARM64 未验证**：有 `aarch64` 产物，但本机为 x64，未实测。
3. **Windows 10 兼容性未验证**：`TargetPlatformMinVersion` 为 17763，但本机只有 Windows 11 26300。
4. **仓库已纳入 Git**：基线提交为 `1ecb98f`；`packages.lock.json` 已入库。
5. **模板包为 0.0.7-alpha**：模板本身是预发布产物，生成的文件结构与后续 `winapp` 版本可能变化。
6. **GitHub 不可达**：影响拉取依赖源码、核对许可证与上游实现；需网络环境支持或改用镜像。
7. **视频不再与 XAML 重叠**：阶段 3 起视频由**服务进程**的原生窗口呈现，UI 进程不再承载视频表面，
   §10.3 中“子窗口遮挡 XAML”的约束只适用于 UI 内嵌场景（当前未使用）。
8. **构建工具再分发**：`Microsoft.Windows.SDK.BuildTools` 受 Windows SDK 许可约束，发布前需确认打包内容。
9. **GENA 事件未实现**：`SUBSCRIBE` 返回 501，控制端需轮询 `GetTransportInfo`（详见 §11.3）。
10. **RenderingControl 未实现**：音量的 DLNA 侧控制缺失，目前只能经 IPC 调整。

---

## 9. 下一阶段建议

1. 决定 libmpv 许可证路线（LGPL 构建 vs 接受 GPL）；在原生产物能合规分发之前，不进入对外发布流程。
2. 把仓库纳入 Git 管理并提交 `packages.lock.json`。
3. 阶段 3：定义 IPC 协议（版本字段、请求 ID、错误结构、大小上限），创建 `DlnRenderer.Service` 与
   `DlnRenderer.Dlna` 项目，实现 `GetStatus` 与 `RendererStateChanged`，在 UI 上显示服务连接状态。
4. 补充 `docs/protocol.md`，并把播放器接口固定为 IPC 的命令契约来源。
5. 为 IPC 消息解析与 DIDL-Lite 解析先行编写单元测试（可在无真实网络的情况下验证）。

---

## 10. 阶段 1.5：libmpv 播放引擎（已验证）

### 10.1 二进制来源与许可证（实测，非推断）

| 项 | 结论 |
| --- | --- |
| 使用中的二进制 | `libmpv-2.dll`，116 MiB，x64（PE `Machine=0x8664`） |
| 版本 | `mpv v0.41.0-1224-g6a262d11f`（Scoop `mpv-dev` 包 `20261019-git-6a262d11f6`） |
| 上游来源 | `zhongfly/mpv-winbuild` 的 `mpv-dev-x86_64-20261019-...7z`（第三方构建） |
| SHA-256 | `53513b8f7cbae3700bdb4f7af1933cbdc512fcafcd88d38c594087f9d4fd7881` |
| 构建选项 | `mpv-configuration` 实测**不含** `-Dgpl=false` → **GPL-2.0-or-later** |
| 本地存放 | `src/DlnRenderer.Player/native/x64/`，已被 `.gitignore` 排除，不入库 |
| 获取方式 | `scripts/fetch-libmpv.ps1`（校验哈希 + 写 `libmpv.provenance.json`） |

头文件本身已明确区分：`client.h` 为 ISC 许可（允许任意绑定），而 “mpv core is by default still GPLv2+ —
unless built with `-Dgpl=false`, which makes it LGPLv2+”。本轮只使用头文件中的 C API 声明，因此绑定代码本身不受 GPL 传染；
受约束的是链接/分发的 native DLL。

**已排除的“看似 LGPL”来源**：NuGet 包 `Endpne.LibMPV.Windows` 0.41.0 的 nuspec 声明
`LGPL-2.1-or-later`，但下载并实测其 `build/x64/libmpv-2.dll`（mpv v0.41.0-85-g468d34c9b，
sha256 `38acd030...`）的 `mpv-configuration` **同样不含** `-Dgpl=false`，即实际为 GPL 构建。
结论：**不能以 NuGet 元数据判断 native 许可证**，必须以构建选项为准。

**许可证策略（本轮决定）**：

- 不修改项目整体许可证，不声明项目为 GPL。
- 原生产物仅用于本机开发验证，**不随源码、安装包或发布物分发**。
- 因此在完成下列任一选择前，本版本**不得标记为可发布**：
  1. 采用 LGPL 构建（官方 CI 的 `-lgpl` 产物，或自行 `-Dgpl=false` 编译并保留构建脚本/源码归档）；
  2. 明确接受 GPL-2.0-or-later，并准备对应源码、许可证与通知文件。
- 若选择 GPL，安装包必须包含：libmpv 完整对应源码获取方式、GPL-2.0 全文、mpv 版权声明，
  以及 FFmpeg/libass/libplacebo 等依赖的许可证与通知（见 `docs/licenses.md`）。

### 10.2 播放器模块结构

`src/DlnRenderer.Player`（`net10.0-windows`，不依赖 WinUI）：

| 文件 | 职责 |
| --- | --- |
| `Mpv/MpvNative.cs` | 仅声明实际用到的 C API；UTF-8 参数编组；错误文本 |
| `Mpv/MpvInteropTypes.cs` | event/property/end-file 结构体与枚举镜像 |
| `Mpv/MpvPlayer.cs` | `IMediaPlayer` 实现：初始化、事件循环、状态机接入、命令、释放 |
| `Mpv/MpvPlayerOptions.cs` | 窗口句柄、vo/ao 覆盖、硬解、节流间隔、日志级别 |
| `Mpv/MpvStateMapper.cs` | 纯函数：native 信号 → 领域状态（可单测） |
| `Mpv/PositionThrottle.cs` | 纯逻辑：进度节流（可单测） |
| `Mpv/MpvVideoSurface.cs` | Win32 子窗口，作为 mpv 的 `wid` 输出目标 |

接口仍为 Core 中的 `IMediaPlayer`，UI 与后续 DLNA 层只看到领域模型，不接触 native API。

### 10.3 视频渲染方案对比与选择

| 方案 | 结果 |
| --- | --- |
| libmpv render API（`render.h` + D3D11/ANGLE + `SwapChainPanel`） | 合成效果最好，但需要 Composition/交换链互操作层，代码量与失败面显著增加；首版不采用 |
| **HWND 子窗口 + `wid`**（已采用） | 实现最小：创建 Win32 子窗口作为视频区域，mpv 直接绘制；已验证可播放、可缩放、可释放 |
| 独立顶层窗口同步位置 | 需要自行管理激活、层级、生命周期；不比子窗口简单，未采用 |
| 直接把顶层 WinUI HWND 交给 mpv | mpv 会占据整个窗口绘制区，与 XAML 内容冲突；未采用 |

已知约束（实测）：

- WinUI 3 把 XAML 内容托管在 `Microsoft.UI.Content.DesktopChildSiteBridge` 子窗口中；本项目的视频窗口与其同级，
  **层级会互相覆盖**。已通过在每个尺寸/可见性变更时 `SetWindowPos(HWND_TOP)` 将视频窗置于最上层。
- 视频区域**不能被 XAML 覆盖**：后续若需要字幕/控件叠加，必须改用 render API 或额外的覆盖窗口。
- 子窗口必须在其父窗口销毁前释放；当前在 `AppWindow.Closing` 中同步释放播放器与窗口。
- DPI：XAML 边界按 `XamlRoot.RasterizationScale` 换算为物理像素后再设置子窗口位置。

### 10.4 依赖锁定与锁文件

- 所有直接依赖已固定到 `Directory.Packages.props`（中央包管理），`Version="*"` 已全部消除。
- `packages.lock.json` **只对 App 项目启用**：WinUI 工程的还原始终带 `RuntimeIdentifier`（模板默认 win-x64，
  `winapp` 还会额外传 `-r win-x64`），锁文件因此稳定；而类库/测试项目会被 `winapp` 以带 RID 和不带 RID 两种方式还原，
  锁文件必然产生 `NU1004`。它们的版本由中央包管理精确固定。
- 验证命令：`dotnet restore DlnRenderer.sln --locked-mode`（本轮通过）。
- 升级依赖时：修改 `Directory.Packages.props` → 对 App 执行带 force evaluate 的还原 → 审查锁文件差异。

### 10.5 状态同步与线程模型

- mpv 事件循环跑在专用后台线程（`mpv-event-loop`），只在该线程读取 native 事件。
- 领域状态由 `PlaybackStateMachine` 守卫；native 信号先经 `MpvStateMapper` 映射，再 `TryTransitionTo`，
  非法跳转被拒绝并记录，不会绕过 Core 的规则。
- 加载期间使用显式 `_loadPending` 标记：`idle-active` 在新文件加载完成前仍是旧值，
  若直接映射会把状态错误地拉回 `Stopped`（本轮实测并修复）。
- 进度事件按 250 ms 节流；状态/轨道/音量变化立即发布；相同状态记录去重。
- `StatusChanged` 在 mpv 线程触发，App 通过 `DispatcherQueue.TryEnqueue` 回到 UI 线程。
- 命令在 `Task.Run` 中执行，UI 线程不阻塞。
- 释放：置位停止标记 → `mpv_wakeup` → 有界等待（3 s）事件线程退出 → `mpv_terminate_destroy`；
  重复调用安全。

### 10.6 实测结果

| 验证项 | 结果 |
| --- | --- |
| 清理后 Debug 构建 | 0 错误 / 0 警告 |
| 单元 + 原生集成测试 | 40 通过 / 0 失败 |
| 本地音频播放 | `current-ao=wasapi`，48 kHz 双声道，`time-pos` 以 1× 推进 |
| 本地视频播放 | 子窗口内实际渲染（截图色彩统计：1768/2418 采样点为高饱和像素） |
| 画面在动 | 相隔约 1.5 s 的两帧平均通道差 12.76（非静态画面） |
| 窗口缩放 | 缩放后子窗口跟随新区域，画面继续渲染（1809/2370 高饱和采样） |
| 播放/暂停/恢复/停止/Seek/音量 | 集成测试覆盖，全部通过 |
| 连续打开多个媒体 | 音频→视频→音频 顺序加载，轨道类型与位置正确重置 |
| 关闭应用后释放 | 日志出现 `player disposed`，进程退出，无残留子窗口 |

尚未验证：人耳可听的音频输出（只能确认设备输出会话）、ARM64、Windows 10。

### 10.7 本轮发现并修复的缺陷

1. 加载竞态：`LoadAsync` 在 mpv 尚未上报新文件前就发布了状态，`idle-active=true` 把 `Transitioning` 拉回 `Stopped`。
2. 轨道判定：用 `vid`/`aid` 判断音视频类型不可靠——它们是选择项（默认 `auto`），纯音频文件也被判为视频；
   改为解析 `track-list` 并忽略 `albumart`。
3. 替换播放：旧文件结束事件会清掉“加载中”标记，使新媒体卡在 `Stopped`；已区分替换与真实结束/错误。
4. 层级：WinUI 内容桥接窗口会盖住视频子窗口；已在尺寸/可见性变化时重新置顶。

---

## 11. 阶段 3：DLNA MVP（已验证）

### 11.1 进程与职责划分

| 进程 / 模块 | 职责 |
| --- | --- |
| `DlnRenderer.Service.exe` | 后台常驻：SSDP、HTTP/SOAP、播放会话、IPC 服务端、原生视频窗口 |
| `DlnRenderer.App`（WinUI） | 只做状态展示与 IPC 控制；**不持有播放器，也不承载视频** |
| `DlnRenderer.Dlna` | 纯协议层，不引用 WinUI，也不直接引用 native 播放器 |
| `DlnRenderer.Player` | libmpv 适配；被服务引用，UI 已不再引用 |

这样做的直接理由：AGENTS.md 要求「UI 退出不应导致服务停止，服务负责播放引擎协调」。
若视频仍渲染在 UI 进程里，关闭窗口就必然中断画面，两种要求无法同时成立。
因此把视频窗口移到服务进程（`VideoPresenter`，一个带消息循环的 Win32 顶层窗口 + 子 HWND 作为 mpv `wid`）。

实测：关闭 UI 后服务仍存活、仍可被发现与描述；重新打开 UI 会**复用**同一服务实例（未产生重复进程）。

### 11.2 协议覆盖（仅声明已实现的部分）

| 能力 | 状态 |
| --- | --- |
| SSDP 应答 `ssdp:all` / `upnp:rootdevice` / `MediaRenderer:1` / `uuid:...` | 已实现，实测返回 3 条标准响应 |
| `ssdp:alive` / `ssdp:byebye` 公告 | 已实现（启动、周期、优雅退出） |
| 设备描述、AVTransport 与 ConnectionManager 的 SCPD | 已实现 |
| AVTransport：`SetAVTransportURI`、`Play`、`Pause`、`Stop`、`Seek`、`GetTransportInfo`、`GetPositionInfo`、`GetMediaInfo`、`GetDeviceCapabilities`、`GetTransportSettings`、`GetCurrentTransportActions` | 已实现 |
| ConnectionManager：`GetProtocolInfo`、`GetCurrentConnectionIDs`、`GetCurrentConnectionInfo` | 已实现，sink 仅声明 `http-get:*:*:*` |
| GENA（`SUBSCRIBE`/`NOTIFY`） | **未实现**，返回 501 |
| RenderingControl（音量/静音） | **未实现** |

设备描述中**没有**写入 `X_DLNADOC` 等 DLNA 认证标记：本项目未做 DLNA 认证，不应宣称符合某版本规范。

### 11.3 关键设计决定

1. **媒体 URI 只允许 `http` / `https`。**
   `file:` 与裸 Windows 路径（`C:\...`）都会被 `Uri` 解析为 file scheme，若放行，DLNA 控制接口就变成了
   任意本地文件读取入口。实测两个用例（`file:///...`、`C:\media\a.mp4`）均被拒绝。
2. **错误必须如实上报。**
   - 无媒体时 `Post/Pause/Seek` → `701`；`InstanceID != 0` → `718`；非法 URI → `600`；
     未知动作 → `401`；播放器执行失败 → `501`（不再以原始异常冒泡成无错误码的 500）。
   - IPC 侧同样不伪装：无媒体时 `Play` 返回失败，而不是“成功但状态仍是 Stopped”。
3. **`Stop` 保留当前 URI**（符合 AVTransport 语义），因此 `Play` 需要检测“有 URI 但播放器已卸载”并重新加载。
   这是本轮实测发现的真实缺陷：直接 `Play` 会成功返回，但 mpv 里没有任何媒体。
4. **按网卡给出 LOCATION。**
   每个 SSDP socket 都用自己的本地地址生成 `LOCATION`，HTTP 绑定 `0.0.0.0` 接收所有网卡。
   实测发现本机存在 `198.18.0.1`（VPN/隧道保留段），若被选中，局域网控制端无法访问其描述 URL；
   已在网卡选择中排除 `198.18.0.0/15`、`169.254.0.0/16`、`Tunnel` 类型，并优先以太网/Wi-Fi。
5. **命名管道缓冲必须显式设置。**
   `PipeOptions.Asynchronous` 的默认缓冲区很小；服务端推送快照与客户端下发命令同时发生时，
   两个方向都会被填满并互相等待，表现为 IPC 测试挂起。服务端统一使用
   `IpcProtocol.PipeBufferBytes`（256 KiB）；客户端无法指定缓冲区，因此由服务端决定。
6. **IPC 安全**：`PipeOptions.CurrentUserOnly`，只允许当前用户连接；消息带版本、请求 ID、
   4 字节长度前缀、64 KiB 上限与 30 s 读超时。

### 11.4 实测结果

见 `docs/testing.md` §2.3。要点：真实 SSDP 发现 → 描述 → `SetAVTransportURI` → `Play` →
`GetPositionInfo` 位置推进 → `Pause` → `Seek` → `Stop` 全部通过；服务窗口内确实渲染出视频画面；
UI 退出后服务继续工作，UI 重启后复用原服务。

### 11.5 尚未完成（阶段 3 交付时）

- 第三方控制端实测、GENA 事件、RenderingControl、字幕与多音轨。
- 服务作为 Windows 服务的安装与交互式会话问题（首版明确不采用）。

> 后续进展：第三方控制端实测、GENA 事件订阅与 RenderingControl 已在后续阶段完成，
> 见 §12 与 §14。字幕、多音轨与 Windows 服务模式仍未实现。
---

## 12. 局域网发现失败专项诊断（真实手机控制端）

### 12.1 现象

协议脚本（同一台机器上的 SSDP/SOAP/HTTP 探针）全部通过，但真实手机上的控制端搜不到设备：
BubbleUPnP 的设备列表为空，视频 App 的「投屏」列表里也没有 DlnRenderer。
同一台手机上 MiniCast 可以被发现，说明网络、防火墙与手机侧均正常。

### 12.2 证据链

抓包在本机不可行（本机发出的多播不会回到本机监听器），因此以**服务端行为日志 + 真实客户端回访**作为证据：

| 观测点 | 结论 |
| --- | --- |
| 手机发出 `M-SEARCH`（`ssdp:all`、`MediaRenderer:1`、`service:AVTransport:1` 等） | 服务**收到**，日志含来源、ST、MX |
| 服务应答 | 已向请求源的单播地址与端口发出 `HTTP/1.1 200 OK` |
| 手机回访 `GET /description.xml` | **多次**取到描述（HTTP 200、XML 合法） |
| 手机随后请求 SCPD / SOAP | **从不发生** → 说明客户端解析描述后判定「这不是可用渲染器」 |

即故障点在 **C/D 类（响应已发出，但内容不被接受）**，不在网络层。

### 12.3 五个根因（按影响排序）

1. **设备描述使用了 `d:` 前缀命名空间**（决定性原因）
   生成结果是 `<d:root><d:device><d:deviceType>`。这在 XML 上等价于默认命名空间，
   但部分投屏 App 的解析器按**元素名字符串**匹配，`d:device` 无法识别为 `device`。
   改为与 MiniCast 一致的默认命名空间写法（`<device>`，`xmlns=` 声明在 root）后，
   BubbleUPnP 与视频 App **立即可以发现并投屏**。

2. **只应答设备级搜索目标，不应答服务级目标**
   客户端的发现策略不同：BubbleUPnP 会搜 `urn:schemas-upnp-org:service:AVTransport:1`。
   原实现只认 `MediaRenderer:1` / `upnp:rootdevice` / `uuid` / `ssdp:all`，
   对服务级 ST 一律「not answered」。
   现应答全部 6 个目标（rootdevice、udn、设备类型、三个服务类型），与 MiniCast 的注册集合一致。

3. **`GetProtocolInfo` 只返回通配符 `http-get:*:*:*`**
   多数控制端在投屏前用该动作协商格式，通配符会被判为「不接受任何格式」，导致
   **设备可见但无法投送**。现采用格式声明清单（含 DLNA.ORG_PN），作为嵌入资源随程序分发。
   阶段 D 用自生成样片实测 libmpv 后，把清单从 392 条收紧到 277 条：移除了没有解码器的
   格式（RealMedia、MIDI、ATRAC3、QCELP）、DRM profile、占位值和无法显示的静态图片，
   并修正了把音频声明成 `video/*` 的错误条目。依据见 `docs/media-matrix.md`。

4. **SSDP 绑定到了每个网卡的精确地址**
   原实现为每块网卡各绑一个 `地址:1900`。Windows 对同一精确地址分桶，
   当 Windows 自身的 `SSDPSRV` 已占用该地址时，多播只会投递给桶内的某一个 socket。
   改为单个通配 socket `0.0.0.0:1900`（与 MiniCast 相同），并在每块可用网卡上加入多播组。

5. **缺少 `dlna:X_DLNADOC` 与 `Date` 响应头**
   前者是 DLNA 设备能力标记，后者是 RFC 7231 对源服务器的要求；两者都会让严格客户端
   判定响应不规范。

另外修复：

- SSDP 应答的 `ST` 现在**等于请求的 ST**（`ssdp:all` 除外），不再对任何搜索都回全部目标。
- 同一请求被多个网卡重复收到时按 250 ms 窗口去重（窗口过大会吞掉客户端的正常重试）。
- 多播发送显式绑定出口网卡（`IP_MULTICAST_IF` 需**网络字节序**的接口索引，
  传主机字节序会报 `AddressNotAvailable`）。
- 网卡排序优先有默认网关的接口，避免选中 Hyper-V/WSL 虚拟网卡作为 `LOCATION`。
- 服务默认使用固定端口 18090（而非每次随机），便于防火墙规则与客户端缓存。
- IPC 命名管道缓冲区显式设置，修复服务端推送与客户端请求并发时的双向写死锁。
- 无媒体时的 `Play`/`Pause`/`Seek` 明确失败，不再伪装成功。

### 12.4 回归测试

| 测试文件 | 覆盖内容 |
| --- | --- |
| `SsdpDiscoveryTests` | 服务级 ST 必须被应答、应答的 ST 等于请求 ST、`ssdp:all` 返回全部目标、无关目标不应答 |
| `DeviceDescriptionDiscoveryTests` | XML 声明、`X_DLNADOC`、相对服务 URL 及解析结果、三服务顺序、RenderingControl SCPD |
| `ConnectionManagerProtocolInfoTests` | sink 列表非通配、覆盖 mp4/mkv/mp3/flac/avi/ts、含 `DLNA.ORG_PN`、条目均为 http-get |

并保留原有 AVTransport / Player / IPC 测试。

### 12.5 验证方法（可复现）

```powershell
# 结构校验：逐项对照 UPnP 设备架构的必需元素
pwsh tools/control-point/validate-description.ps1 -Location "http://<本机LAN地址>:18090/description.xml"

# 观察客户端实际发了哪些 ST、是否取描述、发了哪些 SOAP 动作
#   （服务日志已记录：M-SEARCH received / ssdp answered / not answered / http GET / soap action）
```

### 12.6 尚未解决

- 手机端「系统投屏」（控制中心里的投屏）通常是 Miracast/Wi-Fi Direct，与 DLNA 不是同一套协议；
  本机 WLAN 处于断开状态（仅有以太网），因此该系统功能看不到设备属预期行为，与渲染器无关。
- 网络接口变化（拔插网线、Wi-Fi 切换）后不会重新加入多播组，需要重启服务。见 §14.7。
---

## 13. 投屏窗口：全屏与置顶

### 13.1 职责归属

窗口状态由服务进程独占：`VideoPresenter` 拥有顶层 HWND 与其消息循环，`VideoWindowPolicy` 决定「此刻该不该显示」。
WinUI 进程完全不参与——UI 关闭后窗口与播放继续工作，这一点由 `SessionWindowLifecycleTests` 与真实服务端到端验证覆盖。

```
mpv 状态事件 ──► VideoWindowPolicy（纯策略，可测）
DLNA/IPC 命令 ─►        │
                        ▼
              VideoPresenter（自带 STA 线程）
                 ├─ 显示器选择 FullscreenGeometry（纯几何，可测）
                 ├─ 窗口放置 SetWindowPos
                 └─ 置顶 TopmostWindow（唯一权威实现）
```

### 13.2 决策：mpv 不管理窗口

`fullscreen=no`、`ontop=no`、`border=no` 在初始化时显式固定。
若同时让 mpv 的 `fullscreen`/`ontop` 生效，两套系统会在每次尺寸变化时争夺同一窗口的几何与焦点。
**单一权威**：窗口布局只由 `VideoPresenter` 决定，mpv 只负责在给定的 `wid` 子窗口里绘制。

### 13.3 显示策略（`VideoWindowPolicy`）

| 状态 | 窗口 |
| --- | --- |
| `SetAVTransportURI`（libmpv 以 paused 打开） | 隐藏——未按播放前不抢占屏幕 |
| `Play` → Playing / Buffering / Transitioning | 全屏 + 置顶 |
| 用户 `Pause`（Play 之后） | 保持全屏 + 置顶 |
| `Seek` / 切换媒体 | 复用同一窗口，状态不变 |
| `Stop` / 播放自然结束 / 错误 | 隐藏并退出置顶 |

「paused」的两种含义必须区分：**预加载**（尚未 Play）与**用户暂停**。
由 `playbackStarted` 标记区分，仅在 `Play` 置位、`Stop` 清除。

### 13.4 全屏与置顶机制

- **矩形**来自 `EnumDisplayMonitors` 的整块显示器，而非工作区或固定 1920x1080；
  多显示器按窗口所在显示器选择，未拔插时保持原选择。
- **样式**：`WS_POPUP`（无标题栏/边框/阴影）+ `WS_EX_TOOLWINDOW`（不进 Alt+Tab，
  避免出现无法用键盘关掉的条目）。
- **置顶**：窗口可见之后调用 `SetWindowPos(HWND_TOPMOST)` 并**回读 `WS_EX_TOPMOST` 验证**。
  对隐藏窗口提前设置该样式会被系统丢弃，这是本轮定位到的真实缺陷。
- **焦点**：窗口当前不是前台时带 `SWP_NOACTIVATE`，不抢用户焦点；没有任何定时器轮询置顶。
- **显示器变化**：`MonitorEventsListener` 接收 `WM_DISPLAYCHANGE`/`WM_SETTINGCHANGE`，
  250 ms 去抖后重算矩形。
- **退出**：`Escape` 隐藏窗口（回到播放器外层），不影响 AVTransport 状态；
  其余按键（Alt+Tab、Win）一律交给 `DefWindowProc`。
- **隐藏**：先退出置顶再 `SW_HIDE` 并缩到 1x1。非前台进程调用 `ShowWindow(SW_HIDE)`
  可能只改内部标志而不清屏，缩窗能保证从屏幕上消失。
- **显示**：顶层与 mpv 子窗口必须一起恢复。`Hide` 隐藏的是子窗口（真正承载画面），
  因此 `ShowFullscreen` 先 `ShowWindow(child, SW_SHOW)` 再放置顶层，
  且幂等快速路径必须同时校验「顶层可见 && 子窗口可见 && 矩形正确」。
  只检查顶层时，Stop 之后再投屏会出现「有声音、无画面」——这是实际发生过的回归。
- **验证口径**：判断「是否有画面」不能只看顶层窗口的 `IsWindowVisible`，
  必须检查 mpv 子窗口的可见性与尺寸。

### 13.5 DPI

应用清单声明 `PerMonitorV2`。这是必需的而非可选：进程未声明 DPI 感知时，
2560x1440@150% 的显示器只会报告 1707x960，全屏窗口便只覆盖屏幕 2/3。
运行时调用无效——.NET 启动阶段已经创建过窗口，Windows 会拒绝之后的感知级别变更。

### 13.6 播放器侧的配套修复

1. 加载入口显式经过 `Stopped -> Transitioning`，并从 `Error` 恢复（状态机唯一合法出口）。
2. `loadfile` 之后强制发布一次状态：重播同一媒体时状态对象与上次完全相同，去重会吞掉 Transitioning 边沿。
3. 按 libmpv 的 playlist entry 编号丢弃过期 end-file：Stop 与新加载交错时，旧文件的结束事件不得改写新文件的状态。
4. `FILE_LOADED` 是「该文件现已加载」的权威事件，同时清除 `loadPending`。
5. 重载带 3 次有界重试，失败时抛出明确错误（`Play` 不得在无法播放时返回成功）。
6. 服务把 mpv 日志转发到服务日志：被拒绝的文件从此有可诊断的原因。

上述改动均有回归测试；`SessionWindowLifecycleTests` 专门覆盖快速 Play/Stop/重播序列。
---

## 14. GENA 事件订阅与 RenderingControl

### 14.1 结构

```
控制端 ──SUBSCRIBE/UNSUBSCRIBE──► DlnaDeviceHost ──► GenaEventing
                                                      ├─ GenaRequestParser（头校验、超时钳制）
                                                      ├─ GenaCallbackPolicy（SSRF 防护）
                                                      ├─ GenaSubscriptionRegistry（生命周期、配额）
                                                      └─ GenaNotifier（有界投递）
RendererSession ──TransportEventRaised/RenderingEventRaised──► GenaEventing ──► NOTIFY
```

事件源与 IPC 快照、UI 显示来自**同一个** `PlaybackStatus`，因此三者不会给出互相矛盾的状态。

### 14.2 事件变量

| 服务 | 事件化变量 | 说明 |
| --- | --- | --- |
| AVTransport | `LastChange` | AV 规范要求的唯一事件变量；`TransportState`、`TransportStatus`、`CurrentTrackURI`、`CurrentTrackDuration`、`CurrentTransportActions` 位于其内层转义 XML |
| RenderingControl | `LastChange` | 含 `Volume`、`Mute`，均带 `channel="Master"` |
| ConnectionManager | 无 | 本渲染器没有需要事件化的连接管理状态 |

SCPD 中只有 `LastChange` 标记 `sendEvents="yes"`，其余变量保持 `no`——这与 AV 规范一致，
也避免客户端收到它无法解析的裸变量通知。

### 14.3 协议语义

- **SEQ**：每个订阅独立计数，初始事件为 0，此后每次 +1。多订阅者不共享计数器，否则会看到缺号。
- **初始事件**：订阅成功后立即发送，包含全部事件化变量的当前值。
- **状态码**：缺/错头 → `412`；`SID` 与 `CALLBACK`/`NT` 混用 → `400`；未知或过期 `SID` → `412`；
  超出配额 → `500`；非 SUBSCRIBE/UNSUBSCRIBE 方法 → `405`。
- **续订**：保留 SID 与 SEQ，只更新截止时间；过期订阅不可复活。
- **位置不推送**：AV 规范期望客户端轮询 `GetPositionInfo`；每秒推送会让每个订阅者收到
  4 次 HTTP 请求，而控制端本来就已轮询该值。

### 14.4 回调安全（SSRF）

`GenaCallbackPolicy` 只接受：

- `http` scheme（拒绝 https：没有为该客户端配置信任；拒绝 file/ftp 等任何非 HTTP 操作）；
- 无凭据的 URL；
- **IP 字面量**而非域名（不对攻击者提供的名称做 DNS 解析）；
- 拒绝 loopback、未指定地址、组播/广播、以及 `169.254.0.0/16`（云元数据地址）；
  IPv4-mapped IPv6 会先被还原为 IPv4 再判定，避免绕过；
- 私有网段**默认允许**：真实控制端就是局域网内的手机，若按「同子网」限制会切断经第二个网卡
  或 VPN 接入的客户端。可用 `GenaOptions.AllowPrivateNetworkCallbacks` 收紧。
- 不跟随重定向、不走系统代理、限制响应体大小。

### 14.5 投递与故障隔离

- 发布是 fire-and-forget：播放/暂停/SOAP 路径**不会等待**任何回调。
- 并发闸门限制在途 NOTIFY 数量；连接超时与总超时分开设置。
- 每个订阅独立记录连续失败次数，达到上限即丢弃该订阅；一个客户端失败不影响其他订阅者。
- 服务关闭时先关闭 HTTP 端点，再取消在途投递，避免通知比服务活得久。

### 14.6 本轮修复的缺陷

1. **`<m:Name>` 构建失败**：`XName` 不允许本地名含冒号，事件体构建抛异常导致所有通知丢失。
   改为用事件命名空间 + 本地名创建元素。
2. **并发命令乱序**：`MpvPlayer` 每个命令各自 `Task.Run`，同一属性的并发写入可能后发先至。
   改为取号排队，使「最后调用者胜出」成立。
3. **`SetVolume` 返回时值尚不可读**：libmpv 异步应用属性并回传事件；现在等待值可观测后再返回，
   使「设置后立即读取」成立。
4. **`DesiredMute` 接受了 `yes`/`no`**：UPnP 布尔只允许 `0/1`、`false/true`，
   宽松解析会发明客户端没发过的值。
5. **投递闸门满时静默跳过**：改为计入失败预算，避免僵死回调永久占用配额。
6. **过期清理周期可能长于超时**：改为不超过最短可授予超时的一半。

### 14.7 尚未解决

- **真实第三方控制端未验证**：GENA 与 RenderingControl 只有自动化与本机回调验证，
  从未被真实客户端订阅或调用过。见 `docs/testing.md` §5.3。
- **网络接口变化**：`SsdpServer` 不监听 `NetworkAddressChanged`，拔插网线或切换 Wi-Fi 后
  不会重新加入多播组，需要重启服务。
- **日志无轮换**：`ServiceLog` 没有保留策略，长时间常驻会无限增长。
- **无系统托盘**：关闭主窗口即退出 UI；后台服务继续运行但没有入口重新打开或退出整个应用。
- **设置不持久化**：设备名、端口等只能通过命令行参数指定，重启后回到默认值。
---

## 15. 媒体能力边界（阶段 D）

协议层原来照抄 392 条 `SinkProtocolInfo`，等于向控制端承诺「这些都能投」。阶段 D 用
ffmpeg 自生成样片逐个实测 libmpv，把承诺改成了事实。

### 15.1 测量方式

- 样片：`tools/media-compat/make-fixtures.ps1` 生成 22 个（含 2 个负样本），不入库。
- 播放：`MediaCompatibilityTests` 通过 libmpv 真实加载、播放并观察位置推进；
  `invalid-*` / `unsupported-*` 必须失败，其余必须播放——失败即测试红，不会静默写进文档。
- 渲染：`tools/media-compat/measure-media.ps1` 跑测试并生成 `docs/media-matrix.md`。

### 15.2 声明的收缩（392 → 277）

| 移除类别 | 依据 |
| --- | --- |
| 无解码器格式（RealMedia、MIDI、ATRAC3、QCELP） | MIDI 用手工 SMF 实测 `unrecognized file format`；其余 libavcodec 无解码器 |
| DRM profile（`WMDRM_*`） | 渲染器没有解密能力，声明即虚假承诺 |
| 占位值（`video/unknown` 等） | 从别的设备抄来的条目 |
| 静态图片（`image/*` 60 余条） | 无幻灯片功能，mpv 显示一帧即结束，看起来像投屏失败 |
| 音频写成 `video/*`（`video/mp3` 等） | 声明本身错误，控制端会按错误 MIME 投送 |

`SinkProtocolInfoTests` 把这些类别钉住：下次从别的设备抄清单时，回流会让测试失败。

### 15.3 硬件解码

`MpvPlayer.CurrentHardwareDecodingMethod` 读 mpv 的 `hwdec-current`，反映**实际**解码方式：
H.264/VP9/HEVC 10bit/AV1 → `d3d11va-copy`，VP8 → `nvdec-copy`，MPEG-2/MPEG-4 → 软件。
显式请求不存在的 `vaapi` 时 mpv 自动回退软解并正常播放，不会让文件失败。

### 15.4 字幕与多音轨（D4/D5，决定延后）

样片 `video-h264-dualaudio-subrip.mkv`（双音轨 + 内嵌字幕）可播，说明引擎侧无障碍；
但主流控制端只投单个媒体 URI，不会提供字幕 URI，AVTransport 1.0 也没有音轨选择动作。
在没有控制端会发这些请求的前提下实现接口只会得到死代码，因此延后，
重新评估条件记录在 `ROADMAP.md` 阶段 D。

---

## 16. 分发与运行形态（阶段 E）

### 16.1 决定

两条渠道并存，共用同一套 App/Service/libmpv 产物，**服务都是 UI 在用户会话里拉起的后台子进程**：

| 渠道 | 安装方式 | 服务如何运行 |
| --- | --- | --- |
| 绿色包 | `install.ps1` 装到 `%ProgramFiles%\DlnRenderer`（管理员） | UI 拉起；托盘「退出应用」时停；关窗后继续运行 |
| MSIX（用户级） | `winapp cert install`（一次性）+ `Add-AppxPackage`（当前用户，不提权） | 同上 |

完整论证见 `docs/packaging.md`，MSIX 逐项可行性见 `docs/msix-assessment.md`。

**MSIX 不注册 Windows 服务。** 曾实测 `desktop7:Service`（SCM 托管，`LocalSystem`）：起停、自启、
卸载清理都正常，但服务落在 **session 0**，没有用户桌面，libmpv 报
`Failed to create swapchain (0x887a0022)`，投屏没有画面。"投屏出画面"是产品的核心，所以放弃；
同时 `windows.service` 需要机器级受限能力（`packagedServices` / `localSystemServices`），
与"用户级 MSIX"互斥。`build-msix.ps1` 的解包回验会断言清单里**没有**这些声明。

### 16.2 进程拓扑（安装后）

```text
<安装目录>\                    绿色包: %ProgramFiles%\DlnRenderer\
│                              MSIX:   %ProgramFiles%\WindowsApps\DlnRenderer_<版本>_x64__<发布者ID>\
├── DlnRenderer.exe            WinUI 3 UI：窗口、托盘、设置页
├── DlnRenderer.Service.exe    后台服务：SSDP、HTTP/SOAP、GENA、libmpv、IPC 服务端
├── libmpv-2.dll               只有服务进程加载它
├── Assets\                    窗口标题栏图标与托盘图标（运行时从磁盘加载）
└── *.dll                      WinUI / Windows App SDK 依赖
```

UI 启动时按 `ServiceClient.ResolveServicePath()` 的优先级查找服务：**先看自己所在目录**
（安装后的布局），找不到再回退到开发布局。两条渠道因此行为一致，无需额外配置。

两种渠道的差异只在这几处：

| 项 | 绿色包 | MSIX（用户级） |
| --- | --- | --- |
| 开机启动 | HKCU Run | 清单 `windows.startupTask`，设置页经 `StartupTask` API 开关（`StartupRegistration` 按是否有包标识分流） |
| 框架依赖 | 进程自己引导 Windows App Runtime | 清单必须声明 `Microsoft.WindowsAppRuntime.2`，否则 UI 启动即以 `REGDB_E_CLASSNOTREG` 崩溃 |
| 数据目录 | `%LOCALAPPDATA%\DlnRenderer` | 同左（实测未被虚拟化） |
| 受限能力 | 无 | `runFullTrust` |

`ServiceStatusReporter`（`StartServiceCtrlDispatcher` + `SERVICE_RUNNING` / `SERVICE_STOPPED` 回报）
是为 SCM 托管写的；用户级路径上它检测到"不是 SCM 启动"就不介入，但对被 SCM 启动的场景
（如用户自行 `sc create`）仍然正确，并有 `ServiceLaunchDetectorTests` 覆盖。
`ServiceLog.DataDirectory` 同理：session 0 才改用 `%ProgramData%`，用户会话走 `%LOCALAPPDATA%`。


### 16.3 端口与防火墙

固定 TCP 端口 **18090**（`ServiceStartupOptions.DefaultHttpPort`）+ UDP 1900。固定端口是
防火墙规则、控制端缓存与书签能跨重启生效的前提。`firewall-rules.ps1` 只对专用/域网络放行，
公网配置文件下不开放。

### 16.4 停止顺序（为什么不是 kill）

卸载/升级先通过 IPC `Shutdown` 请求服务退出，与托盘「退出应用」是同一条路径。硬杀会留下
两个问题：SSDP socket 进入 `TIME_WAIT` 导致新装的副本短时间内无法重新加入多播组；视频窗口
与 named pipe 由服务自己持有，异常退出不会释放干净。因此脚本顺序是：
IPC 请求 → 等待退出 → 仅在仍存活时 `Stop-Process` → 进程消失后才动文件（否则 DLL 被锁）。
