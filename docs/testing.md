# 测试指南

本文件记录当前仓库实际执行过的验证。命令均在 Windows 11 build 26300、.NET SDK 10.0.402 上运行。

## 1. 自动化测试

```powershell
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

实测结果：**369 通过 / 0 失败，0 跳过**（Core 25、Player 20、Dlna 218、Service 106）。

连续 3 次完整运行均为 0 失败（`dotnet test DlnRenderer.sln -c Debug --no-build`）。

### 1.1 Core（25）

| 组 | 覆盖内容 |
| --- | --- |
| `PlaybackStateMachineTests` | 合法/非法转换、幂等重复状态、事件载荷、未定义枚举值拒绝 |
| `PlaybackStatusTests` | 默认值、`with` 不可变性、`MediaInfo` 必需 URI |

### 1.2 Player（15）

纯逻辑（不加载 native DLL）：

| 测试 | 覆盖内容 |
| --- | --- |
| `MpvStateMapperTests`（6） | idle/paused/buffering/error 映射；映射结果在 `Transitioning` 下均为合法转换 |
| `PositionThrottleTests`（3） | 首次必发、间隔内丢弃、`Reset` 强制放行 |

原生集成（需要 `libmpv-2.dll`，缺失时以明确 Skip 跳过）：

| 测试 | 覆盖内容 |
| --- | --- |
| 音频播放生命周期 | 真实解码 WAV：时长、`MediaKind.Audio`、播放推进、暂停后位置冻结、Seek、音量、静音、停止 |
| 视频解码 | 真实解码 MP4 夹具：时长、`MediaKind.Video`、播放推进 |
| 错误路径 | 打开不存在的文件 → `Error` + 非空错误信息 |
| 资源释放 | 释放 < 5 s、重复释放安全、释放后调用抛 `ObjectDisposedException` |
| 顺序加载 | 音频→视频→音频，类型正确、位置重置 |
| 播放中释放 | 播放中释放 < 5 s |

### 1.3 Dlna（218）

| 组 | 覆盖内容 |
| --- | --- |
| `SsdpMessageTests` | M-SEARCH 解析与拒绝、MX 钳制、目标匹配、响应与 NOTIFY 字段 |
| `NetworkInterfacesTests` | 显式地址优先、排除 198.18/15 隧道与 169.254/16 地址、地址属于在线网卡 |
| `DescriptionTests` | 设备身份、仅声明已实现的服务、SCPD 动作/参数与状态变量一致 |
| `AvTransportServiceTests` | URI 与元数据、scheme 白名单、Fault 码（402/600/701/718/401）、状态映射、位置/时长、Seek、错误不伪装成功 |
| `ConnectionManagerServiceTests` | `GetProtocolInfo` sink、连接信息、未知动作 |
| `DidlLiteParserTests` | 字段提取、损坏/空/XXE 输入容错 |
| `DlnaDeviceHostTests` | 回环上的真实 HTTP：描述/SCPD、SOAP 控制、Fault（含 `utf-8` 声明）、405/404/501、端口释放与冲突、重复启动 |

### 1.4 Service（106）

本节只展开 IPC；窗口、GENA 与 RenderingControl 的服务层测试见 §4.1、§5.1。

`IpcServerTests` 通过真实 Named Pipe 验证：

- 连接后收到初始快照，`GetStatus` 返回状态
- `Play`/`Pause`/`Stop`/`Seek`/`SetVolume` 到达渲染器
- 非法参数、未知命令、协议版本不一致均返回失败且不触达渲染器
- 渲染器失败被如实报告（不伪装为成功）
- 状态变化会推送给已连接的客户端
- 协议违规只断开该客户端，服务继续服务新客户端
- 客户端断开后服务仍在监听，可重新连接
- 多客户端可独立下发命令

> 已知历史缺陷（已修复并有测试覆盖）：命名管道在异步模式下默认缓冲区很小，
> 服务端推送快照与客户端下发命令同时发生时会出现双向写死锁，表现为测试进程挂起。
> 修复方式是服务端显式指定 `IpcProtocol.PipeBufferBytes` 缓冲区。

## 2. 真实链路验收（手工/脚本）

`tools/control-point/` 下的脚本使用**真实协议**（真实 UDP 组播、真实 HTTP、真实 SOAP），
不是 mock。它们不是第三方控制端，替代不了手机端验收，但能客观复现协议行为。

### 2.1 准备

```powershell
# 1. 媒体源（模拟控制端提供的 HTTP 媒体）
node tools/control-point/media-server.js "$env:TEMP\dln-media" 18777

