# 分发形式决策（阶段 E / E1 / E2）

状态：**两条渠道并存，均已在本机验证**（2026-10-22）。MSIX 是**用户级**包，不注册 Windows 服务。

| 渠道 | 产物 | 安装方式 | 服务如何运行 | 状态 |
| --- | --- | --- | --- | --- |
| 绿色包 | `dist/DlnRenderer/` 文件夹 + `install.ps1` | 复制到 `%ProgramFiles%\DlnRenderer`（管理员） | UI 在用户会话里拉起 `DlnRenderer.Service.exe` | 已验证 |
| MSIX（用户级） | `dist/msix/DlnRenderer-x64.msix` | `winapp cert install`（一次性，管理员）+ `Add-AppxPackage`（**当前用户，无需提权**） | 同上：UI 在用户会话里拉起服务 | 已验证（见 §6） |

两条渠道共用同一套 App/Service/libmpv 产物，区别只在"怎么摆文件、怎么安装、怎么卸载"。

## 1. 决定

**MSIX 是用户级包，服务不由 SCM 托管。** 这是实测后改变的结论，原因见 §2。

- 绿色包：`tools/package/install.ps1` 装到 `%ProgramFiles%\DlnRenderer`，补防火墙规则、
  快捷方式与 HKCU Run 开机启动项，`tools/package/uninstall.ps1` 成对清理。
- MSIX：`tools/package/build-msix.ps1` 出包并签名；安装是 `winapp cert install` +
  `Add-AppxPackage`，卸载走系统"设置 → 应用"。开机启动走清单里的 `windows.startupTask`，
  由设置页通过 `StartupTask` API 开关。

## 2. 为什么不用 `windows.service`（实测教训）

上一版把服务注册成 SCM 托管的打包服务（`desktop7:Service`，`LocalSystem`）。服务的起停、
自启、卸载清理都验证通过，**但投屏没有画面**：

```
mpv fatal: Failed to create swapchain: ... (0x887a0022)
mpv error: Failed initializing any suitable GPU context!
player status Error ... no audio or video data played
```

SCM 托管的服务运行在 **session 0**，那里没有用户桌面，GPU 交换链建不出来，mpv 无法输出视频。
本产品的核心用途就是"投屏出画面"，所以这条路在当前架构下不成立。

另一个事实：用户级包（`Add-AppxPackage` 不提权）**不能**注册 `windows.service`——该扩展需要
受限能力 `packagedServices` / `localSystemServices`，属于机器级部署。想要"用户级 MSIX"就必须放弃它。

改成用户级后的收益：

| 项 | 用户级 MSIX |
| --- | --- |
| 视频窗口 | ✅ 服务在用户会话（session 1）里，窗口、GPU、全屏置顶都和绿色包一致 |
| 数据目录 | ✅ `%LOCALAPPDATA%\DlnRenderer`，无需 ProgramData、无需 ACL |
| 安装权限 | ✅ 安装包本身不需要管理员（只有首次信任开发证书需要） |
| 卸载 | ✅ 系统卸载即可 |
| 需要的受限能力 | 仅 `runFullTrust` |

代价：服务不再"开机即在"，而是随用户登录（`windows.startupTask`）或手动打开 UI 才起来。
这与绿色包一致，也是 AGENTS.md §4 对首版的要求（普通后台子进程，不装 Windows Service）。

## 3. 实际交付物

| 文件 | 作用 |
| --- | --- |
| `tools/package/build-dist.ps1` | 绿色包：发布 App/Service，复制 libmpv 与 Assets，并校验产物完整性 |
| `tools/package/build-msix.ps1` | MSIX：先发布 Service 再发布 App 到同一暂存目录 → 复制 libmpv 与 Assets → `winapp package` + 签名 → **解包回验** |
| `tools/package/verify-service.ps1` | MSIX 体检：包已装、**无 SCM 服务**、清单含运行时依赖、UI 启动且不崩溃、服务在**用户会话**（非 session 0）、`description.xml` 可达 |
| `tools/package/install.ps1` / `uninstall.ps1` | 绿色包安装/卸载 |
| `tools/package/stop-service.ps1` | 通过 IPC `Shutdown` 干净停止服务。**按协议版本列表依次尝试**：它要停的往往是尚未升级的旧服务，而版本不匹配时服务端只回错误帧、不断连，所以同一条管道上可以试多个版本。升版时同步脚本里的版本列表 |
| `tools/package/firewall-rules.ps1` | UDP 1900（SSDP）+ TCP 18090（描述/SOAP/GENA），限定专用/域网络 |

