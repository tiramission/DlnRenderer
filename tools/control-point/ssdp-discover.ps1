param(
    [string]$SearchTarget = "urn:schemas-upnp-org:device:MediaRenderer:1",
    [int]$TimeoutMs = 4000
)

$ErrorActionPreference = "Stop"

$udp = New-Object System.Net.Sockets.UdpClient
$udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::Socket, [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true) | Out-Null
$udp.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)))
$udp.Client.ReceiveTimeout = 1000

$request = "M-SEARCH * HTTP/1.1`r`n" +
           "HOST: 239.255.255.250:1900`r`n" +
           "MAN: `"ssdp:discover`"`r`n" +
           "MX: 2`r`n" +
           "ST: $SearchTarget`r`n" +
           "`r`n"
$bytes = [System.Text.Encoding]::ASCII.GetBytes($request)
$endpoint = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Parse("239.255.255.250"), 1900)

# Send from each local IPv4 address so every interface's socket sees the search.
$sent = 0
foreach ($nic in [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces()) {
    if ($nic.OperationalStatus -ne 'Up') { continue }
    foreach ($ua in $nic.GetIPProperties().UnicastAddresses) {
        if ($ua.Address.AddressFamily -ne 'InterNetwork') { continue }
        try {
            $udp.Client.SetSocketOption([System.Net.Sockets.SocketOptionLevel]::IP, [System.Net.Sockets.SocketOptionName]::MulticastInterface, [System.BitConverter]::ToInt32($ua.Address.GetAddressBytes(), 0)) | Out-Null
            $udp.Send($bytes, $bytes.Length, $endpoint) | Out-Null
            $sent++
        } catch { }
    }
}
Write-Output "searches-sent=$sent st=$SearchTarget"

$responses = @{}
$deadline = (Get-Date).AddMilliseconds($TimeoutMs)
while ((Get-Date) -lt $deadline) {
    try {
        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $data = $udp.Receive([ref]$remote)
        $text = [System.Text.Encoding]::ASCII.GetString($data)
        if ($text -match "(?im)^USN:\s*(.+)$") { $responses[$Matches[1].Trim()] = $text }
    } catch [System.Net.Sockets.SocketException] { }
}
$udp.Close()

Write-Output "responses=$($responses.Count)"
foreach ($usn in $responses.Keys | Sort-Object) {
    $text = $responses[$usn]
    $location = if ($text -match "(?im)^LOCATION:\s*(.+)$") { $Matches[1].Trim() } else { "" }
    $st = if ($text -match "(?im)^ST:\s*(.+)$") { $Matches[1].Trim() } else { "" }
    $cache = if ($text -match "(?im)^CACHE-CONTROL:\s*(.+)$") { $Matches[1].Trim() } else { "" }
    $server = if ($text -match "(?im)^SERVER:\s*(.+)$") { $Matches[1].Trim() } else { "" }
    $ext = $text -match "(?im)^EXT:\s*$"
    Write-Output ("USN=" + $usn + " | ST=" + $st + " | LOCATION=" + $location + " | CACHE=" + $cache + " | EXT=" + $ext + " | SERVER=" + $server)
}
