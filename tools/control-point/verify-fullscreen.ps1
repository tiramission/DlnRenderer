# Manual desktop verification of the fullscreen overlay (run from an interactive session).
#
# Why this exists: a hidden window cannot receive WM_KEYDOWN, so "Escape leaves fullscreen" and
# "Alt+Tab / Win key still work" cannot be asserted from an automated run. This script puts the
# overlay on screen and asks the operator to confirm what they see.
#
# Usage:  pwsh tools/control-point/verify-fullscreen.ps1
$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class FsWin {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repo "src\DlnRenderer.Service\bin\Debug\net10.0-windows\DlnRenderer.Service.exe"
$mediaDir = Join-Path $env:TEMP "dln-e2e"

function Find-Overlay([uint32]$processId) {
    $script:found = @()
    $cb = [FsWin+EnumProc]{
        param($h, $l)
        $wpid = 0
        [FsWin]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
        if ($wpid -eq $processId) {
            $sb = New-Object System.Text.StringBuilder 256
            [FsWin]::GetClassName($h, $sb, 256) | Out-Null
            if ($sb.ToString() -like "DlnRendererVideoHost*") { $script:found += $h }
        }
        return $true
    }
    [FsWin]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    if ($script:found.Count -eq 0) { return $null }

    $h = $script:found[0]
    $r = New-Object FsWin+RECT
    [FsWin]::GetWindowRect($h, [ref]$r) | Out-Null
    $ex = [FsWin]::GetWindowLongPtr($h, -20).ToInt64()
    [pscustomobject]@{
        Hwnd     = $h
        Visible  = [FsWin]::IsWindowVisible($h)
        Topmost  = [bool]($ex -band 0x8)
        Foreground = ([FsWin]::GetForegroundWindow() -eq $h)
        Width    = $r.R - $r.L
        Height   = $r.B - $r.T
        Origin   = "$($r.L),$($r.T)"
    }
}

function Soap($service, $serviceType, $action, $inner) {
    $n = $inner -replace '<([A-Za-z]+)>', '<u:$1>' -replace '</([A-Za-z]+)>', '</u:$1>'
    $envelope = '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/"><s:Body><u:' + $action + ' xmlns:u="urn:schemas-upnp-org:service:' + $serviceType + '">' + $n + '</u:' + $action + '></s:Body></s:Envelope>'
    $r = Invoke-WebRequest "http://127.0.0.1:18090/control/$service" -Method Post `
        -Headers @{ SOAPAction = "`"urn:schemas-upnp-org:service:$serviceType#$action`""; "Content-Type" = 'text/xml; charset="utf-8"' } `
        -Body ([Text.Encoding]::UTF8.GetBytes($envelope)) -UseBasicParsing -TimeoutSec 20
    return $r.Content
}

function Ask($question) {
    Write-Host ""
    Write-Host "  >>> $question" -ForegroundColor Cyan
    $answer = Read-Host "      [y]es / [n]o"
    return $answer -match '^(y|yes)$'
}

if (-not (Test-Path (Join-Path $mediaDir "long.mp4"))) {
    New-Item -ItemType Directory -Force -Path $mediaDir | Out-Null
    Write-Host "generating test clip..."
    & ffmpeg -y -v error -f lavfi -i "testsrc=size=640x360:rate=30:duration=60" `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p (Join-Path $mediaDir "long.mp4")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ffmpeg could not create the test clip" -ForegroundColor Red
        exit 1
    }
}

Get-Process DlnRenderer.Service -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

$media = Start-Process -FilePath "node" -ArgumentList (Join-Path $repo "tools\control-point\media-server.js"), $mediaDir, "18777" -PassThru -WindowStyle Hidden
$svc = Start-Process -FilePath $exe -ArgumentList "--log", (Join-Path $env:TEMP "verify-fullscreen.log") -PassThru
$url = "http://127.0.0.1:18777/long.mp4"
$results = @()

try {
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest "http://127.0.0.1:18090/description.xml" -UseBasicParsing -TimeoutSec 2 | Out-Null; break } catch { Start-Sleep -Milliseconds 500 }
    }

    $meta = "<DIDL-Lite xmlns=`"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/`" xmlns:dc=`"http://purl.org/dc/elements/1.1/`" xmlns:upnp=`"urn:schemas-upnp-org:metadata-1-0/upnp/`"><item id=`"1`" parentID=`"0`" restricted=`"1`"><dc:title>Long</dc:title><upnp:class>object.item.videoItem</upnp:class><res protocolInfo=`"http-get:*:video/mp4:*`">$url</res></item></DIDL-Lite>"
    Soap "avtransport" "AVTransport:1" "SetAVTransportURI" "<InstanceID>0</InstanceID><CurrentURI>$url</CurrentURI><CurrentURIMetaData>$([System.Security.SecurityElement]::Escape($meta))</CurrentURIMetaData>" | Out-Null
    Soap "avtransport" "AVTransport:1" "Play" "<InstanceID>0</InstanceID><Speed>1</Speed>" | Out-Null
    Start-Sleep -Seconds 3

    $o = Find-Overlay ([uint32]$svc.Id)
    Write-Host ""
    Write-Host "Overlay: visible=$($o.Visible) topmost=$($o.Topmost) foreground=$($o.Foreground) origin=$($o.Origin) size=$($o.Width)x$($o.Height)"

    $results += "window covers the whole screen including the taskbar area: " + (Ask "Does the video fill the entire screen, with no desktop, taskbar strip or window border visible?")

    # Ordinary window above the overlay.
    $notepad = Start-Process notepad -PassThru
    Start-Sleep -Seconds 2
    $o2 = Find-Overlay ([uint32]$svc.Id)
    Write-Host "After launching Notepad: overlay visible=$($o2.Visible) topmost=$($o2.Topmost)"
    $results += "ordinary window cannot cover the overlay: " + (-not (Ask "Did Notepad appear ON TOP of the video? (answer y only if it covered the video)"))
    Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue

    # Alt+Tab must still work.
    $results += "Alt+Tab still works: " + (Ask "Press Alt+Tab now. Does the Windows task switcher open normally?")
    $results += "Win key still works: " + (Ask "Press the Windows key. Does the Start menu open normally?")

    # Escape leaves the overlay.
    $escaped = Ask "With the video focused, press Escape. Did the video window disappear?"
    $results += "Escape leaves fullscreen: $escaped"
    $o3 = Find-Overlay ([uint32]$svc.Id)
    Write-Host "After Escape: visible=$($o3.Visible)"
    $results += "Escape actually hid the window (measured): " + (-not $o3.Visible)

    # Play again for the finish.
    Soap "avtransport" "AVTransport:1" "Play" "<InstanceID>0</InstanceID><Speed>1</Speed>" | Out-Null
    Start-Sleep -Seconds 3
    $o4 = Find-Overlay ([uint32]$svc.Id)
    Write-Host "After replay: visible=$($o4.Visible) topmost=$($o4.Topmost) size=$($o4.Width)x$($o4.Height)"
    $results += "replay restores fullscreen after Escape: " + ($o4.Visible -and $o4.Width -gt 1000)

    Soap "avtransport" "AVTransport:1" "Stop" "<InstanceID>0</InstanceID>" | Out-Null
    Start-Sleep -Seconds 2
    $o5 = Find-Overlay ([uint32]$svc.Id)
    Write-Host "After Stop: visible=$($o5.Visible)"
    $results += "Stop hides the overlay (measured): " + (-not $o5.Visible)
}
finally {
    Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $media.Id -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "== results ==" -ForegroundColor Cyan
$results | ForEach-Object { Write-Host "  $_" }