`build-msix.ps1` 的解包回验会断言：清单**没有** `windows.service` / `packagedServices` /
`localSystemServices`；**有** `Microsoft.WindowsAppRuntime.2` 依赖、`runFullTrust`、`startupTask`；
包里有两个 exe、`libmpv-2.dll`、`AppIcon.ico`。断言前会先剥掉 XML 注释，避免清单里解释性文字被误匹配。

## 4. 绿色包与 MSIX 的差异（用户可见）

| 行为 | 绿色包 | MSIX（用户级） |
| --- | --- | --- |
| 服务生命周期 | UI 拉起；托盘"退出应用"时停；关窗后继续运行 | 同左 |
| 开机启动 | HKCU Run | `windows.startupTask`（设置页开关；用户也可在"设置 → 应用 → 启动"改） |
| 安装 | `install.ps1`（管理员 PowerShell） | `Add-AppxPackage`（当前用户） |
| 安装位置 | `%ProgramFiles%\DlnRenderer` | `%ProgramFiles%\WindowsApps\...`（受保护，用户不可写） |
| 升级 | 覆盖文件；脚本先用 `stop-service.ps1` 通过 IPC 停掉正在跑的服务（**无孤儿进程**） | 提高版本号后 `Add-AppxPackage`（**同版本号内容不同会被拒绝**，见 §5）。**没有提权钩子停旧服务进程** |
| 升级后 | 新 UI 拉起新服务，若失败需在设置页"重启后台服务" | 同左，但旧进程可能残留，见 §5 |
| 数据目录 | `%LOCALAPPDATA%\DlnRenderer` | `%LOCALAPPDATA%\DlnRenderer`（未被虚拟化） |
| 卸载 | `uninstall.ps1` | "设置 → 应用 → 卸载" |

## 5. 已知限制与未验证项

- **防火墙入站规则仍需单独添加。** manifest 的 `internetClientServer` / `privateNetworkClientServer`
  不创建防火墙规则。绿色包由 `firewall-rules.ps1` 补；MSIX 渠道**没有**安装期钩子能提权加规则，
  需要用户手动运行 `firewall-rules.ps1`，或在首次运行时由 UI 引导（**未实现**）。
- **同版本号重装会被拒绝**（`0x80073CFB`）。开发期重打包需先 `Remove-AppxPackage` 再安装；
  发布时必须递增版本号。
- **依赖的 Windows App Runtime 必须已安装。** 清单声明了对 `Microsoft.WindowsAppRuntime.2 ≥ 2.5.1`
  的框架依赖；缺失时 `Add-AppxPackage` 会失败而不是装完再崩。干净机器上的行为**未验证**。
- **服务不再开机即起**，只在用户登录后（启用开机启动时）或手动打开 UI 后可用。
- **UI 与后台服务必须同版本，升级后要重启服务。** 两者是独立可执行文件：旧服务会静默忽略它不
  认识的 IPC 字段（例如 `VideoTarget.Enabled`），线协议版本不匹配时则直接拒绝请求——表现为"修好
  的缺陷又出现了"。绿色包由 `install.ps1` 先停旧进程再覆盖，基本能自愈；**MSIX 渠道没有停服
  钩子**，跨协议版本升级后旧进程会残留，界面会提示"版本不一致"，需用户在设置页重启后台服务。
  契约与排查见 [`ipc-protocol.md`](ipc-protocol.md) §3、§6。
- **MSIX 下"开机启动"未做重启实测。** `StartupTask` 的 API 路径编译通过，但没有真正注销/重启验证。
- **正式代码签名。** 当前是**固定在仓库中**的 dev 证书（`tools/package/devcert.*`，
  指纹 `8781E2D1…`，有效期至 2027-10-22）；分发给其他机器仍需 CA 证书（阶段 F5）。