# 2. 后台服务
src\DlnRenderer.Service\bin\Debug\net10.0-windows\DlnRenderer.Service.exe --http-port 18090
```

### 2.2 发现与控制

```powershell
# SSDP 发现：应返回 3 条标准响应，LOCATION 指向真实网卡地址
pwsh tools/control-point/ssdp-discover.ps1 -SearchTarget "urn:schemas-upnp-org:device:MediaRenderer:1"

# 设备描述 + ConnectionManager + 完整 AVTransport 流程
pwsh tools/control-point/renderer-check.ps1 -MediaUrl "http://<本机LAN地址>:18777/long.mp4"

# IPC（与 UI 相同的命令通道）
pwsh tools/control-point/ipc-check.ps1
```

### 2.3 实测结果

| 项 | 结果 |
| --- | --- |
| SSDP 发现 | 3 条响应（`uuid`、`upnp:rootdevice`、`MediaRenderer:1`），`LOCATION` 为真实 LAN 地址 |
| 设备描述/SCPD | HTTP 200，AVTransport 与 ConnectionManager 均可达 |
| `SetAVTransportURI` → `Play` | `GetTransportInfo` 返回 `PLAYING` |
| `GetPositionInfo` | 返回真实时长与推进中的 `RelTime`，回显 DIDL 元数据 |
| `Pause` | `PAUSED_PLAYBACK` |
| `Seek(REL_TIME 00:00:30)` | `RelTime=00:00:30` |
| `Stop` | `STOPPED` |
| 非法 `InstanceID` | SOAP Fault 500，`errorCode=718`，XML 声明为 `utf-8` |
| 视频渲染 | 服务窗口经 `PrintWindow` 捕获：3676/4400 采样点为高饱和像素，画面为测试图案 |
| UI 退出后 | 服务进程存活，设备仍可被描述与发现 |
| UI 重新打开 | 复用同一服务进程（未产生重复实例） |
| IPC 无媒体时 `Play` | 明确失败，不再伪装成功 |

### 2.4 仍需人工确认

- **第三方控制端**（BubbleUPnP、Windows「投送到设备」等）：本机未安装可用的第三方 DLNA 控制端，
  尚未完成该项验收。需在真实手机上补测。
- **声音可听**：已验证 `ao=wasapi` 实际输出，但未经人耳确认。
- ARM64 与 Windows 10 未实测。

---

## 3. 局域网发现专项验证（真实手机控制端）

### 3.1 环境

| 项 | 值 |
| --- | --- |
| 电脑 | Windows 11 build 26300，以太网 `192.168.1.9/24`，网关 `192.168.1.1` |
| 其他网卡 | Hyper-V `172.25.96.1`、WSL `172.20.80.1`、VPN 隧道 `198.18.0.1`（均为虚拟/隧道） |
| 手机 | 同一 Wi-Fi `192.168.1.x` |
| 控制端 | BubbleUPnP、手机视频 App 的「投屏」按钮 |
| 防火墙 | 自定义 `_all` 入站规则（Allow / Any），未阻断 |
| 系统服务 | Windows `SSDPSRV` 正在运行并占用 UDP 1900 |

### 3.2 诊断手段

本机发出的多播不会被本机监听器收到，**无法用本机抓包观察服务自身的响应**，
因此按下列方式取证：

1. 服务端日志区分「未收到请求 / 收到但未匹配 / 已发送响应」，并记录来源地址、ST、LOCATION。
2. HTTP 层记录每个 `GET`/`POST` 的来源地址与 SOAP 动作名。
3. 以前一版本 `validate-description.ps1` 逐项校验描述文档结构。
4. 用真实手机控制端反复回访，观察它是否继续请求 SCPD 与 SOAP。

### 3.3 结果对照

| 阶段 | BubbleUPnP | 视频 App | 判定依据 |
| --- | --- | --- | --- |
| 初始版本 | 找不到 | 找不到 | 服务只应答设备级 ST，客户端取描述后不再请求 SCPD |
| 补 `X_DLNADOC`/`URLBase`/相对 URL | 找不到 | 找不到 | 客户端仍只取描述 |
| 补服务级 ST 应答 | **能找到** | 找不到 | BubbleUPnP 搜 `service:AVTransport:1`，此前被判定「not answered」 |
| 换成 MiniCast 的 392 条协议清单 | 能找到 | 找不到 | 解决「可见但不可投送」 |
| **描述改用默认命名空间（去掉 `d:` 前缀）** | **能找到** | **能找到并投屏** | 投屏链路 `SetAVTransportURI → 加载 → Play` 全部出现在日志中 |

### 3.4 投屏链路日志（实测节选）

```text
soap action SetAVTransportURI from 192.168.1.72
player loading http://192.168.1.72:33649/xxxx.mp4
soap action Play from 192.168.1.72
player command play applied
soap action GetTransportInfo from 192.168.1.72   (控制端持续轮询)
```

### 3.5 仍然存在的限制

- 「系统投屏」（控制中心里的投屏按钮）在 Android 上通常是 Miracast/Wi-Fi Direct，
  与 DLNA 是不同协议；本机 WLAN 断开时它看不到设备属预期。DLNA 投送请用
  视频 App 或 BubbleUPnP 内的「投屏/渲染器」入口。
- GENA 事件未实现，控制端靠轮询获取状态。
- 未做长时间播放与网络中断恢复的压力测试。
---

## 4. 投屏窗口全屏与置顶（本轮新增）

### 4.1 自动化覆盖

| 测试 | 数量 | 覆盖内容 |
| --- | --- | --- |
| `VideoWindowPolicyTests` | 14 | Play 后全屏；Pause/Buffering/Transitioning 保持；Stop/Error 隐藏；**预加载（paused 但未 Play）不占屏**；纯音频不占屏；媒体切换时的陈旧事件不驱动窗口 |
| `FullscreenGeometryTests` | 11 | 覆盖整块显示器（非工作区）；多显示器负坐标；首选显示器保持；显示器拔出回退；分辨率变化重算；真实桌面至少一个可用显示器 |
| `VideoPresenterTests` | 14 | 真实 HWND：无标题栏/无边框的 WS_POPUP、`WS_EX_TOPMOST`、`WS_EX_TOOLWINDOW`、矩形等于某块显示器；重复 Show 幂等；Hide 保留 HWND 且退出置顶；**Hide 后再 Show 必须重新显示 mpv 子窗口并恢复尺寸**；反复 Play/Stop 循环；并发 Show/Hide 后状态自洽；Dispose 释放 |
| `SessionWindowLifecycleTests` | 8 | `RendererSession` + 真实 libmpv：Load 不占屏 / Play 占屏并置顶；Pause 保持、Stop 隐藏；播完自动隐藏；切换媒体复用同一 HWND；连续 Play/Stop；加载失败不留空白全屏；Seek/Pause 不掉出置顶；快照与窗口一致 |

窗口相关测试归入 `video-window` collection（`DisableParallelization = true`）：置顶带是桌面级共享资源，并行执行会互相干扰。

### 4.2 真实服务端到端

`tools/control-point/verify-fullscreen.ps1` 面向人工确认（Escape、Alt+Tab、Win 键）；
自动化版本的 21 项检查通过 SOAP 驱动真实服务并读取 Win32 状态：

| 阶段 | 断言 |
| --- | --- |
| 启动 | 窗口隐藏、`WS_EX_TOOLWINDOW`、初始 1x1 |
| `SetAVTransportURI` | 仍隐藏（预加载不抢屏） |
| `Play` | 可见、置顶、覆盖整块显示器 |
| `Pause` / `Seek` | 保持可见与置顶，HWND 不变 |
| 切换媒体 | 同一 HWND，仍只有一个 mpv 表面 |
| `Stop` | 隐藏、退出置顶、HWND 保留 |
| 重新 `Play` | 同一 HWND 再次全屏 |
| 服务退出 | 进程退出，无残留窗口 |

实测：**26/26 通过**（显示器 2560x1440 @150% 缩放）。其中 4 项专门检查 mpv 子窗口是否可见、是否覆盖整窗——只看顶层窗口会漏掉"有声音无画面"。

### 4.3 本轮复现并修复的缺陷

1. **DPI 未声明**：进程按 96 DPI 被虚拟化，2560x1440@150% 显示器报告为 1707x960，全屏窗口只覆盖屏幕 2/3。
   必须在应用清单声明 `PerMonitorV2`；`Main` 运行后再调用 `SetProcessDpiAwarenessContext` 会被系统拒绝。
2. **置顶样式未生效**：对隐藏窗口设置 `WS_EX_TOPMOST` 后 Windows 不保留该样式；必须在窗口可见后再置顶并回读验证。
3. **预加载占屏**：libmpv 打开文件即为 paused，旧策略把 Paused 一律当"暂停中"，导致仅设置 URI 就黑屏。
4. **Stop 后事件倒灌**：Stop 与 loadfile 交错时，旧文件的 end-file 会把状态改回 Stopped，令新文件卡在"已加载但未播放"。
   现按 libmpv 的 playlist entry 编号丢弃过期事件。
5. **状态机粘滞**：`Stopped -> Paused` 不是合法转换，重播时状态机被永久卡住；加载入口与真实播放恢复均显式经过 `Transitioning`。
6. **快速 Play/Stop 后重载丢失事件**：重载带重试与有界等待，失败时明确报错而不是假装成功。
7. **Stop 后再投屏只有声音没有画面**（回归）：`Hide` 会把 mpv 子窗口隐藏并缩到 1x1，而 `ShowFullscreen` 的幂等快速路径只检查顶层矩形，
   于是顶层"看起来已全屏"直接返回，子窗口一直不可见。现快速路径同时校验子窗口可见性，正常路径显式调用 `ShowWindow(child, SW_SHOW)`。
   该缺陷在真机 Stop→重播时复现，截图取证后修复。
---

## 5. GENA 事件订阅与 RenderingControl（本轮新增）

### 5.1 自动化覆盖

| 测试 | 数量 | 覆盖内容 |
| --- | --- | --- |
| `GenaSubscriptionTests` | 23 | 订阅返回 SID/TIMEOUT、续订保留 SID、无效 SID → 412、SID+CALLBACK 混用 → 400、过期订阅不可续订、取消订阅与重复取消、缺失/错误头 → 412、回调 URL 安全策略、初始事件 `SEQ=0`、状态变化 SEQ 递增、相同状态不重复通知、多订阅者各自独立计数、RenderingControl 音量/静音事件、XML 转义、失败订阅者不影响他人、拒绝连接的端口、无响应回调超时、订阅数上限 → 500、服务关闭清理、过期清理 |
| `RenderingControlServiceTests` | 35 | 音量读取/写入回读、边界 0/100、非法值（越界→601、格式错→402）不触及播放器、静音 0/1/true/false、非法布尔值拒绝、Channel 缺省为 Master、未知 Channel、未知/非法 InstanceID、未实现动作 → 401、预设、播放器故障 → 501 |
| `RenderingControlIntegrationTests` | 10 | **真实 libmpv**：SOAP SetVolume 真正改变播放器音量、GetVolume 读回播放器值、静音真实生效、音量变化触发 GENA 事件且携带实际值、快照与播放器一致、音量/静音在停止与切换媒体后保留、播放中与停止时均可设置、越界值 fault 且不改播放器、静音事件送达 |
| `VolumeStateConsistencyTests` | 5 | 连续写入后播放器与快照一致且每次写入立即可读、取消订阅与发布并发、快照与 SOAP 对静音的表述一致、慢回调下关闭不阻塞、播放失败后事件系统仍可用 |
| `MpvCommandOrderingTests` | 5 | 音量/静音在 setter 返回后立即可观测、播放期间同样成立、连续写入以最后一次为准、越界拒绝 |

### 5.2 真实服务端到端（loopback + 真实 LAN 回调）

`tools/control-point/verify-eventing.ps1` 用真实 HTTP 回调端点驱动真实服务：

| 阶段 | 断言 |
| --- | --- |
| 订阅 | AVTransport / RenderingControl 返回 `uuid:` SID，TIMEOUT 按请求授予 |
| 初始事件 | `SEQ=0`、包含 `propertyset` 与 `LastChange`、初始状态 STOPPED、带 `NT`/`NTS` |
| 续订 | 返回同一 SID，更新 TIMEOUT |
| 音量 | `SetVolume` 触发携带新值的事件，`GetVolume` 读回 63 |
| 静音 | `SetMute` 触发事件，`GetMute` 读回 1 |
| 非法输入 | 越界音量 → 601，且播放器仍为 63 |
| 订阅拒绝 | 缺 CALLBACK、link-local（169.254.169.254）、loopback 回调均 → 412 |
| 取消 | UNSUBSCRIBE → 200，重复 → 412 |

实测：**22/22 通过**。

### 5.3 尚未完成的验证（重要）

- **真实第三方控制端未完成本轮回归。** 本轮结束时服务日志中**没有任何来自手机的投屏记录**
  （`SetAVTransportURI` 计数为 0，唯一的 SUBSCRIBE 来自本机自测）。
  因此**不能声称 GENA 已通过真实客户端互操作验证**。
- 上一轮（阶段 2.1）确认过 BubbleUPnP 与手机视频 App 能发现并投屏；本轮改动没有修改 SSDP、
  设备描述结构或 AVTransport 动作，自动化回归覆盖了这些路径，但**实机复测仍需补做**。
- 曾观察到手机（192.168.1.12）取走描述后未请求 SCPD，当时服务已停止运行，
  不能据此判断客户端行为。
- 阶段 B 的补测、取证工具与手机端待执行清单见 **§7**。

### 5.4 已知限制

- 位置（`RelativeTimePosition`）不通过事件推送，需控制端轮询 `GetPositionInfo`。
  这是 AV 规范的做法，也避免每个订阅者每秒收到 4 次 HTTP 请求。
- ConnectionManager 没有可事件化的变量；其订阅端点存在但初始事件为空属性集。
---

## 6. 网络变化恢复与日志保留（阶段 A）

### 6.1 网络变化恢复（A4）

| 检查 | 方式 | 结果 |
| --- | --- | --- |
| 接口差异：新增 / 消失 / 地址更换 / 全部断开 / 幂等 | 9 个单元测试（`MembershipDeltaTests`） | 通过 |
| 事件突发不丢弃、不异常、成员集不变 | 真实 `SsdpServer` 反射触发 5 次事件 | 通过 |
| 启动后多播成员与描述端点 | `verify-network-recovery.ps1 -Simulate` | 3 个成员，HTTP 200 |
| **真实拔插网线 / 切换 Wi-Fi** | **人工，需管理员权限** | **未验证** |

**为什么未验证**：本机无管理员权限，`Disable-NetAdapter` / `New-NetIPAddress` 均被拒绝；
`ipconfig /renew` 没有引起地址变化（地址未变时服务正确地保持静默）。
真实网络切换需要人工在管理员会话中执行。

### 6.2 日志保留（A5）

| 检查 | 方式 | 结果 |
| --- | --- | --- |
| 超过 7 天的文件被删除 | 7 个单元测试 + 手动植入 10/30 天文件 | 通过 |
| 窗口边界（第 7 天）保留 | 单元测试 | 通过 |
| 当天文件永不删除 | 单元测试 | 通过 |
| 非 `service-yyyyMMdd.log` 不受影响 | 单元测试 + 手动植入无关文件 | 通过 |
| 启动时即触发清理 | 单元测试 + 实机启动验证 | 通过 |

---

## 7. 阶段 B：真实控制端互操作验收

### 7.1 判定规则

只有**真实控制端**（日志中不是本机地址的客户端）留下的证据才算通过。
本机脚本能证明协议行为，但不能替代第三方客户端；报告中的「仅本地脚本」不计入阶段 B 完成。

### 7.2 本轮实测：真实控制端（2026-10-21 18:09–18:10）

证据来源：`%LOCALAPPDATA%\DlnRenderer\logs\service-20261021.log` 最近一次服务会话，
由 `stage-b-report.ps1` 生成（见 [`docs/stage-b-evidence.md`](stage-b-evidence.md)）。

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 设备发现 | **通过** | 4 台局域网设备共 73 次 M-SEARCH，服务应答 17 次 |
| 设备描述 / SCPD | **通过** | `192.168.1.72` GET 描述 1 次、SCPD 3 次 |
| 视频投送 | **通过（服务端）** | `SetAVTransportURI` + `Play`，加载 `http://192.168.1.72:57645/external/video/media/1000034306.mp4`，`kind=Video`，位置持续推进 |
| 暂停 / 继续 | **通过** | `Pause` 2 次、`Play` 3 次，状态在 `PLAYING` 与 `PAUSED_PLAYBACK` 间切换 |
| Seek | **通过** | `Seek` 1 次，随后 `GetPositionInfo` 位置跳变 |
| 停止 | **通过** | `Stop` 1 次 |
| 音量 / 静音 | 客户端未发送 | `GetVolume` / `SetVolume` / `GetMute` / `SetMute` 调用均为 0 |
| 事件订阅 | **失败** | SUBSCRIBE 3 次全部被接受，初始 NOTIFY 全部返回 HTTP 412 |
| 音乐投送 | 未发生 | 本次会话只有一条 `player loading`，且为 videoItem |
| UI 退出后继续播放 | 未验证 | 会话以 `ctrl+c` 结束 |

