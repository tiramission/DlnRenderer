<#
.SYNOPSIS
  Copies the license and third-party notice files into a distribution folder.

.DESCRIPTION
  GPL-2.0-or-later is a distribution license: the obligations travel with the artifacts, not with
  the git repository. A user who receives the portable folder or the MSIX package must get LICENSE,
  THIRD-PARTY-NOTICES.md and the license texts without having to find this repository, so both
  packaging scripts call this one.

  It is a separate script rather than a copy-pasted block so a new channel cannot ship with the
  legal files forgotten, and it fails loudly when a source file is missing: silently packaging a
  release without its license is worse than failing the build.

.PARAMETER Destination
  Distribution folder to write into (the publish output before packaging, or dist/DlnRenderer).

.EXAMPLE
  pwsh tools/package/stage-legal-files.ps1 -Destination dist/DlnRenderer
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)

$required = @('LICENSE', 'THIRD-PARTY-NOTICES.md')
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $repoRoot $_)) }
if ($missing) {
    throw "missing legal file(s) in the repository: $($missing -join ', '). Every distribution must carry them."
}

$licenseDir = Join-Path $repoRoot 'LICENSES'
if (-not (Test-Path -LiteralPath $licenseDir)) {
    throw "missing LICENSES directory: the notices reference license texts that are not present."
}

$target = [IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $target | Out-Null

foreach ($name in $required) {
    Copy-Item -LiteralPath (Join-Path $repoRoot $name) -Destination (Join-Path $target $name) -Force
}

# Replaced, not merged: a stale text from an older release would contradict THIRD-PARTY-NOTICES.md.
$licenseTarget = Join-Path $target 'LICENSES'
if (Test-Path -LiteralPath $licenseTarget) {
    Remove-Item -LiteralPath $licenseTarget -Recurse -Force
}
Copy-Item -Path $licenseDir -Destination $licenseTarget -Recurse -Force

$count = (Get-ChildItem -LiteralPath $licenseTarget -File).Count
Write-Host "staged LICENSE, THIRD-PARTY-NOTICES.md and $count license texts into $target" -ForegroundColor DarkGray
