<#
.SYNOPSIS
    Copies a verified local libmpv-2.dll into the player project for development.

.DESCRIPTION
    libmpv is NOT vendored in this repository. The binaries available locally are
    GPL-2.0-or-later builds (see docs/architecture.md), so they must not be redistributed
    until the project licensing decision is made.

    This script copies the DLL and records its hash so the exact binary used during
    development is traceable.

.PARAMETER Source
    Path to an existing libmpv-2.dll. Defaults to the Scoop mpv-dev package.

.PARAMETER ExpectedSha256
    Expected hash. Defaults to the zhongfly build verified on 2026-10-20.

.PARAMETER SkipHashCheck
    Copies the DLL without verifying the hash. The build is recorded as unverified.

.EXAMPLE
    pwsh scripts/fetch-libmpv.ps1
#>
[CmdletBinding()]
param(
    [string]$Source = "$env:USERPROFILE\AppData\Local\Programs\Scoop\apps\mpv-dev\current\libmpv-2.dll",
    [string]$ExpectedSha256 = "53513b8f7cbae3700bdb4f7af1933cbdc512fcafcd88d38c594087f9d4fd7881",
    [switch]$SkipHashCheck
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Source)) {
    throw "libmpv-2.dll not found at '$Source'. Install a libmpv build and pass -Source <path>."
}

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
