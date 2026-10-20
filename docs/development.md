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

预期结果（本轮实测）：构建 0 错误 / 0 警告，测试 40 通过 / 0 失败。

测试分两类：

- **纯单元测试**：状态机、状态映射、进度节流 —— 不需要 native DLL。
- **原生集成测试**：真实启动 libmpv，使用 `ao=null` / `vo=null` 保证可重复；DLL 缺失时这些用例会
  以明确的 `Skip` 信息跳过，而不是静默通过。

## 4. 运行

```powershell
winapp run src/DlnRenderer.App -c Debug
```

直接播放某个本地文件（用于验证）：

```powershell
winapp run src/DlnRenderer.App -c Debug -- --play "C:\path\video.mp4"
```

附加诊断日志（`--log` 仅在显式指定时写文件）：

```powershell
winapp run src/DlnRenderer.App -c Debug -- --play "C:\path\video.mp4" --log "$env:TEMP\dln.log"
```

关闭：直接关闭窗口。窗口的 `AppWindow.Closing` 会同步释放 mpv 与原生子窗口；
日志中应出现 `player disposed`。

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

## 7. 目录说明

```text
src/DlnRenderer.App/         WinUI 3 UI + 播放器视图模型 + 诊断日志
src/DlnRenderer.Core/        领域模型、播放状态机、IMediaPlayer
src/DlnRenderer.Player/      libmpv 互操作、状态映射、Win32 视频子窗口
tests/DlnRenderer.Core.Tests/
tests/DlnRenderer.Player.Tests/  含 13 KB 视频夹具 Media/sample-video.mp4
scripts/fetch-libmpv.ps1
docs/
```
