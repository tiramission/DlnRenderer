# 测试指南

本文件记录当前仓库实际执行过的验证。命令均在 Windows 11 build 26300、.NET SDK 10.0.402 上运行。

## 1. 自动化测试

```powershell
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

实测结果（2026-10-22）：**403 通过 / 0 失败，0 跳过**（Core 43、Player 23、Dlna 222、Service 115）。

连续 3 次完整运行均为 0 失败（`dotnet test DlnRenderer.sln -c Debug --no-build`）。

### 1.1 Core（43）

| 组 | 覆盖内容 |
| --- | --- |
| `PlaybackStateMachineTests` | 合法/非法转换、幂等重复状态、事件载荷、未定义枚举值拒绝 |
| `PlaybackStatusTests` | 默认值、`with` 不可变性、`MediaInfo` 必需 URI |
| `SettingsStoreTests`（18，阶段 C） | 缺文件给默认值、读写往返、JSON 键名可读、损坏/错形/未知字段容错、非法设备名不落盘、长度上下界、原子写入（临时文件不残留）、覆盖写 |

### 1.2 Player（23）

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

### 1.4 Service（110）

本节只展开 IPC；窗口、GENA 与 RenderingControl 的服务层测试见 §4.1、§5.1。

`ServiceStartupOptionsTests` 覆盖命令行解析与阶段 C 新增的设置优先级：
`ApplySettings` 只补未显式给出的值，`--device-name` / `--no-hwdec` 等命令行始终优先于文件设置。

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
| 换成 MiniCast 的格式协议清单 | 能找到 | 找不到 | 解决「可见但不可投送」（阶段 D 后按实测收紧为 277 条） |
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

> 本节是阶段 2.1 结束时的快照。阶段 B 已补做真实控制端互操作，结果见 **§7**（投送链路通过，
> 但事件订阅因 `NOTIFY` 返回 412 仍未通过），那里才是当前状态。

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

## 8. 阶段 C：设置、托盘与主界面（真实窗口验收）

### 8.1 验收方式

不靠“代码写了就算”：应用由 `winapp run` 以真实窗口启动，再用 `winapp ui`（UI Automation，
即屏幕阅读器同款读取方式）逐项操作和读取控件，证据同时取自 UI 与服务日志。

```powershell
# 1) 启动（会自动构建）；--log 是 UI 侧诊断日志
winapp run src/DlnRenderer.App -c Debug -- --log "$env:TEMP\dln-ui.log"

# 2) 逐项验证（-a 传进程 PID 或进程名）
winapp ui invoke "设置" -a <app>
winapp ui set-value DeviceNameBox "DlnRenderer-Test" -a <app>
winapp ui search "保存" -a <app>
winapp ui invoke <保存按钮 slug> -a <app>