日志原文：

```text
2026-10-21 18:09:31.140 soap action Stop from 192.168.1.72
2026-10-21 18:09:31.160 soap action SetAVTransportURI from 192.168.1.72
2026-10-21 18:09:31.167 player loading http://192.168.1.72:57645/external/video/media/1000034306.mp4
2026-10-21 18:09:31.301 soap action Play from 192.168.1.72
2026-10-21 18:09:35.502 soap action Pause from 192.168.1.72
2026-10-21 18:09:38.614 soap action Play from 192.168.1.72
2026-10-21 18:10:07.841 soap action Seek from 192.168.1.72
2026-10-21 18:09:29 SUBSCRIBE RenderingControl from 192.168.1.72 accepted -> http://192.168.1.72:58645/dev/.../RenderingControl/event/cb
2026-10-21 18:09:31.828 notify failed for uuid:0903b795-... (HTTP 412); 1/5
```

「通过（服务端）」只说明服务端收到并执行了投送，画面是否出现、声音是否可听仍需人眼确认。
同日 14:13 会话中，同一客户端订阅过 AVTransport 与 RenderingControl，
`NOTIFY` 记录为 `timed out`（回调 `192.168.1.72:8081`）与 `HTTP 412`，没有任何一次 2xx。

#### 本机真实协议回归

