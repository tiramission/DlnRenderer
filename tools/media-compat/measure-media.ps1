<#
.SYNOPSIS
  Measures what the player engine really decodes and renders docs/media-matrix.md from the results.

.DESCRIPTION
  Stage D (D1) deliverable. The evidence comes from real playback, not from the protocol
  declaration: fixtures are generated with ffmpeg (see make-fixtures.ps1), the Player integration
  tests load and play every one of them through libmpv, and each sample appends a result row.
  This script then turns those rows into the matrix.

  The tests are the gate, the script only formats: if any fixture fails, dotnet test fails, the
  script stops with a non-zero exit code and no document is written.

.EXAMPLE
  pwsh tools/media-compat/measure-media.ps1
#>
[CmdletBinding()]
param(
    # Where the fixture samples are generated (gitignored).
    [string]$FixtureDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts\media-fixtures'),
    # Where the matrix is written.
    [string]$OutputPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'docs\media-matrix.md')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

# 1. Fixtures (skipped when they already exist).
& (Join-Path $PSScriptRoot 'make-fixtures.ps1') -OutputDirectory $FixtureDirectory
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { exit 1 }

# 2. Real playback through libmpv, one result row per sample.
$resultsPath = Join-Path ([System.IO.Path]::GetTempPath()) ("dlnr-media-results-" + [guid]::NewGuid().ToString('N') + '.txt')
$env:DLNR_MEDIA_FIXTURES = (Resolve-Path $FixtureDirectory).Path
$env:DLNR_MEDIA_RESULTS = $resultsPath

