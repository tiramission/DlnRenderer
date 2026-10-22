# DlnRenderer — 项目实施规范

## 1. 项目目标

开发一款面向 Windows 10/11 的开源 DLNA Media Renderer（DLNA 媒体渲染器），提供现代化的 Windows 原生 GUI，整体视觉风格接近 Windows 11 Fluent Design / WinUI 3。

应用允许手机、平板、电脑及其他 DLNA 控制端发现本设备，并向其推送视频、音乐等媒体内容，在本机进行播放。

这不是 DLNA Media Server，不负责向其他设备共享本地媒体库；它的核心职责是作为 DLNA 播放目标接收媒体播放指令。

### 核心目标

1. 使用 WinUI 3 构建现代化 Windows 原生桌面应用。
2. 使用 `winapp` CLI 管理 Windows 应用开发工具链、项目初始化、构建、运行和打包相关流程。
3. 实现独立于 UI 的 DLNA Renderer 后端。
4. 使用 mpv/libmpv 作为媒体播放引擎，支持视频、音乐、字幕及硬件解码。
5. 支持后台常驻、系统托盘、开机启动及前端退出后的独立运行。
6. 采用模块化、可测试、易维护的架构。
7. 所有核心功能均使用开源技术，并明确第三方组件的许可证义务。

### 非目标

首个版本不实现以下功能：

- DLNA Media Server。
- AirPlay 接收器。
- Miracast 屏幕镜像接收器。
- Chromecast 协议接收器。
- 在线视频聚合或媒体库刮削。
- 账号系统、云同步或遥测平台。
- 自行实现音视频编解码器。
- 将现有 Python 托盘程序简单套壳并宣称为原生 WinUI 3 应用。

## 2. 技术栈

### Windows 应用

- C#
- .NET：优先采用当前受支持且与 WinUI 3 模板兼容的稳定版本。
- Windows App SDK
- WinUI 3
- XAML
- MVVM
- `CommunityToolkit.Mvvm`，仅在兼容性及维护状态确认后引入。
- `Microsoft.Extensions.Hosting` / DI：按需要使用。
- Windows 目标平台：Windows 10/11，具体最低版本由所选 Windows App SDK 和依赖决定。

不得为了追求最新版本而引入未经验证的预览版 SDK。

### 播放引擎

优先使用 libmpv。

要求：

- 支持视频和音乐播放。
- 支持播放、暂停、停止、跳转、音量和播放状态查询。
- 支持常见音视频格式。
- 尽可能支持硬件解码。
- 支持字幕及音轨选择。
- 通过明确的接口封装 libmpv，不允许将播放器调用散落在 UI 代码中。
- 正确处理 native DLL、运行时依赖、架构和资源释放。
- 发布前审核 libmpv 及其打包依赖的许可证，尤其是 GPL/LGPL 构建差异和再分发义务。

不要假设某个 libmpv NuGet 包一定是官方包。引入之前必须核实其维护状态、许可证、原生二进制来源和 Windows 支持情况。

### DLNA 后端

采用独立进程运行的 DLNA Renderer 服务。

推荐优先评估以下两种路线：

A. 复用成熟的开源 DLNA Renderer，例如 MiniCast 的相关实现，前提是能够将协议后端与托盘 UI 解耦，并确认其实际许可证及依赖。

B. 如果复用方案的架构或依赖不合适，则采用 C#/.NET 独立实现。

选择路线之前，必须完成简短的技术调研，并将结论记录在 `docs/architecture.md`。不得仅因熟悉某种语言就直接重写完整协议栈。

如果需要自行实现协议，至少覆盖：

- SSDP 设备发现与公告。
- UPnP Device Description。
- UPnP AVTransport。
- RenderingControl。
- 必要的 ConnectionManager 能力声明。
- SOAP 请求与响应。
- DIDL-Lite 媒体元数据解析。
- 媒体 URI 和协议能力处理。
- 播放状态和事件通知。
- 网络接口选择、地址变化和异常恢复。

不得在未验证的情况下宣称完全符合 DLNA 认证规范。

## 3. 总体架构

采用前端与后端分离的结构。

