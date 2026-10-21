<#
.SYNOPSIS
    Fetches a verified libmpv-2.dll into the player project for development or CI.

.DESCRIPTION
    libmpv is NOT vendored in this repository. It is GPL-2.0-or-later, and the project is
    GPL-2.0-or-later too (docs/architecture.md §17): the obligation is to tell recipients what
    they got, which THIRD-PARTY-NOTICES.md does, not to put the binary in git.

    By default the script copies an existing local DLL (Scoop's mpv-dev package) and records
    its hash. With -Download it fetches the exact build that was reviewed, from the upstream
    release archive, so a clean machine - CI included - can run the player-backed tests for
    real instead of skipping them.

    Two hashes gate the result: the 7z archive's, checked right after download, and the DLL's,
    checked before it is copied. Both must match the pinned values; the DLL hash is the one
    recorded in docs/licenses.md and the one every distribution carries.

.PARAMETER Source
    Path to an existing libmpv-2.dll. Defaults to the Scoop mpv-dev package.

.PARAMETER Download
    Download the pinned upstream release archive and extract the DLL from it, instead of
    requiring a local copy. Needs 7z on PATH (GitHub's windows runners ship it).

.PARAMETER ExpectedSha256
    Expected DLL hash. Defaults to the zhongfly build verified on 2026-10-20.

.PARAMETER ArchiveUrl
    Release archive to download with -Download. Defaults to the pinned 20261019 build.

.PARAMETER ExpectedArchiveSha256
    Expected archive hash for -Download.

.PARAMETER SkipHashCheck
    Copies the DLL without verifying the hash. The build is recorded as unverified.

.EXAMPLE
    pwsh scripts/fetch-libmpv.ps1
    pwsh scripts/fetch-libmpv.ps1 -Download
#>
[CmdletBinding()]
param(
    [string]$Source = "$env:USERPROFILE\AppData\Local\Programs\Scoop\apps\mpv-dev\current\libmpv-2.dll",
    [string]$ExpectedSha256 = "53513b8f7cbae3700bdb4f7af1933cbdc512fcafcd88d38c594087f9d4fd7881",
    [switch]$Download,
    [string]$ArchiveUrl = "https://github.com/zhongfly/mpv-winbuild/releases/download/2026-10-19-6a262d11f6/mpv-dev-x86_64-20261019-git-6a262d11f6.7z",
    [string]$ExpectedArchiveSha256 = "2c71ab5cc5efd4bb055c6d21d7978ac0af9cad313c7673ab8acb362382d27dd1",
    [switch]$SkipHashCheck
)

$ErrorActionPreference = "Stop"

# Defined before the -Download block uses it: PowerShell runs top-down, so a function that is
# declared later is not callable from here.
function Get-Sha256([string]$Path) {
    # Get-FileHash is not available in every PowerShell host, so hash through .NET directly.
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

if ($Download) {
    $sevenZip = Get-Command 7z, 7za -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $sevenZip) {
        throw "7z is required to extract the archive. Install it (scoop install 7zip) or pass -Source <path> instead."
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ("libmpv-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $work | Out-Null
    try {
        $archive = Join-Path $work "libmpv.7z"
        Write-Host "downloading $ArchiveUrl"
        # ProgressPreference costs most of the download time in a non-interactive host.
        $previousProgress = $ProgressPreference
        $ProgressPreference = "SilentlyContinue"
        try {
            Invoke-WebRequest -Uri $ArchiveUrl -OutFile $archive -MaximumRedirection 10
        }
        finally {
            $ProgressPreference = $previousProgress
        }

        $archiveHash = (Get-Sha256 $archive).ToLowerInvariant()
        if ($archiveHash -ne $ExpectedArchiveSha256.ToLowerInvariant()) {
            throw @"
Downloaded archive hash mismatch.
  expected: $ExpectedArchiveSha256
  actual:   $archiveHash
The release asset changed or the download was tampered with. Re-review the build before using it.
"@
        }
        Write-Host "archive sha256 verified: $archiveHash"

        & $sevenZip.Source x -y -o"$work" "$archive" libmpv-2.dll | Out-Null
        $extracted = Join-Path $work "libmpv-2.dll"
        if (-not (Test-Path -LiteralPath $extracted)) {
            throw "the archive did not contain libmpv-2.dll"
        }
        $Source = $extracted
    }
    catch {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
        throw
    }
}

if (-not (Test-Path -LiteralPath $Source)) {
    throw "libmpv-2.dll not found at '$Source'. Install a libmpv build and pass -Source <path>, or use -Download."
}

$hash = Get-Sha256 $Source
$verified = $hash -eq $ExpectedSha256.ToLowerInvariant()

if (-not $verified) {
    if (-not $SkipHashCheck) {
        throw @"
Hash mismatch for '$Source'.
  expected: $ExpectedSha256
  actual:   $hash
This is not the build that was reviewed for phase 1.5. If you intentionally switched
builds, re-check its license (mpv-configuration must contain -Dgpl=false for LGPL) and
pass -SkipHashCheck, then update the default hash and docs/architecture.md.
"@
    }

    Write-Warning "Hash mismatch ignored; this binary is NOT a reviewed build."
}

$targetDir = Join-Path $PSScriptRoot "..\src\DlnRenderer.Player\native\x64"
$targetDir = [IO.Path]::GetFullPath($targetDir)
New-Item -ItemType Directory -Force -Path $targetDir | Out-Null

$target = Join-Path $targetDir "libmpv-2.dll"
Copy-Item -LiteralPath $Source -Destination $target -Force

$info = [ordered]@{
    source         = $Source
    sha256         = $hash
    verified       = $verified
    sizeBytes      = (Get-Item -LiteralPath $target).Length
    copiedAtUtc    = (Get-Date).ToUniversalTime().ToString("o")
    licenseWarning = "GPL-2.0-or-later unless the build was configured with -Dgpl=false (LGPL). Development use only."
}

$info | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $targetDir "libmpv.provenance.json") -Encoding UTF8

Write-Host "Copied libmpv to $target"
Write-Host "sha256: $hash (verified: $verified)"