try {
    & dotnet test (Join-Path $repoRoot 'tests\DlnRenderer.Player.Tests\DlnRenderer.Player.Tests.csproj') `
        --filter 'FullyQualifiedName~MediaCompatibilityTests' -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'A fixture failed to play (or played when it must not). No matrix was written.'
        exit 1
    }

    if (-not (Test-Path $resultsPath)) {
        Write-Error 'The tests produced no result rows; nothing to render.'
        exit 1
    }
}
finally {
    Remove-Item Env:DLNR_MEDIA_FIXTURES -ErrorAction SilentlyContinue
    Remove-Item Env:DLNR_MEDIA_RESULTS -ErrorAction SilentlyContinue
}

$rows = Get-Content -Path $resultsPath | Where-Object { $_ } | ForEach-Object {
    $parts = $_ -split '\|', 6
    [pscustomobject]@{
        File     = $parts[0]
        Kind     = $parts[1]
        Duration = $parts[2]
        Hwdec    = $parts[3]
        Outcome  = $parts[4]
        Detail   = $parts[5]
    }
}

$fixtures = $rows | Where-Object { $_.File -notmatch '^#' -and $_.File -notlike 'hwdec-*' }
$hwdecRows = $rows | Where-Object { $_.File -like 'hwdec-*' }

$played = @($fixtures | Where-Object Outcome -eq 'played').Count
$failed = @($fixtures | Where-Object Outcome -eq 'failed').Count

$mpvDll = Get-ChildItem -Path (Join-Path $repoRoot 'tests\DlnRenderer.Player.Tests\bin') -Filter 'libmpv-2.dll' `
    -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
$mpvVersion = if ($mpvDll) { $mpvDll.VersionInfo.FileVersion } else { 'unknown' }
$ffmpegRaw = (& ffmpeg -version 2>$null | Select-Object -First 1)
$ffmpegVersion = if ($ffmpegRaw -match 'ffmpeg version (\S+)') { $Matches[1] } else { 'unknown' }
$os = [System.Environment]::OSVersion.VersionString

# "video-hevc10-aac.mp4" -> "hevc10-aac · mp4". The codecs are part of the fixture name on
# purpose, so the matrix needs no extra metadata file to say what each row was.
function Get-CodecLabel([string]$name) {
    $base = [System.IO.Path]::GetFileNameWithoutExtension($name)
    foreach ($prefix in @('video-', 'audio-', 'unsupported-', 'invalid-')) {
        if ($base.StartsWith($prefix)) { $base = $base.Substring($prefix.Length); break }
    }
    $ext = [System.IO.Path]::GetExtension($name).TrimStart('.')
    if (-not $ext) { $ext = 'bin' }
    "$base · $ext"
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('# 媒体兼容性矩阵（阶段 D 实测）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('> 本文件由 `pwsh tools/media-compat/measure-media.ps1` 生成，数据来自**真实播放**：')
[void]$sb.AppendLine('> 样片用 ffmpeg 自生成（无版权、单个 < 300 KB，不入库），由 `MediaCompatibilityTests`')
[void]$sb.AppendLine('> 通过 libmpv 逐个加载、播放并观察位置推进。声明过但实测不可播的条目已从')
[void]$sb.AppendLine('> `SinkProtocolInfo` 移除。')
[void]$sb.AppendLine()
[void]$sb.AppendLine("## 测量环境")
[void]$sb.AppendLine()
[void]$sb.AppendLine("| 项 | 值 |")
[void]$sb.AppendLine("| --- | --- |")
[void]$sb.AppendLine("| 日期 | $(Get-Date -Format 'yyyy-MM-dd') |")
[void]$sb.AppendLine("| 系统 | $os |")
[void]$sb.AppendLine("| libmpv | $mpvVersion |")
[void]$sb.AppendLine("| ffmpeg（生成样片） | $ffmpegVersion |")
[void]$sb.AppendLine("| 样片数 | $($fixtures.Count)（$played 可播 / $failed 不可播） |")
[void]$sb.AppendLine()
[void]$sb.AppendLine('复现：')
[void]$sb.AppendLine()
[void]$sb.AppendLine('```powershell')
[void]$sb.AppendLine('pwsh tools/media-compat/make-fixtures.ps1   # 生成样片到 artifacts/media-fixtures')
[void]$sb.AppendLine('pwsh tools/media-compat/measure-media.ps1   # 重新测量并覆盖本文件')
[void]$sb.AppendLine('```')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 视频')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 样片 | 编码 · 容器 | 时长 | 实际解码 | 结论 |')
[void]$sb.AppendLine('| --- | --- | --- | --- | --- |')
foreach ($row in ($fixtures | Where-Object { $_.File -like 'video-*' -or $_.File -like 'invalid-*' } | Sort-Object File)) {
    $verdict = if ($row.Outcome -eq 'played') { '可播' } else { "不可播（$($row.Detail)）" }
    $duration = if ($row.Duration) { "$($row.Duration) s" } else { '—' }
    [void]$sb.AppendLine("| ``$($row.File)`` | $(Get-CodecLabel $row.File) | $duration | $($row.Hwdec) | $verdict |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 音频')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 样片 | 编码 · 容器 | 时长 | 结论 |')
[void]$sb.AppendLine('| --- | --- | --- | --- |')
foreach ($row in ($fixtures | Where-Object { $_.File -like 'audio-*' -or $_.File -like 'unsupported-*' } | Sort-Object File)) {
    $verdict = if ($row.Outcome -eq 'played') { '可播' } else { "不可播（$($row.Detail)）" }
    $duration = if ($row.Duration) { "$($row.Duration) s" } else { '—' }
    [void]$sb.AppendLine("| ``$($row.File)`` | $(Get-CodecLabel $row.File) | $duration | $verdict |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 硬件解码（D3）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('实际解码方式由 `MpvPlayer.CurrentHardwareDecodingMethod` 读取 mpv 的 `hwdec-current`，')
[void]$sb.AppendLine('不是“请求了什么”，而是“mpv 真正用了什么”。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 场景 | hwdec-current | 结果 |')
[void]$sb.AppendLine('| --- | --- | --- |')
foreach ($row in ($hwdecRows | Sort-Object File)) {
    $verdict = if ($row.Outcome -eq 'played') { '播放正常' } else { "失败（$($row.Detail)）" }
    [void]$sb.AppendLine("| ``$($row.File)`` | $($row.Hwdec) | $verdict |")
}
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 因实测/无解码器而从声明中移除的条目（D2）')
[void]$sb.AppendLine()
[void]$sb.AppendLine('移除 115 条（392 → 277），分类与依据：')
[void]$sb.AppendLine()
[void]$sb.AppendLine('| 类别 | 例子 | 依据 |')
[void]$sb.AppendLine('| --- | --- | --- |')
[void]$sb.AppendLine('| 音频内容被声明成 `video/*` | `video/mp3`、`video/wma`、`video/wav` | 声明本身是错的；控制端会以错误 MIME 投送 |')
[void]$sb.AppendLine('| libavcodec 没有解码器 | RealMedia（`video/rmvb`、`audio/x-ra`）、MIDI（`audio/midi`）、ATRAC3、QCELP | MIDI 用手工构造的 SMF 实测：`unrecognized file format`；其余格式无法生成样片，按无解码器记录 |')
[void]$sb.AppendLine('| DRM 保护的 profile | `WMDRM_WMA*`、`WMDRM_WMV*` | 渲染器没有 DRM 能力，声明即虚假承诺 |')
[void]$sb.AppendLine('| 占位值 | `video/unknown`、`audio/unknown` | 从别的设备抄来的占位项 |')
[void]$sb.AppendLine('| 静态图片 | `image/jpeg`、`image/png` 等 60 余条 | 没有幻灯片功能：mpv 显示一帧后立即结束，看起来像投屏失败 |')
[void]$sb.AppendLine()
[void]$sb.AppendLine('保留的 `video/swf` / `application/x-shockwave-flash` 是实测结论：ffmpeg 封装的 SWF（FLV1+MP3）可播。')
[void]$sb.AppendLine()
[void]$sb.AppendLine('## 未覆盖 / 已知限制')
[void]$sb.AppendLine()
[void]$sb.AppendLine('- 只测了本机（x64 + D3D11 显卡）。老机器没有硬解时，视频走软件解码，CPU 占用更高，但不受影响的格式集合一致。')
[void]$sb.AppendLine('- 只测 2.5 秒自生成样片，验证的是“能不能解码与推进”，不是长片、码率上限或 seek 精度。')
[void]$sb.AppendLine('- 内嵌字幕与双音轨样片（`video-h264-dualaudio-subrip.mkv`）可以播放，但渲染器尚未暴露字幕/音轨选择（D4/D5，见 ROADMAP）。')

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
Set-Content -Path $OutputPath -Value $sb.ToString() -Encoding utf8
Write-Output "wrote $OutputPath ($played playable, $failed unplayable)"
