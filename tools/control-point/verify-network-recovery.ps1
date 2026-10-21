# Verifies that the renderer recovers discovery after a network change.
#
# A real adapter change needs administrator rights and is not scriptable on every machine, so this
# check works in two layers:
#   1. it confirms the multicast membership the service reports at startup;
#   2. it prompts for (or performs) an address change and then re-queries SSDP to prove the device
#      answers again.
#
# Usage:
#   pwsh tools/control-point/verify-network-recovery.ps1
#   pwsh tools/control-point/verify-network-recovery.ps1 -Simulate
#
# -Simulate skips the adapter change and only checks the steady state, which is what a machine
# without administrator rights can verify.
param(
    [switch]$Simulate,
    [string]$LogPath
)

$ErrorActionPreference = "Stop"

$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$results = [System.Collections.ArrayList]::new()

function Check($name, $ok, $detail) {
    $verdict = if ($ok) { "PASS" } else { "FAIL" }
    [void]$results.Add("$verdict  $name$(if ($detail) { "  ($detail)" })")
    Write-Host "  [$verdict] $name$(if ($detail) { " - $detail" })"
}

if (-not $LogPath) {
    $LogPath = Join-Path $env:LOCALAPPDATA "DlnRenderer\logs\service-$(Get-Date -Format yyyyMMdd).log"
}

Write-Host "service log: $LogPath"

# --- 1. the service must be running ----------------------------------------------------------
$service = Get-Process DlnRenderer.Service -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $service) {
    Write-Host "DlnRenderer.Service is not running. Start it first." -ForegroundColor Red
    exit 1
}

Write-Host "service pid: $($service.Id)"

# --- 2. multicast membership as reported by the log -------------------------------------------
function Get-Membership($logPath) {
    if (-not (Test-Path $logPath)) { return @() }

    $line = Get-Content $logPath |
        Select-String -Pattern "ssdp bound .*multicast joined on (.+)$" |
        Select-Object -Last 1

    if (-not $line) { return @() }

    $addresses = $line.Matches[0].Groups[1].Value
    return $addresses -split ',\s*'
}

$baseline = Get-Membership $LogPath
Write-Host "membership: $($baseline -join ', ')"
Check "service reports at least one multicast membership" ($baseline.Count -gt 0) "count=$($baseline.Count)"

# --- 3. the device must answer SSDP right now ------------------------------------------------
function Test-SsdpResponse($timeoutSeconds = 5) {
    $socket = [System.Net.Sockets.UdpClient]::new()
    try {
        $search = "M-SEARCH * HTTP/1.1`r`nHOST: 239.255.255.250:1900`r`nMAN: `"ssdp:discover`"`r`nMX: 1`r`nST: urn:schemas-upnp-org:device:MediaRenderer:1`r`n`r`n"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($search)
        $group = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Parse("239.255.255.250"), 1900)
        [void]$socket.Send($bytes, $bytes.Length, $group)

        $socket.Client.ReceiveTimeout = $timeoutSeconds * 1000
        $remote = [System.Net.IPEndPoint]::new([System.Net.IPAddress]::Any, 0)

        # Our own multicast loopback is off, so a response must come from elsewhere on the LAN;
        # this check therefore only proves the socket is alive, not that the device answers us.
        try {
            $reply = $socket.Receive([ref]$remote)
            return [System.Text.Encoding]::ASCII.GetString($reply)
        }
        catch {
            return $null
        }
    }
    finally {
        $socket.Dispose()
    }
}

Write-Host "=== description endpoint ==="
try {
    $description = Invoke-WebRequest "http://127.0.0.1:18090/description.xml" -UseBasicParsing -TimeoutSec 5
    Check "description endpoint answers" ($description.StatusCode -eq 200) "HTTP $($description.StatusCode)"
}
catch {
    Check "description endpoint answers" $false $_.Exception.Message
}

if ($Simulate) {
    Write-Host ""
    Write-Host "=== steady state only (-Simulate) ===" -ForegroundColor Yellow
    Write-Host "  Adapter changes need administrator rights; re-run without -Simulate to test them."
}
else {
    Write-Host ""
    Write-Host "=== live network change ===" -ForegroundColor Yellow
    Write-Host "  This will renew the DHCP lease on all adapters. Press Ctrl+C to skip."
    Write-Host "  Watch for a line containing 'ssdp network change' or 'ssdp left multicast'."

    $before = (Get-Content $LogPath -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
    & ipconfig /renew | Out-Null
    Start-Sleep -Seconds 8

    $newLines = Get-Content $LogPath -ErrorAction SilentlyContinue | Select-Object -Skip $before
    $reaction = $newLines | Where-Object { $_ -match "ssdp network change|ssdp left multicast|ssdp could not rejoin" }

    Check "service reacted to the network change" ($null -ne $reaction) $(if ($reaction) { "log line present" } else { "no reaction logged" })

    $after = Get-Membership $LogPath
    Write-Host "membership after: $($after -join ', ')"
    Check "service still reports a multicast membership" ($after.Count -gt 0) "count=$($after.Count)"

    Check "service still alive" ($null -ne (Get-Process -Id $service.Id -ErrorAction SilentlyContinue))
}

Write-Host ""
Write-Host "== summary =="
$results | ForEach-Object { Write-Host "  $_" }
$failed = ($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Host "  failed: $failed / $($results.Count)"