- 只测了 x64；没有 ARM64/x86 的 libmpv 构建（见 [licenses.md](licenses.md)）。

## 6. 用户级 MSIX 验证记录（2026-10-22）

| 检查项 | 结果 |
| --- | --- |
| 清单无 SCM 服务、含运行时依赖/`runFullTrust`/`startupTask` | ✅ `build-msix.ps1` 解包回验通过 |
| `Add-AppxPackage`（当前用户，无提权）安装 | ✅ `Status: Ok`，`Get-Service DlnRenderer.Service` 无结果 |
| UI 启动且不崩溃 | ✅ session 1，响应正常，窗口句柄有效，无 Application 1000 崩溃事件 |
| 服务由 UI 拉起，运行在用户会话 | ✅ `DlnRenderer.Service` session 1 |
| 日志与状态目录 | ✅ `C:\Users\jaign\AppData\Local\DlnRenderer\logs\`，日志标注 `launch: interactive/ui` |
| 视频窗口创建 | ✅ `video surface created (hwnd …)`，投送时 `video presenter fullscreen/hidden` 切换 |
| 真实控制端（192.168.1.72）发现 → 取描述/SCPD → 订阅 → `SetAVTransportURI` → `Play` | ✅ 日志完整 |
| 投送期间 mpv 错误（swapchain / GPU context） | ✅ **无**（上一版 session 0 下每次必现） |
| `description.xml` → 200，含 `AVTransport` | ✅ |

> 未覆盖：画面是否真的显示在屏幕上我没有做截图确认，只确认了窗口创建、presenter 切换和播放状态为
> `Playing`、无 mpv 报错。需要你目视确认一次。

## 7. CI 打包与发布（`release.yml`）

打包曾经只在一台机器上手工跑过，"能发布"于是依赖那台机器。现在整条路径在 CI 上可复现。

### 7.1 触发

手动（`workflow_dispatch`），三个输入：

| 输入 | 作用 |
| --- | --- |
| `version` | 必须与 `Directory.Build.props` 的 `<Version>` **和** `Package.appxmanifest` 的 `Identity Version` 一致；不一致直接失败 |
| `publish` | 关掉只产出 workflow artifact，不开 GitHub Release |
| `prerelease` | 是否标记为预发布 |

```powershell
# 正式版
gh workflow run release.yml -f version=0.2.0 -f publish=true -f prerelease=false
# 预发布（冒烟验证用）
gh workflow run release.yml -f version=0.2.1 -f publish=true -f prerelease=true
```

发正式版前必须把 `Directory.Build.props` 的 `<Version>` 与 `Package.appxmanifest` 的
`Identity Version`（三位版本 + `.0`）一起改上去，否则 workflow 第一步就失败。

**若本版本包含 IPC 线协议版本变更**（`IpcProtocol.Version`），还要：

- 同步 `tools/control-point/ipc-check.ps1` 的 `$protocolVersion` 与 `tools/package/stop-service.ps1`
  的版本列表——两者自己拼消息，过期会让脚本命令全被拒绝；
- 在 Release 说明里写明"升级后需重启后台服务"，因为旧进程不会自己退出（§5）。

### 7.2 步骤

1. `microsoft/setup-WinAppCli@v0.2`（钉 `v0.7.1`）——官方文档指定的 CI 安装方式，优于自己下载。
2. 预热打包工具链：`winapp tool -- makeappx /?` 与 `signtool /?`，**按输出横幅判断**而不是退出码
   （`makeappx /?` 即使成功也返回 1）。缺 Build Tools 时 winapp 会自行下载。
3. `fetch-libmpv.ps1 -Download`（双哈希）与 `choco install 7zip`。
4. 使用**仓库内固定的** dev 证书 `tools/package/devcert.pfx`：校验它与 `devcert.cer` 指纹一致、且未临近过期，不一致或即将过期直接失败。不会再生成新证书。
5. `build-dist.ps1` → `build-msix.ps1 -Cert <本次证书>` → `hash-release.ps1`。
6. 组装资产 + 导出 `.cer` + 写 `SHA256SUMS-assets.txt` + 生成发布说明。
7. `gh release create`。

### 7.3 发布资产

| 资产 | 用途 |
| --- | --- |
| `DlnRenderer-<ver>-win-x64.zip` | 绿色包（含 `install.ps1`） |
| `DlnRenderer-<ver>-win-x64.msix` | 用户级 MSIX |
| `DlnRenderer-dev.cer` | **公钥证书**（与仓库中的 `tools/package/devcert.cer` 逐字节相同），用户只需信任一次，后续版本无需重做 |
| `SHA256SUMS-assets.txt` | 上面每个资产的哈希 |
| `SHA256SUMS-portable.txt` | 绿色包内 86 个文件的逐文件哈希 |
| `release-manifest.json` | 版本、commit、libmpv 指纹、文件清单 |

`.pfx`（含私钥）不挂在 Release 上，但**它在公开仓库里**（`tools/package/devcert.pfx`，密码 `password`）。
这是为了固定签名身份而做的取舍，后果必须说清楚：

- **好处**：MSIX 升级要求签名身份不变且证书已受信任；固定证书让用户只信任一次，`.cer` 在每次
  发布中字节一致（哈希也不变）。
- **代价**：任何拿到仓库的人都能签出 Windows 会显示为 `CN=AppPublisher` 的包。因此这个证书
  **只证明身份标签，不证明来源**。验证来源请用 `SHA256SUMS-assets.txt` 里的哈希，或 GitHub
  Release 页面的 asset digest。
- **期限**：证书有效期至 **2027-10-22**。workflow 在剩余不足 14 天时会直接失败，提醒续期
  （`-ForceCert` 重新生成后需同步导出新的 `.cer`，并接受旧安装无法升级）。
- **正式发布**仍应换成 CA 证书；届时 Publisher 会变化，属于一次性的破坏性切换，需要写进升级说明。

### 7.4 用户如何装（dev 证书）

```powershell
# 1. 一次性信任（管理员）
Import-Certificate -FilePath .\DlnRenderer-dev.cer -CertStoreLocation Cert:\LocalMachine\Root
# 2. 安装（当前用户，无需提权）
Add-AppxPackage -Path .\DlnRenderer-<版本>-win-x64.msix
```

### 7.5 实测（2026-10-22）

证书固定在 `8781E2D1…` 之后，连续发了 `v0.1.0`（run `39714170910`）与正式版 `v0.2.0`
（run `39715222046`），均全部步骤通过、零 annotation。`v0.2.0` 是首个**正式版**（非 pre-release），
GitHub 已标记为 `Latest`。

| 检查 | 结果 |
| --- | --- |
| 两个版本签名者指纹 | ✅ 均为 `8781E2D1…` |
| `DlnRenderer-dev.cer` 哈希跨版本 | ✅ 两版均为 `c2c4554f…`，与仓库内 `devcert.cer` 一致 |
| 下载后 `sha256sum -c` 重算 | ✅ `OK`，与 GitHub 自报的 asset digest 一致 |
| 信任 `.cer` 后签名状态 | ✅ `UnknownError` → `Valid` |
| `Add-AppxPackage` 安装发布包 | ✅ `Status: Ok` |
| **0.1.0 → 0.2.0 原地升级**（不先卸载） | ✅ 安装后版本变为 `0.2.0.0`，`Status: Ok` |
| 正式版**全新安装**（无旧版本） | ✅ `0.2.0.0`，`Status: Ok`；签名 `Valid`，签名者 = `8781E2D1…` |
| 旧版本回装被系统拒绝（防降级） | ✅ `0x80073D06`，提示已装更高版本 `0.2.0.0`（预期行为，非缺陷） |
| 发布说明 markdown 渲染 | ✅ 行内代码与代码块正常（不再出现 ```` `` ````） |
| 清理（卸载 + 移除证书信任） | ✅ 残留包 0，根存储中 AppPublisher 证书 0 |

「原地升级」这一项是固定证书存在的主要理由：如果用每次新生成的证书，上表这一行必然失败。

**未覆盖**：干净机器（无 .NET 10 桌面运行时、无 Windows App Runtime）的安装体验；
ARM64；CA 签名。发布说明里已如实声明这些未知项。
