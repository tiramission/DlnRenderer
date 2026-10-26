# 第三方组件与许可证声明

本文件随 DlnRenderer 的每个分发包一起提供（绿色包与 MSIX 包内均有副本），说明包内第三方
组件的来源、许可证与获取对应源码的方式。

- 仓库：[tiramission/DlnRenderer](https://github.com/tiramission/DlnRenderer)
- 分发包内容：`DlnRenderer.exe`（UI）、`DlnRenderer.Service.exe`（后台服务）、
  `libmpv-2.dll` 与 `libvlc\win-x64\`（随包的 native 二进制）、`Assets\`、`LICENSE`、`LICENSES\`、本文件。
- 本项目代码的许可证：**GPL-2.0-or-later**，全文见 `LICENSE`。
- 本文档中的每一条结论都注明了核对依据（上游许可证文件 URL 或本地二进制实测方式），
  不依赖记忆或第三方元数据。核对方法与日期见 §9。

## 1. 本项目

```text
DlnRenderer — Windows DLNA Media Renderer
Copyright (C) 2026 DlnRenderer contributors
License: GPL-2.0-or-later
```

DlnRenderer 是自由软件：你可以依据自由软件基金会发布的 GNU 通用公共许可证（第 2 版或
你选择的任何更新版本）的条款重新分发和/或修改它。分发时不提供任何担保，包括适销性或
特定用途适用性的默示担保。详见 `LICENSE`。

本项目**不使用** libmpv 的 GPL 豁免声明做任何"链接边界"主张：可执行代码与 libmpv 之间是
动态加载（`LoadLibrary` + `libmpv-2.dll`），项目仍整体选择 GPL 以消除歧义。

## 2. libmpv（随包分发的 native 组件）

| 项 | 值 |
| --- | --- |
| 文件 | `libmpv-2.dll`（x64 PE，`Machine=0x8664`） |
| 大小 | 116.3 MiB（121,995,776 字节） |
| SHA-256 | `53513b8f7cbae3700bdb4f7af1933cbdc512fcafcd88d38c594087f9d4fd7881` |
| mpv 版本 | `mpv v0.41.0-1224-g6a262d11f` |
| FFmpeg 版本 | `N-127696-g8b3525e71` |
| libplacebo 版本 | `v7.377.0 (v7.360.0-172-gccb74c6-dirty)` |
| 构建者 | [zhongfly/mpv-winbuild](https://github.com/zhongfly/mpv-winbuild)（第三方 CI 构建，mingw-w64 交叉编译） |
| 许可证 | **GPL-2.0-or-later** |

许可证判定依据（本地实测，非推断）：

1. libmpv 暴露的 `mpv-configuration` 属性**不含** `-Dgpl=false`，即 mpv 核心按默认的
   GPLv2+ 构建（见 mpv 上游 `Copyright` 文件的说明）。
2. 二进制内部含有 `x264 - core` 与 `x265` 构建标识。x264、x265 均为 GPL 组件，FFmpeg
   只能在 `--enable-gpl` 下包含它们，因此随包的 FFmpeg 亦为 GPLv2+（FFmpeg `LICENSE.md`：
   "In this case, FFmpeg's license changes to GPL v2+"）。

实测得到的构建配置（原文）：

```text
-Dprefix=/__w/mpv-winbuild/mpv-winbuild/mpv-winbuild-cmake/build64/install/x86_64-w64-mingw32
-Dlibdir=/__w/mpv-winbuild/mpv-winbuild/mpv-winbuild-cmake/build64/install/x86_64-w64-mingw32/lib
-Ddefault_library=shared -Dprefer_static=true -Ddebug=true -Db_ndebug=true -Doptimization=3
-Db_lto=true -Db_lto_mode=thin -Dlibmpv=true -Dpdf-build=enabled -Dlua=enabled
-Djavascript=enabled -Dsdl2-gamepad=enabled -Dlibarchive=enabled -Dlibbluray=enabled
-Ddvdnav=enabled -Duchardet=enabled -Drubberband=enabled -Dlcms2=enabled -Dopenal=enabled
-Dspirv-cross=enabled -Dvulkan=enabled -Dvapoursynth=enabled -Dsubrandr=enabled
-Dsixel=enabled -Dgl=enabled -Degl-angle=enabled -Dlibcurl=enabled
-Dc_args=-Wno-error=int-conversion --cross-file=.../meson_cross.txt
```

### 2.1 对应源码（GPL §3）

| 组件 | 对应源码 |
| --- | --- |
| DlnRenderer | <https://github.com/tiramission/DlnRenderer>（分发包对应的版本即最新 tag/commit） |
| mpv / libmpv | <https://github.com/mpv-player/mpv>，commit `6a262d11f`（`v0.41.0-1224-g6a262d11f`） |
| FFmpeg | <https://github.com/FFmpeg/FFmpeg>，commit `8b3525e71`（`N-127696`） |
| 构建脚本 | <https://github.com/zhongfly/mpv-winbuild> 中的 mpv-winbuild-cmake 配方与 meson 交叉文件 |

若无法通过上述地址获取对应源码，可在仓库 Issues 中提出，我们会提供一份与二进制版本一致的
源码副本；该承诺自对应版本发布起 3 年内有效。

## 3. libVLC（第二个播放内核，随包分发的 native 组件）

播放内核二选一（设置页），默认 mpv；选择 VLC 内核时，服务加载 `libvlc\win-x64\` 下的
官方构建。文件随 NuGet 包 `VideoLAN.LibVLC.Windows` 一起分发。

| 项 | 值 |
| --- | --- |
| 文件 | `libvlc\win-x64\libvlc.dll`、`libvlccore.dll` 与 `plugins\`、`lua\`、`hrtfs\` 全树（仅保留 x64，x86/arm64 已在工程内裁剪） |
| 载荷体积 | 约 108 MiB |
| libvlc 版本 | 3.0.24（`VersionInfo.ProductVersion = 3.0.24`） |
| libvlc.dll SHA-256 | `c0b1bedbc8e11267b39111733920a6af73d08791e333e4f8902eaffe4daf6db9` |
| libvlccore.dll SHA-256 | `c089f281affe1e85c61edd5996c2b496191f82fa5cdc7ef709b0c09bf754cdbe` |
| 来源 | [VideoLAN.LibVLC.Windows 3.0.24](https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.24)（VideoLAN 官方构建，`code.videolan.org/videolan/libvlc-nuget`） |
| 许可证 | **LGPL-2.1-or-later**（nuspec `license = LGPL-2.1-or-later`） |

许可证判定依据与义务履行：

1. 许可证取自包内 `videolan.libvlc.windows.3.0.24.nuspec` 的 `license` 字段（SPDX 表达式），
   与 nuget.org 包页一致；LGPL-2.1-or-later 全文随包放在 `LICENSES\LGPL-2.1-or-later.txt`。
2. 本项目以动态加载方式使用 libvlc，本项目自身为 GPL-2.0-or-later，与 LGPL 及包内个别
   GPL 模块的义务兼容；逆向工程、反编译等 LGPL 对动态链接使用方的义务因此不构成本包的
   限制条款。
3. 对应源码：<https://code.videolan.org/videolan/vlc>（VLC 3.0.x，libvlc 源码同仓库）、
   <https://code.videolan.org/videolan/libvlc-nuget>（打包配方）。用户替换
   `libvlc\win-x64\` 下的文件即可完成 LGPL 意义上的重新链接。

## 4. LibVLCSharp（播放内核的 .NET 绑定，随包分发的托管组件）

| 项 | 值 |
| --- | --- |
| 程序集 | `LibVLCSharp.dll`（随服务分发） |
| 版本 | 3.10.2 |
| 来源 | [LibVLCSharp 3.10.2](https://www.nuget.org/packages/LibVLCSharp)（VideoLAN 官方 .NET 绑定） |
| 许可证 | **LGPL-2.1-or-later** |

## 5. 静态链接进 `libmpv-2.dll` 的组件

以下组件由第三方构建静态编入 libmpv，**不是**独立 DLL，无法单独替换。
"核对依据"列是许可证结论的来源；带 `*` 的条目未能取到上游 LICENSE 文件，按上游项目自身
声明记录，需在对外发布前补一次文件级核对（见 §10）。

| 组件 | 在包内的作用 | 许可证 | 核对依据 |
| --- | --- | --- | --- |
| mpv / libmpv | 播放核心、A/V 同步、OSD | GPL-2.0-or-later | mpv `Copyright` |
| FFmpeg（libav\*） | 解复用、解码、滤镜、缩放 | GPL-2.0-or-later（本构建启用 GPL 组件） | FFmpeg `LICENSE.md` |
| x264 | H.264 编解码 | GPL-2.0-or-later | x264 `COPYING` |
| x265 | HEVC 编解码 | GPL-2.0-or-later | x265 `COPYING` |
| libvpx | VP8/VP9 解码 | BSD-3-Clause | libvpx `LICENSE` |
| dav1d | AV1 解码 | BSD-2-Clause | dav1d `COPYING` |
| libaom | AV1 编解码 | BSD-3-Clause | aomedia `LICENSE` |
| SVT-AV1 | AV1 编码 | BSD-3-Clause Clear | SVT-AV1 `LICENSE.md` |
| Opus | 音频编解码 | BSD-3-Clause | opus `COPYING` |
| Vorbis | 音频编解码 | BSD-3-Clause | vorbis `COPYING` |
| libass | 字幕渲染 | ISC | libass `COPYING` |
| libplacebo | GPU 视频渲染 | LGPL-2.1-or-later | libplacebo `LICENSE` |
| libarchive | 压缩包/蓝光（libbluray 依赖） | BSD-2-Clause | libarchive `COPYING` |
| libbluray | 蓝光导航 | LGPL-2.1-or-later | libbluray `COPYING` |
| libdvdread | DVD 读取 | GPL-2.0-or-later | libdvdread `COPYING` |
| libdvdnav | DVD 导航 | GPL-2.0-or-later | libdvdnav `COPYING` |
| rubberband | 变速不变调 | GPL-2.0-or-later | rubberband `COPYING` |
| LCMS2 | 色彩管理 | MIT | Little-CMS `LICENSE` |
| OpenAL Soft | 音频输出 | LGPL-2.0-or-later | openal-soft `COPYING` |
| SPIRV-Cross | 着色器跨编译 | Apache-2.0 | SPIRV-Cross `LICENSE` |
| VapourSynth | 视频滤镜接口 | LGPL-2.1-or-later `*` | 上游项目声明 |
| libsixel | 终端图像输出 | MIT | libsixel `LICENSE` |
| ANGLE | OpenGL ES（`egl-angle`） | BSD-3-Clause | ANGLE `LICENSE` |
| libcurl | 网络取流 | curl | curl `COPYING` |
| fontconfig | 字体匹配 | MIT | fontconfig `COPYING` |
| FreeType | 字体解析 | FTL 或 GPL-2.0 | FreeType `LICENSE.TXT` |
| HarfBuzz | 文字整形 | MIT（Old MIT） | HarfBuzz `COPYING` |
| FriBidi | 双向文字 | LGPL-2.1-or-later | fribidi `COPYING` |
| libpng | PNG | libpng-2.0 | libpng `LICENSE` |
| zlib | 压缩 | Zlib | zlib `LICENSE` |
| Lua | 脚本（`script` 命令、配置） | MIT `*` | 上游项目声明 |
| Expat | XML 解析 | MIT | libexpat `COPYING` |
| zimg | 缩放/色彩转换 | WTFPL | zimg `COPYING` |
| XZ Utils / liblzma | 压缩 | 0BSD（liblzma） | xz `COPYING` |
| bzip2 | 压缩 | bzip2-1.0.6 `*` | 上游项目声明 |
| uchardet | 字符集检测 | MPL-1.1 | uchardet `COPYING` |

对应许可证全文放在 `LICENSES\` 目录（GPL-2.0、LGPL-2.1、LGPL-2.0、Apache-2.0、ISC、MIT、
BSD-2-Clause、BSD-3-Clause、curl、FTL、Zlib、WTFPL 及 MPL-1.1 见上游仓库）。

## 6. 托管依赖（NuGet）

这些程序集随包分发。宽松型组件的完整许可条款在各自的包内 `LICENSE`/`license.txt`；
LibVLCSharp 为 LGPL-2.1-or-later（见 §4），其条款以 §4 的义务说明为准。

| 组件 | 版本 | 许可证 |
| --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.5.1 | MIT |
| CommunityToolkit.Mvvm | 8.4.2 | MIT |
| LibVLCSharp | 3.10.2 | LGPL-2.1-or-later（§4） |
| Microsoft.Windows.SDK.NET.Ref / CsWinRT 等 Windows SDK 投影 | 随 TFM | MIT |
| .NET 运行时 | 由用户机器提供（框架依赖发布） | MIT |

`Microsoft.Windows.SDK.BuildTools` 只用于构建，不随应用分发。

## 7. 不随包分发的工具

`docs/licenses.md` §1 列出开发期工具（Node.js、FFmpeg CLI、`winapp` 等）。它们不进入
分发包，因此不在本文件中定义分发义务。

## 8. 本项目的来源可见性

本项目是 GPL 软件：任何人都可以从上述仓库取得与分发包对应的完整源码。分发包**不含**
源码压缩包，这是 GPL §3 允许的"通过同一网络位置提供"的形式；若你收到的是物理介质，
同样可通过仓库取得源码，或按 §2.1 的最后一段提出请求。

## 9. 如何复现本文档的结论

```powershell
# 1. 打印随包 libmpv 的版本、构建配置、FFmpeg/libass/libplacebo 版本与 SHA-256
pwsh tools/verify-libmpv.ps1

# 2. 确认分发包内的 native 二进制与本文档记录一致
pwsh tools/package/hash-release.ps1 -Verify

# 3. 核对 §3 记录的 libvlc 哈希（随 NuGet 包分发，包版本锁定于 Directory.Packages.props）
Get-FileHash dist\DlnRenderer\libvlc\win-x64\libvlc.dll -Algorithm SHA256
Get-FileHash dist\DlnRenderer\libvlc\win-x64\libvlccore.dll -Algorithm SHA256
```

核对日期：2026-10-22（libmpv 相关章节）；2026-10-27（libVLC / LibVLCSharp 章节）。许可证依据
均为当日从上游仓库 `raw.githubusercontent.com` / `code.videolan.org` / `gitlab.freedesktop.org` /
`aomedia.googlesource.com` 直接取得的 `LICENSE`/`COPYING` 文件，libVLC 章节另核对了
nuget.org 包页与包内 nuspec。

## 10. 已知缺口

1. §5 中带 `*` 的条目（VapourSynth、Lua、bzip2）本轮未取到上游许可证文件，结论来自项目
   自身声明，需在首次公开发布前补齐文件级核对。
2. 本文档描述的是**当前锁定的那个** libmpv 构建。更换 libmpv 版本或构建配方时，§2、§5
   必须重新核对——`scripts/fetch-libmpv.ps1` 的哈希校验正是为此存在。
3. §3 的 libvlc 记录的是 VideoLAN.LibVLC.Windows 3.0.24 这个包。升级该包（或打开
   x86/arm64 裁剪开关）时，§3 的体积与哈希必须重新核对；包内插件树的个别模块可能适用
   GPL，升级时应对 `plugins\` 做一次许可证抽查。
4. 本文档不是法律意见。分发前如需确认义务边界，请咨询专业人士。
