param(
    [string]$MediaUrl = "",
    # Audio and video differ only in the DIDL class and protocolInfo this check advertises; both go
    # through the same AVTransport actions, so the acceptance run needs to be able to say which one
    # it actually exercised.
    [ValidateSet("video", "audio")]
    [string]$MediaKind = "video",
    # Skip multicast discovery and use this description URL. Needed on a machine that cannot receive
    # its own multicast (Windows SSDPSRV also owns UDP 1900); discovery still has to be verified
    # separately, with a real control point.
    [string]$Location = "",
    [switch]$KeepRunning
)

$ErrorActionPreference = "Stop"

function Invoke-Soap {
    param([string]$Url, [string]$ServiceType, [string]$Action, [hashtable]$Arguments)

    $inner = ""
    foreach ($name in $Arguments.Keys) {
        $inner += "<$name>$([System.Security.SecurityElement]::Escape([string]$Arguments[$name]))</$name>"
    }

    $body = '<?xml version="1.0" encoding="utf-8"?>' +
            '<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">' +
            "<s:Body><u:$Action xmlns:u=`"$ServiceType`">$inner</u:$Action></s:Body></s:Envelope>"

    $headers = @{ SOAPAction = "`"$ServiceType#$Action`""; "Content-Type" = 'text/xml; charset="utf-8"' }
    $response = Invoke-WebRequest -Uri $Url -Method Post -Headers $headers -Body ([System.Text.Encoding]::UTF8.GetBytes($body)) -UseBasicParsing -TimeoutSec 30
    return [xml]$response.Content
}

function Show-Action {
    param([xml]$Xml, [string]$Action)
    $result = @()
    $node = $Xml.SelectSingleNode("//*[local-name()='${Action}Response']")
    if ($node) {
        foreach ($child in $node.ChildNodes) {
            if ($child.NodeType -eq 'Element') { $result += ("{0}={1}" -f $child.LocalName, $child.InnerText) }
        }
    }
    return ($result -join " | ")
}

$avt = "urn:schemas-upnp-org:service:AVTransport:1"
$cm = "urn:schemas-upnp-org:service:ConnectionManager:1"

# 1. Discovery over real multicast, unless the caller already knows the description URL.
if ($Location) {
    Write-Output "== SSDP discovery skipped: using $Location =="
    $location = $Location
} else {
    Write-Output "== SSDP discovery =="
    $discovery = & "$PSScriptRoot\ssdp-discover.ps1" -SearchTarget "urn:schemas-upnp-org:device:MediaRenderer:1"
    $discovery | ForEach-Object { Write-Output "  $_" }
    $location = ($discovery | Where-Object { $_ -match "ST=urn:schemas-upnp-org:device:MediaRenderer:1" } | ForEach-Object {
        if ($_ -match "LOCATION=([^|]+)") { $Matches[1].Trim() }
    } | Select-Object -First 1)

    if (-not $location) { throw "device not discovered" }
}
Write-Output "== device description: $location =="
$description = Invoke-WebRequest -Uri $location -UseBasicParsing -TimeoutSec 15
$device = [xml]$description.Content
$base = ([uri]$location).GetLeftPart([System.UriPartial]::Authority)
$friendlyName = $device.SelectSingleNode("//*[local-name()='friendlyName']").InnerText
$udn = $device.SelectSingleNode("//*[local-name()='UDN']").InnerText
$deviceType = $device.SelectSingleNode("//*[local-name()='deviceType']").InnerText
Write-Output "  friendlyName=$friendlyName"
Write-Output "  deviceType=$deviceType"
Write-Output "  udn=$udn"

$services = @{}
# The description carries relative URLs without a leading slash (relative to the description URL,
# as the specification allows). Resolve them as URIs instead of concatenating strings, or a valid
# description is mistaken for a malformed one.
$descriptionUri = [Uri]$location
foreach ($service in $device.SelectNodes("//*[local-name()='service']")) {
    $type = $service.SelectSingleNode("*[local-name()='serviceType']").InnerText
    $control = $service.SelectSingleNode("*[local-name()='controlURL']").InnerText
    $scpd = $service.SelectSingleNode("*[local-name()='SCPDURL']").InnerText
    $services[$type] = ([Uri]::new($descriptionUri, $control)).AbsoluteUri
    $scpdUri = ([Uri]::new($descriptionUri, $scpd)).AbsoluteUri
    $scpdResponse = Invoke-WebRequest -Uri $scpdUri -UseBasicParsing -TimeoutSec 15
    Write-Output "  service=$type scpdBytes=$($scpdResponse.Content.Length)"
}