```text
DlnRenderer/
├── AGENTS.md
├── README.md
├── DlnRenderer.sln
├── src/
│   ├── DlnRenderer.App/
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Controls/
│   │   ├── Services/
│   │   ├── Themes/
│   │   └── Assets/
│   ├── DlnRenderer.Core/
│   │   ├── Models/
│   │   ├── Interfaces/
│   │   └── State/
│   ├── DlnRenderer.Dlna/
│   │   ├── Ssdp/
│   │   ├── Upnp/
│   │   ├── Soap/
│   │   ├── Metadata/
│   │   └── Network/
│   ├── DlnRenderer.Player/
│   │   ├── Mpv/
│   │   └── Audio/
│   └── DlnRenderer.Service/
│       ├── Ipc/
│       ├── Hosting/
│       └── Program.cs
├── tests/
│   ├── DlnRenderer.Core.Tests/
│   ├── DlnRenderer.Dlna.Tests/
│   └── DlnRenderer.Player.Tests/
├── docs/
│   ├── architecture.md
│   ├── protocol.md       # DLNA/UPnP 侧（控制端 ↔ 渲染器）
│   ├── ipc-protocol.md   # 命名管道侧（界面 ↔ 后台服务）
│   ├── development.md
│   └── testing.md
└── packaging/
```

此目录树是目标结构，不要求机械地一次性创建所有目录。根据真实依赖关系决定项目拆分，避免空项目、过度抽象和无意义的包装层。

### 进程职责

**DlnRenderer.App**

负责 WinUI 3 界面、用户交互、设置、托盘入口和播放状态展示。

**DlnRenderer.Service**

负责持续运行的 DLNA 服务、网络发现、协议请求处理及播放任务协调。

**DlnRenderer.Dlna**

负责协议实现，不依赖 WinUI 3。

**DlnRenderer.Player**

负责播放器适配，不依赖 WinUI 3 或 UI 控件。

**DlnRenderer.Core**

存放领域模型、共享接口、播放状态和业务规则。

各模块应遵循依赖倒置原则，禁止 Core 反向依赖 UI 或具体网络实现。

## 4. 进程通信与生命周期

UI 与后台服务之间使用 Windows Named Pipes 进行本机 IPC。

协议应具备：

- 版本字段。
- 请求 ID。
- 明确的请求、响应和错误结构。
- 异步请求处理。
- 播放状态事件。
- 客户端断线和重新连接处理。
- 合理的超时和取消机制。
- 日志中的关联 ID。
- 消息大小限制与输入校验。

建议定义以下命令：

- `GetStatus`
- `GetSettings`
- `UpdateSettings`
- `Play`
- `Pause`
- `Stop`
- `Seek`
- `SetVolume`
- `SelectAudioTrack`
- `SelectSubtitleTrack`
- `GetCapabilities`
- `Shutdown`

服务端主动推送的事件至少包括：

- `RendererStateChanged`
- `MediaChanged`
- `PlaybackPositionChanged`
- `PlaybackError`
- `NetworkStateChanged`

播放状态模型应明确区分：

- `Stopped`
- `Playing`
- `Paused`
- `Buffering`
- `Transitioning`
- `Error`

对于尚未实现的状态或命令，应明确返回不支持错误，不允许静默成功。

Named Pipes 应限制访问权限，避免其他本地用户任意控制播放或修改配置。

### 生命周期要求

- 服务启动失败时，UI 能显示明确的错误信息。
- UI 退出不应自动导致服务停止，除非用户明确选择退出整个应用。
- 后台服务应有独立的启动、停止、重启和日志机制。
- 托盘菜单应支持打开主窗口、查看播放状态和退出应用。
- 首版可以将服务作为普通后台子进程运行；不要未经需求分析就安装 Windows Service。
- 如果后续需要 Windows Service，必须单独设计交互式用户会话、媒体输出、权限和安装卸载流程。

## 5. DLNA 功能要求

### SSDP

- 支持局域网设备发现。
- 正确响应 M-SEARCH。
- 提供设备描述 URL。
- 支持设备启动公告和正常退出的 byebye 公告。
- 正确处理网络接口变化。
- 避免硬编码单一 IP 地址。
- 对无效请求、重复请求和异常报文进行防护。

### AVTransport

首版应优先实现：

