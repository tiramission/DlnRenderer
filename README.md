# DlnRenderer

面向 Windows 10/11 的开源 DLNA Media Renderer（媒体渲染器），使用 WinUI 3 构建原生界面。

> 当前状态：**阶段 1.5 —— 本地播放引擎已可用**。
> 可以从界面或命令行打开本地音视频并播放，**尚不能接收 DLNA 投屏**。
> 因 libmpv 许可证尚未定案，**当前产物不可对外分发**（见下文“许可证”）。

## 已实现

- WinUI 3（Windows App SDK 2.5.1 / .NET 10）原生窗口与播放页面
- libmpv 播放引擎封装：打开本地文件、播放/暂停/停止、进度查询、Seek、音量、静音
- 音频与视频区分（依据实际轨道列表，而非控制端声明）
- 视频通过 Win32 子窗口渲染，支持窗口缩放
- 播放状态统一由 `PlaybackStateMachine` 守卫，进度事件按 250 ms 节流
- 关闭窗口时释放 native 资源；播放失败显示明确错误
- 单元测试与原生集成测试（40 个）

## 尚未实现

DLNA 协议（SSDP/AVTransport/RenderingControl）、后台服务与 IPC、系统托盘、设置页、字幕/多音轨、安装包。

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

```powershell
winapp run src/DlnRenderer.App -c Debug
winapp run src/DlnRenderer.App -c Debug -- --play "C:\path\video.mp4"
```

清理开发包：

```powershell
winapp unregister --manifest src/DlnRenderer.App/Package.appxmanifest
```

## 目录结构

```text
src/DlnRenderer.App/        WinUI 3 UI、播放器视图模型
src/DlnRenderer.Core/       领域模型、播放状态机、IMediaPlayer
src/DlnRenderer.Player/     libmpv 互操作、状态映射、Win32 视频子窗口
tests/DlnRenderer.Core.Tests/
tests/DlnRenderer.Player.Tests/
scripts/fetch-libmpv.ps1
docs/
```

`DlnRenderer.Dlna` / `DlnRenderer.Service` 将在有真实协议/IPC 实现时创建，而不是先建立空项目。

## 许可证

项目整体许可证**尚未确定**。当前开发所用的 libmpv 二进制经实测为 **GPL-2.0-or-later** 构建，
因此它只用于本地验证，不随源码或安装包分发，本版本也不得标记为可发布。

可选的后续路线（详见 `docs/architecture.md` §10.1）：

1. 改用 LGPL 构建（官方 `-lgpl` 产物或自行 `-Dgpl=false` 编译）；
2. 明确接受 GPL-2.0-or-later，并准备完整的源码与通知文件。

注意：NuGet 上有包声称 LGPL，但实测其 DLL 仍为 GPL 构建，不能依据包元数据判断。
