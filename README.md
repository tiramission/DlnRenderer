# DlnRenderer

面向 Windows 10/11 的开源 DLNA Media Renderer（媒体渲染器），使用 WinUI 3 构建原生界面。

> 当前状态：**阶段 3（DLNA MVP）已打通**。手机/电脑上的 DLNA 控制端可以发现本机、推送视频或音乐，
> 播放、暂停、Seek、停止在真实链路上均已验证。
> 因 libmpv 许可证尚未定案，**当前产物不可对外分发**（见下文“许可证”）。

## 已实现

- WinUI 3（Windows App SDK 2.5.1 / .NET 10）原生窗口，界面显示服务连接状态与当前媒体
- 独立的 Renderer 服务进程：`DlnRenderer.Service.exe`
  - SSDP 发现（M-SEARCH 应答、`ssdp:alive` / `ssdp:byebye` 公告）
  - 设备描述、AVTransport / ConnectionManager 的 SCPD
  - SOAP 控制：`SetAVTransportURI`、`Play`、`Pause`、`Stop`、`Seek`、`GetTransportInfo`、`GetPositionInfo`、`GetMediaInfo`
  - libmpv 播放引擎，视频在服务自己的原生窗口中渲染
  - Named Pipes IPC（仅当前用户可访问），向 UI 推送状态、接收播放控制
- 前端与后端分离：关闭 UI 不会停止播放或发现；重新打开 UI 会复用已有的服务进程
- 单元测试与集成测试：163 个（Core 25、Player 15、Dlna 110、Service 13）

## 尚未实现

RenderingControl（音量的 DLNA 侧）、GENA 事件订阅（`SUBSCRIBE` 目前返回 501）、
字幕与多音轨选择、系统托盘、设置页、安装包。

架构与决策记录见 [docs/architecture.md](docs/architecture.md)，
开发步骤见 [docs/development.md](docs/development.md)，
测试与人工验收见 [docs/testing.md](docs/testing.md)，
依赖与许可证见 [docs/licenses.md](docs/licenses.md)。

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
dotnet restore DlnRenderer.sln
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

服务参数见 `DlnRenderer.Service.exe --help` 等价说明（`--http-port`、`--no-ssdp`、`--device-name`、
`--listen-address`、`--log`、`--no-hwdec`）。

清理开发包：

```powershell
winapp unregister --manifest src/DlnRenderer.App/Package.appxmanifest
```

## 目录结构

```text
src/DlnRenderer.App/        WinUI 3 UI（只做状态展示与 IPC 控制）
src/DlnRenderer.Core/       领域模型、播放状态机、IPC 契约
src/DlnRenderer.Dlna/       SSDP、HTTP、SOAP、AVTransport、ConnectionManager、DIDL 解析
src/DlnRenderer.Player/     libmpv 互操作与状态映射
src/DlnRenderer.Service/    后台服务：播放会话、DLNA 宿主、IPC 服务端、视频窗口
tests/                      Core / Player / Dlna / Service 测试
tools/control-point/        真实协议验证脚本（SSDP 发现、SOAP 控制、媒体源、IPC）
docs/
```

## 许可证

项目整体许可证尚未确定。当前使用的 libmpv Windows 二进制为 **GPL-2.0-or-later** 构建，
仅存放于被忽略的本地目录、不随仓库或安装包分发。详见 [docs/architecture.md](docs/architecture.md) 与
[docs/licenses.md](docs/licenses.md)。