- `SetAVTransportURI`
- `GetTransportInfo`
- `GetPositionInfo`
- `Play`
- `Pause`
- `Stop`
- `Seek`
- `GetMediaInfo`

对于 `Next`、`Previous`、`SetNextAVTransportURI` 等功能，必须根据实际播放队列设计决定是否支持。不能为了表面兼容而返回虚假的成功状态。

### RenderingControl

根据播放器能力实现：

- `GetVolume`
- `SetVolume`
- `GetMute`
- `SetMute`

需要区分播放器内部音量和系统主音量，避免未经用户授权修改 Windows 全局音量。

### ConnectionManager

- 正确声明可用的协议能力。
- 根据实际支持情况返回 MIME 类型和传输协议信息。
- 不得将未测试的格式全部声明为可播放。
- 对无法支持的传输方式明确返回不支持。

### 元数据

处理常见 DIDL-Lite 元素：

- 标题。
- 艺术家。
- 专辑。
- 封面 URI。
- 媒体 URI。
- MIME 类型。
- 时长。
- 字幕资源（如果控制端提供）。

必须容忍元数据缺失、编码差异、未知 MIME 类型及不完整 XML。

### HTTP 媒体资源

DLNA 控制端提供的媒体通常通过 HTTP URI 访问。

要求：

- 优先直接播放控制端提供的媒体 URI。
- 不得无条件下载整个文件到本地。
- 支持常见 HTTP Range 请求所对应的媒体访问行为。
- 正确处理 URL 编码、重定向和网络错误。
- 记录可诊断的失败原因。
- 不允许把任意远程 URL 直接传给存在不受限本地文件访问能力的播放器而不做安全评估。

对于本地网络和远程 URL 的访问，应有明确的安全边界。不得为兼容性而默认关闭所有 TLS 校验。

## 6. 媒体播放功能

首版目标：

- 视频播放。
- 音乐播放。
- 暂停、继续和停止。
- 进度查询与 Seek。
- 音量和静音。
- 字幕显示。
- 多音轨选择（播放器能力允许时）。
- 基本播放错误处理。
- 媒体标题和封面展示。

以下功能可后续实现：

- 图片幻灯片。
- 高级播放队列。
- 字幕下载。
- 播放历史。
- 媒体格式统计。
- 音频输出设备选择。
- 逐帧控制和高级播放器参数。

不得把后续功能作为首个可运行版本的阻塞条件。

## 7. WinUI 3 UI 设计规范

视觉目标是现代 Windows 11 应用，而不是传统 WinForms、WPF 或托盘工具。

### 设计原则

- Fluent Design。
- 合理使用 Mica/Acrylic，注意系统版本和窗口背景兼容性。
- 使用统一的颜色、间距、圆角、图标和字体体系。
- 采用 NavigationView 或适合小型播放器的精简导航结构。
- 支持深色、浅色和跟随系统主题。
- 优先使用原生 WinUI 控件。
- 使用清晰的空状态、加载状态、错误状态。
- 避免过多卡片、边框、阴影和视觉噪声。
- 支持高 DPI 和窗口尺寸变化。
- 关键交互支持键盘导航和无障碍语义。

### 主窗口

主页需要展示：

- 当前播放状态。
- 媒体封面或默认占位图。
- 标题、艺术家等可用元数据。
- 播放进度和时长。
- 播放、暂停、停止控制。
- 音量控制。
- 当前 Renderer 服务状态。
- 最近的错误或网络异常提示。

当没有媒体播放时，展示简洁的待机页面，明确提示设备已准备好接收投屏。

### 设置页面

至少包含：

- 设备名称。
- 服务开关。
- 网络接口或自动选择。
- 开机启动。
- 关闭窗口时的行为。
- 硬件解码设置。
- 音频及字幕设置。
- 日志和诊断信息。
- 版本与许可证信息。

设置应具有持久化存储，并对无效输入进行验证。

### UI 线程规则

- 不在 UI 线程执行阻塞式网络请求。
- 不在 UI 线程执行长时间播放初始化。
- 不直接从后台线程修改 XAML 控件。
- 使用 MVVM 和异步命令。
- 播放状态应通过统一状态模型更新，避免多处各自维护状态。

## 8. winapp CLI 工作流

`winapp` CLI 是 Windows 应用开发工具链的一部分，不应被视为通用 Solution 管理器。

