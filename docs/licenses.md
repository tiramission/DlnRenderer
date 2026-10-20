# 依赖与许可证清单

状态：**开发验证阶段，不可对外分发**。项目整体许可证尚未确定，见 `docs/architecture.md` §10.1。

## 1. 托管依赖

全部版本由 `Directory.Packages.props` 集中固定（本轮实测解析结果）。

| 组件 | 版本 | 许可证 | 用途 | 备注 |
| --- | --- | --- | --- | --- |
| .NET SDK / Runtime | 10.0.402 / 10.0.13 | MIT | 运行时与构建 | Microsoft |
| Microsoft.WindowsAppSDK | 2.5.1 | MIT | WinUI 3 / 窗口 / 打包 | nuspec 内含 `license.txt` |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Windows SDK 许可条款 | 构建工具 | **非** OSI 开源；注意再分发条款 |
| Microsoft.Windows.SDK.BuildTools.WinApp | 0.7.1 | Microsoft 许可条款 | `dotnet run` 包身份支持 | 仅开发期 |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | MVVM（`ObservableObject`） | App |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | 测试宿主 | 测试项目 |
| xunit | 2.9.3 | Apache-2.0 | 单元/集成测试 | 测试项目 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | 测试适配器 | 测试项目 |
| coverlet.collector | 6.0.4 | MIT | 覆盖率收集 | 测试项目 |

## 2. 原生依赖

| 组件 | 版本 / 标识 | 许可证 | 分发状态 |
| --- | --- | --- | --- |
| libmpv（zhongfly 构建） | mpv `v0.41.0-1224-g6a262d11f`，`20261019-git-6a262d11f6` | **GPL-2.0-or-later** | 仅本地开发；**不随包分发** |
| mpv 客户端头文件 | 同构建 | ISC | 仅 API 声明，用于手写 P/Invoke |
| FFmpeg（libav*，静态编入 libmpv） | 随上面的构建（`f853d12` 系） | LGPL-2.1+ / GPL（取决于构建开关） | 随后续分发方案决定 |
| libass / libplacebo / libbluray / libdvdnav / libarchive / lua / javascript 等 | 随构建 | 各自许可证（ISC/LGPL/GPL 混合） | 分发前需逐项列明 |

注意：Scoop `mpv-dev` 包内**不含任何 LICENSE/COPYING/NOTICE 文件**（目录仅有 `include/`、`libmpv-2.dll`、
`libmpv.dll.a`），许可证信息只能从上游仓库与 mpv 文档获取，分发时必须自行随包附带。

已核实的二进制指纹：

```text
libmpv-2.dll  x64
sha256 53513b8f7cbae3700bdb4f7af1933cbdc512fcafcd88d38c594087f9d4fd7881
source scoop mpv-dev 20261019-git-6a262d11f6 (zhongfly/mpv-winbuild)
mpv-configuration 不含 -Dgpl=false  ->  GPL build
```

## 3. 已评估但未采用

| 组件 | 声称许可证 | 结论 |
| --- | --- | --- |
| NuGet `Endpne.LibMPV.Windows` 0.41.0 | LGPL-2.1-or-later（nuspec） | 实测其 `libmpv-2.dll` 同为 GPL 构建，**元数据不可信**，未采用 |
| NuGet `HanumanInstitute.LibMpv` 0.10.1 | MIT（托管封装） | 本身合规，但本项目只需约 10 个函数，自写 P/Invoke 更小，未引入 |
| Macast（Python DLNA renderer） | GPL-3.0（PyPI 元数据实测） | 与本项目定位/许可冲突，未复用 |

## 4. 分发前必须补齐

若最终选择 GPL 路线：

1. 随安装包提供 libmpv 的完整对应源码或书面获取方式（GPL §3）。
2. 包含 GPL-2.0 许可证全文与 mpv 版权声明。
3. 逐项列出 FFmpeg 及其编码器/滤镜的许可证与通知（GPL 构建下部分组件为 GPL）。
4. 说明 `Microsoft.Windows.SDK.BuildTools` 仅用于构建，不随应用分发（除非打包需要）。
5. 提供 `THIRD-PARTY-NOTICES` 文件；当前仓库**尚未包含**。

若最终选择 LGPL 路线：

1. 替换为 `-Dgpl=false` 构建，并保留其构建脚本与版本归档。
2. 允许用户替换该 DLL（LGPL §4 动态链接与反向工程条款）。
3. 仍需列出静态编入的 FFmpeg 等组件许可证。

在完成上述任一项之前，本仓库产物**不得标记为可发布版本**。
