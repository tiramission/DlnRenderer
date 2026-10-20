# Drives the renderer through the same IPC pipe the WinUI app uses.
# Verifies the UI-facing command path independently of DLNA.
$ErrorActionPreference = "Stop"

$pipeName = "DlnRenderer.Service.v1"

function Read-Message {
    param([System.IO.Stream]$Stream, [int]$TimeoutMs = 5000)

    $header = New-Object byte[] 4
    $read = 0
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($read -lt 4) {
        if ($sw.ElapsedMilliseconds -gt $TimeoutMs) { throw "timed out waiting for frame header" }
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

function Send-Message {
    param([System.IO.Stream]$Stream, [hashtable]$Message)

    $json = $Message | ConvertTo-Json -Compress -Depth 8
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $header = [System.BitConverter]::GetBytes([int]$bytes.Length)
    $Stream.Write($header, 0, 4)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
}

$pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::InOut)
$pipe.Connect(5000)
Write-Output "connected to $pipeName"

$hello = Read-Message -Stream $pipe
Write-Output "initial: type=$($hello.type) state=$($hello.snapshot.playback.state) discovery=$($hello.snapshot.discoveryActive)"

function Invoke-Command {
    param([string]$Command, [double]$Seek, [int]$VolumeMsg)

    $id = [Guid]::NewGuid().ToString("N")
    $message = @{ type = "request"; version = "1.0"; requestId = $id; command = $Command }
    if ($Seek -gt 0) { $message.seekSeconds = $Seek }
    if ($VolumeMsg -ge 0) { $message.volume = $VolumeMsg }
    Send-Message -Stream $pipe -Message $message

    while ($true) {
        $reply = Read-Message -Stream $pipe
        if ($null -eq $reply) { throw "connection closed" }
        if ($reply.type -eq "response" -and $reply.requestId -eq $id) { return $reply }
    }
}

$play = Invoke-Command -Command "Play"
Write-Output "Play -> success=$($play.success) state=$($play.snapshot.playback.state)"

Start-Sleep -Milliseconds 800
$pause = Invoke-Command -Command "Pause"
Write-Output "Pause -> success=$($pause.success) state=$($pause.snapshot.playback.state)"

$seek = Invoke-Command -Command "Seek" -Seek 20
Write-Output "Seek(20) -> success=$($seek.success) position=$($seek.snapshot.playback.position)"

$volume = Invoke-Command -Command "SetVolume" -VolumeMsg 30
Write-Output "SetVolume(30) -> success=$($volume.success) volume=$($volume.snapshot.playback.volume)"

$stop = Invoke-Command -Command "Stop"
Write-Output "Stop -> success=$($stop.success) state=$($stop.snapshot.playback.state)"

$bad = Invoke-Command -Command "FormatDisk"
Write-Output "unknown command -> success=$($bad.success) error=$($bad.error)"

$pipe.Dispose()
