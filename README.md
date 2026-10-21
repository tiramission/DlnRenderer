# DlnRenderer

面向 Windows 10/11 的开源 DLNA Media Renderer（媒体渲染器），使用 WinUI 3 构建原生界面。

> 当前状态：**DLNA 投屏链路可用，协议层完整**。设备发现、投屏播放、全屏置顶窗口、
> GENA 事件订阅与音量/静音控制均已实现，并有自动化测试覆盖。
> **但尚未达到可对外分发的状态**：没有安装包、开机启动未实测、libmpv 许可证未定案（见下文）。

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
# 391 通过 / 0 失败 / 0 跳过
# Core 43、Player 20、Dlna 218、Service 110
```

协议层另有真实服务端到端脚本，见 `tools/control-point/`。

## 尚未实现

| 项 | 说明 |
| --- | --- |
| **开机启动** | 设置页有开关，但目前写入 HKCU Run 指向构建目录的 exe；打包形态定案后需要 StartupTask 并实测 |
| **安装包** | 无 `packaging/`；`Package.appxmanifest` 的 Publisher 仍是模板占位值 |
| **字幕与多音轨** | 播放器层无字幕加载与音轨选择接口 |
| **播放队列** | 单媒体模型，`NextURI` 恒为空 |
| **网络接口变化** | 拔插网线或切换 Wi-Fi 后不会重新加入多播组，需要重启服务 |
| **日志轮换** | 服务日志无保留策略 |

## 已知限制

- **只支持 x64**：libmpv 二进制仅提供 x64，ARM64 未验证。
- **只在 Windows 11 build 26300 实测**：`TargetPlatformMinVersion` 声明为 17763（Windows 10 1809），
  但 Windows 10 未实测。
- **真实第三方控制端未完成本轮回归**：GENA 与 RenderingControl 只有自动化与本机回调验证，
  从未被真实客户端订阅或调用。详见 [docs/testing.md](docs/testing.md) §5.3。
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
pwsh scripts/fetch-libmpv.ps1
```

`libmpv-2.dll` 不随仓库提供（GPL 构建，仅限本地开发）。脚本会校验哈希并记录来源。

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
- [docs/licenses.md](docs/licenses.md) —— 依赖与许可证清单

## 许可证

项目整体许可证尚未确定。当前使用的 libmpv Windows 二进制为 **GPL-2.0-or-later** 构建，
仅存放于被忽略的本地目录、不随仓库或安装包分发。仓库尚未包含 `THIRD-PARTY-NOTICES`。
分发前必须完成 [docs/licenses.md](docs/licenses.md) §4 列出的全部事项。