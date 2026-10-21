<#
.SYNOPSIS
  Builds the distributable DlnRenderer folder (stage E, E3).

.DESCRIPTION
  Produces a self-contained folder with both executables plus libmpv, so the app can be
  copied or zipped without an installer. See docs/packaging.md for why this is the
  primary distribution form: the service must outlive the UI, and an unpackaged
  process is the model that was actually tested.

  What it does:
    1. Publishes DlnRenderer.App and DlnRenderer.Service (framework-dependent, x64).
    2. Copies libmpv-2.dll next to the service (the only component that loads it).
    3. Verifies the result: both exes present, libmpv present, and no test binaries.

  The output is NOT committed (dist/ is gitignored).

.EXAMPLE
  pwsh tools/package/build-dist.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist\DlnRenderer'),
    # libmpv is not vendored (GPL, see docs/licenses.md). Point this at a local build.
    [string]$LibmpvPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'src\DlnRenderer.Player\native\x64\libmpv-2.dll')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

# 1. Publish both executables.
# -p:PublishTrimmed=false: the App project trims for non-Debug configurations, but trimming
# requires a self-contained publish (NETSDK1102). A framework-dependent build is the right
# default here because the .NET runtime is a prerequisite anyway (see docs/packaging.md).
Write-Step "publishing DlnRenderer.App ($Configuration, win-x64)"
& dotnet publish (Join-Path $repoRoot 'src\DlnRenderer.App\DlnRenderer.csproj') `
    -c $Configuration -r win-x64 --self-contained false -p:PublishTrimmed=false `
    -o $OutputDirectory --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "App publish failed ($LASTEXITCODE)" }

Write-Step "publishing DlnRenderer.Service ($Configuration, win-x64)"
& dotnet publish (Join-Path $repoRoot 'src\DlnRenderer.Service\DlnRenderer.Service.csproj') `
    -c $Configuration -r win-x64 --self-contained false `
    -o $OutputDirectory --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Service publish failed ($LASTEXITCODE)" }

# 2. libmpv lives next to the service, which is the only process that loads it.
if (-not (Test-Path -LiteralPath $LibmpvPath)) {
    throw @"
libmpv-2.dll not found at '$LibmpvPath'.
libmpv is not vendored (GPL-2.0-or-later). Copy a verified build there with
scripts/fetch-libmpv.ps1, or pass -LibmpvPath <path>.
"@
}
Write-Step "copying libmpv-2.dll"
Copy-Item -LiteralPath $LibmpvPath -Destination (Join-Path $OutputDirectory 'libmpv-2.dll') -Force

# 3. The App icons are Content items: the XAML title-bar icon and the tray icon load them from
#    disk at runtime, so an unpackaged build must carry the Assets folder. The publish output
#    keeps them only when the project is built with the MSIX layout, hence the explicit copy.
$assetsSource = Join-Path $repoRoot 'src\DlnRenderer.App\Assets'
if (Test-Path -LiteralPath $assetsSource) {
    $assetsTarget = Join-Path $OutputDirectory 'Assets'
    if (Test-Path -LiteralPath $assetsTarget) {
        # A previous run can leave 'Assets' as a file (the app's embedded resource index),
        # which would make the recursive copy fail.
        Remove-Item -LiteralPath $assetsTarget -Force
    }
    Write-Step "copying app assets (window and tray icons)"
    Copy-Item -Path $assetsSource -Destination $assetsTarget -Recurse -Force
}
# 4. Verify what we claim to ship: a folder missing an exe, the native DLL or the icons is not a release.
Write-Step "verifying output"
$required = @(
    'DlnRenderer.exe',
    'DlnRenderer.Service.exe',
    'libmpv-2.dll',
    'Assets\AppIcon.ico',
    'Assets\Square44x44Logo.scale-200.png'
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $OutputDirectory $_)) }
if ($missing) { throw "incomplete distribution, missing: $($missing -join ', ')" }

$strayTests = Get-ChildItem -Path $OutputDirectory -Filter '*Test*' -File -ErrorAction SilentlyContinue
if ($strayTests) {
    Write-Warning "test artifacts in the output: $($strayTests.Name -join ', ')"
}

$size = [math]::Round(((Get-ChildItem -Path $OutputDirectory -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host ""
Write-Host "distribution ready: $OutputDirectory ($size MB)" -ForegroundColor Green
Write-Host "  run: DlnRenderer.exe (UI)  |  DlnRenderer.Service.exe (headless backend)" -ForegroundColor Green
