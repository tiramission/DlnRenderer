<#
.SYNOPSIS
  Builds and signs the DlnRenderer MSIX package (stage E, MSIX channel).

.DESCRIPTION
  MSIX is a formal delivery target alongside the portable build. Both channels share the same
  App/Service/libmpv output: this script only adds packaging and signing on top, so there is no
  second implementation to keep in sync.

  Steps:
    1. Generates a development certificate whose Publisher matches the manifest (winapp cert
       generate), unless -Cert points at an existing one. A real CA certificate is the
       production path; see docs/msix-assessment.md §D.
    2. Publishes the Service and the App into one staging folder and copies libmpv and the
       app's loose Assets in, so the package carries both executables.
    3. Packages that folder with winapp package and signs it.
    4. Verifies the result: the package exists, is signed, and the manifest inside is per-user
       (no SCM service) with the Windows App Runtime dependency, runFullTrust and the startup task.

  The package is unsigned-installable only after 'winapp cert install', which needs admin once
  per certificate. That is MSIX's security model, not a defect.

.PARAMETER Configuration
  Build configuration for the published output.

.PARAMETER Cert
  Path to an existing .pfx. When omitted, a development certificate is generated.

.PARAMETER CertPassword
  Password for the certificate (winapp's default is 'password').

.PARAMETER Output
  Directory for the .msix. Defaults to dist/ (gitignored).

.PARAMETER ForceCert
  Regenerate the development certificate even if one already exists. Off by default so repeat
  builds keep the same signature identity (MSIX upgrades need an identical Publisher).

.EXAMPLE
  pwsh tools/package/build-msix.ps1
  pwsh tools/package/build-msix.ps1 -Cert ./codesign.pfx   # production certificate

.NOTES
  Run from PowerShell, not Git Bash: makeappx.exe (used by the verification step) rejects
  '/p' because Git Bash rewrites the leading slash into 'P:/'.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Cert = '',
    [string]$CertPassword = 'password',
    [string]$Arch = 'x64',
    [string]$Output = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist\msix'),
    [switch]$ForceCert
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$here = $PSScriptRoot
$appProject = Join-Path $repoRoot 'src\DlnRenderer.App\DlnRenderer.csproj'

function Write-Step([string]$message) { Write-Host "==> $message" -ForegroundColor Cyan }

# --- 0. Preconditions ---------------------------------------------------------------
foreach ($tool in 'winapp', 'dotnet') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool is not on PATH."
    }
}

$libmpv = Join-Path $repoRoot 'src\DlnRenderer.Player\native\x64\libmpv-2.dll'
if (-not (Test-Path -LiteralPath $libmpv)) {
    throw @"
libmpv-2.dll is missing. It is not vendored (GPL-2.0-or-later, docs/licenses.md).
Copy a verified build with scripts/fetch-libmpv.ps1 first.
"@
}

$manifest = Join-Path $repoRoot 'src\DlnRenderer.App\Package.appxmanifest'
if (-not (Test-Path -LiteralPath $manifest)) { throw "missing manifest: $manifest" }

# --- 1. Certificate -----------------------------------------------------------------
# The Publisher in the manifest and the certificate subject must match, or signtool refuses
# the package. winapp infers the Publisher from the manifest when --manifest is given.
#
# The development certificate is reused when it already exists: regenerating it would change the
# signature identity and force a reinstall of every package built earlier (MSIX upgrades require
# the same Publisher + Name). Pass -ForceCert to replace it deliberately.
$certPath = $Cert
if ([string]::IsNullOrWhiteSpace($certPath)) {
    $certPath = Join-Path $here 'devcert.pfx'
    if ((Test-Path -LiteralPath $certPath) -and -not $ForceCert) {
        Write-Step "reusing the existing development certificate $certPath"
    }
    else {
        if (Test-Path -LiteralPath $certPath) { Remove-Item -LiteralPath $certPath -Force }
        Write-Step "generating a development certificate (Publisher from the manifest)"
        & winapp cert generate --manifest $manifest --output $certPath --password $CertPassword
        if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "cert generate failed" }
        if (-not (Test-Path -LiteralPath $certPath)) { throw "no certificate produced at $certPath" }
        Write-Host "  certificate generated; trust it once per machine with: winapp cert install $certPath" -ForegroundColor Green
    }
}
else {
    Write-Step "using existing certificate $certPath"
    & winapp cert info $certPath
}