# 3) 托盘验证：先展开隐藏图标区，再定位图标
winapp ui invoke "显示隐藏的图标" -a explorer
winapp ui search "DlnRenderer" -a explorer
winapp ui click <图标 slug> -a explorer --double   # 双击恢复窗口
winapp ui click <图标 slug> -a explorer --right    # 右键菜单
```

服务日志：`%LOCALAPPDATA%\DlnRenderer\logs\service-yyyyMMdd.log`。
设置文件：`%LOCALAPPDATA%\DlnRenderer\settings.json`。
窗口截图（本地产物，`artifacts/` 在 `.gitignore` 中）：`artifacts/stage-c/*.png`。

### 8.2 实测结果（2026-10-21）

| 检查 | 结果 | 证据 |
| --- | --- | --- |
| 启动不崩、窗口可见 | 通过 | `winapp run` 报 PID；主窗口标题 `DlnRenderer`，响应正常 |
| 主界面显示服务状态 | 通过 | UIA 读到 `后台服务：已连接 · 局域网发现已开启（DlnRenderer-Test）` |
| 设置改名并保存 | 通过 | `settings.json` 写入 `deviceName: "DlnRenderer-Test"` |
| 重启服务后新名生效 | 通过 | 服务日志 `settings: device name 'DlnRenderer-Test', hwdec=True`；新进程 PID |
| 服务读设置、命令行优先 | 通过 | C5 端到端脚本 + §1.4 的 `ApplySettings` 优先级测试 |
| 关闭窗口到托盘 | 通过 | 点关闭后：窗口隐藏、UI 进程仍在、服务进程仍在 |
| 托盘图标存在 | 通过 | UIA 找到 `Button "DlnRenderer — 待机"`（位于隐藏图标区） |
| 双击托盘恢复窗口 | 通过 | 窗口重新可见，状态仍为已连接 |
| 托盘右键菜单 | 通过 | 菜单项：`打开主窗口` / `DlnRenderer — 待机`（灰显状态行）/ `退出应用` |
| 退出整个应用 | 通过 | 点设置页“退出整个应用”后，服务日志出现 `shutdown requested over ipc` → `service stopped`，两个进程都退出 |
| 音量初始值与真实状态一致 | 通过 | 快照 volume=100，音量滑块停在最右（此前停在 0，见 8.3） |
| 控件无障碍名称 | 通过 | UIA 读到 `播放` / `暂停` / `停止` / `静音` / `播放进度` / `音量` |
| 空闲连接保活 | 通过 | 连接保持 > 70 秒仍在（此前 30 秒被服务端丢弃，见 8.3） |

### 8.3 本轮复现并修复的缺陷

| 缺陷 | 现象 | 修复 |
| --- | --- | --- |
| IPC 空闲连接被服务端丢弃 | UI 显示“后台服务已断开连接”，服务日志 `ipc client read timed out` | 客户端每 10 秒发 `GetStatus` 保活（低于服务端 30 s 看门狗）；主界面断线后每 5 秒重连；`ConnectAsync` 加锁防重复建连 |
| 音量滑块初始值不反映真实音量 | 首个快照 volume=100 与视图模型默认值相同 → 无属性变更 → 滑块停在 0 | 加载完成后显式同步控件值 |
| 时长到达后进度条上限不更新 | `DurationSeconds` 变更只同步了音量滑块 | 位置与时长变更都触发进度条同步 |
| 设备名可能不保存 | `x:Bind` TwoWay 只在失焦时提交，点“保存”提交时读到旧值 | 保存前从 TextBox 显式取值 |
| 控件缺少无障碍名称 | UIA 读到空名称的按钮与滑块 | 补 `AutomationProperties.Name` |

### 8.4 本轮未验证 / 不修复

- **开机启动**：只确认了“未注册”状态（注册表 Run 项不存在）。写入会指向构建目录里的
  exe，而打包形态未定（ROADMAP E1/R3）；打包后需要 StartupTask，阶段 E 再实测。
- **封面图**：UI 已接入 `CoverUri`，但本轮没有控制端投送带封面的媒体，未做真实验证。
- **UI 侧播放联动**：进度/暂停/Seek 在真实投送时的界面反馈未在本轮重测（阶段 B 已验证服务端链路）。
- **MSIX 下的托盘与开机启动差异**：托盘已验证（阶段 C）；开机启动在 MSIX 渠道走
  `windows.startupTask`（默认 `Enabled=false`，由设置页控制），绿色包走 HKCU Run。
  两条路径都只在开发机本机验证过，见 §11。
- 托盘图标位于隐藏图标区（任务栏策略未改），“可见性”只验证到 UIA 能读到。
## 9. 阶段 D：媒体兼容性实测（2026-10-21）

### 9.1 做法

协议层原来照抄 392 条格式声明，本阶段用**真实播放**替代声明：ffmpeg 自生成样片
（无版权、单个 < 300 KB、不入库），由 libmpv 逐个加载、播放并观察位置推进。

```powershell
pwsh tools/media-compat/make-fixtures.ps1     # 生成样片到 artifacts/media-fixtures
pwsh tools/media-compat/measure-media.ps1     # 跑测量并生成 docs/media-matrix.md
```

`measure-media.ps1` 先跑 `MediaCompatibilityTests`，任何样片结果与文件名承诺不符就整体失败、
不写文档——所以「矩阵里有一行」等价于「这条断言这次真的过了」，不是抄来的结论。

文件名即契约：`invalid-*`（不是媒体）与 `unsupported-*`（有封装无解码器）必须失败，
其余必须播放。两个方向都断言，防止「删错声明」和「悄悄坏掉」两种情况。

### 9.2 实测结果

| 项 | 结果 |
| --- | --- |
| 样片 | 22 个（20 可播 / 2 正确失败：`invalid-not-media.mp4`、`unsupported-midi.mid`） |
| 视频容器 | mp4、mkv、mov、flv、avi、ts、webm、swf 全部可播 |
| 编码 | H.264、HEVC 10bit、MPEG-4、MPEG-2、VP8、VP9、AV1 全部可播 |
| 音频 | aac、ac3、flac、mp3、opus、pcm、vorbis 全部可播 |
| 硬解 | H.264/VP9/HEVC10/AV1 → `d3d11va`，VP8 → `nvdec`，MPEG-2/MPEG-4 → 软件 |
| 硬解回退 | 显式请求不存在的 `vaapi` → 回退软解，播放正常（D3） |

完整矩阵（含 ffmpeg/libmpv 版本、每个样片的实际解码方式）见 `docs/media-matrix.md`。

### 9.3 声明的收缩（D2，392 → 277）

移除类别与依据见 `docs/architecture.md` §15.2；`SinkProtocolInfoTests` 把
「不得声明图片 / DRM / MIDI / RealMedia / 音频冒充 video/*」钉成断言。

### 9.4 未覆盖

- 只测 2.5 秒自生成样片：验证「能解码、能推进」，不覆盖长片、码率上限、seek 精度。
- 只在本机（x64 + D3D11 显卡）测过；无硬解的机器走软解，格式集合一致但 CPU 占用不同。
- 没有真实第三方控制端投送 MKV/HEVC 的回归记录（阶段 B 只覆盖了常见 mp4）。
## 10. 阶段 E：安装、升级与卸载（2026-10-21）

### 10.1 分发形式决策

绿色包 + 安装脚本，与 MSIX 渠道并存。完整理由、两条渠道的差异与重新评估条件见
[`docs/packaging.md`](packaging.md)；MSIX 的逐项可行性论证见
[`docs/msix-assessment.md`](msix-assessment.md)。决定性约束——**后台服务必须在 UI 退出后继续运行**——在用户级 MSIX 下由与绿色包相同的机制
满足：UI 在用户会话里拉起服务进程，关窗不影响它。曾试过用 `desktop7:Service`（SCM 托管），
但服务会跑在 session 0，投屏无画面，已放弃（见 §11）。
该结论取代了本文档早期"不做 MSIX"的说法。

### 10.2 交付物与实测命令

| 脚本 | 作用 |
| --- | --- |
| `tools/package/build-dist.ps1` | 发布 App + Service（Release / win-x64 / 框架依赖），复制 libmpv 与 Assets，并校验产物（缺 exe / native DLL / 图标即失败） |
| `tools/package/install.ps1` | 停旧实例 → 复制到 `%ProgramFiles%\DlnRenderer` → 防火墙规则 → 开机启动 → 快捷方式 |
| `tools/package/stop-service.ps1` | 通过 IPC `Shutdown` 干净停止服务（与托盘"退出应用"同一路径） |
| `tools/package/firewall-rules.ps1` | `-Action Add` / `Remove`：UDP 1900（SSDP）+ TCP 18090（描述/SOAP/GENA） |
| `tools/package/uninstall.ps1` | 停进程 → 删防火墙规则 → 删数据目录 → 删快捷方式与启动项 → 删安装目录 |

### 10.3 三步实测（本机，Windows 11 build 26300）

**安装**（`install.ps1`，管理员 PowerShell）

- 输出：`installed to C:\Program Files\DlnRenderer`、防火墙两条规则 verified、
  快捷方式创建、HKCU Run 写入。
- 运行验证：从安装目录启动 `DlnRenderer.Service.exe --device-name InstallTest`，
  日志显示 `ssdp alive announced on 192.168.1.9`、`http listening on 0.0.0.0:18090`；
  `GET http://127.0.0.1:18090/description.xml` 返回 **HTTP 200**，
  `<friendlyName>InstallTest</friendlyName>` 正确。
- **真实设备发现**：日志出现 `ssdp answered 6 target(s) for ST=ssdp:all to 192.168.1.12`
  （局域网内另一台设备在搜索），说明防火墙规则确实放通了入站 SSDP。

**升级**（服务进程运行中再次执行 `install.ps1`）

- 升级前：`DlnRenderer.Service` PID 4768 在运行，安装目录有旧版标记文件。
- 脚本先通过 IPC 停掉该进程（`service closed the connection` → `DlnRenderer.Service has exited`），
  再覆盖文件，最后确认 `tasklist` 中已无该进程 —— **无孤儿进程**。
- 副作用记录：升级不会删除安装目录里用户自己放的文件（本次的标记文件仍在）。
  升级覆盖的是发布产物，不做整目录清空，这是有意的（避免误删日志/配置）。

**卸载**（`uninstall.ps1 -RemoveShortcuts`，管理员 PowerShell）

- IPC 停止 → 无进程 → 防火墙两条规则移除 → `%LOCALAPPDATA%\DlnRenderer` 删除 →
  开始菜单/桌面快捷方式删除 → HKCU Run 项删除 → `C:\Program Files\DlnRenderer` 删除。
- 卸载后复核：数据目录不存在、两个快捷方式不存在、`Get-NetFirewallRule DlnRenderer*` 计数 **0**、
  安装目录不存在。
- 未提权运行时脚本不再硬失败：防火墙步骤报告警告并继续完成其余清理。

### 10.4 本轮发现并修复的脚本缺陷

| 缺陷 | 现象 | 修复 |
| --- | --- | --- |
| `$LASTEXITCODE` 在调用运算符调用脚本时保持 `$null` | 安装/卸载脚本在防火墙步骤**误报**失败（规则其实已建/已删） | 改为实际查询 `Get-NetFirewallRule` 验证状态 |
| `PublishTrimmed=true` + 框架依赖发布 | `dotnet publish` 报 NETSDK1102 | 显式 `-p:PublishTrimmed=false`（框架依赖本就无需裁剪） |
| Assets 是发布产物中的文件名 | 递归复制 Assets 失败 | 复制前删除同名文件，并把 `Assets\AppIcon.ico` 等列入产物校验 |
| 卸载脚本在未提权时整体中断 | 无管理员权限的用户连数据目录都清不掉 | 防火墙步骤包 try/catch，其余清理继续 |

### 10.5 未验证 / 已知限制

- **未在干净虚拟机或 Windows Sandbox 中执行**：三步都在开发机本机完成，机器上已有
  .NET 运行时与 Windows App Runtime。干净机器上的首次运行（缺运行时、缺防火墙提示）未验证。
- **未验证 UI 进程本身**：只启动并验证了 `DlnRenderer.Service.exe`。WinUI 窗口、托盘、
  设置在安装形态下的表现未测（阶段 C 已验证开发目录形态）。
- **未做升级播放中保留/终止的实测**：升级时服务被停掉，正在播放的会话随之结束。
  ROADMAP E6 允许"正确终止"，但"播放中升级"这一具体场景未跑。
- **Windows 10 未测**：安装脚本与防火墙命令在 Win10 上应等价，但未实测。
- **无"添加或删除程序"条目**：卸载依赖 `uninstall.ps1`，见 `docs/packaging.md` §3。

## 11. 阶段 E2：用户级 MSIX 渠道（2026-10-22）

> 本节取代 2026-10-21 的旧版"MSIX 渠道与服务生命周期"。旧版验证的是 SCM 托管服务（`desktop7:Service`）
> 的起停，结论在那个范围内是真的；但**它没有真的启动过 UI，也没有真的投过屏**，所以漏掉了两个会让
> 产品不可用的问题（见 §11.3）。该方案已放弃，改为用户级包。

### 11.1 做法

用户级 MSIX 与绿色包共用同一套发布产物，区别只在安装方式。清单**不含** `windows.service`，
服务由 UI 在用户会话里拉起。

```powershell
pwsh tools/package/build-msix.ps1                  # 发布 Service+App → 暂存 → 打包 → 签名 → 解包回验
winapp cert install tools/package/devcert.pfx      # 一次性，管理员：信任 dev 证书
Add-AppxPackage -Path dist/msix/DlnRenderer-x64.msix   # 当前用户，不提权
pwsh tools/package/verify-service.ps1              # 体检
```

`build-msix.ps1` 的解包回验断言（先剥 XML 注释再匹配）：

- 清单**没有** `Category="windows.service"`、`packagedServices`、`localSystemServices`；
- 清单**有** `Microsoft.WindowsAppRuntime.2` 依赖、`runFullTrust`、`windows.startupTask`；
- 包里有 `DlnRenderer.exe`、`DlnRenderer.Service.exe`、`libmpv-2.dll`、`Assets\AppIcon.ico`。

### 11.2 实测结果

| 检查项 | 结果 |
| --- | --- |
| 打包 + 签名 + 解包回验 | 通过，签名 `Valid (CN=AppPublisher)`，78.9 MB |
| `Add-AppxPackage`（当前用户，无提权） | `Status: Ok`；`Get-Service DlnRenderer.Service` 无结果 |
| `verify-service.ps1` | 全部通过 |
| UI 启动 | session 1，`Responding=True`，有窗口句柄，无 Application 1000 崩溃事件 |
| 服务 | 由 UI 拉起，**session 1**（上一版是 session 0） |
| 日志位置 | `C:\Users\jaign\AppData\Local\DlnRenderer\logs\`（用户目录，未被虚拟化） |
| 视频窗口 | `video surface created (hwnd …)`；投送时 `video presenter fullscreen/hidden` 正常切换 |
| 真实控制端 192.168.1.72 | 取 `description.xml`/SCPD → `SUBSCRIBE` → `SetAVTransportURI` → `Play`，`Playing` |
| 投送期间 mpv 错误 | **无**（上一版 session 0 下每次必现 swapchain 失败） |
| 全量测试 | Core 43 / Player 23 / Dlna 222 / Service 115，共 403，0 失败 |

> 测试环境注意：机器提交内存吃紧时 `dotnet test` 会报 `OutOfMemoryException` 或
> `Win32Exception 1455 页面文件太小`，与代码无关。`dotnet build-server shutdown` 后加 `-m:1` 串行即可。

### 11.3 本轮发现并修复的缺陷

| 缺陷 | 现象 | 根因 | 修复 |
| --- | --- | --- | --- |
| **主界面打不开、没有托盘图标** | UI 每次启动即退出，事件日志 `.NET Runtime 1026`：`COMException 0x80040154 REGDB_E_CLASSNOTREG`，堆栈在 `DeploymentManagerAutoInitializer` | 清单漏了 `PackageDependency`：打包后的进程拿不到 Windows App Runtime 框架包，首次 WinRT 激活失败 | 清单加 `Microsoft.WindowsAppRuntime.2 ≥ 2.5.1.0`；`build-msix.ps1` 与 `verify-service.ps1` 都断言它 |
| **投屏没有窗口** | 控制链路全通，但 `mpv fatal: Failed to create swapchain (0x887a0022)` → `no audio or video data played` | 服务由 SCM 托管，跑在 **session 0**，没有桌面，GPU 交换链建不出来 | 放弃 `windows.service`，改用户级包，服务在用户会话里运行 |
| 开机启动在打包形态下会失效 | （设计层面）HKCU Run 指向 `WindowsApps\<版本>\` 路径，每次升级都变 | 该路径随版本变化且用户不可列出 | `StartupRegistration` 按是否有包标识分流：打包用 `StartupTask` API，未打包仍用 HKCU Run |
| 同版本号重装被拒 | `Add-AppxPackage` → `0x80073CFB` | 同标识不同内容 | 开发期先 `Remove-AppxPackage`；发布必须递增版本 |
| `build-msix.ps1` 断言误报 | `service=True`，但清单里并无服务扩展 | 清单注释里写了 "windows.service" 这几个字被正则命中 | 匹配 `Category="windows.service"`，并先剥掉 XML 注释 |
| 整个解决方案构建失败（NETSDK1150） | `dotnet build DlnRenderer.sln` 报"非自包含可执行文件不能由自包含可执行文件引用" | 为把服务 exe 放进包给 App 加了 `ProjectReference`；`win-x64.pubxml` 令 App 自包含而 Service 不是 | 去掉引用，`build-msix.ps1` 先发布 Service 再发布 App 到同一暂存目录并断言文件在包内 |
| App 发布输出不含服务 exe | 改文件复制后包里仍没有 `DlnRenderer.Service.exe` | `None`+`CopyToOutputDirectory` 只进构建输出，不进发布输出 | 由脚本显式暂存并双重断言 |
| `build-msix.ps1` 两处笔误 | `Remove-Item` 删目录缺 `-Recurse`；`-ForegroundColor Green#` | — | 修正 |

### 11.4 此前 SCM 方案修复的两个缺陷（代码保留）

`Program.cs` 走 `StartServiceCtrlDispatcher` 并正确回报 `SERVICE_RUNNING` / `SERVICE_STOPPED`
（`ServiceStatusReporter`）。用户级包不再使用 SCM，但这套逻辑**对被 SCM 启动的场景仍然正确**
（例如用户自行 `sc create` 注册），并有 `ServiceLaunchDetectorTests` 覆盖；在用户级路径上它不触发。

### 11.5 MSIX 渠道未验证

- **画面是否真的显示在屏幕上**：我只确认了窗口创建、presenter 切换、`Playing` 且无 mpv 报错，
  **没有截图确认**，需要目视。
- **托盘图标**：确认了 UI 进程与窗口句柄，没有截图确认托盘图标。
- **开机启动重启实测**：`StartupTask` 路径只验证了编译与现有测试，没有真正注销/重启。
- **MSIX 渠道的防火墙入站规则**：没有自动方案；本机能被发现是因为绿色包的规则仍在。
- **干净机器**：缺 Windows App Runtime 时的安装体验未验证。
- **卸载后的数据目录**：保留策略未定，也未验证残留。
- **正式代码签名**：当前是 dev 证书（阶段 F5）。

## 12. CI 自动化测试（GitHub Actions，2026-10-22）

### 12.1 触发与步骤

`.github/workflows/ci.yml`，push/PR 到 `main`，`windows-latest`：

1. Checkout + .NET 10（`global.json` 钉住 10.0.402，装 feature band）
2. `scripts/fetch-libmpv.ps1 -Download` —— 拉评审过的那个 libmpv 构建，**双哈希**：先校验 7z
   归档（`2c71ab5c…`），再校验解出的 DLL（`53513b8f…`，即 `docs/licenses.md` 记录的指纹）
3. `choco install ffmpeg` + 显式写 `GITHUB_PATH` + `ffmpeg -version` 断言（镜像不带 ffmpeg）
4. `restore` → `build -c Debug -p:Platform=x64` → `test`（同参数 + `--filter`，`--no-build`）
5. 跑 `stage-legal-files.ps1` 并断言 `LICENSE` / `THIRD-PARTY-NOTICES.md` / `LICENSES/` 就位
   （守住“分发包必带法律文件”的契约）

**为什么是 Debug 而不是 Release**：`DlnRenderer.csproj` 对非 Debug 配置设 `PublishReadyToRun=True`，
WinUI/AppSDK 的构建目标会把 R2R 优化带进 `dotnet build`，runner 上缺 RID 运行时包直接报
`a valid runtime package was not found`。Release 发布路径由 `tools/package/build-dist.ps1`
（显式 `-r win-x64`）覆盖，不进 CI。

### 12.2 绿色结果（run 39708351714，2026-10-22）

| 项目 | 结果 |
| --- | --- |
| Core | 43 / 43 |
| Player | 22 / 22（另 1 个 GPU 相关测试被 `--filter` 排除，见 12.4） |
| Dlna | 222 / 222 |
| Service | 115 / 115（含 8 个用 ffmpeg 现生成素材的窗口生命周期测试，真实运行） |
| 合计 | **402 通过 / 0 失败 / 0 跳过**，日志含 libmpv 归档哈希、ffmpeg 9.0.2 版本行 |

本次运行的三个"看不见的"断言（均实际核对过日志）：

- **构建日志里没有 `libmpv-2.dll is missing` 警告**——这本身就是 fetch 步骤生效的证据。
  该警告只在"本地没有 DLL"时出现，所以它一旦在 CI 日志里出现，说明 fetch 步骤坏了，
  而不是应该去屏蔽它。
- **没有 `Skipped` 行**——与本地"无 DLL 时 23 跳过"的对照组（§12.5）相区分。
- **没有任何 annotation**：`actions/checkout` 与 `actions/setup-dotnet` 已从 v4（node20）升到
  v5（node24），否则每个 job 都会带一条 Node.js 20 弃用警告。

### 12.3 迭代过程中暴露的三个真实问题（均已修复）

| 轮次 | 现象 | 根因与处理 |
| --- | --- | --- |
| 1 | Build 失败：`a valid runtime package was not found` | Release + R2R；改为与仓库文档一致的 Debug |
| 2 | Service 15 个失败（`libmpv is not available`） | CI 拉取 libmpv；`RequireMpv()` 抛异常改为共享 `MpvFact` 跳过兜底 |
| 3 | Service 7 个失败（`cannot find ffmpeg`）、Player 1 个硬解失败 | 装 ffmpeg + 新增 `MpvFFmpegFact`；GPU 相关测试显式排除 |
| 4 | Player 1 个失败（`Nullable object must have a value`） | **真实竞态**：`Paused` 快照里 `Duration` 还是 null；改为等两者同时满足 |
| 5 | 全绿但每个 job 带 `Node.js 20 is deprecated` annotation | action 版本落后：`checkout@v4`/`setup-dotnet@v4` 声明 node20，被强制跑在 node24；升到 v5 |

### 12.4 CI 环境事实（不是产品缺陷）

- **runner 虚拟机没有可用硬解**：`dxva2_vld` 初始化失败，mpv 回退软解。因此
  `MediaCompatibilityTests.Hardware_decoding_is_used_when_the_machine_supports_it` 被按全名
  排除——它断言的是“机器有解码器时必须用上”，前提是机器有解码器。同一测试在真实 GPU 机器
  通过（阶段 D）；覆盖软解回退的兄弟测试在 CI 正常运行。
- **镜像不带 ffmpeg**：由 choco 安装，`-version` 断言保证路径正确时才继续。
- **镜像不带 libmpv**（刻意不入库）：由 `-Download` 拉取，校验失败则步骤报错。

### 12.5 三条路径的本地实测（2026-10-22，Windows 11 26300）

| 路径 | 结果 |
| --- | --- |
| libmpv + ffmpeg + GPU 齐全 | 403 通过 / 0 失败 / 0 跳过 |
| 移走 `libmpv-2.dll` | Service 92 通过 / **23 跳过** / 0 失败，跳过消息指向 `scripts/fetch-libmpv.ps1` |
| 从 PATH 隐藏 ffmpeg | Service 107 通过 / **8 跳过** / 0 失败，消息指向安装 ffmpeg |

### 12.5b 发布工作流（`release.yml`）

独立于 `ci.yml`，手动触发，构建绿色包 + MSIX 并开 GitHub Release。用固定证书连续发了
`v0.1.0`（`39714170910`）与 `v0.2.0`（`39714549806`），均全部步骤通过、**零 annotation**；
下载回来的资产 `sha256sum -c` 全部 `OK`，两个版本的 `.cer` 哈希一致，`.cer` 签名验证为 `Valid`，
MSIX 安装成功且 **0.1.0 → 0.2.0 原地升级成功**（`Status: Ok`）。
完整记录与证书步骤见 [`docs/packaging.md`](packaging.md) §7。

迭代中暴露的两个真问题（与 CI 主工作流同源：**不要把"看起来成功"当成功**）：

| 现象 | 根因 |
| --- | --- |
| `makeappx is not available through winapp (1)` | `makeappx /?` 成功时也返回退出码 1；按退出码判定会误判可用工具链为损坏。改为先看输出再判 |
| `Get-AuthenticodeSignature` 报 `UnknownError` | dev 证书不在根存储，签名**本身有效**；旧代码把"未信任"当作失败。现在断言签名者指纹等于使用的证书 |
| `upload-artifact@v5` 仍报 node20 弃用 | 靠大版本号推测目标 Node 版本是错的：v4/v5 都是 node20，v6 才是 node24。改为读 tag 的 `action.yml` 核实 |
| 每次发布 `.cer` 都变，且 MSIX 无法原地升级 | 证书原本每次 run 重新生成。改为**入仓固定**证书（`8781E2D1…`）；已验证 0.1.0 → 0.2.0 原地升级成功 |
| 发布说明里行内代码变成了双反引号，围栏退化成仂零零的 `` ` `` | PowerShell here-string 里双反引号是字面量。改为从 `release-notes.template.md` 模板渲染 |

### 12.6 CI 不覆盖

真实控制端投屏、网络中断恢复、8 小时长跑、MSIX 安装、开机启动重启——仍在各阶段的手工清单里。
