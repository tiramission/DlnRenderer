<#
.SYNOPSIS
  Builds a distribution twice into two different folders and asserts the output is identical.

.DESCRIPTION
  A release whose bytes change on every build cannot be audited, cannot be matched to a
  reproducible-source claim, and cannot be checked by a user who wants to verify what they
  downloaded. This script builds the same git state twice and compares every file hash, so a
  nondeterministic build (a timestamp, a GUID, a machine path baked into an assembly) fails
  loudly at build time instead of being discovered after publishing.

  It does not require libmpv to be present, but the run that produces a distributable folder
  does. libmpv is copied, not rebuilt, so its hash is identical in both folders by construction.

  Run with pwsh 7.

.PARAMETER First
  Output folder for the first build. Defaults to dist/repro/A.

.PARAMETER Second
  Output folder for the second build. Defaults to dist/repro/B.

.PARAMETER Keep
  Keep both folders after a successful comparison. Off by default to reclaim ~200 MB.

.EXAMPLE
  pwsh tools/package/verify-reproducible.ps1
#>
[CmdletBinding()]
param(
    [string]$First = '',
    [string]$Second = '',
    [switch]$Keep
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

if (-not $First) { $First = Join-Path $repoRoot 'dist\repro\A' }
if (-not $Second) { $Second = Join-Path $repoRoot 'dist\repro\B' }

if ($First -eq $Second) { throw 'The two output folders must differ.' }

function Get-FileMap([string]$root) {
    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
        # SHA256SUMS.txt / release-manifest.json embed their own build time and path, so they
        # legitimately differ between runs and are excluded from the comparison.
        if ($relative -in @('SHA256SUMS.txt', 'release-manifest.json')) { continue }
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    return $map
}

Write-Host "==> build 1 -> $First" -ForegroundColor Cyan
if (Test-Path -LiteralPath $First) { Remove-Item -LiteralPath $First -Recurse -Force }
& (Join-Path $PSScriptRoot 'build-dist.ps1') -OutputDirectory $First
if ($LASTEXITCODE -ne 0) { throw "build 1 failed" }

Write-Host "==> build 2 -> $Second" -ForegroundColor Cyan
if (Test-Path -LiteralPath $Second) { Remove-Item -LiteralPath $Second -Recurse -Force }
& (Join-Path $PSScriptRoot 'build-dist.ps1') -OutputDirectory $Second
if ($LASTEXITCODE -ne 0) { throw "build 2 failed" }

$mapA = Get-FileMap $First
$mapB = Get-FileMap $Second

$differences = @()
foreach ($name in $mapA.Keys) {
    if (-not $mapB.ContainsKey($name)) { $differences += "only in build 1: $name" }
    elseif ($mapA[$name] -ne $mapB[$name]) { $differences += "differs: $name" }
}
foreach ($name in $mapB.Keys) {
    if (-not $mapA.ContainsKey($name)) { $differences += "only in build 2: $name" }
}

if ($differences) {
    $differences | Sort-Object | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw "build is NOT reproducible: $($differences.Count) file(s) differ."
}

if (-not $Keep) {
    Remove-Item -LiteralPath (Split-Path -Parent $First) -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "reproducible: $($mapA.Count) files are byte-identical across two builds" -ForegroundColor Green
