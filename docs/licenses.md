# 依赖与许可证清单

状态：**许可证已确定（阶段 F）**。本项目采用 **GPL-2.0-or-later**，见仓库根目录 `LICENSE`。
分发包内的第三方组件声明见 [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md)。

决策与理由记录在 [`docs/architecture.md`](architecture.md) §17.1。

## 1. 托管依赖

全部版本由 `Directory.Packages.props` 集中固定。

| 组件 | 版本 | 许可证 | 用途 | 备注 |
| --- | --- | --- | --- | --- |
| .NET SDK / Runtime | 10.0.402 / 10.0.13 | MIT | 运行时与构建 | 框架依赖发布，运行时由用户机器提供 |
| Microsoft.WindowsAppSDK | 2.5.1 | MIT | WinUI 3 / 窗口 / 打包 | nuspec 内含 `license.txt` |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Windows SDK 许可条款 | 构建工具 | **非** OSI 开源；仅构建期使用，不随包分发 |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.7.1 | Microsoft 许可条款 | `dotnet run` 包身份支持 | 仅开发期 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM（`ObservableObject`） | App |
| LibVLCSharp | 3.10.2 | **LGPL-2.1-or-later** | 第二播放内核（libvlc）的 .NET 绑定 | Player；官方 VideoLAN 项目维护，net10.0 原生支持，2026-10-27 核对 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | 测试宿主 | 测试项目 |
| xunit | 2.9.3 | Apache-2.0 | 单元/集成测试 | 测试项目 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | 测试适配器 | 测试项目 |
| coverlet.collector | 6.0.4 | MIT | 覆盖率收集 | 测试项目 |

`DlnRenderer.Dlna` 与 `DlnRenderer.Service` **没有直接引入任何第三方 NuGet 依赖**：
SSDP 用 `System.Net.Sockets`，HTTP 用 `TcpListener`，SOAP/DIDL 用 `System.Xml.Linq`，
IPC 用 `System.IO.Pipes`（`LibVLCSharp` 经由 `DlnRenderer.Player` 传递引用到服务，记录在
`THIRD-PARTY-NOTICES.md` §4）。

开发用工具（**不随产品分发**，因此不产生分发义务）：

| 组件 | 版本 | 许可证 | 用途 |
| --- | --- | --- | --- |
| Node.js | 24.22.0 | MIT | `tools/control-point/media-server.js` 媒体源与验证脚本 |
| FFmpeg（`ffmpeg`/`ffprobe`） | 9.0.2 | LGPL/GPL（取决于构建） | 仅用于生成测试媒体 |

## 2. 原生依赖

| 组件 | 版本 / 标识 | 许可证 | 分发状态 |
| --- | --- | --- | --- |
| libmpv（zhongfly 构建） | mpv `v0.41.0-1224-g6a262d11f`，FFmpeg `N-127696-g8b3525e71` | **GPL-2.0-or-later** | **随包分发**（`libmpv-2.dll`），声明见 `THIRD-PARTY-NOTICES.md` |
| mpv 客户端头文件 | 同构建 | ISC | 仅 API 声明，用于手写 P/Invoke |
| FFmpeg（libav\*，静态编入 libmpv） | 同构建，含 x264/x265（GPL 组件） | GPL-2.0-or-later | 随后续分发方案决定 |

已核实的二进制指纹（`pwsh tools/verify-libmpv.ps1` 可复现）：

```text
libmpv-2.dll  x64  121,995,776 bytes
sha256 53513b8f7cbae3700bdb4f7af1933cbdc512fcafcd88d38c594087f9d4fd7881
source scoop mpv-dev 20261019-git-6a262d11f6 (zhongfly/mpv-winbuild)
mpv-configuration 不含 -Dgpl=false  ->  GPL build
二进制内含 x264 / x265 构建标识      ->  FFmpeg 亦为 --enable-gpl 构建
```

注意：Scoop `mpv-dev` 包内**不含任何 LICENSE/COPYING/NOTICE 文件**（目录仅有 `include/`、
`libmpv-2.dll`、`libmpv.dll.a`），许可证信息只能从上游仓库获取——这正是仓库内自带
`THIRD-PARTY-NOTICES.md` 与 `LICENSES/` 的原因。

### 2.1 libVLC（第二播放内核）

随包分发的第二组原生二进制，来自 VideoLAN 官方 NuGet 包（与 libmpv 不同：有官方包、
nuspec 明示许可证、来源可追溯到 VideoLAN 自己的构建流水线）。

