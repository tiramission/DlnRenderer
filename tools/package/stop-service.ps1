<#
.SYNOPSIS
  Stops a running DlnRenderer service through the IPC pipe (stage E, E5).

.DESCRIPTION
  The uninstall path needs a clean stop, not a kill: the service owns the named pipe, the
  SSDP socket and the video window, and a hard kill leaves the SSDP socket in TIME_WAIT so a
  freshly installed copy cannot rejoin multicast for a while.

  This speaks the same length-prefixed JSON protocol as the WinUI app (IpcProtocol.PipeName),
  so it exercises the real shutdown path. If the pipe is gone (service already stopped) that
  is a success, not an error.

.EXAMPLE
  pwsh tools/package/stop-service.ps1
#>
[CmdletBinding()]
param(
    [string]$PipeName = 'DlnRenderer.Service.v1',
    [int]$TimeoutMs = 5000
)

$ErrorActionPreference = 'Stop'

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(
    ".", $PipeName, [System.IO.Pipes.PipeDirection]::InOut)

try {
    $pipe.Connect($TimeoutMs)
}
catch {
    Write-Host "no service listening on $PipeName (already stopped)" -ForegroundColor Yellow
    return
}

function Send-Message([System.IO.Stream]$Stream, [hashtable]$Message) {
    $json = $Message | ConvertTo-Json -Compress -Depth 8
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $header = [System.BitConverter]::GetBytes([int]$bytes.Length)
    $Stream.Write($header, 0, 4)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
}

function Read-Message([System.IO.Stream]$Stream, [int]$ReadTimeoutMs = 5000) {
    $header = New-Object byte[] 4
    $read = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($read -lt 4) {
        if ($sw.ElapsedMilliseconds -gt $ReadTimeoutMs) { throw "timed out waiting for frame header" }
        $n = $Stream.Read($header, $read, 4 - $read)
        if ($n -eq 0) { return $null }
        $read += $n
    }

    $length = [System.BitConverter]::ToInt32($header, 0)
    if ($length -le 0 -or $length -gt 65536) { throw "invalid frame length $length" }

    $payload = New-Object byte[] $length
    $read = 0
    while ($read -lt $length) {
        $n = $Stream.Read($payload, $read, $length - $read)
        if ($n -eq 0) { throw "stream ended mid-frame" }
        $read += $n
    }

    return [System.Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
}

# The newest first, then the ones a still-running older build would accept. Upgrades stop whatever
# is already installed - usually an older service - and a service refuses a request whose wire
# version it does not recognise while keeping the connection open, so each version can be tried on
# the same pipe. Keep in sync with IpcProtocol.Version (src/DlnRenderer.Core/Ipc/IpcProtocol.cs).
$protocolVersions = @('1.1', '1.0')

$stopped = $false
try {
    foreach ($version in $protocolVersions) {
        $message = @{
            type      = 'request'
            version   = $version
            requestId = [Guid]::NewGuid().ToString('N')
            command   = 'Shutdown'
        }

        Send-Message $pipe $message
        $response = Read-Message -Stream $pipe -ReadTimeoutMs 5000
        if ($null -eq $response) {
            # The service closes the pipe as it exits; no response frame is still a successful stop.
            Write-Host "service closed the connection (shutting down)" -ForegroundColor Green
            $stopped = $true
            break
        }

        if ($response.success -eq $true) {
            Write-Host "service acknowledged shutdown (protocol $version)" -ForegroundColor Green
            $stopped = $true
            break
        }

        Write-Verbose "protocol $version rejected Shutdown: $($response.error)"
    }

    if (-not $stopped) {
        Write-Warning "no protocol version was accepted; the running service is newer than this script"
    }
}
finally {
    $pipe.Dispose()
}

# Wait for the process to actually leave before the caller touches files or ports.
$deadline = (Get-Date).AddSeconds(10)
while ((Get-Date) -lt $deadline) {
    $alive = Get-Process -Name 'DlnRenderer.Service' -ErrorAction SilentlyContinue
    if (-not $alive) {
        Write-Host "DlnRenderer.Service has exited" -ForegroundColor Green
        return
    }
    Start-Sleep -Milliseconds 300
}

Write-Warning "DlnRenderer.Service is still running after 10s; terminate it with Stop-Process."
