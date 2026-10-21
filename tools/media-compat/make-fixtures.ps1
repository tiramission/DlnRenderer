<#
.SYNOPSIS
  Generates the small, self-made media samples used by the stage D compatibility measurement.

.DESCRIPTION
  Every file is synthesised from lavfi test patterns and sine tones, so the samples carry no
  copyright, stay a few hundred KB each and can be rebuilt on any machine with ffmpeg. The
  directory is intentionally outside the repository's tracked content (artifacts/ is ignored).

  A negative control ("this is not a media file" named *.mp4) is written too: a renderer that
  reports "plays fine" for it is broken, and the measurement needs that column to be meaningful.

.EXAMPLE
  pwsh tools/media-compat/make-fixtures.ps1
  pwsh tools/media-compat/make-fixtures.ps1 -OutputDirectory D:\fixtures -Force
#>
[CmdletBinding()]
param(
    # Default: <repo>/artifacts/media-fixtures (artifacts/ is in .gitignore).
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'artifacts\media-fixtures'),
    # Regenerate even when files already exist.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    Write-Error 'ffmpeg was not found on PATH. Install it (e.g. `scoop install ffmpeg`) and rerun.'
    exit 1
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$resolved = (Resolve-Path $OutputDirectory).Path

# 2.5 s of a moving test pattern with a sine tone: short enough to stay tiny, long enough that
# position advancement is measurable.
$videoSource = @('-f', 'lavfi', '-i', 'testsrc2=size=320x180:rate=25:duration=2.5')
$toneA = @('-f', 'lavfi', '-i', 'sine=frequency=440:duration=2.5')
$toneB = @('-f', 'lavfi', '-i', 'sine=frequency=880:duration=2.5')

function New-FFmpegArgs {
    param(
        # NOT named $Input: that is an automatic variable in PowerShell and would come back empty.
        [string]$OutputPath,
        [string[]]$Sources,
        [string[]]$Codecs,
        [string[]]$Extra = @()
    )
    @('-hide_banner', '-loglevel', 'error', '-y') + $Sources + $Codecs + $Extra + $OutputPath
}