- `SetAVTransportURI` → `Play` → `PLAYING` → `GetPositionInfo`（2 分钟测试片，位置推进）
- `Pause` → `PAUSED_PLAYBACK`；`Seek(REL_TIME 00:00:30)` 回读 30 秒；`Stop` → `STOPPED`
- 音频 DIDL（`audioItem.musicTrack`）走同一链路，30 秒 MP3 正常推进
- GENA + RenderingControl：**22/22 通过**（本机回调，非第三方）

> 本机无法自收多播（§3.2），本地回归用 `renderer-check.ps1 -Location` 跳过发现；
> 发现能力由真实客户端日志证明。

### 7.3 验收清单与已记录结果

重新取证命令：

```powershell
# 1) 启动服务（日志写入 %LOCALAPPDATA%\DlnRenderer\logs\service-yyyyMMdd.log）
src\DlnRenderer.Service\bin\Debug\net10.0-windows\DlnRenderer.Service.exe --http-port 18090

# 2) 手机完成操作后生成证据表
pwsh tools/control-point/stage-b-report.ps1 -OutputPath docs/stage-b-evidence.md
```

| 检查 | 192.168.1.72（本轮真实客户端） | BubbleUPnP | 手机视频 App | 判定依据 |
| --- | --- | --- | --- | --- |
| 设备发现 | 通过 | — | — | M-SEARCH + 服务应答 |
| 视频投送 | 通过（服务端） | 待填 | 待填 | `SetAVTransportURI` + `Play` + 实际加载 |
| 音乐投送 | 未发生 | 待填 | 待填 | 本次会话没有 audioItem |
| 暂停 / 继续 | 通过 | 待填 | 待填 | `Pause` / `Play` 状态切换 |
| 停止 | 通过 | 待填 | 待填 | `Stop` → `STOPPED` |
| Seek | 通过 | 待填 | 待填 | `Seek` 后位置跳转 |
| 音量 | 客户端未发送 | 待填 | 待填 | RenderingControl 调用为 0 |
| 静音 | 客户端未发送 | 待填 | 待填 | 同上 |
| 事件订阅 | **失败（HTTP 412）** | 待填 | 待填 | SUBSCRIBE 3 次，NOTIFY 2xx 为 0 |
| 状态观察 | 无法判断 | 待填 | 待填 | 需要客户端截图 |
| UI 退出 | 未验证 | 待填 | 待填 | 需要真实会话 |