# --- 2. Publish ---------------------------------------------------------------------
# -p:PublishTrimmed=false: the App project trims in non-Debug configurations, and trimming
# requires a self-contained publish (NETSDK1102).
#
# Both executables are published separately and then staged together. The App project copies the
# service exe into its build output but NOT into its publish output (the WinUI packaging pipeline
# computes its own file list), so 'winapp package' would otherwise receive a folder without
# DlnRenderer.Service.exe - the package would install and then have no service binary to launch.
# Staging explicitly, then asserting the files are inside the .msix in step 5, is what makes that
# failure impossible to ship.
$publishDir = Join-Path $repoRoot "artifacts\msix\publish-$Arch"
$serviceProject = Join-Path $repoRoot 'src\DlnRenderer.Service\DlnRenderer.Service.csproj'

Write-Step "publishing DlnRenderer.Service ($Configuration, win-$Arch)"
& dotnet publish $serviceProject `
    -c $Configuration -a $Arch --self-contained false `
    -o $publishDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "service publish failed" }

Write-Step "publishing DlnRenderer.App ($Configuration, win-$Arch)"
& dotnet publish $appProject `
    -c $Configuration -a $Arch --self-contained false -p:PublishTrimmed=false `
    -o $publishDir --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

# libmpv is not vendored (GPL-2.0-or-later, docs/licenses.md), so it is copied in by path like
# build-dist.ps1 does. The service is the only process that loads it.
Copy-Item -LiteralPath $libmpv -Destination (Join-Path $publishDir 'libmpv-2.dll') -Force

# The app's Assets must be loose files as well as PRI resources: the window/title-bar icon is
# resolved from disk and the tray icon loads it straight out of AppContext.BaseDirectory, so a
# package without the .ico installs fine and then shows a stock icon.
$assetsSource = Join-Path $repoRoot 'src\DlnRenderer.App\Assets'
if (Test-Path -LiteralPath $assetsSource) {
    $assetsTarget = Join-Path $publishDir 'Assets'
    if (Test-Path -LiteralPath $assetsTarget) {
        # -Recurse is required: Assets is a directory, and -LiteralPath without -Recurse throws
        # "Object reference not set to an instance of an object" on one.
        Remove-Item -LiteralPath $assetsTarget -Recurse -Force
    }
    Copy-Item -Path $assetsSource -Destination $assetsTarget -Recurse -Force
}

# Legal files belong inside the package too: a user who installs the MSIX and never sees the
# repository still has to receive the GPL text and the third-party notices.
Write-Step "staging license and third-party notices"
& (Join-Path $here 'stage-legal-files.ps1') -Destination $publishDir

# --- 3. Verify the package layout ---------------------------------------------------
Write-Step "verifying package layout"
# The publish folder is exactly what ends up in the package, so this is the last cheap place to
# catch a missing file. DlnRenderer.Service.exe and libmpv-2.dll are the two that cannot be
# rebuilt by hand; the assets are the ones that fail silently (a packaged app whose install
# folder lacks the .ico falls back to a stock icon and the tray icon becomes generic).
$required = @(
    'DlnRenderer.exe',
    'DlnRenderer.Service.exe',
    'libmpv-2.dll',
    'resources.pri',
    'Assets\AppIcon.ico',
    'Assets\Square150x150Logo.scale-200.png',
    'Assets\Square44x44Logo.scale-200.png',
    'Assets\Wide310x150Logo.scale-200.png',
    'Assets\SplashScreen.scale-200.png',
    'Assets\StoreLogo.png',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'LICENSES\GPL-2.0-or-later.txt'
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishDir $_)) }
if ($missing) {
    throw "package layout incomplete, missing: $($missing -join ', ')"
}
Write-Host "  layout ok: both executables, libmpv, PRI and all tile/window/tray assets are present" -ForegroundColor Green

# --- 4. Package and sign ------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$packagePath = Join-Path $Output "DlnRenderer-$Arch.msix"