开始实施前，Agent 必须：

1. 检查 Windows、.NET SDK、Windows App SDK、Visual Studio Build Tools 及 `winapp` CLI 的实际安装状态。
2. 执行 `winapp --version`、`winapp --help` 和 `winapp new --list`。
3. 记录当前实际可用的模板、参数和 SDK 版本。
4. 验证项目创建命令是否能够正常结束。
5. 遇到无输出、卡住或网络错误时，先诊断问题，不得无限重试。
6. 不得凭记忆假设某个 CLI 子命令或参数存在。

建议先在独立的临时目录测试模板：

```powershell
winapp --version
winapp --help
winapp new --list

dotnet --info
dotnet --list-sdks

winapp new --help
```

根据实际 CLI 版本确认 `winui-mvvm` 或其他模板是否存在，再执行创建命令。

如果项目模板创建成功，保留 CLI 生成的官方文件和默认配置，不要未经分析就大规模替换项目文件。

后续构建、运行和打包应优先使用当前版本 `winapp --help` 中确认存在的命令。

同时保留标准 .NET 构建入口，以便 CI、自动化测试和独立类库项目不依赖 GUI 开发命令：

```powershell
dotnet restore
dotnet build DlnRenderer.sln
dotnet test DlnRenderer.sln
```

以上命令中的 Solution 路径应以实际创建结果为准。

如果 `winapp run` 或类似命令不存在，或不支持当前项目结构，应使用模板生成的项目配置及标准 .NET/Windows 应用构建工具，不得伪造命令成功。

## 9. 开发阶段

采用可运行、可验证的增量开发方式。

### 阶段 0：环境与架构验证

- 检查开发环境。
- 验证 `winapp` CLI。
- 验证 WinUI 3 模板创建。
- 验证 libmpv 在目标 Windows 架构上的可用性。
- 比较 DLNA 后端复用与自研两种方案。
- 输出 `docs/architecture.md`。
- 明确第三方许可证和依赖来源。

交付：能够构建的最小 WinUI 3 项目，以及架构决策记录。

### 阶段 1：基础 UI 和 IPC

- 实现主窗口和基本导航。
- 建立 Core 项目。
- 定义 IPC 消息协议。
- 实现前端连接状态和错误提示。
- 实现后台服务进程的启动和退出。

交付：UI 能连接后台服务并显示其状态。

### 阶段 2：播放引擎

- 集成 libmpv。
- 实现播放适配接口。
- 实现播放、暂停、停止、Seek、音量和状态查询。
- 加入基础音视频测试素材。
- 验证 native 依赖和资源释放。

交付：通过内部测试接口能够播放本地视频与音乐。

### 阶段 3：DLNA MVP

- 实现或集成 SSDP。
- 实现设备描述和服务描述。
- 实现必要的 AVTransport 操作。
- 实现 RenderingControl 基础操作。
- 接入播放引擎。
- 测试真实控制端发现、播放和停止。

交付：手机或其他 DLNA 控制端可以发现设备，并成功投放视频和音乐。

### 阶段 4：现代化 UI 和后台运行

- 完成主播放页面。
- 完成设置页面。
- 完成系统托盘。
- 完成主题适配。
- 完成服务独立运行和前端重连。
- 加入日志、错误提示和故障恢复。

交付：具备完整基本交互的 Windows 桌面应用。

### 阶段 5：稳定性、安装和发布

- 测试网络断开与恢复。
- 测试应用关闭和重新打开。
- 测试长时间播放。
- 测试常见音视频格式。
- 验证 MSIX 或其他 Windows 安装方案。
- 验证升级、卸载和后台进程清理。
- 编写安装说明和许可证清单。

交付：可供其他 Windows 用户安装和使用的首个测试版。

## 10. 测试要求

### 单元测试

- 播放状态转换。
- IPC 消息解析和错误处理。
- DIDL-Lite 元数据解析。
- SOAP 请求与响应。
- SSDP 报文解析。
- MIME 类型与协议能力匹配。

### 集成测试

- 后台服务启动与停止。
- UI 连接和重连。
- 播放器初始化与释放。
- HTTP 媒体 URL 播放。
- 暂停、恢复、停止和 Seek。
- 播放器错误与 DLNA 状态同步。

