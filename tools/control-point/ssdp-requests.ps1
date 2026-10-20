param(
    [string]$InterfaceAddress = "192.168.1.9",
    [int]$Seconds = 40
)

# Records every inbound SSDP datagram on the physical LAN interface, including whether it is a
# unicast search addressed to this host and which ST it asks for.
# The key question this answers: does BubbleUPnP ask via multicast (which our per-interface
# sockets must join) or via unicast to a specific address?

$ErrorActionPreference = "Stop"

$multicast = [System.Net.IPAddress]::Parse("239.255.255.250")
$local = [System.Net.IPAddress]::Parse($InterfaceAddress)

$udp = New-Object System.Net.Sockets.UdpClient
$udp.Client.SetSocketOption(
    [System.Net.Sockets.SocketOptionLevel]::Socket,
    [System.Net.Sockets.SocketOptionName]::ReuseAddress, $true) | Out-Null
$udp.Client.Bind((New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 1900)))
$udp.Client.SetSocketOption(
    [System.Net.Sockets.SocketOptionLevel]::IP,
    [System.Net.Sockets.SocketOptionName]::AddMembership,
    (New-Object System.Net.Sockets.MulticastOption($multicast, $local))) | Out-Null
$udp.Client.ReceiveTimeout = 1000

Write-Output "listening on ${InterfaceAddress}:1900 for ${Seconds}s"
Write-Output "columns: time | from | first-line | ST | MAN | MX | TARGET"

$deadline = (Get-Date).AddSeconds($Seconds)
while ((Get-Date) -lt $deadline) {
    try {
        $remote = New-Object System.Net.IPEndPoint([System.Net.IPAddress]::Any, 0)
        $data = $udp.Receive([ref]$remote)
        $text = [System.Text.Encoding]::ASCII.GetString($data)
        $first = ($text -split "`r`n")[0]

        if ($first -match '^(?i)M-SEARCH') {
            $st = ""; if ($text -match '(?im)^ST:\s*(.+?)\r?$') { $st = $Matches[1].Trim() }
            $man = ""; if ($text -match '(?im)^MAN:\s*(.+?)\r?$') { $man = $Matches[1].Trim() }
            $mx = ""; if ($text -match '(?im)^MX:\s*(.+?)\r?$') { $mx = $Matches[1].Trim() }
            $host_ = ""; if ($text -match '(?im)^HOST:\s*(.+?)\r?$') { $host_ = $Matches[1].Trim() }

            Write-Output ("{0} | {1,-22} | {2} | ST={3} | MAN={4} | MX={5} | HOST={6}" -f `
                (Get-Date -Format "HH:mm:ss.fff"), "$($remote.Address):$($remote.Port)", $first, $st, $man, $mx, $host_)
        }
        elseif ($first -match '^(?i)NOTIFY') {
            $nt = ""; if ($text -match '(?im)^NT:\s*(.+?)\r?$') { $nt = $Matches[1].Trim() }
            $srv = ""; if ($text -match '(?im)^SERVER:\s*(.+?)\r?$') { $srv = $Matches[1].Trim() }
            Write-Output ("{0} | {1,-22} | NOTIFY NT={2} | SERVER={3}" -f (Get-Date -Format "HH:mm:ss.fff"), "$($remote.Address):$($remote.Port)", $nt, $srv)
        }
    }
    catch [System.Net.Sockets.SocketException] {
    }
}

$udp.Close()
Write-Output "done"
