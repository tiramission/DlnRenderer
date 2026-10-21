<#
.SYNOPSIS
  Installs the DlnRenderer portable distribution (stage E, E4/E6).

.DESCRIPTION
  Copies the folder produced by build-dist.ps1 into %ProgramFiles%\DlnRenderer, adds the two
  firewall rules the service needs, and offers the per-user "start when I sign in" Run entry.

  Why an install script and not MSIX: see docs/packaging.md. The short version is that the
  backend is a plain process that must outlive the UI, and a packaged WinUI app launching an
  unpackaged child is a combination this project has not verified end to end.

  Run from an elevated shell (the firewall and Program Files steps need it).

.PARAMETER Source
  Folder produced by build-dist.ps1.

.PARAMETER Destination
  Install location. Kept as %ProgramFiles%\DlnRenderer so uninstall can find it again.

.PARAMETER NoStartupEntry
  Skips the HKCU Run registration. The user can still start the app from the Start menu.

.EXAMPLE
  pwsh tools/package/build-dist.ps1
  pwsh tools/package/install.ps1        # as administrator
#>
[CmdletBinding()]
param(
    [string]$Source = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist\DlnRenderer'),
    [string]$Destination = (Join-Path ${env:ProgramFiles} 'DlnRenderer'),
    [int]$Port = 18090,
    [switch]$NoStartupEntry
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    throw 'Installing to Program Files and adding firewall rules requires an elevated shell.'
}

if (-not (Test-Path -LiteralPath (Join-Path $Source 'DlnRenderer.exe'))) {
    throw "no distribution at '$Source'. Run pwsh tools/package/build-dist.ps1 first."
}

# 1. Stop a running instance before replacing its files: Windows locks loaded DLLs, and a
#    half-replaced folder is worse than a late upgrade.
Write-Step "stopping any running instance"
& (Join-Path $here 'stop-service.ps1') | Out-Host

# 2. Copy the distribution.
Write-Step "installing to $Destination"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Copy-Item -Path (Join-Path $Source '*') -Destination $Destination -Recurse -Force

$exe = Join-Path $Destination 'DlnRenderer.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "install incomplete: $exe missing" }

# 3. Firewall: without these the first launch looks broken (nothing discoverable).
#    $LASTEXITCODE is not reliable for a script invoked with the call operator (it stays $null
#    when the script merely returns), so verify the rules actually exist instead.
Write-Step "adding firewall rules"
$serviceExe = Join-Path $Destination 'DlnRenderer.Service.exe'
& (Join-Path $here 'firewall-rules.ps1') -Action Add -Port $Port -ProgramPath $serviceExe

$ssdpRule = 'DlnRenderer SSDP (UDP 1900)'
$serviceRule = "DlnRenderer Service (TCP $Port)"
$present = Get-NetFirewallRule -DisplayName $ssdpRule, $serviceRule -ErrorAction SilentlyContinue
if (-not $present) {
    throw "firewall rules were not created; check that this shell is elevated."
}
Write-Host "verified: $($present.DisplayName -join ', ')" -ForegroundColor Green

# 4. Optional per-user startup. Same HKCU Run value the app's Settings page writes
#    (StartupRegistration.ValueName), so the toggle and the installer cannot disagree.
if (-not $NoStartupEntry) {
    Write-Step "registering per-user startup"
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    Set-ItemProperty -Path $runKey -Name 'DlnRenderer' -Value "`"$exe`"" -Type String
    Write-Host "  HKCU\...\Run\DlnRenderer -> $exe" -ForegroundColor Green
}

# 5. Shortcuts, so the app is reachable without knowing the install path.
Write-Step "creating shortcuts"
$shell = New-Object -ComObject WScript.Shell
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
New-Item -ItemType Directory -Force -Path $startMenu | Out-Null
$shortcut = $shell.CreateShortcut((Join-Path $startMenu 'DlnRenderer.lnk'))
$shortcut.TargetPath = $exe
$shortcut.WorkingDirectory = $Destination
$shortcut.Description = 'DLNA media renderer'
$shortcut.Save()

$desktop = [Environment]::GetFolderPath('Desktop')
$desktopShortcut = $shell.CreateShortcut((Join-Path $desktop 'DlnRenderer.lnk'))
$desktopShortcut.TargetPath = $exe
$desktopShortcut.WorkingDirectory = $Destination
$desktopShortcut.Description = 'DLNA media renderer'
$desktopShortcut.Save()

Write-Host ""
Write-Host "installed to $Destination" -ForegroundColor Green
Write-Host "  start menu and desktop shortcuts created" -ForegroundColor Green
if (-not $NoStartupEntry) {
    Write-Host "  starts automatically when you sign in (disable in the app's Settings page)" -ForegroundColor Green
}
Write-Host ""
Write-Host "first launch: allow DlnRenderer through the firewall if Windows asks." -ForegroundColor Yellow