### 实际控制端测试

至少选取两个不同实现的 DLNA 控制端进行测试。

优先考虑 VLC 和 BubbleUPnP，但应以目标平台上实际可用的版本为准。

测试内容：

- 设备发现。
- 视频播放。
- 音乐播放。
- 暂停与恢复。
- 停止。
- Seek。
- 音量。
- 封面与元数据。
- 网络中断和恢复。

如果某个控制端未提供某项功能，应区分是 Renderer 不支持、控制端未发送请求，还是媒体资源自身的限制。

## 11. 安全与可靠性

- 默认仅提供必要的局域网服务。
- 不将后台 IPC 暴露到局域网。
- 限制 Named Pipes 的访问权限。
- 验证网络报文和 XML 输入。
- 避免 XML 外部实体处理风险。
- 对请求和媒体资源进行合理的超时控制。
- 不在日志中输出敏感凭据。
- 避免无限重试、无界队列和内存泄漏。
- 正确处理网络接口变化和进程异常退出。
- 仅在必要时请求管理员权限。
- Windows 防火墙规则应明确用途，并支持撤销。
- 安装、升级和卸载必须能够正确处理后台进程。

## 12. 代码质量与 Agent 工作规则

Agent 必须遵循以下工作方式：

1. 修改前先检查实际仓库结构、现有代码和依赖，不得假设文件存在。
2. 每个阶段完成后执行构建与相关测试。
3. 不允许用空方法、硬编码成功结果或模拟播放状态冒充真实功能。
4. 不允许在未说明的情况下用 Mock 结果替代真实 DLNA 行为。
5. 不允许为了通过编译而随意禁用分析器、删除测试或吞掉异常。
6. 新增依赖时记录用途、许可证和版本。
7. 对重要架构选择记录理由及替代方案。
8. 遇到环境、网络或工具链问题时，先提供可复现的诊断信息。
9. 不得在没有实际验证的情况下声称构建、测试或打包成功。
10. 每次交付应说明改动、验证命令、结果、已知问题和下一步。
11. 优先完成一个端到端可运行的小功能，而不是一次生成大量未验证代码。
12. 如果需求存在关键歧义，先说明影响并提出具体选择；非关键细节可采用合理默认值继续推进。

## 13. 首版验收标准

首个可用版本至少满足：

- [ ] 使用 WinUI 3 原生窗口，而不是 WebView 作为主 UI。
- [ ] 采用现代 Windows Fluent Design 风格。
- [ ] 项目可通过已验证的 Windows/.NET 构建工具链构建。
- [ ] 能启动独立 Renderer 后端。
- [ ] 前端可以连接并控制后端。
- [ ] 在局域网内可被真实 DLNA 控制端发现。
- [ ] 至少一个真实控制端可以成功投放视频。
- [ ] 至少一个真实控制端可以成功投放音乐。
- [ ] 播放、暂停、停止和 Seek 能正确工作，或对明确不支持的操作返回正确错误。
- [ ] 音量控制与播放器实际状态一致。
- [ ] 前端退出后，按设计保持后台服务运行。
- [ ] 前端重新打开后能恢复显示当前播放状态。
- [ ] 网络中断和后台进程异常有明确错误处理。
- [ ] 有可复现的测试记录。
- [ ] 有安装、运行、卸载和许可证说明。

## 14. Agent 首次执行任务

收到本文件后，不要立即一次性生成完整产品。

请按以下顺序开始：

1. 检查当前仓库和开发环境。
2. 验证 `winapp` CLI 的可用命令和模板。
3. 确认 .NET SDK、Windows App SDK 和 libmpv 的实际兼容性。
4. 对比复用 MiniCast 后端与 C# 自研后端的成本、许可证和技术风险。
5. 输出简洁的架构决策记录。
6. 创建可构建的 WinUI 3 MVVM 项目和基础 Solution。
7. 创建 Core、DLNA、Player、Service 的必要项目。
8. 建立最小 IPC 协议。
9. 完成一次真实构建与运行验证。
10. 汇报实际结果，再进入下一阶段。

**第一阶段的成功标准不是代码量，而是拥有一个结构正确、能够构建运行、后续可以可靠扩展的项目骨架。**

