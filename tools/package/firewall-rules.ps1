<#
.SYNOPSIS
  Adds or removes the Windows Firewall rules DlnRenderer needs (stage E, E4).

.DESCRIPTION
  The service listens on UDP 1900 (SSDP) and on a fixed TCP port (18090 by default, see
  ServiceStartupOptions.DefaultHttpPort) for device description, SOAP control and GENA callbacks.
  Without rules the first launch looks like "the app is broken": nothing is discoverable.

  Two rules are created so they can be told apart in the firewall UI:
    - DlnRenderer SSDP (inbound, UDP 1900)
    - DlnRenderer Service (inbound, TCP <port>)

  The uninstall path removes exactly these names; nothing else is touched.

  Requires an elevated shell: run from an administrator PowerShell.

.PARAMETER Action
  Add or Remove the rules.

.PARAMETER Port
  TCP port of the service (default 18090, the ServiceStartupOptions.DefaultHttpPort default).

.EXAMPLE
  pwsh tools/package/firewall-rules.ps1 -Action Add     # as administrator
  pwsh tools/package/firewall-rules.ps1 -Action Remove  # during uninstall
#>
[CmdletBinding()]
param(
    [ValidateSet('Add', 'Remove')]
    [string]$Action = 'Add',
    [int]$Port = 18090,
    [string]$ProgramPath = ''
)

$ErrorActionPreference = 'Stop'

$ssdpRule = 'DlnRenderer SSDP (UDP 1900)'
$serviceRule = "DlnRenderer Service (TCP $Port)"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    throw 'Firewall rules require an elevated shell. Start PowerShell as Administrator and rerun.'
}

if ($Action -eq 'Add') {
    $existing = Get-NetFirewallRule -DisplayName $ssdpRule, $serviceRule -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "rules already present, refreshing" -ForegroundColor Yellow
        $existing | Remove-NetFirewallRule
    }

    New-NetFirewallRule -DisplayName $ssdpRule `
        -Direction Inbound -Protocol UDP -LocalPort 1900 `
        -Action Allow -Profile Private,Domain `
        -Description 'DlnRenderer SSDP discovery (M-SEARCH responses and alive/bye announcements).' | Out-Null

    $serviceParams = @{
        DisplayName = $serviceRule
        Direction   = 'Inbound'
        Protocol    = 'TCP'
        LocalPort   = $Port
        Action      = 'Allow'
        Profile     = 'Private,Domain'
        Description = 'DlnRenderer DLNA service: device description, SOAP control and GENA callbacks.'
    }
    if ($ProgramPath -and (Test-Path -LiteralPath $ProgramPath)) {
        # Scoping to the binary keeps the port from being opened by unrelated programs.
        $serviceParams['Program'] = $ProgramPath
    }
    New-NetFirewallRule @serviceParams | Out-Null

    Write-Host "added: $ssdpRule" -ForegroundColor Green
    Write-Host "added: $serviceRule" -ForegroundColor Green
}
else {
    $rules = Get-NetFirewallRule -DisplayName $ssdpRule, $serviceRule -ErrorAction SilentlyContinue
    if (-not $rules) {
        Write-Host "no DlnRenderer firewall rules present" -ForegroundColor Yellow
        return
    }

    $rules | Remove-NetFirewallRule
    foreach ($name in $ssdpRule, $serviceRule) {
        Write-Host "removed: $name" -ForegroundColor Green
    }
}
