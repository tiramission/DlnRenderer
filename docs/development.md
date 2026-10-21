# 开发指南

本文件中的命令均在本仓库实际执行过（Windows 11 build 26300，.NET SDK 10.0.402）。

## 1. 环境要求

| 组件 | 版本 | 说明 |
| --- | --- | --- |
| Windows | 10 1809+ / 11 | 实测环境为 Windows 11 build 26300 |
| .NET SDK | 10.0.4xx | `global.json` 固定 10.0.402，允许滚动到更高 feature band |
| Windows SDK | 10.0.28000.0 | 通过 NuGet 的 SDK BuildTools 提供，无需安装 Visual Studio |
| winapp CLI | 0.7.1 | 创建/运行 Windows 应用、注册开发包 |
| 音频设备 | 可选 | 无设备时播放器仍可运行，但听不到声音 |

不需要安装 Visual Studio 或 VS Build Tools。

## 2. 首次准备：获取 libmpv

`libmpv-2.dll` 是原生依赖，**不在仓库中**（GPL 构建，仅限本地开发，见 `docs/architecture.md` §10.1）。

```powershell
pwsh scripts/fetch-libmpv.ps1
```

脚本会：

1. 从本机 Scoop 的 `mpv-dev` 包复制 `libmpv-2.dll` 到 `src/DlnRenderer.Player/native/x64/`。
2. 校验 SHA-256（默认 `53513b8f...`，可用 `-ExpectedSha256` 指定其他构建）。
3. 写出 `libmpv.provenance.json`（来源、哈希、时间）。

其他来源的构建需先用 `-SkipHashCheck` 复制，并**自行确认许可证**：只有 `mpv-configuration`
包含 `-Dgpl=false` 的构建才是 LGPL。

缺少 DLL 时构建仍会成功，但会出现一条警告，运行时会明确报“找不到 libmpv-2.dll”。

## 3. 构建与测试