Write-Output "== ConnectionManager.GetProtocolInfo =="
$protocolInfo = Invoke-Soap -Url $services[$cm] -ServiceType $cm -Action "GetProtocolInfo" -Arguments @{}
Write-Output ("  " + (Show-Action -Xml $protocolInfo -Action "GetProtocolInfo"))

if ([string]::IsNullOrWhiteSpace($MediaUrl)) {
    Write-Output "== no media url supplied; skipping transport checks =="
    return
}

Write-Output "== AVTransport.GetTransportInfo (before load) =="
$before = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetTransportInfo" -Arguments @{ InstanceID = 0 }
Write-Output ("  " + (Show-Action -Xml $before -Action "GetTransportInfo"))

$itemClass = if ($MediaKind -eq "audio") { "object.item.audioItem.musicTrack" } else { "object.item.videoItem" }
$protocolInfo = if ($MediaKind -eq "audio") { "http-get:*:audio/mpeg:*" } else { "http-get:*:video/mp4:*" }
$didl = '<DIDL-Lite xmlns="urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/" xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:upnp="urn:schemas-upnp-org:metadata-1-0/upnp/">' +
        '<item id="1" parentID="0" restricted="1"><dc:title>DLNA Test Clip</dc:title>' +
        "<upnp:class>$itemClass</upnp:class>" +
        "<res protocolInfo=`"$protocolInfo`">$([System.Security.SecurityElement]::Escape($MediaUrl))</res>" +
        '</item></DIDL-Lite>'

Write-Output "== SetAVTransportURI =="
$set = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "SetAVTransportURI" -Arguments @{
    InstanceID = 0; CurrentURI = $MediaUrl; CurrentURIMetaData = $didl
}
Write-Output ("  " + (Show-Action -Xml $set -Action "SetAVTransportURI"))

Write-Output "== Play =="
$play = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "Play" -Arguments @{ InstanceID = 0; Speed = 1 }
Write-Output ("  " + (Show-Action -Xml $play -Action "Play"))

Start-Sleep -Seconds 5

Write-Output "== GetTransportInfo (after play) =="
$info = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetTransportInfo" -Arguments @{ InstanceID = 0 }
Write-Output ("  " + (Show-Action -Xml $info -Action "GetTransportInfo"))

Write-Output "== GetPositionInfo =="
$position = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetPositionInfo" -Arguments @{ InstanceID = 0 }
Write-Output ("  " + (Show-Action -Xml $position -Action "GetPositionInfo"))

Write-Output "== Pause =="
$pause = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "Pause" -Arguments @{ InstanceID = 0 }
$paused = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetTransportInfo" -Arguments @{ InstanceID = 0 }
Write-Output ("  " + (Show-Action -Xml $paused -Action "GetTransportInfo"))

Write-Output "== Seek to 30s =="
$null = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "Seek" -Arguments @{ InstanceID = 0; Unit = "REL_TIME"; Target = "00:00:30" }
$seeked = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetPositionInfo" -Arguments @{ InstanceID = 0 }
Write-Output ("  " + (Show-Action -Xml $seeked -Action "GetPositionInfo"))

Write-Output "== Stop =="
$null = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "Stop" -Arguments @{ InstanceID = 0 }
$stopped = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetTransportInfo" -Arguments @{ InstanceID = 0 }
Write-Output ("  " + (Show-Action -Xml $stopped -Action "GetTransportInfo"))

Write-Output "== invalid instance id (expect SOAP fault 718) =="
try {
    $null = Invoke-Soap -Url $services[$avt] -ServiceType $avt -Action "GetTransportInfo" -Arguments @{ InstanceID = 7 }
    Write-Output "  UNEXPECTED: no fault"
} catch {
    $fault = $_.ErrorDetails.Message
    if ($fault -match "<errorCode>(\d+)</errorCode>") {
        Write-Output "  fault errorCode=$($Matches[1])"
    } else {
        Write-Output "  fault (unparsed): $($_.Exception.Message)"
    }
}
