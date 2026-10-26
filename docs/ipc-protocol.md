# IPC 管道协议（界面 ↔ 后台服务）

本文记录 UI（`DlnRenderer.exe`）与后台服务（`DlnRenderer.Service.exe`）之间那条命名管道上的
消息契约。DLNA/UPnP 那一侧（控制端 ↔ 渲染器）在 [`protocol.md`](protocol.md)，两者不重叠。

契约的单一来源是 `src/DlnRenderer.Core/Ipc/IpcProtocol.cs`，UI 与服务都引用它；本文记录的是
**为什么**这样定，以及排查时该看什么。

## 1. 为什么需要一条自己的协议

服务是独立进程，UI 退出后它继续播放。因此需要一条双向通道：UI 下发命令并上报"可以把画面画
在哪儿"，服务推送状态快照。

选命名管道而不是 HTTP 或本地回环端口，是因为：

- 不需要端口，因此不需要防火墙规则，也不与 SSDP/描述端口抢号（`docs/architecture.md` §16.3）。
- `PipeOptions.CurrentUserOnly` 一条选项就把访问限制到当前用户，回环端口要靠 ACL 才能达到
  同等效果。
- 单实例语义天然成立：管道名只有一个监听者，第二个服务实例起不来。

## 2. 传输与帧格式

| 项 | 值 |
| --- | --- |
| 管道名 | `DlnRenderer.Service.v1`（`IpcProtocol.PipeName`） |
| 方向 | `InOut`，全双工 |
| 帧 | 4 字节小端长度 + UTF-8 JSON |
| 单帧上限 | `MaxMessageBytes` = 64 KiB |
| 服务端缓冲区 | `PipeBufferBytes` = 256 KiB |

**缓冲区不是调优参数，是正确性前提。** 以 `PipeOptions.Asynchronous` 创建的管道，Windows
默认只给一个页大小的缓冲区；而本协议是全双工的——服务推快照的同时 UI 在下命令——两端同时
写满就会互相等对方读，表现为测试进程挂起。缓冲区必须远大于单帧上限，才能让任何单条消息都
不会阻塞。只有服务端能设：`NamedPipeClientStream` 没有接受缓冲区大小的构造函数，客户端继承
服务端建好的管道。这是真实缺陷，[`testing.md`](testing.md) 有测试覆盖。

`PipeName` 里的 `v1` 是**管道名版本**，不随消息字段变化；消息版本是下面那一节的事。

## 3. 消息版本与不匹配时的行为

`IpcProtocol.Version` 目前的线协议版本是 **1.1**。

**规则**：只要某个字段的**含义**变了，就升版；服务收到版本不同的 `request` 时返回
`success=false` + `error: "unsupported protocol version '...'"`，**不断开连接**。

### 为什么必须拒绝，而不是宽容

UI 与服务是两个独立可执行文件，可以一个是新构建、一个是旧的。旧服务**静默忽略**它不认识的
JSON 字段——而 `System.Text.Json` 的默认行为就是这样，不报错、不告警。

这件事造成过一个真实的假象：1.0 时代新增 `VideoTarget.Enabled`（"界面现在没地方画，请挂起"），
旧服务不认识它，按默认值 `true` 处理，同时又看到 `hostWindow` 为空，于是按"没有 UI"处理，
把画面铺到全屏浮层。**用户在界面上点设置图标，电影突然铺满整屏。** 代码里早已修好，但服务
进程还是旧二进制，于是"修好了却没修好"，只能靠翻日志和猜。

所以现在不匹配就明确拒绝。被拒不是断线，是一条 `success=false` 的应答。

### 消费侧：拒绝 ≠ 断连

`ServiceClient` 把两者分成不同事件，这是有意的：

| 事件 | 含义 | UI 行为 |
| --- | --- | --- |
| `Faulted` | 管道断了（服务退出、协议违规） | 视为未连接 |
| `RequestRejected` | 服务应答了，只是拒绝 | 显示原因，**不触发重连循环** |

把拒绝当成断线会启动一次永远修不好的重连循环——重试同一条请求，服务照样拒绝。其中
"protocol version" 这一条会被翻译成可操作提示：请在设置页重启后台服务。

### 版本自检：`RendererSnapshot.ServiceBuild`

快照带服务自己的程序集版本号（`ServiceBuild`），UI 与自身版本比对，不一致就把服务状态栏
降级为**警告**：

```
后台服务：版本不一致（服务 0.2.0 / 界面 0.3.0）· 请在设置页重启后台服务
```

字段为 `null` 表示服务比这个字段还旧，同样算不一致。这是**主动**告警，与上面的被动拒绝互补：
拒绝只在用户真的按了某个按钮之后才发生，而升级之后用户可能一直不按。

### 谁必须跟着改

升版会波及所有自己拼消息的调用方，不只是 C# 代码：

| 位置 | 说明 |
| --- | --- |
| `tools/control-point/ipc-check.ps1` | 顶部 `$protocolVersion`，注释指明须与 `IpcProtocol.Version` 同步 |
| `tools/package/stop-service.ps1` | 按 `@('1.1','1.0')` 依次尝试，见 §6 |

## 4. 消息

一条扁平信封 `IpcMessage` 承载请求、应答与推送事件，故意不做嵌套，这样在日志里一眼能读。

`type`：`request` / `response` / `event`。请求带 `requestId`，应答带回同一个 id——UI 靠它把
应答与请求对上，而不是靠顺序（推送事件会插在中间）。

命令表：

