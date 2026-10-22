# DlnRenderer

面向 Windows 10/11 的开源 DLNA Media Renderer（媒体渲染器），使用 WinUI 3 构建原生界面。

> 当前状态：**DLNA 投屏链路可用，协议层完整**。设备发现、投屏播放、全屏置顶窗口、
> GENA 事件订阅与音量/静音控制均已实现，并有自动化测试覆盖。
> 分发渠道有两条（绿色包 + 用户级 MSIX），安装/升级/卸载与 MSIX 下的投送链路已在本机实测；
> **但尚未达到可对外分发的状态**：干净机器未验证、未代码签名（见下文）。
> 许可证已定案（阶段 F）：**GPL-2.0-or-later**，法律文件随包分发，见 `LICENSE` 与
> [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)。

[![CI](https://github.com/tiramission/DlnRenderer/actions/workflows/ci.yml/badge.svg)](https://github.com/tiramission/DlnRenderer/actions/workflows/ci.yml) [![Release](https://github.com/tiramission/DlnRenderer/actions/workflows/release.yml/badge.svg)](https://github.com/tiramission/DlnRenderer/actions/workflows/release.yml)

## 已实现

### 界面（`DlnRenderer.App`）

- WinUI 3 原生窗口（Windows App SDK 2.5.1 / .NET 10），Mica 背景与自绘标题栏
- 显示服务连接状态、当前媒体、播放进度与错误提示
- 播放 / 暂停 / 停止、进度拖动、音量滑块、静音开关
- 关闭窗口不影响后台播放；重新打开会复用已有服务进程；服务断开后自动重连
- 系统托盘：双击打开主窗口，右键菜单含打开 / 当前状态 / 退出整个应用
- 设置页：设备名、开机启动、关闭窗口行为、硬件解码；保存到 `%LOCALAPPDATA%\DlnRenderer\settings.json`，服务启动时读取（命令行参数优先）
- 主页 / 设置导航（`NavigationView`），控件带无障碍名称

### 后台服务（`DlnRenderer.Service.exe`）

- 独立进程：SSDP 发现、HTTP/SOAP 控制、GENA 事件、libmpv 播放、Named Pipe IPC 服务端
- 视频在全屏置顶窗口中渲染，支持多显示器、DPI 感知、`Escape` 退出全屏
- 仅当前用户可连接的 Named Pipe（`PipeOptions.CurrentUserOnly`）

### 协议覆盖

| 服务 | 已实现 |
| --- | --- |
| SSDP | M-SEARCH 应答（6 个目标全应答）、`ssdp:alive` / `ssdp:byebye` 公告 |
| 设备描述 / SCPD | 默认命名空间、相对服务 URL、`dlna:X_DLNADOC` |
| AVTransport | `SetAVTransportURI`、`Play`、`Pause`、`Stop`、`Seek`、`GetTransportInfo`、`GetPositionInfo`、`GetMediaInfo`、`GetDeviceCapabilities`、`GetTransportSettings`、`GetCurrentTransportActions` |
| ConnectionManager | `GetProtocolInfo`（277 条格式声明，阶段 D 按 libmpv 实测收紧，见 [docs/media-matrix.md](docs/media-matrix.md)）、`GetCurrentConnectionIDs`、`GetCurrentConnectionInfo` |
| RenderingControl | `GetVolume`、`SetVolume`、`GetMute`、`SetMute`、`ListPresets`、`SelectPreset` |
| GENA | `SUBSCRIBE` / 续订 / `UNSUBSCRIBE`，`LastChange` 事件（AVTransport 与 RenderingControl） |

音量与静音作用于**播放器自身**，不修改 Windows 主音量。

### 测试

```text
dotnet test DlnRenderer.sln -c Debug
# 403 通过 / 0 失败 / 0 跳过
# Core 43、Player 23、Dlna 222、Service 115
```

CI（GitHub Actions，push/PR 触发，见 [.github/workflows/ci.yml](.github/workflows/ci.yml)）在干净
runner 上拉取评审过的 libmpv（双哈希校验）、安装 ffmpeg 后跑同一套：402 通过 / 0 失败 /
0 跳过（另 1 个硬解测试因 runner 无 GPU 按名排除）。环境事实与迭代记录见
[`docs/testing.md`](docs/testing.md) §12。

协议层另有真实服务端到端脚本，见 `tools/control-point/`；
媒体兼容性实测见 `tools/media-compat/`；安装/卸载脚本见 `tools/package/`。

### 下载与安装（发布版）

从 [Releases](https://github.com/tiramission/DlnRenderer/releases) 下载，两种方式任选：

**绿色包**

1. 解压 `DlnRenderer-<版本>-win-x64.zip`
2. 以管理员 PowerShell 运行 `install.ps1`（复制到 Program Files、添加防火墙规则、创建快捷方式）
3. 启动 `DlnRenderer.exe`，在局域网内的 DLNA 控制端里选这台设备投屏

**MSIX（用户级，安装本身不需要管理员）**

```powershell
# 一次性：信任发布证书（需要管理员）
Import-Certificate -FilePath .\DlnRenderer-dev.cer -CertStoreLocation Cert:\LocalMachine\Root
# 安装（当前用户）
Add-AppxPackage -Path .\DlnRenderer-<版本>-win-x64.msix
```

证书固定不变，**信任一次即可**，之后每个版本直接在上一版上覆盖升级。

> ⚠️ 该证书的私钥公开在仓库中，所以它只代表身份标签（`CN=AppPublisher`），**不代表来源**。
> 验证来源请核对发布页的 `SHA256SUMS-assets.txt` 或 GitHub 显示的 asset digest。
> 正式 CA 签名为阶段 F5 的遗留项，见 [docs/packaging.md](docs/packaging.md) §5。

前置条件：Windows 10 1809+ / Windows 11（x64）、.NET 10 桌面运行时；MSIX 还需
Windows App Runtime 2.5.1+（清单已声明，缺失时安装器会直接报错而不是装完再崩）。

### 自行打包

```powershell
pwsh tools/package/build-dist.ps1   # 绿色包 → dist/DlnRenderer
pwsh tools/package/build-msix.ps1   # MSIX → dist/msix/DlnRenderer-x64.msix
```

两条渠道共用同一套发布产物。MSIX 需要 `winapp` CLI 与 Windows Kits 的 `makeappx.exe`
（用于打包后解包回验）。安装 MSIX 前需一次性信任 dev 证书（需管理员）：
`winapp cert install tools/package/devcert.pfx` 或用 Release 里的 `DlnRenderer-dev.cer`。证书在仓库中固定，
**只信任一次即可**，后续版本升级无需重做；但它的私钥是公开的，所以只代表身份标签，
**不代表来源**——来源请核对 `SHA256SUMS-assets.txt`。渠道差异见 [docs/packaging.md](docs/packaging.md)。

## 尚未实现

| 项 | 说明 |
| --- | --- |
| **开机启动** | 设置页开关已接入：未打包走 HKCU Run（安装形态已实测，阶段 E），打包（MSIX）走 `StartupTask` API（仅验证到编译，**未做注销/重启实测**）；"登录后自动启动并可直接投屏"尚未做完整重启验证 |
| **安装包** | 有绿色包 + 安装脚本与**用户级 MSIX 包**（`tools/package/`，见 [docs/packaging.md](docs/packaging.md)）。MSIX 以当前用户安装、不注册 Windows 服务，服务由 UI 在用户会话里拉起；CI 已产出并在本机验证安装与原地升级（`docs/packaging.md` §7.5）。**仍未经干净机器验证**，MSIX 的开机启动（`StartupTask`）未做重启实测，Publisher 仍是 dev 证书值而非 CA |
| **字幕与多音轨** | 播放器层无字幕加载与音轨选择接口 |
| **播放队列** | 单媒体模型，`NextURI` 恒为空 |
| **网络接口变化的真实拔插验证** | 已监听 `NetworkAddressChanged` 并增量增删组播成员（单元测试覆盖），但没有真实拔插网线/切换 Wi-Fi 的验证记录 |
| **MSIX 渠道的防火墙入站规则** | 清单能力不创建防火墙规则，用户级安装也没有提权钩子；需手动运行 `tools/package/firewall-rules.ps1`，UI 引导未实现 |
| **升级后需重启后台服务** | 界面与后台服务是两个可执行文件，必须同版本：旧服务会忽略新字段、拒绝新版号，表现为"修好的问题又出现"。升级后若界面提示"版本不一致"，在设置页点"重启后台服务"（见 [docs/ipc-protocol.md](docs/ipc-protocol.md) §6） |

## 已知限制

- **只支持 x64**：libmpv 二进制仅提供 x64，ARM64 未验证。
- **只在 Windows 11 build 26300 实测**：`TargetPlatformMinVersion` 声明为 17763（Windows 10 1809），
  但 Windows 10 未实测。
- **真实控制端的事件订阅仍未通过**：真实客户端能发现、取描述/SCPD、投送、暂停、Seek、停止
  （[docs/testing.md](docs/testing.md) §7.2），但 `SUBSCRIBE` 被接受后初始 `NOTIFY` 返回 HTTP 412 /
  超时，没有任何一次 2xx；音量/静音也没有真实客户端调用记录。GENA 目前只有本机回调与
  `verify-eventing.ps1`（22 项）的验证。
- **手机「系统投屏」不适用**：控制中心里的投屏通常是 Miracast/Wi-Fi Direct，与 DLNA 无关。
  DLNA 投送请用视频 App 内置的「投屏」或 BubbleUPnP 这类控制端。

## 环境要求

- Windows 10 1809 (build 17763)+，实测 Windows 11 build 26300
- .NET SDK 10.0.4xx（`global.json` 固定 10.0.402，允许向上滚动）
- Windows SDK 10.0.28000.0（通过 NuGet 提供，无需 Visual Studio）
- `winapp` CLI 0.7.1
- 可选：Windows App Runtime 2.5.1（运行时）

## 首次准备

```powershell
pwsh scripts/fetch-libmpv.ps1            # 已有 Scoop mpv-dev 时直接复制
pwsh scripts/fetch-libmpv.ps1 -Download  # 没有本地副本时拉取锁定的上游归档
```

`libmpv-2.dll` 不入库（GPL-2.0-or-later 二进制），但**随分发包提供**。脚本校验两层 SHA-256
（归档与 DLL）并写出 `libmpv.provenance.json` 记录来源。

## 构建与测试

```powershell
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

解决方案的 `Any CPU` 配置映射到 **x64**。

## 运行

界面（会自动启动后台服务）：

```powershell
winapp run src/DlnRenderer.App -c Debug
```

只运行后台服务（无界面，仍可被 DLNA 控制端发现）：

```powershell
src\DlnRenderer.Service\bin\Debug\net10.0-windows\DlnRenderer.Service.exe
```

服务参数：`--http-port`、`--no-ssdp`、`--device-name`、`--listen-address`、`--log`、
`--no-hwdec`、`--exit-after`。

清理开发包：

```powershell
winapp unregister --manifest src/DlnRenderer.App/Package.appxmanifest
```

## 验证脚本

`tools/control-point/` 下均为真实协议验证，不依赖公网：

| 脚本 | 用途 |
| --- | --- |
| `ssdp-discover.ps1` | 观察 SSDP 应答 |
| `ssdp-requests.ps1` | 观察控制端实际发出的搜索目标 |
| `validate-description.ps1` | 逐项校验设备描述结构 |
| `renderer-check.ps1` | 走一遍 SOAP 控制链路 |
| `verify-fullscreen.ps1` | 人工确认全屏置顶窗口行为 |
| `verify-fullscreen-e2e.ps1` | 自动化验证窗口生命周期（26 项） |
| `verify-eventing.ps1` | 自动化验证 GENA 与音量/静音（22 项） |
| `ipc-check.ps1` | 检查 Named Pipe 连接与命令 |
| `media-server.js` / `capture-window.ps1` | 本地媒体源与窗口截图 |

## 目录结构

```text
src/DlnRenderer.App/        WinUI 3 UI（只做状态展示与 IPC 控制）
src/DlnRenderer.Core/       领域模型、播放状态机、IPC 契约
src/DlnRenderer.Dlna/       SSDP、HTTP、SOAP、AVTransport、ConnectionManager、
                            RenderingControl、GENA、DIDL 解析
src/DlnRenderer.Player/     libmpv 互操作与状态映射
src/DlnRenderer.Service/    后台服务：播放会话、DLNA 宿主、IPC 服务端、视频窗口
tests/                      Core / Player / Dlna / Service 测试
tools/control-point/        真实协议验证脚本
docs/                       架构、开发、测试与许可证文档
```

## 文档

- [ROADMAP.md](ROADMAP.md) —— 当前阶段判断、优先级与阶段计划
- [docs/architecture.md](docs/architecture.md) —— 架构决策记录与已知限制
- [docs/development.md](docs/development.md) —— 开发步骤
- [docs/testing.md](docs/testing.md) —— 自动化测试与真实设备验收记录
- [docs/packaging.md](docs/packaging.md) —— 分发渠道决策与两条渠道的差异
- [docs/msix-assessment.md](docs/msix-assessment.md) —— MSIX 逐项可行性评估
- [docs/licenses.md](docs/licenses.md) —— 依赖与许可证清单
- [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) —— 随包第三方组件声明

## 许可证

本项目采用 **GPL-2.0-or-later**，全文见 [`LICENSE`](LICENSE)。选择与 libmpv 的许可证一致，
理由见 [docs/architecture.md](docs/architecture.md) §17。

分发包内含 `LICENSE`、`LICENSES/`（第三方许可证全文）与 [`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md)，
由 `tools/package/stage-legal-files.ps1` 在打包时写入。核对 libmpv 的实际构建与哈希：

```powershell
pwsh tools/verify-libmpv.ps1   # 打印版本、构建配置、许可证判定与 SHA-256
```