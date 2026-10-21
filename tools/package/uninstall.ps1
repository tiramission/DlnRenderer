<#
.SYNOPSIS
  Stops DlnRenderer and cleans what it left behind (stage E, E5).

.DESCRIPTION
  The failure this prevents: the UI closes, the service keeps playing, and the user is left
  with a process holding port 1900 and a firewall rule after "uninstalling". The rules
  this removes are exactly the ones tools/package/firewall-rules.ps1 adds.

  Order matters: the service is asked to shut down over IPC first (so it can release the
  pipe, the ports and the video window), then terminated if it is still alive. Only after the
  processes are gone are files deleted, otherwise Windows keeps the DLLs locked.

.PARAMETER KeepSettings
  Keeps %LOCALAPPDATA%\DlnRenderer (settings and logs). Useful for upgrades.

.PARAMETER Port
  TCP port of the service, used to find the matching firewall rule (default 18090).

.PARAMETER RemoveShortcuts
  Also removes the start-menu/desktop shortcuts and the HKCU startup entry that install.ps1
  created. Off by default so a repair reinstall keeps them.

.EXAMPLE
  pwsh tools/package/uninstall.ps1                 # full clean, keep nothing
  pwsh tools/package/uninstall.ps1 -KeepSettings   # upgrade-friendly
#>
[CmdletBinding()]
param(
    [switch]$KeepSettings,
    [int]$Port = 18090,
    [string]$LogDirectory = (Join-Path $env:LOCALAPPDATA 'DlnRenderer'),
    [string]$Destination = (Join-Path ${env:ProgramFiles} 'DlnRenderer'),
    [switch]$RemoveShortcuts
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

$serviceName = 'DlnRenderer.Service'
$stopped = $false
$here = $PSScriptRoot

# 1. Ask the service to exit through the pipe it already owns. It releases the named pipe,
#    the SSDP socket and the video window itself, so nothing is left half-initialised and a
#    fresh install can rebind port 1900 immediately.
Write-Step "requesting shutdown over IPC"
& (Join-Path $here 'stop-service.ps1')
if ($LASTEXITCODE -eq 0) { $stopped = $true }

# 2. Terminate whatever is left: the UI process and the service process.
Write-Step "stopping processes"
$processes = Get-Process -Name $serviceName, 'DlnRenderer' -ErrorAction SilentlyContinue
if ($processes) {
    $processes | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500

    $remaining = Get-Process -Name $serviceName, 'DlnRenderer' -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Warning "still running: $($remaining.Name -join ', ')"
    }
    else {
        $stopped = $true
        Write-Host "stopped $($processes.Name -join ', ')" -ForegroundColor Green
    }
}
elseif (-not $stopped) {
    Write-Host "no DlnRenderer process running" -ForegroundColor Yellow
}

# 3. Firewall rules (same names the install path adds).
#    $LASTEXITCODE is not reliable for a script invoked with the call operator (it stays $null on
#    success), so verify the rules are actually gone instead of trusting the exit code.
Write-Step "removing firewall rules"
try {
    & (Join-Path $here 'firewall-rules.ps1') -Action Remove -Port $Port
}
catch {
    # Unelevated uninstall is a legitimate path (a user without admin rights). Report it and
    # keep going: the rest of the cleanup must not depend on the firewall step.
    Write-Warning "could not remove firewall rules: $($_.Exception.Message)"
}

$ssdpRule = 'DlnRenderer SSDP (UDP 1900)'
$serviceRule = "DlnRenderer Service (TCP $Port)"
$leftover = Get-NetFirewallRule -DisplayName $ssdpRule, $serviceRule -ErrorAction SilentlyContinue
if ($leftover) {
    Write-Warning "firewall rules still present: $($leftover.DisplayName -join ', ')"
}
else {
    Write-Host "no DlnRenderer firewall rules remain" -ForegroundColor Green
}

# 4. Settings and logs.
if ($KeepSettings) {
    Write-Step "keeping $LogDirectory"
}
elseif (Test-Path -LiteralPath $LogDirectory) {
    Write-Step "removing $LogDirectory"
    Remove-Item -LiteralPath $LogDirectory -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $LogDirectory) {
        Write-Warning "could not fully remove $LogDirectory"
    }
    else {
        Write-Host "removed $LogDirectory" -ForegroundColor Green
    }
}
else {
    Write-Host "no data directory at $LogDirectory" -ForegroundColor Yellow
}

# 5. Startup entry and shortcuts (only what install.ps1 created).
if ($RemoveShortcuts) {
    Write-Step "removing shortcuts and startup entry"
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\DlnRenderer.lnk'
    $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'DlnRenderer.lnk'
    foreach ($link in $startMenu, $desktop) {
        if (Test-Path -LiteralPath $link) {
            Remove-Item -LiteralPath $link -Force
            Write-Host "removed $link" -ForegroundColor Green
        }
    }

    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' `
        -Name 'DlnRenderer' -ErrorAction SilentlyContinue
    Write-Host "removed startup entry" -ForegroundColor Green
}

# 6. The installed folder. Files are not locked once the processes are gone.
if (Test-Path -LiteralPath $Destination) {
    Write-Step "removing $Destination"
    Remove-Item -LiteralPath $Destination -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Destination) {
        Write-Warning "could not fully remove $Destination (files in use?)"
    }
    else {
        Write-Host "removed $Destination" -ForegroundColor Green
    }
}
else {
    Write-Host "no installed folder at $Destination" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "uninstall complete" -ForegroundColor Green