# name -> argument list. Order matters only for readability.
$targets = [ordered]@{
    'video-h264-aac.mp4'              = New-FFmpegArgs (Join-Path $resolved 'video-h264-aac.mp4') ($videoSource + $toneA) @('-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '64k')
    'video-hevc10-aac.mp4'            = New-FFmpegArgs (Join-Path $resolved 'video-hevc10-aac.mp4') ($videoSource + $toneA) @('-c:v', 'libx265', '-preset', 'veryfast', '-pix_fmt', 'yuv420p10le', '-x265-params', 'log-level=none', '-c:a', 'aac', '-b:a', '64k')
    'video-h264-aac.mkv'              = New-FFmpegArgs (Join-Path $resolved 'video-h264-aac.mkv') ($videoSource + $toneA) @('-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '64k')
    'video-vp9-opus.mkv'              = New-FFmpegArgs (Join-Path $resolved 'video-vp9-opus.mkv') ($videoSource + $toneA) @('-c:v', 'libvpx-vp9', '-b:v', '120k', '-c:a', 'libopus', '-b:a', '48k')
    'video-vp8-opus.webm'             = New-FFmpegArgs (Join-Path $resolved 'video-vp8-opus.webm') ($videoSource + $toneA) @('-c:v', 'libvpx', '-b:v', '120k', '-c:a', 'libopus', '-b:a', '48k')
    'video-av1-opus.webm'             = New-FFmpegArgs (Join-Path $resolved 'video-av1-opus.webm') ($videoSource + $toneA) @('-c:v', 'libsvtav1', '-preset', '10', '-crf', '40', '-pix_fmt', 'yuv420p', '-c:a', 'libopus', '-b:a', '48k')
    'video-mpeg4-mp3.avi'             = New-FFmpegArgs (Join-Path $resolved 'video-mpeg4-mp3.avi') ($videoSource + $toneA) @('-c:v', 'mpeg4', '-q:v', '5', '-c:a', 'libmp3lame', '-b:a', '96k')
    'video-h264-aac.mov'              = New-FFmpegArgs (Join-Path $resolved 'video-h264-aac.mov') ($videoSource + $toneA) @('-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '64k')
    'video-mpeg2-mp2.ts'              = New-FFmpegArgs (Join-Path $resolved 'video-mpeg2-mp2.ts') ($videoSource + $toneA) @('-c:v', 'mpeg2video', '-q:v', '5', '-c:a', 'mp2', '-b:a', '96k')
    'video-h264-aac.flv'              = New-FFmpegArgs (Join-Path $resolved 'video-h264-aac.flv') ($videoSource + $toneA) @('-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '64k', '-flvflags', 'no_duration_filesize')
    'video-mpeg2-ac3.mkv'             = New-FFmpegArgs (Join-Path $resolved 'video-mpeg2-ac3.mkv') ($videoSource + $toneA) @('-c:v', 'mpeg2video', '-q:v', '5', '-c:a', 'ac3', '-b:a', '96k')
    'audio-mp3.mp3'                   = New-FFmpegArgs (Join-Path $resolved 'audio-mp3.mp3') $toneA @('-c:a', 'libmp3lame', '-b:a', '96k')
    'audio-aac.m4a'                   = New-FFmpegArgs (Join-Path $resolved 'audio-aac.m4a') $toneA @('-c:a', 'aac', '-b:a', '96k', '-movflags', '+faststart')
    'audio-flac.flac'                 = New-FFmpegArgs (Join-Path $resolved 'audio-flac.flac') $toneA @('-c:a', 'flac')
    'audio-opus.opus'                 = New-FFmpegArgs (Join-Path $resolved 'audio-opus.opus') $toneA @('-c:a', 'libopus', '-b:a', '64k')
    'audio-vorbis.ogg'                = New-FFmpegArgs (Join-Path $resolved 'audio-vorbis.ogg') $toneA @('-c:a', 'libvorbis')
    'audio-pcm.wav'                   = New-FFmpegArgs (Join-Path $resolved 'audio-pcm.wav') $toneA @('-c:a', 'pcm_s16le')
    'audio-ac3.ac3'                   = New-FFmpegArgs (Join-Path $resolved 'audio-ac3.ac3') $toneA @('-f', 'ac3', '-c:a', 'ac3', '-b:a', '192k')
    # Suspicious declarations, measured on purpose: the sink list claims video/swf and audio/midi,
    # and D2 only removes an entry once something here proves it cannot be played.
    'video-flv1-mp3.swf'              = New-FFmpegArgs (Join-Path $resolved 'video-flv1-mp3.swf') ($videoSource + $toneA) @('-c:v', 'flv1', '-q:v', '5', '-c:a', 'libmp3lame', '-b:a', '96k', '-f', 'swf')
}

# MKV with two audio tracks and one embedded SRT subtitle: the evidence for the D4/D5 decision
# (does the engine see subtitles and alternate tracks at all?).
$subtitlePath = Join-Path $resolved 'sample.srt'
@"
1
00:00:00,000 --> 00:00:01,500
hello from the embedded subtitle

2
00:00:01,500 --> 00:00:02,500
second line
"@ | Set-Content -Path $subtitlePath -Encoding utf8

$targets['video-h264-dualaudio-subrip.mkv'] = New-FFmpegArgs `
    (Join-Path $resolved 'video-h264-dualaudio-subrip.mkv') `
    ($videoSource + $toneA + $toneB + @('-i', $subtitlePath)) `
    @('-map', '0:v', '-map', '1:a', '-map', '2:a', '-map', '3:s',
      '-c:v', 'libx264', '-preset', 'veryfast', '-pix_fmt', 'yuv420p',
      '-c:a', 'aac', '-b:a', '64k', '-c:s', 'srt',
      '-metadata:s:a:0', 'language=eng', '-metadata:s:a:1', 'language=chi',
      '-metadata:s:s:0', 'language=eng')

$generated = 0
$skipped = 0
foreach ($name in $targets.Keys) {
    $path = Join-Path $resolved $name
    if ((Test-Path $path) -and -not $Force) {
        $skipped++
        continue
    }

    Write-Host "  generating $name"
    & ffmpeg @($targets[$name])
    if ($LASTEXITCODE -ne 0) {
        Write-Error "ffmpeg failed for $name (exit $LASTEXITCODE)"
        exit 1
    }

    $generated++
}

# Negative control: the name says media, the content is plain text. A measurement without this
# cannot tell "reports failure correctly" from "reports success for everything".
$controlPath = Join-Path $resolved 'invalid-not-media.mp4'
if (-not (Test-Path $controlPath) -or $Force) {
    Set-Content -Path $controlPath -Value 'this is not a media file' -Encoding ascii
    $generated++
}
else {
    $skipped++
}

Remove-Item -Path $subtitlePath -ErrorAction SilentlyContinue

# A minimal Standard MIDI File (format 0, one note): ffmpeg has no MIDI encoder, so the bytes are
# written by hand. "unsupported-" is the file-name contract the measurement reads: those files and
# "invalid-*" must fail to play, everything else must play.
$midPath = Join-Path $resolved 'unsupported-midi.mid'
if (-not (Test-Path $midPath) -or $Force) {
    $smf = [byte[]]@(
        0x4D, 0x54, 0x68, 0x64, 0, 0, 0, 6, 0, 0, 0, 1, 0x01, 0xE0,
        0x4D, 0x54, 0x72, 0x6B, 0, 0, 0, 12,
        0x00, 0x90, 0x3C, 0x64,
        0x83, 0x60, 0x80, 0x3C, 0x64,
        0x00, 0xFF, 0x2F, 0x00)
    [System.IO.File]::WriteAllBytes($midPath, $smf)
    $generated++
}
else {
    $skipped++
}

$files = Get-ChildItem -Path $resolved -File
$total = [math]::Round(($files | Measure-Object -Property Length -Sum).Sum / 1KB)
Write-Output "fixtures in $resolved : $($files.Count) files, ${total} KB (generated $generated, reused $skipped)"
