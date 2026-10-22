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

`libmpv-2.dll` 是原生依赖，**不在仓库中**（GPL-2.0-or-later 二进制，二进制不入库，但**随分发包提供**；
义务与决策见 `docs/architecture.md` §17）。

```powershell
pwsh scripts/fetch-libmpv.ps1            # 本机已有 Scoop mpv-dev 时：直接复制
pwsh scripts/fetch-libmpv.ps1 -Download  # 干净机器/CI：拉取锁定的上游归档并解出 DLL
```

脚本会：

1. 从本机 Scoop 的 `mpv-dev` 包复制 `libmpv-2.dll`（或 `-Download` 时从上游 release 归档解出）
   到 `src/DlnRenderer.Player/native/x64/`。
2. 校验 SHA-256（默认 `53513b8f...`，可用 `-ExpectedSha256` 指定其他构建）。`-Download` 还会
   先校验 7z 归档本身的哈希，两个哈希都对才落盘。
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

预期结果（本轮实测）：构建 0 警告 / 0 错误，测试 **403 通过 / 0 失败**（Core 43、Player 23、Dlna 222、Service 115）。

测试分两类：

- **纯单元测试**：状态机、状态映射、进度节流、SSDP/SOAP/DIDL 解析 —— 不需要 native DLL。
- **原生/集成测试**：真实启动 libmpv（`ao=null` / `vo=null`）、真实 Named Pipe、回环 HTTP。
  DLL 缺失时播放器用例会以明确的 `Skip` 信息跳过，而不是静默通过。窗口生命周期测试还会
  用 `ffmpeg` 现生成素材，缺 ffmpeg 时同样跳过并说明原因。

### 3.1 CI 上的同一套测试

`.github/workflows/ci.yml` 在 `windows-latest` 上跑与上面完全相同的命令，但它**自己供应**两个原生
依赖，所以播放器相关用例是真实执行而不是跳过：`fetch-libmpv.ps1 -Download`（双哈希校验）、
`choco install ffmpeg`。2026-10-22 的结果是 **402 通过 / 0 失败 / 0 跳过**（另 1 个 GPU 硬解测试
因 runner 无显卡按全名排除）。细节与迭代记录见 `docs/testing.md` §12。

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

服务启动时先读 `%LOCALAPPDATA%\DlnRenderer\settings.json`（设置页写入），命令行参数优先于文件设置。

### 4.1 UI 自动化验收（`winapp ui`）

窗口改动不用靠肉眼：`winapp ui` 用 UI Automation 读取与操作控件，与屏幕阅读器同一通道。

```powershell
winapp run src/DlnRenderer.App -c Debug -- --log "$env:TEMP\dln-ui.log"   # 另开一个终端窗口

winapp ui inspect -a <PID> --depth 8 -i      # 交互元素树（含控件名称、位置）
winapp ui search "后台服务" -a <PID>          # 按文本找元素
winapp ui set-value DeviceNameBox "名字" -a <PID>
winapp ui invoke <按钮 slug> -a <PID>
winapp ui screenshot -a <PID> --json         # 窗口截图
winapp ui invoke "显示隐藏的图标" -a explorer  # 托盘图标在隐藏图标区
winapp ui search "DlnRenderer" -a explorer
```

具体验收项与实测结果见 [`testing.md`](testing.md) §8。

附加诊断日志（`--log` 仅在显式指定时写文件）：

```powershell
winapp run src/DlnRenderer.App -c Debug -- --log "$env:TEMP\dln-ui.log"
```

关闭界面只会关闭 UI：后台服务、播放与 DLNA 发现会继续运行（这是设计要求）。
要停止整个应用，请在设置页点“退出整个应用”或在托盘菜单选“退出应用”（都会通过 IPC `Shutdown`
停掉服务）；单独杀掉服务进程时，在控制台按 `Ctrl+C` 即可。

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
src/DlnRenderer.Service/     后台服务：RendererSession、VideoPresenter、IpcServer、ServiceStatusReporter、Program
tests/DlnRenderer.Core.Tests/
tests/DlnRenderer.Player.Tests/    含 13 KB 视频夹具 Media/sample-video.mp4
tests/DlnRenderer.Dlna.Tests/
tests/DlnRenderer.Service.Tests/
tools/control-point/         ssdp-discover / renderer-check / ipc-check / media-server / capture-window
tools/package/               build-dist / install / uninstall / firewall-rules / build-msix / verify-service
tools/assets/                make-icons（生成应用图标与 MSIX 瓦片/启动图）
src/DlnRenderer.App/Assets/  AppIcon.ico、瓦片与启动图（均由 make-icons 生成）
scripts/fetch-libmpv.ps1
docs/
```

## 9. 图标与图片资源

`src/DlnRenderer.App/Assets/` 里的 PNG/ICO **不是手绘后丢进去的二进制**，由脚本生成：

```powershell
pwsh tools/assets/make-icons.ps1              # 重新生成全部资源
pwsh tools/assets/make-icons.ps1 -Preview artifacts/icon-preview   # 另外导出各尺寸对比图
```

- `AppIcon.ico` 含 16/20/24/32/40/48/64/96/128/256 十个 32bpp DIB 帧（标题栏与托盘都读这个文件）；
- 小于 24px 的尺寸只用播放标记，24px 起用“屏幕边框 + 标记”：更小的尺寸上边框会细到一个像素以下，
  糊成一团；
- 瓦片、启动图与 Store 图标比例写在脚本的 `$assets` 表里，尺寸必须与文件名里的 `scale-200` /
  `targetsize-*` 限定符一致，否则打包后系统挑不到该变体。

改图标时不要只改 PNG：先改脚本里的几何与配色，再重新生成，否则下次生成会把改动覆盖掉。

## 10. 调试 DLNA 的实用命令

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