填写规则：客户端没有该入口时写「客户端未提供」，不要写「渲染器不支持」；
不得用「代码已实现」代替「已验证」。

### 7.4 本轮修复的缺陷（B5 的前置工作）

| 缺陷 | 影响 | 修复 |
| --- | --- | --- |
| NOTIFY 成功不写日志 | B3 无法取证，只能看到失败 | 记录 SID、SEQ、回调、HTTP 状态、耗时 |
| AVTransport 事件体缺 `AVTransportURI` / 元数据 | 播放中途订阅的客户端无法从事件得知曲目 | 初始与变化事件都带 URI 与 DIDL 元数据（新增回归测试） |
| `verify-eventing.ps1` 结束时不退出 | 检查跑完后进程挂起 | 回调线程改为轮询 accept + 读超时 |
| `renderer-check.ps1` 拼接相对 URL | 把规范的相对 `controlURL` 误判为非法 | 按描述 URL 解析 |
| `media-server.js` 正斜杠 root 全 404 | 投送测试全部失败 | `path.resolve` 后再做包含检查 |
| 本机无法验证 SSDP 发现 | 本机多播不自收 | 新增 `-Location` 跳过发现；发现由真实客户端日志证明 |
| 报告将 0 次成功 NOTIFY 判为「通过」 | 验收表出现假阳性 | 事件订阅判定同时要求 SUBSCRIBE 与 NOTIFY 2xx |
| 报告未按服务会话截取 | 旧会话数据混入本轮证据 | 默认只统计最近一次 `service starting` 之后；`-AllSessions` 可覆盖 |

