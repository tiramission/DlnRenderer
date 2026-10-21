<#
.SYNOPSIS
  Writes or verifies the SHA-256 manifest for a release folder.

.DESCRIPTION
  A release nobody can check is a release nobody should install. This script records every file
  in a built distribution (portable folder or MSIX output) with its hash, plus the facts that make
  the build identifiable: product version, libmpv fingerprint, and the git commit it came from.

  Two files are written next to the artifacts:
    SHA256SUMS.txt         "<hash>  <relative path>" for every file, sorted, LF endings, so
                           sha256sum -c and Get-FileHash loops can both consume it.
    release-manifest.json  the same data plus metadata (version, commit, libmpv sha256).

  -Verify recomputes everything and exits 1 on any mismatch, missing or unexpected file, so it can
  be used in CI after a download.

.PARAMETER Path
  Release folder (default dist/DlnRenderer) or a single file such as dist/msix/*.msix.

.PARAMETER Verify
  Compare against the existing SHA256SUMS.txt instead of writing it.

.EXAMPLE
  pwsh tools/package/hash-release.ps1
  pwsh tools/package/hash-release.ps1 -Path dist/msix
  pwsh tools/package/hash-release.ps1 -Verify
#>
[CmdletBinding()]
param(
    [string]$Path = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'dist\DlnRenderer'),
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

function Get-Sha256([string]$file) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($file)
    try { return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant() }
    finally { $stream.Dispose(); $sha.Dispose() }
}

$target = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $target)) { throw "'$target' does not exist. Build the artifacts first." }

# Files that describe the hashes are never part of them, or a re-run could never match.
$manifestNames = @('SHA256SUMS.txt', 'release-manifest.json')
$root = if (Test-Path -LiteralPath $target -PathType Container) { $target } else { Split-Path -Parent $target }

$files = if (Test-Path -LiteralPath $target -PathType Container) {
    Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $manifestNames -notcontains $_.Name } |
        Sort-Object FullName
}
else {
    @(Get-Item -LiteralPath $target)
}

if ($files.Count -eq 0) { throw "'$target' contains no files." }

$entries = foreach ($file in $files) {
    $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/') -replace '\\', '/'
    [pscustomobject]@{ RelativePath = $relative; Sha256 = (Get-Sha256 $file.FullName); SizeBytes = $file.Length }
}
$entries = @($entries)

$sumsPath = Join-Path $root 'SHA256SUMS.txt'
$manifestPath = Join-Path $root 'release-manifest.json'

if ($Verify) {
    if (-not (Test-Path -LiteralPath $sumsPath)) { throw "'$sumsPath' is missing; run without -Verify first." }

    $expected = @{}
    foreach ($line in Get-Content -LiteralPath $sumsPath) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw "unreadable line in SHA256SUMS.txt: '$line'" }
        $expected[$Matches[2]] = $Matches[1]
    }

    $problems = @()
    foreach ($entry in $entries) {
        if (-not $expected.ContainsKey($entry.RelativePath)) { $problems += "unexpected: $($entry.RelativePath)" }
        elseif ($expected[$entry.RelativePath] -ne $entry.Sha256) { $problems += "changed:    $($entry.RelativePath)" }
    }
    foreach ($name in $expected.Keys) {
        if (-not ($entries | Where-Object RelativePath -eq $name)) { $problems += "missing:    $name" }
    }

    if ($problems) {
        $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        throw "$($problems.Count) hash problem(s) in '$root'."
    }

    Write-Host "verified $($entries.Count) files against SHA256SUMS.txt" -ForegroundColor Green
    return
}

$lines = $entries | ForEach-Object { "$($_.Sha256)  $($_.RelativePath)" }
# Join with LF explicitly: a CRLF file is painful to check on the Linux side of a release pipeline.
[IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

$version = '0.0.0'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (Test-Path -LiteralPath $propsPath) {
    $match = [regex]::Match((Get-Content -LiteralPath $propsPath -Raw), '<Version>([^<]+)</Version>')
    if ($match.Success) { $version = $match.Groups[1].Value }
}

$commit = ''
try { $commit = (& git -C $repoRoot rev-parse --short HEAD 2>$null) } catch { $commit = '' }

$libmpv = $entries | Where-Object RelativePath -eq 'libmpv-2.dll' | Select-Object -First 1
$manifest = [ordered]@{
    product        = 'DlnRenderer'
    version        = $version
    commit         = "$commit".Trim()
    generatedAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    sourcePath     = $target
    fileCount      = $entries.Count
    totalBytes     = ($entries | Measure-Object SizeBytes -Sum).Sum
    license        = 'GPL-2.0-or-later'
    libmpvSha256   = if ($libmpv) { $libmpv.Sha256 } else { $null }
    files          = $entries
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

$sizeMb = [math]::Round($manifest.totalBytes / 1MB, 1)
Write-Host "wrote SHA256SUMS.txt and release-manifest.json for $($entries.Count) files ($sizeMb MB)" -ForegroundColor Green
Write-Host "  version $version$(if ($commit) { " @ $commit" })"
if ($libmpv) { Write-Host "  libmpv sha256 $($libmpv.Sha256)" }
