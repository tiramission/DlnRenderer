# 测试指南

本文件记录当前仓库实际执行过的验证。命令均在 Windows 11 build 26300、.NET SDK 10.0.402 上运行。

## 1. 自动化测试

```powershell
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

实测结果：**163 通过 / 0 失败，0 跳过**（Core 25、Player 15、Dlna 110、Service 13）。

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