### 7.5 仍未验证

- 真实控制端的音量/静音与事件订阅（投送已通过服务端，音乐未测）。
- 画面出现与否、声音是否可听（需要人眼/人耳确认）。
- 关闭 UI 后继续播放、重开 UI 恢复状态。
- 封面与元数据展示（UI 未使用 `AlbumArtUri`）。
- 网络中断恢复的真实拔插场景（§6.1）。

### 7.6 已记录、本轮不修复的问题

| # | 问题 | 证据 | 初步判断 |
| --- | --- | --- | --- |
| 1 | 真实客户端 GENA `NOTIFY` 全部失败 | SUBSCRIBE 3 次均被接受，初始 NOTIFY 均返回 HTTP 412；同日 14:13 另有 `timed out` | 可能因初始事件在 SUBSCRIBE 200 响应之前发出，客户端尚未登记 SID 而以 412 拒绝；需改为先响应再投递初始事件，并用同一客户端复测 |
| 2 | 14:13 会话 `NOTIFY timed out` | 回调地址 `192.168.1.72:8081` 不可达 | 可能是客户端回调服务已停止，未确认 |
| 3 | 音量/静音未被真实客户端调用 | 四个动作调用次数均为 0 | 无法证明第三方互操作，也不能判定为不支持 |
| 4 | 本次没有音乐投送记录 | 唯一 `player loading` 为 videoItem | 需要再测一次音乐 |

以上四项只记录，不在本轮修改；修复后必须用真实客户端重新生成证据表。