Write-Step "packaging and signing -> $packagePath"
& winapp package $publishDir `
    --manifest $manifest `
    --cert $certPath --cert-password $CertPassword `
    --output $packagePath
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw "winapp package failed" }

if (-not (Test-Path -LiteralPath $packagePath)) { throw "no package produced at $packagePath" }

# --- 5. Verify what we claim --------------------------------------------------------
Write-Step "verifying the package"
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
Write-Host "  signature: $($signature.Status) ($($signature.SignerCertificate.Subject))" -ForegroundColor Green

# 'Valid' means signed AND the signing chain is already trusted on this machine. A development
# certificate is never in the root store, so a perfectly signed package reports 'UnknownError'
# with "chain terminated in an untrusted root" - and treating that as a failure would make every
# release build red. What has to hold is that the package IS signed by the certificate we just
# used, because that is the certificate shipped next to it and the one the user trusts. Anything
# that says the file is unsigned, corrupt or signed by somebody else is a real failure.
$certThumbprint = if ($certPath -and (Test-Path -LiteralPath $certPath)) {
    $plain = ConvertTo-SecureString $CertPassword -AsPlainText -Force
    ([System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certPath, $plain)).Thumbprint
}

$signedByOurCert = $signature.SignerCertificate -and
    $certThumbprint -and
    ($signature.SignerCertificate.Thumbprint -eq $certThumbprint)
$signaturePresent = $signature.Status -in @('Valid', 'UnknownError', 'UntrustedRoot')

if (-not $signaturePresent) {
    throw "the package has no usable signature ($($signature.Status): $($signature.StatusMessage))."
}
if ($certThumbprint -and -not $signedByOurCert) {
    throw "the package is signed by $($signature.SignerCertificate.Thumbprint), not by the certificate in use ($certThumbprint)."
}

Write-Host "  signer thumbprint: $($signature.SignerCertificate.Thumbprint)" -ForegroundColor Green
if ($signature.Status -ne 'Valid') {
    Write-Host "  not trusted on this machine yet (expected for a development certificate):" -ForegroundColor Yellow
    Write-Host "    $($signature.StatusMessage)" -ForegroundColor Yellow
    Write-Host "  installing needs the certificate trusted once, as administrator:" -ForegroundColor Yellow
    Write-Host "    Import-Certificate -FilePath <the .cer we publish> -CertStoreLocation Cert:\LocalMachine\Root" -ForegroundColor Yellow
}

# What the manifest must NOT contain matters as much as what it must: a windows.service
# extension would register the renderer under the SCM in session 0, where libmpv cannot create a
# swapchain and a cast shows no video. The package is per-user on purpose; the UI starts the
# service in the user's own session.
$extracted = Join-Path $repoRoot "artifacts\msix\verify-$Arch"
if (Test-Path -LiteralPath $extracted) { Remove-Item -LiteralPath $extracted -Recurse -Force }
# Locate makeappx without assuming an SDK install path. A CI runner may carry a different set of
# SDK versions than a workstation, and winapp already knows how to fetch/launch Build Tools - so
# ask it first and fall back to the Kits directory. The verification below is the only guard
# against shipping a package with a missing executable, so it must not be skipped just because the
# path differs from the machine it was written on.
function Resolve-MakeAppx {
    $onPath = Get-Command 'makeappx' -ErrorAction SilentlyContinue
    if ($onPath) { return @{ Kind = 'exe'; Path = $onPath.Source } }

    $kits = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Filter 'makeappx.exe' -Recurse `
        -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($kits) { return @{ Kind = 'exe'; Path = $kits.FullName } }

    # winapp tool can provision and run the SDK tool itself; probe it before giving up. Judge by
    # the banner, not the exit code: makeappx /? exits 1 even when it works.
    $probe = & winapp tool -- makeappx /? 2>&1 | Out-String
    if ($probe -match 'MakeAppx') { return @{ Kind = 'winapp' } }

    return $null
}