| 命令 | 参数 | 说明 |
| --- | --- | --- |
| `GetStatus` | — | 取一次快照 |
| `Play` / `Pause` / `Stop` | — | 传输控制 |
| `Seek` | `seekSeconds` | |
| `SetVolume` | `volume` | |
| `SetMute` | `muted` | |
| `SetVideoTarget` | `videoTarget` | 见 §5 |
| `Shutdown` | — | 停掉服务进程，只有"退出整个应用"才发 |

服务在连接建立时主动推一条 `event`（初始快照），此后每次状态变化都推。UI 因此不需要轮询。

### 快照里的 `engine` 字段

`RendererSnapshot.Engine`（字符串，`"mpv"` / `"vlc"`，服务端 `PlaybackEngineInfo.Id`）告诉 UI
当前服务跑的是哪个播放内核。UI 用它渲染主页控制坞与悬浮控制条上的内核徽标。

该字段是**加法演进**，协议版本保持 `1.1`：旧服务不发该字段（UI 隐藏徽标而不是猜一个内核），
旧 UI 收到该字段会忽略。这与 `ServiceBuild` 的处理一致——null 是"信息不存在"，不是"默认 mpv"。

## 5. `VideoTarget`：画面可以画在哪儿

```json
{ "mode": "Embedded", "hostWindow": 123456, "x": 240, "y": 120,
  "width": 1200, "height": 680, "enabled": true, "cornerRadius": 12 }
```

坐标是**物理像素、相对宿主窗口客户区**。只有 UI 知道自己的 DPI 与布局（标题栏、导航栏占多高），
所以缩放由 UI 做；把窗口位置留在契约之外之后，服务在用户拖动窗口时不需要做任何坐标换算。

派生属性，别在消费侧自己重算：

| 属性 | 含义 |
| --- | --- |
| `HasHost` | `hostWindow != 0` |
| `HasArea` | 宽高都大于 1 |
| `CanEmbed` | `Enabled` + `Embedded` + `HasHost` + `HasArea` |
| `CornerRadius` | 嵌入画面的圆角（物理像素），全屏时为 0 |

### 三种"不能画"必须分开

| 情况 | 语义 | 服务行为 |
| --- | --- | --- |
| `enabled=false` | 界面**主动挂起**（用户去了设置页） | 隐藏画面，投屏继续，音频不断 |
| `Embedded` 但没有 host 或矩形 | 界面还没补齐（启动中、导航中、尚未布局） | **隐藏**，等 UI 补齐 |
| `Fullscreen` 且没有 host | 没有 UI 在服务（服务独立运行） | 全屏浮层 |

前两种的结果都是隐藏，区别只在**界面回来时如何恢复**：挂起是一次有意的暂停，导航回去就恢复；
没有 host 是目标还没填完整，等下一次上报。

**嵌入式模式永不回退全屏。** 早期版本让"没有 host/没有矩形"退回全屏，想着"总比什么都没有好"，
结果一次页面导航、一次尚未布局的启动都变成"画面突然铺满整屏"——比短暂没画面更吓人。全屏浮
层只在模式**真的**是 `Fullscreen` 时使用，那才是这条回退存在的意义。

唯一例外是 `SetParent` 被系统拒绝（跨进程挂父窗口可能被拒绝，比如完整性级别不同或宿主不是
顶层窗口）：那时窗口根本进不了主窗口，只剩"全屏"和"什么都不显示"两个选择，而拒绝通常意味着
宿主窗口有问题，让用户看见画面去排查比黑屏好。

### `CornerRadius`

由 UI 计算（窗口态 8 DIP，全屏 0），服务用 `SetWindowRgn` 裁自己的窗口——窗口区域会连带裁掉
里面的 mpv 子窗口，所以画面本身就是圆角，不是盖一层遮罩。方形画面塞在一片圆角卡片里，看起来
像在窗口上开了个洞。

## 6. 运维约束：UI 与服务必须同版本

升级替换 `DlnRenderer.Service.exe` 之后，**必须重启后台服务**，否则就是"新界面 + 旧服务"的组合：
管道能连上，命令被拒，或者旧服务按旧语义执行。

- 绿色包：`install.ps1` 先调 `stop-service.ps1` 停掉正在跑的服务，再覆盖文件。
- MSIX：没有安装/升级期的提权钩子，停不掉旧进程，跨版本升级后要靠用户手动重启（见
  [`msix-assessment.md`](msix-assessment.md)）。

`stop-service.ps1` 因此**按版本列表依次尝试**（新的在前，已知的旧版在后），而不是写死一个：
它要停的恰恰可能是旧服务。版本不匹配时服务只回错误帧、不断连，所以同一条管道上可以试多个
版本。

## 7. 排查

**启动横幅**（`%LOCALAPPDATA%\DlnRenderer\logs\service-*.log`）带 build 号，用来核对两端：

```
2026-10-23 01:30:53.268 service starting (pid 41360, build 0.3.0, udn uuid:...)
```

**每次收到 `SetVideoTarget` 都记一行**——这是"画面位置不对 / 画面突然铺满整屏"的第一手证据：

```
video target: mode=Embedded enabled=False host=0x1A2B3C rect=240,120 1200x680
```

读法：`enabled=False` 是界面挂起，`host=0x0` 是没有宿主窗口，`rect` 全零是页面还没布局。
先分清是 UI 发错了还是服务没执行，再往下查。

手工驱动同一条管道（验证 UI 命令通路，不涉及 DLNA）：

```
pwsh tools/control-point/ipc-check.ps1
```

脚本里的 `$protocolVersion` 必须与 `IpcProtocol.Version` 一致，否则每条命令都会返回版本错误
——那看起来像"服务坏了"，实际是脚本过期。
