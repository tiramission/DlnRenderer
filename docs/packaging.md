# 分发形式决策（阶段 E / E1）

状态：**已决定，已实现，本机已验证**（2026-10-21）。

## 1. 决定

**绿色包 + 安装脚本**：`tools/package/build-dist.ps1` 产出一个自包含文件夹
（`DlnRenderer.exe` + `DlnRenderer.Service.exe` + `libmpv-2.dll` + WinUI 依赖），
`tools/package/install.ps1` 把它装到 `%ProgramFiles%\DlnRenderer` 并补上防火墙规则、
快捷方式与开机启动项，`tools/package/uninstall.ps1` 成对清理。

**不做 MSIX。** 这不是"暂时不做"，而是有明确理由的选择；条件变化时用 §5 的触发条件重新评估。

## 2. 为什么不是 MSIX

决定性因素是本项目的核心架构约束：**后台服务必须在 UI 退出后继续运行**。

| 约束 | MSIX 下的情况 |
| --- | --- |
| 服务进程常驻 | 打包应用可以启动 `runFullTrust` 子进程，但子进程的存活周期、桌面会话归属与"用户注销/重登"行为与普通进程不同，本项目**未验证** |
| 防火墙规则 | 打包应用的网络能力由 manifest 声明，而入站 UDP 1900 / TCP 端口需要管理员权限的防火墙规则；MSIX 无法在安装时可靠添加自定义入站规则 |
| libmpv 分发 | GPL-2.0-or-later（见 [licenses.md](licenses.md)）。MSIX 的 `PackageIntegrity` 会校验包内文件，用户替换 DLL（LGPL/GPL 再分发义务）与包完整性校验直接冲突 |
| 开机启动 | `StartupTask` 只对打包应用可用；本项目 UI 注册的是 HKCU Run（`StartupRegistration`），两种机制并存会增加测试面 |
| 服务独立启动 | 用户可能只启动 `DlnRenderer.Service.exe`（无 UI）。绿色包天然支持；MSIX 下需要 full-trust 启动器绕一圈 |

另一个现实原因：`Package.appxmanifest` 的 `Publisher` 仍是模板占位值 `CN=AppPublisher`，
真实的 Publisher DN 需要代码签名证书才能确定（阶段 F5），在证书就位前做 MSIX 等于把
签名问题提前变成阻塞项。

## 3. 为什么不做"自解压 exe / 安装器"

同一目标的更重方案（Inno Setup、WiX、MSI）能提供"添加或删除程序"入口，但：

- 首个可分发版本的目标用户是个人用户，一个 `install.ps1` 加一份说明已经够用；
- 安装器本身是新的构建与签名依赖，会把阶段 E 的验证面从"文件夹+脚本"扩大到"安装器行为"；
- 阶段 F5 拿到代码签名证书后，如果需要"添加或删除程序"条目，再升级到 MSI/WiX，
  改动集中在打包脚本，不影响产品代码。

如果后续需要，`packaging/` 的目录约定与卸载逻辑（`uninstall.ps1`）可以整体复用。

## 4. 实际交付物

| 文件 | 作用 |
| --- | --- |
| `tools/package/build-dist.ps1` | 发布 App 与 Service（Release, win-x64, 框架依赖），复制 libmpv 与 Assets，并**校验产物完整性**（缺 exe / 缺 native DLL / 缺图标即失败） |
| `tools/package/install.ps1` | 停旧实例 → 复制到 `%ProgramFiles%\DlnRenderer` → 添加防火墙规则 → 注册开机启动 → 创建快捷方式 |
| `tools/package/stop-service.ps1` | 通过 IPC `Shutdown` 干净停止服务（与 WinUI 托盘"退出应用"同一条路径） |
| `tools/package/firewall-rules.ps1` | `-Action Add` / `-Action Remove`：UDP 1900（SSDP）+ TCP 18090（描述/SOAP/GENA），限定在专用/域网络 |
| `tools/package/uninstall.ps1` | 停进程 → 删防火墙规则 → 删数据目录 → （可选）删快捷方式与开机启动 → 删安装目录 |

默认端口 **18090**（`ServiceStartupOptions.DefaultHttpPort`）：固定端口让防火墙规则、
控制端缓存与书签在重启后依然有效。

## 5. 重新评估的触发条件

满足任一条时重新评估 MSIX：

1. 需要进入 Microsoft Store（Store 只收 MSIX/MSIXbundle）。
2. 需要 per-machine 安装与"添加或删除程序"条目（用户明确要求）。
3. 阶段 F 结论采用 LGPL 构建且用户替换 DLL 成为分发要求——这条反而会**进一步排除** MSIX
   （包完整性校验与可替换 DLL 冲突），所以更可能导向"绿色包 + zip"。

## 6. 已知限制

- 只测了 x64；`Platforms` 里声明了 x86/ARM64，但没有对应 libmpv 构建（见 licenses.md）。
- `install.ps1` 与 `uninstall.ps1` 需要管理员 PowerShell（Program Files 与防火墙规则）。
  绿色包本身不需要：把 `dist/DlnRenderer` 复制到任意位置即可运行。
- 没有"添加或删除程序"条目，卸载靠 `uninstall.ps1`。
- 安装/升级/卸载的完整三步验证记录见 [testing.md](testing.md) §10。
