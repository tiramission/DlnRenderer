# 测试指南

本文件记录当前仓库实际执行过的验证。命令均在 Windows 11 build 26300、.NET SDK 10.0.402 上运行。

## 1. 自动化测试

```powershell
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

实测结果：**351 通过 / 0 失败，0 跳过**（Core 25、Player 20、Dlna 207、Service 99）。

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

### 1.3 Dlna（110）

| 组 | 覆盖内容 |
| --- | --- |
| `SsdpMessageTests` | M-SEARCH 解析与拒绝、MX 钳制、目标匹配、响应与 NOTIFY 字段 |
| `NetworkInterfacesTests` | 显式地址优先、排除 198.18/15 隧道与 169.254/16 地址、地址属于在线网卡 |
| `DescriptionTests` | 设备身份、仅声明已实现的服务、SCPD 动作/参数与状态变量一致 |
| `AvTransportServiceTests` | URI 与元数据、scheme 白名单、Fault 码（402/600/701/718/401）、状态映射、位置/时长、Seek、错误不伪装成功 |
| `ConnectionManagerServiceTests` | `GetProtocolInfo` sink、连接信息、未知动作 |
| `DidlLiteParserTests` | 字段提取、损坏/空/XXE 输入容错 |
| `DlnaDeviceHostTests` | 回环上的真实 HTTP：描述/SCPD、SOAP 控制、Fault（含 `utf-8` 声明）、405/404/501、端口释放与冲突、重复启动 |

### 1.4 Service（13）

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

### 5.4 已知限制

- 位置（`RelativeTimePosition`）不通过事件推送，需控制端轮询 `GetPositionInfo`。
  这是 AV 规范的做法，也避免每个订阅者每秒收到 4 次 HTTP 请求。
- ConnectionManager 没有可事件化的变量；其订阅端点存在但初始事件为空属性集。