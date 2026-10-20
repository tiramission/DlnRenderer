# 测试指南

本轮（阶段 1.5）只覆盖播放引擎，不包含 DLNA 协议测试。

## 1. 自动化测试

```powershell
dotnet restore DlnRenderer.sln --locked-mode
dotnet build DlnRenderer.sln -c Debug
dotnet test DlnRenderer.sln -c Debug
```

实测结果：**40 通过 / 0 失败**（Core 25，Player 15）。

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
| `Audio_playback_pause_seek_volume_and_stop_work` | 真实解码 WAV：时长、`MediaKind.Audio`、播放推进、暂停后位置冻结、Seek、音量、静音、停止 |
| `Video_file_is_decoded_and_reports_video_kind` | 真实解码 MP4 夹具：时长、`MediaKind.Video`、播放推进 |
| `Unplayable_file_surfaces_an_error_state` | 打开不存在的文件 → `Error` + 非空错误信息 |
| `Dispose_releases_native_resources_quickly_and_is_idempotent` | 释放 < 5 s、重复释放安全、释放后调用抛 `ObjectDisposedException` |
| `Sequential_loads_replace_media_and_reset_position` | 音频→视频→音频 连续加载，类型正确、位置重置 |
| `Dispose_while_playing_does_not_hang` | 播放中释放 < 5 s |

集成测试使用 `ao=null` / `vo=null`，因此**不验证**真实声卡输出与窗口渲染——这两项由下面的手工验收覆盖。

生成测试媒体的方式（无需仓库外资源）：

- 音频：测试内用 C# 写 16-bit PCM 正弦波 WAV。
- 视频：`tests/DlnRenderer.Player.Tests/Media/sample-video.mp4`（2 s，13 KB，testsrc2 生成）。

## 2. 手工验收（真实输出）

自动化无法断言"看得见、听得到"，必须人工确认。步骤与预期：

### 2.1 视频

```powershell
winapp run src/DlnRenderer.App -c Debug -- --play "C:\path\video.mp4" --log "$env:TEMP\dln.log"
```

预期：

1. 视频区域显示画面，而不是浅色占位卡片。
2. 拖动窗口边缘缩放，画面跟随区域缩放且不消失。
3. 进度条推进；暂停后进度停止。
4. 关闭窗口后进程退出，日志出现 `player disposed`。

本轮实测：画面渲染（1768/2418 采样点为高饱和像素）、相邻帧存在运动（平均通道差 12.76）、
缩放后继续渲染（1809/2370）、关闭后 `player disposed` 且进程退出。

### 2.2 音频

```powershell
winapp run src/DlnRenderer.App -c Debug -- --play "C:\path\audio.mp3" --log "$env:TEMP\dln.log"
```

预期：

1. 能听到声音（本轮**未**验证人耳可听；仅确认 `current-ao=wasapi`、48 kHz 双声道、时间轴按 1× 推进）。
2. 视频区域保持占位卡片，不出现黑色子窗口。
3. 暂停/恢复/停止、音量滑块行为正确。

### 2.3 手动控制

在 UI 中依次执行：打开文件 → 播放 → 暂停 → 恢复 → 拖动进度条 → 调整音量 → 停止。
每步观察标题、状态文字、进度与按钮状态是否一致；错误时 `InfoBar` 应显示原因。

## 3. 未覆盖项

| 项目 | 状态 |
| --- | --- |
| 人耳可听的音频输出 | 未验证（无人工听感确认） |
| ARM64 构建与播放 | 未验证（本机 x64） |
| Windows 10 1809–19045 | 未验证 |
| 字幕、多音轨选择 | 未实现 |
| 网络媒体（HTTP/DLNA URI） | 播放器支持直传 URI，但未做真实控制端测试 |
| DLNA 协议一致性 | 未开始 |
