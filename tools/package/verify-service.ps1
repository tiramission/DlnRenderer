# Checks the per-user MSIX install end to end (stage E, MSIX channel).
#
# What it proves, in order:
#   1. the package is installed and its manifest is per-user (no SCM service registered);
#   2. the UI starts from the package and stays up (a missing Windows App Runtime dependency
#      makes it die at once with REGDB_E_CLASSNOTREG, with no window and no tray icon);
#   3. the UI started the renderer service, and that service runs in the user's own session -
#      session 0 has no desktop, so libmpv could not create a swapchain and a cast shows no video;
#   4. the control plane answers from the installed copy.
#
# Usage:
#   pwsh tools/package/verify-service.ps1            # launch the UI if needed, then check
#   pwsh tools/package/verify-service.ps1 -Stop      # stop everything afterwards
#
# This script cannot prove that a video window appears; that needs a real cast from a control
# point (docs/testing.md section 11).

[CmdletBinding()]
param(
    [switch]$Stop,
    [int]$WaitSeconds = 30,
    [int]$Port = 18090
)

$ErrorActionPreference = 'Continue'
$failures = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }
function Write-Ok([string]$message) { Write-Host "  OK   $message" -ForegroundColor Green }
function Write-Bad([string]$message) {
    Write-Host "  FAIL $message" -ForegroundColor Red
    $script:failures.Add($message)
}

# --- 1. Package -----------------------------------------------------------------------------
Write-Step 'package'
$package = Get-AppxPackage -Name 'DlnRenderer' -ErrorAction SilentlyContinue
if ($null -eq $package) {
    Write-Bad 'DlnRenderer is not installed as an MSIX package'
    Write-Host "MSIX verification: 1 check(s) failed" -ForegroundColor Red
    exit 1
}
Write-Ok "installed $($package.PackageFullName) at $($package.InstallLocation)"

# A leftover SCM registration from an earlier build would start a session-0 copy of the service
# that steals the HTTP port and never shows video.
$scm = Get-Service -Name 'DlnRenderer.Service' -ErrorAction SilentlyContinue
if ($null -ne $scm) { Write-Bad 'a DlnRenderer.Service SCM service is registered; the package must be per-user' }
else { Write-Ok 'no SCM service registered (per-user package)' }

$manifest = Get-Content -LiteralPath (Join-Path $package.InstallLocation 'AppxManifest.xml') -Raw
if ($manifest -match 'Microsoft\.WindowsAppRuntime\.2') { Write-Ok 'manifest declares the Windows App Runtime dependency' }
else { Write-Bad 'manifest has no Microsoft.WindowsAppRuntime dependency' }

# --- 2. UI starts and stays up --------------------------------------------------------------
Write-Step 'UI launch'
$launchedAt = Get-Date
$ui = Get-Process -Name 'DlnRenderer' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $ui) {
    $familyName = $package.PackageFamilyName
    Start-Process 'explorer.exe' -ArgumentList "shell:AppsFolder\$familyName!App"
    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    while ((Get-Date) -lt $deadline -and $null -eq $ui) {
        Start-Sleep -Seconds 1
        $ui = Get-Process -Name 'DlnRenderer' -ErrorAction SilentlyContinue | Select-Object -First 1
    }
}

if ($null -eq $ui) {
    Write-Bad "the UI process did not start within ${WaitSeconds}s"
}
else {
    # Give a startup crash time to happen before declaring success.
    Start-Sleep -Seconds 5
    $ui = Get-Process -Id $ui.Id -ErrorAction SilentlyContinue
    if ($null -eq $ui) {
        Write-Bad 'the UI process exited within 5 seconds of starting'
    }
    else {
        Write-Ok "UI running (pid $($ui.Id), session $($ui.SessionId), responding=$($ui.Responding))"
        if ($ui.MainWindowHandle -ne 0 -or $ui.Responding) { Write-Ok 'UI has a window handle or is responding' }
    }
}

$crashes = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000; StartTime = $launchedAt } -ErrorAction SilentlyContinue |
    Where-Object { $_.Message -match 'DlnRenderer' }
if ($crashes) { Write-Bad "the UI crashed $(@($crashes).Count) time(s) since launch (see the Application event log)" }
else { Write-Ok 'no DlnRenderer crash events since launch' }

# --- 3. Service in the user's session -------------------------------------------------------
Write-Step 'renderer service'
$service = $null
$deadline = (Get-Date).AddSeconds($WaitSeconds)
while ((Get-Date) -lt $deadline -and $null -eq $service) {
    $service = Get-Process -Name 'DlnRenderer.Service' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $service) { Start-Sleep -Seconds 1 }
}

if ($null -eq $service) {
    Write-Bad 'the UI did not start DlnRenderer.Service'
}
else {
    Write-Ok "service running (pid $($service.Id), session $($service.SessionId))"
    if ($service.SessionId -eq 0) {
        Write-Bad 'the service runs in session 0, which has no desktop: video cannot be shown'
    }
    else {
        Write-Ok 'service runs in an interactive user session (video window possible)'
    }
}

# --- 4. Control plane -----------------------------------------------------------------------
Write-Step 'discovery and control plane'
if ($null -ne $service) {
    $description = "http://127.0.0.1:$Port/description.xml"
    try {
        $response = Invoke-WebRequest -Uri $description -TimeoutSec 10 -ErrorAction Stop
        if ($response.StatusCode -eq 200 -and $response.Content -match 'AVTransport') {
            Write-Ok "GET $description -> 200, AVTransport service present"
        }
        else {
            Write-Bad "GET $description -> $($response.StatusCode) but no AVTransport service"
        }
    }
    catch {
        Write-Bad "GET $description failed: $($_.Exception.Message)"
    }
}
else {
    Write-Host '  skip service is not running'
}

if ($Stop) {
    Write-Step 'stopping'
    Get-Process -Name 'DlnRenderer.Service', 'DlnRenderer' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    $left = Get-Process -Name 'DlnRenderer.Service', 'DlnRenderer' -ErrorAction SilentlyContinue
    if ($left) { Write-Bad 'processes remain after stop' } else { Write-Ok 'no leftover processes' }
}

# --- Summary ---------------------------------------------------------------------------------
Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host 'MSIX verification: all checks passed' -ForegroundColor Green
    exit 0
}

Write-Host "MSIX verification: $($failures.Count) check(s) failed" -ForegroundColor Red
$failures | ForEach-Object { Write-Host "  - $_" }
exit 1
