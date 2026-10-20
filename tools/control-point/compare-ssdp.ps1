param(
    [string]$InterfaceAddress = "192.168.1.9",
    [string]$SearchTarget = "ssdp:all",
    [int]$Port = 19910,
    [int]$Seconds = 8
)

# Sends one M-SEARCH and prints every reply verbatim, tagged by the service that sent it.
# Run it with only one renderer active to see exactly what a control point receives.

$ErrorActionPreference = "Stop"
$target = [System.Net.IPAddress]::Parse($InterfaceAddress)

$client = New-Object System.Net.Sockets.UdpClient
$client.Client.Bind((New-Object System.Net.IPEndPoint($target, $Port)))
$client.Client.ReceiveTimeout = 1500

$request = "M-SEARCH * HTTP/1.1`r`n" +
           "HOST: 239.255.255.250:1900`r`n" +
           "MAN: `"ssdp:discover`"`r`n" +
           "MX: 2`r`n" +
           "ST: $SearchTarget`r`n" +
           "`r`n"

$bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
$dest = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse("239.255.255.250"), 1900)
$client.Send($bytes, $bytes.Length, $dest) | Out-Null

Write-Output "sent M-SEARCH ST=$SearchTarget from ${InterfaceAddress}:${Port}"

$deadline = (Get-Date).AddSeconds($Seconds)
$seen = @{}
while ((Get-Date) -lt $deadline) {
    try {
        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $data = $client.Receive([ref]$remote)
        $text = [System.Text.Encoding]::ASCII.GetString($data)
        $key = "$($remote.Address):$($remote.Port)|$text"
        if ($seen.ContainsKey($key)) { continue }
        $seen[$key] = 1

        $server = ""
        if ($text -match '(?im)^SERVER:\s*(.+?)\r?$') { $server = $Matches[1].Trim() }
        $st = ""
        if ($text -match '(?im)^ST:\s*(.+?)\r?$') { $st = $Matches[1].Trim() }

        Write-Output "----- from $($remote.Address):$($remote.Port) SERVER=$server ST=$st -----"
        Write-Output $text
    }
    catch [System.Net.Sockets.SocketException] {
    }
}

$client.Close()
Write-Output "unique-replies=$($seen.Count)"