```powershell
dotnet restore DlnRenderer.sln
dotnet restore DlnRenderer.sln --locked-mode   # 验证锁文件未漂移
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

预期结果（本轮实测）：构建 0 警告 / 0 错误，测试 **351 通过 / 0 失败**（Core 25、Player 20、Dlna 207、Service 99）。

测试分两类：

- **纯单元测试**：状态机、状态映射、进度节流、SSDP/SOAP/DIDL 解析 —— 不需要 native DLL。
- **原生/集成测试**：真实启动 libmpv（`ao=null` / `vo=null`）、真实 Named Pipe、回环 HTTP。
  DLL 缺失时播放器用例会以明确的 `Skip` 信息跳过，而不是静默通过。

## 4. 运行

界面（会自动启动后台服务）：

```powershell
winapp run src/DlnRenderer.App -c Debug
```

只运行后台服务（无界面，DLNA 发现与播放照常工作）：

```powershell
src\DlnRenderer.Service\bin\Debug\net10.0-windows\DlnRenderer.Service.exe --http-port 18090
```

服务参数：`--http-port`、`--no-ssdp`、`--device-name`、`--listen-address`、`--log`、`--no-hwdec`、
`--exit-after`。

附加诊断日志（`--log` 仅在显式指定时写文件）：

```powershell
winapp run src/DlnRenderer.App -c Debug -- --log "$env:TEMP\dln-ui.log"
```

关闭界面只会关闭 UI：后台服务、播放与 DLNA 发现会继续运行（这是设计要求）。
要停止服务，请在控制台按 `Ctrl+C`，或发送 IPC `Shutdown` 命令。

> 服务重复启动时，第二个实例会因为无法占用同一 SSDP 端口 / 管道而明确失败，而不是静默并行运行。

清理开发包：

```powershell
winapp unregister --manifest src/DlnRenderer.App/Package.appxmanifest
```

## 5. 依赖与锁文件

- 所有版本集中在 `Directory.Packages.props`，`PackageReference` 不再写 `Version`。
- `src/DlnRenderer.App/packages.lock.json` 已生成，需提交到版本控制。
- 为什么只有 App 有锁文件：WinUI 工程的还原始终带 `RuntimeIdentifier`，锁文件稳定；类库/测试项目会被
  `winapp` 以带 RID 和不带 RID 两种方式还原，任何一侧带 RID 都会让另一侧报 `NU1004`。
  这些项目由中央包管理精确固定版本。

升级依赖的标准流程：

1. 修改 `Directory.Packages.props` 中的版本。
2. `dotnet restore src/DlnRenderer.App/DlnRenderer.csproj --force-evaluate`
3. 审查 `packages.lock.json` 的 diff。
4. `dotnet restore DlnRenderer.sln --locked-mode` + 完整构建与测试。

**不要**在未验证的情况下升级 Windows App SDK 或 MVVM Toolkit。

## 6. 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| `Output directory ... is not empty`（退出码 2） | `winapp new --output .` 要求空目录；错误只写 stderr。用 `--force` 或指定子目录 |
| `MpvInitializationException: Could not load libmpv-2.dll` | 未运行 `scripts/fetch-libmpv.ps1`，或输出目录缺少 DLL |
| `BadImageFormatException` | DLL 架构与进程不符（当前仅支持 x64） |
| `NU1004` 锁文件与项目不一致 | 依赖或 RID 变更后未更新锁文件；按上节流程重新生成并审查 |
| 视频区域黑屏/被遮住 | 原生子窗口被 WinUI 内容桥接窗口覆盖；检查 `MpvVideoSurface` 的置顶调用 |
| 播放时没有声音 | 确认系统输出设备；`wasapi` 会话存在但无声属环境问题，不是播放器错误 |

## 7. 版本控制

仓库根目录即 `.git` 所在目录，默认分支 `main`。首次基线提交为
`chore: initialize DlnRenderer project baseline`（提交哈希见 `git log -1`）。

被忽略、**不入库**的内容：

- 所有 `bin/`、`obj/`、`.vs/`、测试结果与日志/转储。
- `src/DlnRenderer.Player/native/`：包含 `libmpv-2.dll`（GPL 构建）与 provenance 文件。
  需用 `scripts/fetch-libmpv.ps1` 在本地获取。
- 本地媒体文件（mp4/mkv/webm/mp3/flac/wav 等），用于手工播放验证。
- 机器特定的本地配置（`*.local.json`、`*.secrets.json`、`.env`）。

明确**入库**的例外：

- `tests/DlnRenderer.Player.Tests/Media/sample-video.mp4`（2 秒、约 13 KB 的测试夹具）。
- `src/DlnRenderer.App/packages.lock.json`（App 的依赖锁文件）。
- `src/DlnRenderer.App/Properties/PublishProfiles/*.pubxml`（仅描述目标 RID，无凭据）。

本轮未配置远程仓库，也未推送。

## 8. 目录说明

```text
src/DlnRenderer.App/         WinUI 3 UI（状态展示 + IPC 控制）+ 诊断日志
src/DlnRenderer.Core/        领域模型、播放状态机、IMediaPlayer、IPC 契约
src/DlnRenderer.Dlna/        SSDP、HTTP、SOAP、AVTransport、ConnectionManager、DIDL、UpnpTime
src/DlnRenderer.Player/      libmpv 互操作、状态映射、Win32 视频子窗口
src/DlnRenderer.Service/     后台服务：RendererSession、VideoPresenter、IpcServer、Program
tests/DlnRenderer.Core.Tests/
tests/DlnRenderer.Player.Tests/    含 13 KB 视频夹具 Media/sample-video.mp4
tests/DlnRenderer.Dlna.Tests/
tests/DlnRenderer.Service.Tests/
tools/control-point/         ssdp-discover / renderer-check / ipc-check / media-server / capture-window
scripts/fetch-libmpv.ps1
docs/
```

## 9. 调试 DLNA 的实用命令

```powershell
# 发现
pwsh tools/control-point/ssdp-discover.ps1 -SearchTarget "urn:schemas-upnp-org:device:MediaRenderer:1"

# 完整 AVTransport 流程（需要媒体源）
node tools/control-point/media-server.js "$env:TEMP\dln-media" 18777
pwsh tools/control-point/renderer-check.ps1 -MediaUrl "http://<本机LAN地址>:18777/long.mp4"

# IPC（与 UI 相同的管道）
pwsh tools/control-point/ipc-check.ps1

# 抓取服务视频窗口的内容（不受其他窗口遮挡影响）
pwsh tools/control-point/capture-window.ps1 -Hwnd 0x<窗口句柄>
```

服务日志默认写入 `%LOCALAPPDATA%\DlnRenderer\logs\service-<日期>.log`。
