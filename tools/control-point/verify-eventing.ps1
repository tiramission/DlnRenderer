# End-to-end GENA + RenderingControl check against the real service.
#
# Subscribes to AVTransport and RenderingControl exactly as a control point does, drives the
# renderer over SOAP, and asserts on the NOTIFY messages it receives.
$ErrorActionPreference = "Stop"

$base = "http://127.0.0.1:18090"
$callbackPort = 18991

# The renderer refuses loopback callbacks by default (anti-SSRF), which is the correct production
# behaviour. A real control point is on the LAN, so this check binds to this machine's LAN address.
$lanIp = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -like "192.168.*" } |
    Select-Object -First 1).IPAddress
if (-not $lanIp) { throw "no LAN IPv4 address found; this check needs a real interface" }

$results = [System.Collections.ArrayList]::new()
function Check($name, $ok) {
    $verdict = if ($ok) { "PASS" } else { "FAIL" }
    [void]$results.Add("$verdict  $name")
    Write-Host "  [$verdict] $name"
}

try {
    # --- callback endpoint: a raw TCP listener, so no URL ACL is needed ----------------------
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Parse($lanIp), $callbackPort)
    $listener.Start()
    Write-Host "callback endpoint: http://${lanIp}:$callbackPort/notify"

    $buffer = [System.Collections.Concurrent.ConcurrentQueue[object]]::new()
    $stop = [System.Threading.CancellationTokenSource]::new()

    $job = Start-ThreadJob -ArgumentList $listener, $buffer, $stop.Token -ScriptBlock {
        param($listener, $buffer, $token)
        while (-not $token.IsCancellationRequested) {
            try {
                $client = $listener.AcceptTcpClient()
            } catch { break }
            try {
                $stream = $client.GetStream()
                $bytes = [byte[]]::new(65536)
                $read = $stream.Read($bytes, 0, $bytes.Length)
                $text = [System.Text.Encoding]::UTF8.GetString($bytes, 0, $read)
                $parts = $text -split "`r`n`r`n", 2
                $headers = @{}
                foreach ($line in ($parts[0] -split "`r`n")) {
                    $idx = $line.IndexOf(':')
                    if ($idx -gt 0) { $headers[$line.Substring(0, $idx).Trim()] = $line.Substring($idx + 1).Trim() }
                }
                $body = if ($parts.Count -gt 1) { $parts[1] } else { "" }
                $buffer.Enqueue([pscustomobject]@{
                    Sid = $headers["SID"]
                    Seq = [int]$headers["SEQ"]
                    Nt  = $headers["NT"]
                    Nts = $headers["NTS"]
                    Body = $body
                })
                $ack = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 200 OK`r`nContent-Length: 2`r`nConnection: close`r`n`r`nok")
                $stream.Write($ack, 0, $ack.Length)
                $stream.Flush()
            } catch { }
            finally { $client.Dispose() }
        }
    }

    function Wait-ForNotify($predicate, $timeoutSeconds = 10) {
        $deadline = (Get-Date).AddSeconds($timeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            foreach ($item in $buffer.ToArray()) {
                if (& $predicate $item) { return $item }
            }
            Start-Sleep -Milliseconds 50
        }
        throw "timed out waiting for notification"
    }

    function Subscribe($service, $sid = $null) {
        $request = [System.Net.HttpWebRequest]::Create("$base/event/$service")
        $request.Method = "SUBSCRIBE"
        if ($sid) {
            $request.Headers.Add("SID", $sid)
            $request.Headers.Add("TIMEOUT", "Second-600")
        } else {
            $request.Headers.Add("CALLBACK", "<http://${lanIp}:$callbackPort/notify>")
            $request.Headers.Add("NT", "upnp:event")
            $request.Headers.Add("TIMEOUT", "Second-300")
        }
        $response = $request.GetResponse()
        $result = [pscustomobject]@{
            Sid = $response.Headers["SID"]
            Timeout = $response.Headers["TIMEOUT"]
            Status = [int]$response.StatusCode
        }
        $response.Close()
        return $result
    }

    function Unsubscribe($service, $sid) {
        $request = [System.Net.HttpWebRequest]::Create("$base/event/$service")
        $request.Method = "UNSUBSCRIBE"
        $request.Headers.Add("SID", $sid)
        try { $r = $request.GetResponse(); $code = [int]$r.StatusCode; $r.Close(); return $code }
        catch [System.Net.WebException] { return [int]$_.Exception.Response.StatusCode }
    }

    function Soap($service, $type, $action, $inner) {
        # Arguments are children of the namespaced action element, so they must carry the same prefix.
        $namespaced = [regex]::Replace($inner, '<(/?)([A-Za-z]+)>', '<${1}u:${2}>')
        $envelope = "<?xml version=""1.0"" encoding=""utf-8""?><s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/""><s:Body><u:$action xmlns:u=""urn:schemas-upnp-org:service:$type"">$namespaced</u:$action></s:Body></s:Envelope>"
        $request = [System.Net.HttpWebRequest]::Create("$base/control/$service")
        $request.Method = "POST"
        $request.ContentType = 'text/xml; charset="utf-8"'
        $request.Headers.Add("SOAPAction", """urn:schemas-upnp-org:service:$type#$action""")
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($envelope)
        $request.ContentLength = $bytes.Length
        $stream = $request.GetRequestStream()
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Close()
        $response = $request.GetResponse()
        $reader = [System.IO.StreamReader]::new($response.GetResponseStream())
        $body = $reader.ReadToEnd()
        $reader.Dispose(); $response.Close()
        return $body
    }

    Write-Host "== 1. subscribe =="
    $avt = Subscribe "avtransport"
    $rc = Subscribe "renderingcontrol"
    Write-Host "  avt SID=$($avt.Sid) TIMEOUT=$($avt.Timeout)"
    Write-Host "  rcs SID=$($rc.Sid) TIMEOUT=$($rc.Timeout)"
    Check "AVTransport SUBSCRIBE returns a SID" ($avt.Sid -like "uuid:*")
    Check "AVTransport SUBSCRIBE grants the requested timeout" ($avt.Timeout -eq "Second-300")
    Check "RenderingControl SUBSCRIBE returns a SID" ($rc.Sid -like "uuid:*")

    Write-Host "== 2. initial events =="
    $initialAvt = Wait-ForNotify { param($n) $n.Sid -eq $avt.Sid -and $n.Seq -eq 0 }
    $initialRc = Wait-ForNotify { param($n) $n.Sid -eq $rc.Sid -and $n.Seq -eq 0 }
    Check "AVTransport initial event uses SEQ 0" ($initialAvt.Seq -eq 0)
    Check "AVTransport initial event carries LastChange" ($initialAvt.Body -match "LastChange")
    Check "AVTransport initial event is a propertyset" ($initialAvt.Body -match "propertyset")
    Check "initial transport state is STOPPED" ($initialAvt.Body -match "TransportState[^>]*val=""STOPPED""")
    Check "RenderingControl initial event uses SEQ 0" ($initialRc.Seq -eq 0)
    Check "NOTIFY carries NT/NTS as required" ($initialAvt.Nt -eq "upnp:event" -and $initialAvt.Nts -eq "upnp:propchange")

    Write-Host "== 3. renewal keeps the SID =="
    $renewed = Subscribe "avtransport" $avt.Sid
    Check "renewal returns the same SID" ($renewed.Sid -eq $avt.Sid)
    Check "renewal grants the new timeout" ($renewed.Timeout -eq "Second-600")

    Write-Host "== 4. volume =="
    Soap "renderingcontrol" "RenderingControl:1" "SetVolume" "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>63</DesiredVolume>" | Out-Null
    $volumeEvent = Wait-ForNotify { param($n) $n.Sid -eq $rc.Sid -and $n.Body -match "Volume[^>]*val=""63""" }
    Check "SetVolume produces an event with the new value" ($null -ne $volumeEvent)
    $readBack = Soap "renderingcontrol" "RenderingControl:1" "GetVolume" "<InstanceID>0</InstanceID><Channel>Master</Channel>"
    Check "GetVolume reports 63" ($readBack -match "<CurrentVolume>63</CurrentVolume>")

    Write-Host "== 5. mute =="
    Soap "renderingcontrol" "RenderingControl:1" "SetMute" "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>1</DesiredMute>" | Out-Null
    $muteEvent = Wait-ForNotify { param($n) $n.Sid -eq $rc.Sid -and $n.Body -match "Mute[^>]*val=""1""" }
    Check "SetMute produces an event" ($null -ne $muteEvent)
    $muteRead = Soap "renderingcontrol" "RenderingControl:1" "GetMute" "<InstanceID>0</InstanceID><Channel>Master</Channel>"
    Check "GetMute reports muted" ($muteRead -match "<CurrentMute>1</CurrentMute>")
    Soap "renderingcontrol" "RenderingControl:1" "SetMute" "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredMute>0</DesiredMute>" | Out-Null

    Write-Host "== 6. invalid input =="
    try {
        Soap "renderingcontrol" "RenderingControl:1" "SetVolume" "<InstanceID>0</InstanceID><Channel>Master</Channel><DesiredVolume>150</DesiredVolume>" | Out-Null
        Check "out-of-range volume faults" $false
    } catch [System.Net.WebException] {
        $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        $fault = $reader.ReadToEnd(); $reader.Dispose()
        Check "out-of-range volume faults with 601" ($fault -match "<errorCode>601</errorCode>")
    }
    $unchanged = Soap "renderingcontrol" "RenderingControl:1" "GetVolume" "<InstanceID>0</InstanceID><Channel>Master</Channel>"
    Check "rejected volume left the player at 63" ($unchanged -match "<CurrentVolume>63</CurrentVolume>")

    Write-Host "== 7. rejected subscriptions =="
    try {
        $bad = [System.Net.HttpWebRequest]::Create("$base/event/avtransport")
        $bad.Method = "SUBSCRIBE"
        $bad.Headers.Add("NT", "upnp:event")
        $bad.GetResponse() | Out-Null
        Check "SUBSCRIBE without CALLBACK is rejected" $false
    } catch [System.Net.WebException] {
        Check "SUBSCRIBE without CALLBACK is 412" ([int]$_.Exception.Response.StatusCode -eq 412)
    }

    try {
        $bad2 = [System.Net.HttpWebRequest]::Create("$base/event/avtransport")
        $bad2.Method = "SUBSCRIBE"
        $bad2.Headers.Add("CALLBACK", "<http://169.254.169.254/latest/meta-data/>")
        $bad2.Headers.Add("NT", "upnp:event")
        $bad2.GetResponse() | Out-Null
        Check "link-local CALLBACK is rejected" $false
    } catch [System.Net.WebException] {
        Check "link-local CALLBACK is 412" ([int]$_.Exception.Response.StatusCode -eq 412)
    }

    try {
        $bad3 = [System.Net.HttpWebRequest]::Create("$base/event/avtransport")
        $bad3.Method = "SUBSCRIBE"
        $bad3.Headers.Add("CALLBACK", "<http://127.0.0.1:9999/cb>")
        $bad3.Headers.Add("NT", "upnp:event")
        $bad3.GetResponse() | Out-Null
        Check "loopback CALLBACK is rejected" $false
    } catch [System.Net.WebException] {
        Check "loopback CALLBACK is 412" ([int]$_.Exception.Response.StatusCode -eq 412)
    }

    Write-Host "== 8. unsubscribe =="
    Check "UNSUBSCRIBE succeeds" ((Unsubscribe "avtransport" $avt.Sid) -eq 200)
    Check "second UNSUBSCRIBE is 412" ((Unsubscribe "avtransport" $avt.Sid) -eq 412)
}
finally {
    try { $stop.Cancel() } catch { }
    try { $listener.Stop() } catch { }
    try { Stop-Job $job -ErrorAction SilentlyContinue; Remove-Job $job -Force -ErrorAction SilentlyContinue } catch { }
}

Write-Host ""
Write-Host "== summary =="
$results | ForEach-Object { Write-Host "  $_" }
$failed = ($results | Where-Object { $_ -like "FAIL*" }).Count
Write-Host "  failed: $failed / $($results.Count)"
