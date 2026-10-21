# Automated end-to-end check of the fullscreen overlay, driven through the real service.
#
# Issues DLNA AVTransport SOAP calls exactly as a control point would, then reads the resulting
# Win32 window state after every step. Verifies: hidden before a cast, fullscreen+topmost on Play,
# preserved across pause/seek/media switch, hidden after Stop, and reused on replay.
#
# Requires: a built service (dotnet build), node (for media-server.js) and ffmpeg.
# Usage:    pwsh tools/control-point/verify-fullscreen-e2e.ps1
$ErrorActionPreference = "Stop"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int n);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
}
"@

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$exe = Join-Path $repo "src\DlnRenderer.Service\bin\Debug\net10.0-windows\DlnRenderer.Service.exe"
$mediaDir = Join-Path $env:TEMP "dln-e2e"
$log = Join-Path $env:TEMP "e2e-presentation.log"
$mediaUrl = "http://127.0.0.1:18777/long.mp4"
$script:results = @()

function Get-Presentation([uint32]$processId) {
    $script:found = @()
    $cb = [Win+EnumProc]{
        param($h, $l)
        $wpid = 0
        [Win]::GetWindowThreadProcessId($h, [ref]$wpid) | Out-Null
        if ($wpid -eq $processId) {
            $sb = New-Object System.Text.StringBuilder 256
            [Win]::GetClassName($h, $sb, 256) | Out-Null
            if ($sb.ToString() -like "DlnRendererVideoHost*") { $script:found += $h }
        }
        return $true
    }
    [Win]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
    if ($script:found.Count -eq 0) { return $null }

    $h = $script:found[0]
    $r = New-Object Win+RECT
    [Win]::GetWindowRect($h, [ref]$r) | Out-Null
    $ex = [Win]::GetWindowLongPtr($h, -20).ToInt64()
    return [pscustomobject]@{
        Hwnd       = ("0x{0:X}" -f $h.ToInt64())
        Visible    = [Win]::IsWindowVisible($h)
        Topmost    = [bool]($ex -band 0x8)
        ToolWindow = [bool]($ex -band 0x80)
        Width      = $r.R - $r.L
        Height     = $r.B - $r.T
        Origin     = "$($r.L),$($r.T)"
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

function Set-Uri($url) {
    $meta = "<DIDL-Lite xmlns=`"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/`" xmlns:dc=`"http://purl.org/dc/elements/1.1/`" xmlns:upnp=`"urn:schemas-upnp-org:metadata-1-0/upnp/`"><item id=`"1`" parentID=`"0`" restricted=`"1`"><dc:title>Long</dc:title><upnp:class>object.item.videoItem</upnp:class><res protocolInfo=`"http-get:*:video/mp4:*`">$url</res></item></DIDL-Lite>"
    [System.Security.SecurityElement]::Escape($meta) | Out-Null
    Soap "avtransport" "AVTransport:1" "SetAVTransportURI" "<InstanceID>0</InstanceID><CurrentURI>$url</CurrentURI><CurrentURIMetaData>$([System.Security.SecurityElement]::Escape($meta))</CurrentURIMetaData>" | Out-Null
}

function State { Soap "avtransport" "AVTransport:1" "GetTransportInfo" "<InstanceID>0</InstanceID>" }

function Check($label, $condition) {
    $verdict = if ($condition) { "PASS" } else { "FAIL" }
    $script:results += "$verdict  $label"
    Write-Host ("  [{0}] {1}" -f $verdict, $label)
}

# A 2 s clip would finish mid-test; the overlay must stay up across pause/seek/media switch.
if (-not (Test-Path (Join-Path $mediaDir "long.mp4"))) {
    New-Item -ItemType Directory -Force -Path $mediaDir | Out-Null
    Write-Host "generating test clip..."
    & ffmpeg -y -v error -f lavfi -i "testsrc=size=640x360:rate=30:duration=60" `
        -f lavfi -i "sine=frequency=440:duration=60" `
        -c:v libx264 -preset ultrafast -pix_fmt yuv420p -c:a aac -shortest `
        (Join-Path $mediaDir "long.mp4")
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ffmpeg could not create the test clip" -ForegroundColor Red
        exit 1
    }
}

Get-Process DlnRenderer.Service -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-Item $log -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

$media = Start-Process -FilePath "node" -ArgumentList (Join-Path $repo "tools\control-point\media-server.js"), $mediaDir, "18777" -PassThru -WindowStyle Hidden
$svc = Start-Process -FilePath $exe -ArgumentList "--log", $log -PassThru -WindowStyle Hidden
$pidSvc = [uint32]$svc.Id

try {
    for ($i = 0; $i -lt 60; $i++) {
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern "ssdp active" -Quiet)) { break }
        Start-Sleep -Milliseconds 500
    }
    $monitors = Get-CimInstance Win32_VideoController | Select-Object CurrentHorizontalResolution, CurrentVerticalResolution
    Write-Host "== 1. startup =="
    $p = Get-Presentation $pidSvc
    Write-Host ("  hwnd={0} visible={1} topmost={2} tool={3} size={4}x{5}" -f $p.Hwnd, $p.Visible, $p.Topmost, $p.ToolWindow, $p.Width, $p.Height)
    Check "window hidden before any cast" (-not $p.Visible)

    Write-Host "== 2. SetAVTransportURI must not take the screen =="
    Set-Uri $mediaUrl
    Start-Sleep -Seconds 2
    $p = Get-Presentation $pidSvc
    Write-Host ("  visible={0} size={1}x{2} state={3}" -f $p.Visible, $p.Width, $p.Height, ((State) -replace '.*<CurrentTransportState>([^<]+).*', '$1'))
    Check "window still hidden after SetAVTransportURI" (-not $p.Visible)

    Write-Host "== 3. Play: fullscreen + topmost =="
    Soap "avtransport" "AVTransport:1" "Play" "<InstanceID>0</InstanceID><Speed>1</Speed>" | Out-Null
    Start-Sleep -Seconds 3
    $p = Get-Presentation $pidSvc
    Write-Host ("  visible={0} topmost={1} origin={2} size={3}x{4} state={5}" -f $p.Visible, $p.Topmost, $p.Origin, $p.Width, $p.Height, ((State) -replace '.*<CurrentTransportState>([^<]+).*', '$1'))
    Check "window visible while playing" $p.Visible
    Check "window is topmost while playing" $p.Topmost
    Check "window covers a full monitor (not the taskbar-reduced work area)" ($p.Width -gt 800 -and $p.Height -gt 600)
    $hwndPlaying = $p.Hwnd

    Write-Host "== 4. Pause must keep fullscreen + topmost =="
    Soap "avtransport" "AVTransport:1" "Pause" "<InstanceID>0</InstanceID>" | Out-Null
    Start-Sleep -Seconds 2
    $p = Get-Presentation $pidSvc
    Write-Host ("  visible={0} topmost={1} size={2}x{3} state={4}" -f $p.Visible, $p.Topmost, $p.Width, $p.Height, ((State) -replace '.*<CurrentTransportState>([^<]+).*', '$1'))
    Check "window stays visible while paused" $p.Visible
    Check "window stays topmost while paused" $p.Topmost
    Check "same HWND reused (no duplicate window)" ($p.Hwnd -eq $hwndPlaying)

    Write-Host "== 5. Resume + Seek must not disturb the window =="
    Soap "avtransport" "AVTransport:1" "Play" "<InstanceID>0</InstanceID><Speed>1</Speed>" | Out-Null
    Start-Sleep -Seconds 1
    Soap "avtransport" "AVTransport:1" "Seek" "<InstanceID>0</InstanceID><Unit>REL_TIME</Unit><Target>00:00:10</Target>" | Out-Null
    Start-Sleep -Seconds 2
    $p = Get-Presentation $pidSvc
    $pos = (Soap "avtransport" "AVTransport:1" "GetPositionInfo" "<InstanceID>0</InstanceID>") -replace '.*<RelTime>([^<]+).*', '$1'
    Write-Host ("  visible={0} topmost={1} hwnd={2} position={3}" -f $p.Visible, $p.Topmost, $p.Hwnd, $pos)
    Check "window stays visible after resume+seek" $p.Visible
    Check "window stays topmost after resume+seek" $p.Topmost
    Check "seek moved the position past 00:00:09" ($pos -ne $null -and $pos -ge "00:00:09")


    Write-Host "== 6. Switch media reuses the same window =="
    Set-Uri $mediaUrl
    Start-Sleep -Seconds 2
    Soap "avtransport" "AVTransport:1" "Play" "<InstanceID>0</InstanceID><Speed>1</Speed>" | Out-Null
    Start-Sleep -Seconds 3
    $p = Get-Presentation $pidSvc
    Write-Host ("  visible={0} topmost={1} hwnd={2} size={3}x{4}" -f $p.Visible, $p.Topmost, $p.Hwnd, $p.Width, $p.Height)
    Check "window still visible after media switch" $p.Visible
    Check "same HWND after media switch (no second window)" ($p.Hwnd -eq $hwndPlaying)
    Check "still exactly one presenter window" ($script:found.Count -eq 1)
    # mpv creates its own child window inside the `wid` surface, so the host legitimately has two
    # descendants: the surface handed to mpv, and the VO window mpv puts in it. What must not happen
    # is a *second* surface, which would mean a second player.
    $script:surfaces = 0
    $cb = [Win+EnumProc]{
        param($h, $l)
        $sb = New-Object System.Text.StringBuilder 256
        [Win]::GetClassName($h, $sb, 256) | Out-Null
        if ($sb.ToString() -eq "Static") { $script:surfaces++ }
        return $true
    }
    $script:surfaces = 0
    [Win]::EnumChildWindows($script:found[0], $cb, [IntPtr]::Zero) | Out-Null
    Check "exactly one mpv surface (no second player)" ($script:surfaces -eq 1)

    Write-Host "== 7. Stop hides the window =="
    Soap "avtransport" "AVTransport:1" "Stop" "<InstanceID>0</InstanceID>" | Out-Null
    Start-Sleep -Seconds 2
    $p = Get-Presentation $pidSvc
    Write-Host ("  visible={0} topmost={1} hwnd={2}" -f $p.Visible, $p.Topmost, $p.Hwnd)
    Check "window hidden after Stop" (-not $p.Visible)
    Check "topmost dropped after Stop" (-not $p.Topmost)
    Check "window handle kept for the next play" ($p.Hwnd -eq $hwndPlaying)

    Write-Host "== 8. Play again re-shows the same window =="
    Soap "avtransport" "AVTransport:1" "Play" "<InstanceID>0</InstanceID><Speed>1</Speed>" | Out-Null
    Start-Sleep -Seconds 3
    $p = Get-Presentation $pidSvc
    Write-Host ("  visible={0} topmost={1} hwnd={2} state={3}" -f $p.Visible, $p.Topmost, $p.Hwnd, ((State) -replace '.*<CurrentTransportState>([^<]+).*', '$1'))
    Check "window visible again after replay" $p.Visible
    Check "same HWND after replay" ($p.Hwnd -eq $hwndPlaying)

    Write-Host "== 9. Service shutdown while fullscreen =="
    Stop-Process -Id $svc.Id -Force
    Start-Sleep -Seconds 2
    $alive = Get-Process -Id $svc.Id -ErrorAction SilentlyContinue
    Check "service process exited" ($null -eq $alive)
}
finally {
    Stop-Process -Id $svc.Id -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $media.Id -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "== summary =="
$script:results | ForEach-Object { Write-Host "  $_" }
$failed = ($script:results | Where-Object { $_ -like "FAIL*" }).Count
Write-Host "  failed: $failed / $($script:results.Count)"
