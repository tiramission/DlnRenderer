# MSIX 适用性评估（2026-10-22 修订）

> 本文件取代 `docs/packaging.md` 早期"不做 MSIX"的结论，**同时也修正了本文件上一版"用
> `windows.service` 让 SCM 托管服务"的结论**。MSIX 是正式交付目标之一，形态是**用户级包**。

## 1. 结论先行

1. **MSIX 可行，且以用户级包交付**：`Add-AppxPackage` 以当前用户安装，无需提权（只有首次信任
   开发证书需要管理员）。
2. **服务不注册为 Windows 服务。** `windows.service` 扩展会让服务跑在 session 0，**实测投屏无
   画面**（mpv `Failed to create swapchain 0x887a0022`）。服务保持"UI 在用户会话里拉起的后台
   子进程"，与绿色包、AGENTS.md §4 一致。
3. 因此 `windows.service` / `localSystemServices` / `packagedServices` 都不使用，清单只需要
   `runFullTrust`（受限能力，包必须签名）。

## 2. 事实来源

manifest 相关结论来自本机 Windows SDK 的 schema（`Windows Kits\10\Include\10.0.28000.0\winrt\`
下的 `DesktopManifestSchema*.xsd`、`AppxManifestTypes.xsd`、`UapManifestSchema_v10.xsd`），
行为结论来自本机实测（`docs/testing.md` §11）。工具链：`winapp` CLI 0.7.1、
`makeappx.exe`、`signtool.exe`。

## 3. 逐项拆解

**类型**列区分：`平台限制`（MSIX 本身不允许）、`实现选择`（可换实现）、`未验证假设`。

| # | 问题 | 类型 | 实际情况 | 做法 | 状态 |
| --- | --- | --- | --- | --- | --- |
| 1 | UI 退出后服务继续运行 | 实现选择 | 服务是 UI 拉起的独立进程，关窗不影响它；这与绿色包完全相同 | `ServiceClient` 在用户会话里 `Process.Start` 服务；UI 重开时复用已有管道 | **已验证**（用户级包，服务 session 1） |
| 2 | 服务开机自启 | 实现选择 | 用户级包没有 `windows.service`，开机自启的对象是 **UI**（UI 再拉起服务） | 清单 `windows.startupTask`（默认关闭），设置页通过 `StartupTask` API 开关 | API 路径已编译；**未做重启实测** |
| 3 | 卸载清理 | 平台特性 | 系统卸载移除包；用户数据在 `%LOCALAPPDATA%\DlnRenderer`，不在包内 | 卸载后的用户数据保留策略待定 | 未验证卸载后的残留 |
| 4 | 入站防火墙规则（UDP 1900 / TCP 18090） | 平台限制 | **manifest 能力不创建防火墙规则**；用户级安装也没有提权钩子 | 用户手动运行 `firewall-rules.ps1`，或首次运行由 UI 引导 | **未实现** |
| 5 | SSDP 多播 | 未验证假设 | 服务是用户会话里的普通进程，多播行为与绿色包一致 | — | **已验证**：真实控制端 192.168.1.72 发现并订阅成功 |
| 6 | libmpv 随包分发（GPL） | 实现选择 | 与 MSIX 完整性校验冲突 | `uap10:PackageIntegrity Content Enforcement="off"` 让包内文件可被替换；GPL 义务走阶段 F | manifest 已声明；替换本身未实测 |
| 7 | 服务写状态文件 | 实现选择 | 用户会话下 `%LOCALAPPDATA%` 可用且**未被虚拟化** | 日志在 `C:\Users\<用户>\AppData\Local\DlnRenderer\logs\` | **已验证** |
| 8 | 视频窗口 / GUI | 平台限制（重要） | session 0 无桌面 → 不能出画面；用户会话没有这个问题 | 不用 `windows.service` | **已验证**：窗口创建、presenter 切换、播放无 mpv 报错 |
| 9 | Publisher / 签名 | 平台限制 | MSIX 必须签名才能安装 | 本地 `winapp cert generate` + `cert install`；分发需 CA 证书（阶段 F5） | dev 证书可用；CA 证书未获取 |
| 10 | 升级身份一致性 | 平台限制 | Publisher + Name 必须一致；**同版本号内容不同会被拒绝**（`0x80073CFB`） | 复用 dev 证书（`-ForceCert` 才重建）；发布必须递增版本 | 已遇到并确认 |
| 11 | 框架依赖 | 平台限制 | 清单必须声明 `Microsoft.WindowsAppRuntime.2`，否则打包后的进程拿不到框架包，首次 WinRT 激活抛 `REGDB_E_CLASSNOTREG`，UI 启动即崩 | `PackageDependency Name="Microsoft.WindowsAppRuntime.2" MinVersion="2.5.1.0"` | **已验证**（修复前每次崩溃，修复后稳定运行） |

## 4. 为什么放弃 `windows.service`（上一版的错误结论）

上一版认为 `desktop7:Service` 解决了"UI 退出后服务继续运行"和"开机自启"，并实测了 SCM 的
起停、自启、卸载清理——这些都是真的。**但漏掉了一个更根本的事实：SCM 托管的服务跑在 session 0。**

- session 0 没有用户桌面，`libmpv` 的 D3D11 交换链创建失败：
  `Failed to create swapchain ... (0x887a0022)` → `Failed initializing any suitable GPU context!`
  → `no audio or video data played`。
- 本产品的核心就是"投屏出画面"，这使 SCM 托管在当前架构下不可用。
- 上一版自己的评估已经把它列为"最大的真实张力"，但当时只测了服务起停，**没有真的投一次屏**，
  也没有真的启动 UI，所以问题被当作"待决策项"挂了起来，直到用户实际使用才暴露。

两条出路：

| 方案 | 做法 | 评价 |
| --- | --- | --- |
| A. 视频呈现移到 UI 进程，服务保留 SCM 托管 | 服务管协议/播放控制，UI 承载视频窗口 | 改动大，且需要机器级安装（受限能力 `packagedServices` + `localSystemServices`），**不满足"用户级 MSIX"** |
| **B. 用户级包，服务在用户会话里运行（采用）** | 去掉 `windows.service`，UI 拉起服务 | 改动最小，窗口/GPU/数据目录/权限全部沿用绿色包已验证的路径 |

## 5. 采用方案的代价

- 服务不再"开机即在"：需要用户登录，且需启用开机启动（`startupTask`）或手动打开 UI。
- 没有 SCM 的崩溃自动重启：服务崩溃后由 UI 的重连/重启逻辑处理（UI 已有"重启服务"）。
- MSIX 渠道没有提权钩子：防火墙入站规则要用户手动加或由 UI 引导（未实现）。

## 6. 关键 schema 事实（仍然有效的部分）

`CT_StartupTask`：`TaskId`（必需）、`Enabled`、`DisplayName`。
`uap10:PackageIntegrity`：`Content Enforcement="off"` 允许替换包内文件。
`windows.fullTrustProcess` 与 `runFullTrust`：打包的桌面应用以完全信任运行，不受 AppContainer 限制。

> `windows.service`（`CT_Service`）需要受限能力 `packagedServices`，`StartAccount="localSystem"`
> 还需要 `localSystemServices`，属于机器级部署，**本项目不使用**。

## 7. 仍然未解决

- **MSIX 渠道的防火墙入站规则**：没有自动方案。
- **开机启动的重启实测**：`StartupTask` 路径只验证了编译，没有真正注销/重启。
- **干净机器上的安装**：依赖的 Windows App Runtime 缺失时的体验未验证。
- **卸载后的数据目录保留策略**：尚未决定，也未验证。
- **正式代码签名证书**未获取（阶段 F5）。
- **画面是否真的显示**未做截图确认，需要目视。