$makeappx = Resolve-MakeAppx
if ($makeappx) {
    # 'unpack' is the current subcommand name; 'extract' was the older one. Run from PowerShell,
    # not Git Bash: the leading slash of '/p' is rewritten to 'P:/' there and makeappx fails with
    # "Unknown command line option".
    if ($makeappx.Kind -eq 'exe') {
        & $makeappx.Path unpack /p $packagePath /d $extracted /o | Out-Host
    }
    else {
        & winapp tool -- makeappx unpack /p $packagePath /d $extracted /o | Out-Host
    }
    # unpack reports real failures with a non-zero exit, unlike /?, so this one is checked.
    if ($LASTEXITCODE -ne 0) { throw "makeappx unpack failed ($LASTEXITCODE)" }
    if (-not (Test-Path -LiteralPath (Join-Path $extracted 'AppxManifest.xml'))) {
        throw "makeappx could not unpack $packagePath into $extracted; cannot verify the package contents."
    }
    $packagedManifest = Join-Path $extracted 'AppxManifest.xml'
    if (Test-Path -LiteralPath $packagedManifest) {
        $text = Get-Content -LiteralPath $packagedManifest -Raw
        # Strip XML comments first: the manifest explains in prose why it has no windows.service
        # extension, and matching that sentence would report a service that is not there.
        $text = [regex]::Replace($text, '<!--.*?-->', '', 'Singleline')
        $hasService = $text -match 'Category="windows\.service"'
        $hasStartupTask = $text -match 'windows\.startupTask'
        $hasRuntimeDep = $text -match 'Microsoft\.WindowsAppRuntime\.2'
        $hasFullTrustCap = $text -match 'runFullTrust'
        $hasPackagedServices = $text -match 'packagedServices'
        $hasLocalSystem = $text -match 'localSystemServices'
        Write-Host "  packaged manifest: startupTask=$hasStartupTask runtimeDependency=$hasRuntimeDep runFullTrust=$hasFullTrustCap service=$hasService packagedServices=$hasPackagedServices localSystemServices=$hasLocalSystem" -ForegroundColor Green
        if ($hasService -or $hasPackagedServices -or $hasLocalSystem) {
            throw "the packaged manifest declares an SCM service (or its LocalSystem capabilities). That runs in session 0 and cannot show video; this package must stay per-user."
        }
        if (-not $hasRuntimeDep) {
            throw "the packaged manifest has no Microsoft.WindowsAppRuntime dependency; the UI would crash at startup with REGDB_E_CLASSNOTREG."
        }
        if (-not $hasFullTrustCap) {
            throw "the packaged manifest lost the runFullTrust capability; the UI could not start the service process."
        }
        if (-not $hasStartupTask) {
            throw "the packaged manifest lost the startupTask extension; 'start with Windows' would have nothing to enable."
        }
    }
    else {
        throw "could not read the packaged manifest to verify the service extension."
    }

    # Files, not just declarations: the app loads the icon and libmpv from its install folder at
    # runtime, and a package that silently dropped them would install fine and then misbehave.
    $inPackage = @('DlnRenderer.exe', 'DlnRenderer.Service.exe', 'libmpv-2.dll', 'Assets\AppIcon.ico')
    $missingInPackage = $inPackage | Where-Object { -not (Test-Path -LiteralPath (Join-Path $extracted $_)) }
    if ($missingInPackage) {
        throw "the package is missing files the app loads at runtime: $($missingInPackage -join ', ')"
    }
    Write-Host "  package contents: ui, service, libmpv and app icon all present" -ForegroundColor Green
}
else {
    # Failing here rather than warning: a package nobody unpacked is a package nobody verified, and
    # the checks below (no SCM service, runtime dependency, both executables inside) are exactly the
    # ones whose absence ships a broken release.
    throw "makeappx is unavailable (not on PATH, not under Windows Kits, not via 'winapp tool'); cannot verify the package."
}

$size = [math]::Round((Get-Item -LiteralPath $packagePath).Length / 1MB, 1)
Write-Host ""
Write-Host "MSIX ready: $packagePath ($size MB)" -ForegroundColor Green
Write-Host ""
Write-Host "install (needs the certificate trusted once, admin):" -ForegroundColor Yellow
Write-Host "  winapp cert install $certPath"
Write-Host "  Add-AppxPackage -Path '$packagePath'"