| 组件 | 版本 / 标识 | 许可证 | 分发状态 |
| --- | --- | --- | --- |
| VideoLAN.LibVLC.Windows | 3.0.24（libvlc `3.0.24`） | **LGPL-2.1-or-later**（nuspec `license` 字段） | **随包分发**（`libvlc\win-x64\`，约 108 MiB；x86/arm64 在 `DlnRenderer.Player.csproj` 内裁剪），声明见 `THIRD-PARTY-NOTICES.md` §3 |

已核实的二进制指纹：

```text
libvlc\win-x64\libvlc.dll      sha256 c0b1bedbc8e11267b39111733920a6af73d08791e333e4f8902eaffe4daf6db9
libvlc\win-x64\libvlccore.dll  sha256 c089f281affe1e85c61edd5996c2b496191f82fa5cdc7ef709b0c09bf754cdbe
source https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.24 (code.videolan.org/videolan/libvlc-nuget)
nuspec license = LGPL-2.1-or-later
```

LGPL 义务（对比 libmpv 的 GPL）：动态加载使用方需提供 LGPL 全文与对应源码获取方式、
并允许用户替换库文件——分发包同时携带 `LICENSES\LGPL-2.1-or-later.txt`、`THIRD-PARTY-NOTICES.md`
§3 的源码地址，且库以独立 `libvlc\win-x64\` 目录存在、可整目录替换，均满足。包内插件树的
个别模块可能适用 GPL；本项目自身为 GPL-2.0-or-later，因此不构成额外义务。

## 3. 已评估但未采用

| 组件 | 声称许可证 | 结论 |
| --- | --- | --- |
| NuGet `Endpne.LibMPV.Windows` 0.41.0 | LGPL-2.1-or-later（nuspec） | 实测其 `libmpv-2.dll` 同为 GPL 构建，**元数据不可信**，未采用 |
| NuGet `HanumanInstitute.LibMpv` 0.10.1 | MIT（托管封装） | 本身合规，但本项目只需约 10 个函数，自写 P/Invoke 更小，未引入 |
| Macast（Python DLNA renderer） | GPL-3.0（PyPI 元数据实测） | 与本项目定位/许可冲突，未复用 |

## 4. 分发前必须补齐 → 已完成的清单

阶段 F 的结论是走 GPL 路线，因此对应义务逐项落实如下（原清单见 ROADMAP 阶段 F）：

| # | 义务 | 状态 | 落实位置 |
| --- | --- | --- | --- |
| 1 | 随安装包提供 libmpv 的完整对应源码或书面获取方式（GPL §3） | ✅ | `THIRD-PARTY-NOTICES.md` §2.1：mpv/FFmpeg 精确 commit + 获取地址 + 3 年书面提供承诺 |
| 2 | 包含 GPL-2.0 许可证全文与 mpv 版权声明 | ✅ | 根目录 `LICENSE`（GPL-2.0-or-later 全文）、`LICENSES/`、`THIRD-PARTY-NOTICES.md` §1–§2 |
| 3 | 逐项列出 FFmpeg 及其编解码器/滤镜的许可证与通知（GPL 构建下部分组件为 GPL） | ✅ | `THIRD-PARTY-NOTICES.md` §3：36 项组件逐项列出，含 x264/x265/libvpx/dav1d 等 |
| 4 | 说明 `Microsoft.Windows.SDK.BuildTools` 仅用于构建、不随应用分发 | ✅ | `THIRD-PARTY-NOTICES.md` §4、本文件 §1 |
| 5 | 提供 `THIRD-PARTY-NOTICES` 文件（仓库内曾缺失） | ✅ | `THIRD-PARTY-NOTICES.md`，并随 `dist/` 与 MSIX 包分发 |
| 6 | 项目自身 `LICENSE`（曾缺失，等同于"保留所有权利"） | ✅ | 根目录 `LICENSE`，GPL-2.0-or-later |
| 7 | 分发包内含法律文件（不能只在仓库里） | ✅ | `build-dist.ps1` / `build-msix.ps1` 复制 `LICENSE`、`LICENSES/`、`THIRD-PARTY-NOTICES.md` 并校验存在 |
| 8 | 发布产物的哈希清单，可校验 | ✅ | `tools/package/hash-release.ps1`（生成/校验 `SHA256SUMS.txt`） |
| 9 | 构建可重复的自动化检查（CI） | ✅ | `.github/workflows/ci.yml`：restore/build/test |

**遗留缺口**（不阻塞"可分发"，但对外发布前应补）：见 `THIRD-PARTY-NOTICES.md` §8——
VapourSynth / Lua / bzip2 三项未取到上游 LICENSE 文件；正式代码签名证书未获取（F5 的签名
部分，见 `docs/packaging.md`）。

## 5. 如果将来改走 LGPL

不是"换个 DLL"那么简单，重开条件与代价写在 `docs/architecture.md` §17.3。要点：需要
`-Dgpl=false` 的 libmpv 构建（上游不提供 Windows 预编译产物，需自行交叉编译），按阶段 D
的方法重新测量格式矩阵（x264/x265 编解码不可用），并保留构建脚本与源码归档以证明其
确实是 LGPL 构建。
