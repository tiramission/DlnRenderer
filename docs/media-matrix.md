# 媒体兼容性矩阵（阶段 D 实测）

> 本文件由 `pwsh tools/media-compat/measure-media.ps1` 生成，数据来自**真实播放**：
> 样片用 ffmpeg 自生成（无版权、单个 < 300 KB，不入库），由 `MediaCompatibilityTests`
> 通过 libmpv 逐个加载、播放并观察位置推进。声明过但实测不可播的条目已从
> `SinkProtocolInfo` 移除。

## 测量环境

| 项 | 值 |
| --- | --- |
| 日期 | 2026-10-21 |
| 系统 | Microsoft Windows NT 10.0.26300.0 |
| libmpv | v0.41.0-1224-g6a262d11f |
| ffmpeg（生成样片） | 9.0.2-full_build-www.gyan.dev |
| 样片数 | 22（20 可播 / 2 不可播） |

复现：

```powershell
pwsh tools/media-compat/make-fixtures.ps1   # 生成样片到 artifacts/media-fixtures
pwsh tools/media-compat/measure-media.ps1   # 重新测量并覆盖本文件
```

## 视频

| 样片 | 编码 · 容器 | 时长 | 实际解码 | 结论 |
| --- | --- | --- | --- | --- |
| `invalid-not-media.mp4` | not-media · mp4 | — | software | 不可播（unrecognized file format） |
| `video-av1-opus.webm` | av1-opus · webm | 2.52 s | d3d11va-copy | 可播 |
| `video-flv1-mp3.swf` | flv1-mp3 · swf | 1.20 s | software | 可播 |
| `video-h264-aac.flv` | h264-aac · flv | 2.48 s | d3d11va-copy | 可播 |
| `video-h264-aac.mkv` | h264-aac · mkv | 2.52 s | d3d11va-copy | 可播 |
| `video-h264-aac.mov` | h264-aac · mov | 2.52 s | d3d11va-copy | 可播 |
| `video-h264-aac.mp4` | h264-aac · mp4 | 2.52 s | d3d11va-copy | 可播 |
| `video-h264-dualaudio-subrip.mkv` | h264-dualaudio-subrip · mkv | 2.52 s | d3d11va-copy | 可播 |
| `video-hevc10-aac.mp4` | hevc10-aac · mp4 | 2.52 s | d3d11va-copy | 可播 |
| `video-mpeg2-ac3.mkv` | mpeg2-ac3 · mkv | 2.52 s | software | 可播 |
| `video-mpeg2-mp2.ts` | mpeg2-mp2 · ts | 2.52 s | software | 可播 |
| `video-mpeg4-mp3.avi` | mpeg4-mp3 · avi | 2.56 s | software | 可播 |
| `video-vp8-opus.webm` | vp8-opus · webm | 2.52 s | nvdec-copy | 可播 |
| `video-vp9-opus.mkv` | vp9-opus · mkv | 2.52 s | d3d11va-copy | 可播 |

## 音频

| 样片 | 编码 · 容器 | 时长 | 结论 |
| --- | --- | --- | --- |
| `audio-aac.m4a` | aac · m4a | 2.50 s | 可播 |
| `audio-ac3.ac3` | ac3 · ac3 | 2.51 s | 可播 |
| `audio-flac.flac` | flac · flac | 2.50 s | 可播 |
| `audio-mp3.mp3` | mp3 · mp3 | 2.50 s | 可播 |
| `audio-opus.opus` | opus · opus | 2.51 s | 可播 |
| `audio-pcm.wav` | pcm · wav | 2.50 s | 可播 |
| `audio-vorbis.ogg` | vorbis · ogg | 2.50 s | 可播 |
| `unsupported-midi.mid` | midi · mid | — | 不可播（unrecognized file format） |

## 硬件解码（D3）

实际解码方式由 `MpvPlayer.CurrentHardwareDecodingMethod` 读取 mpv 的 `hwdec-current`，
不是“请求了什么”，而是“mpv 真正用了什么”。

| 场景 | hwdec-current | 结果 |
| --- | --- | --- |
| `hwdec-auto-safe-sample-video` | d3d11va-copy | 播放正常 |
| `hwdec-vaapi-fallback` | software | 播放正常 |

## 因实测/无解码器而从声明中移除的条目（D2）

移除 115 条（392 → 277），分类与依据：

| 类别 | 例子 | 依据 |
| --- | --- | --- |
| 音频内容被声明成 `video/*` | `video/mp3`、`video/wma`、`video/wav` | 声明本身是错的；控制端会以错误 MIME 投送 |
| libavcodec 没有解码器 | RealMedia（`video/rmvb`、`audio/x-ra`）、MIDI（`audio/midi`）、ATRAC3、QCELP | MIDI 用手工构造的 SMF 实测：`unrecognized file format`；其余格式无法生成样片，按无解码器记录 |
| DRM 保护的 profile | `WMDRM_WMA*`、`WMDRM_WMV*` | 渲染器没有 DRM 能力，声明即虚假承诺 |
| 占位值 | `video/unknown`、`audio/unknown` | 从别的设备抄来的占位项 |
| 静态图片 | `image/jpeg`、`image/png` 等 60 余条 | 没有幻灯片功能：mpv 显示一帧后立即结束，看起来像投屏失败 |

保留的 `video/swf` / `application/x-shockwave-flash` 是实测结论：ffmpeg 封装的 SWF（FLV1+MP3）可播。

## 未覆盖 / 已知限制

- 只测了本机（x64 + D3D11 显卡）。老机器没有硬解时，视频走软件解码，CPU 占用更高，但不受影响的格式集合一致。
- 只测 2.5 秒自生成样片，验证的是“能不能解码与推进”，不是长片、码率上限或 seek 精度。
- 内嵌字幕与双音轨样片（`video-h264-dualaudio-subrip.mkv`）可以播放，但渲染器尚未暴露字幕/音轨选择（D4/D5，见 ROADMAP）。

