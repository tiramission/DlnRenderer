param(
    [Parameter(Mandatory = $true)][string]$Location
)

# Validates a device description the way a strict UPnP control point does.
# Every check below maps to a requirement in the UPnP Device Architecture spec or to something
# BubbleUPnP-class clients are known to require; a failure here explains "discovered but not listed".

$ErrorActionPreference = "Stop"
$problems = New-Object System.Collections.ArrayList
$ok = New-Object System.Collections.ArrayList

function Check([bool]$condition, [string]$message) {
    if ($condition) { [void]$ok.Add($message) } else { [void]$problems.Add($message) }
}

try {
    $response = Invoke-WebRequest -Uri $Location -UseBasicParsing -TimeoutSec 10
}
catch {
    Write-Output "FATAL: cannot fetch $Location : $($_.Exception.Message)"
    exit 1
}

$contentType = $response.Headers['Content-Type']
Check ($contentType -like "text/xml*") "Content-Type is text/xml (got '$contentType')"
Check ($null -ne $response.Headers['Date']) "HTTP response has a Date header"

$raw = $response.Content
Check ($raw.TrimStart().StartsWith('<?xml')) "document starts with an XML declaration"
Check (-not $raw.StartsWith([string][char]0xFEFF)) "document has no UTF-8 BOM"

try {
    $doc = New-Object System.Xml.XmlDocument
    $doc.LoadXml($raw)
    [void]$ok.Add("document parses as XML")
}
catch {
    Write-Output "FATAL: XML does not parse: $($_.Exception.Message)"
    exit 1
}

$ns = "urn:schemas-upnp-org:device-1-0"
$root = $doc.DocumentElement
Check ($root.NamespaceURI -eq $ns) "root is in $ns (got $($root.NamespaceURI))"

$spec = $root.SelectSingleNode("*[local-name()='specVersion']")
Check ($null -ne $spec) "specVersion present"

$urlBaseNode = $root.SelectSingleNode("*[local-name()='URLBase']")
$base = if ($urlBaseNode) {
    Check ($urlBaseNode.ParentNode.Name -eq $root.Name) "URLBase is a direct child of root"
    $urlBaseNode.InnerText.Trim().TrimEnd('/')
} else { ([Uri]$Location).GetLeftPart([System.UriPartial]::Authority) }

$device = $root.SelectSingleNode("*[local-name()='device']")
Check ($null -ne $device) "device element present"
if (-not $device) { $problems | ForEach-Object { "FAIL: $_" }; exit 1 }

$deviceType = $device.SelectSingleNode("*[local-name()='deviceType']").InnerText
Check ($deviceType -eq "urn:schemas-upnp-org:device:MediaRenderer:1") "deviceType is MediaRenderer:1 (got $deviceType)"

$udn = $device.SelectSingleNode("*[local-name()='UDN']").InnerText
Check ($udn -like "uuid:*") "UDN starts with uuid: (got $udn)"
$friendly = $device.SelectSingleNode("*[local-name()='friendlyName']")
Check ($null -ne $friendly -and $friendly.InnerText.Trim().Length -gt 0) "friendlyName is non-empty"

$dlnaDoc = $device.SelectSingleNode("*[local-name()='X_DLNADOC']")
Check ($null -ne $dlnaDoc) "dlna:X_DLNADOC present"
if ($dlnaDoc) {
    $dlnaDocNs = $dlnaDoc.NamespaceURI
    Check ($dlnaDocNs -eq "urn:schemas-dlna-org:device-1-0") "X_DLNADOC is in the DLNA namespace (got '$dlnaDocNs')"
}

$services = $device.SelectNodes("*[local-name()='serviceList']/*[local-name()='service']")
Check ($services.Count -ge 3) "at least three services declared (got $($services.Count))"

$required = @(
    "urn:schemas-upnp-org:service:AVTransport:1",
    "urn:schemas-upnp-org:service:RenderingControl:1",
    "urn:schemas-upnp-org:service:ConnectionManager:1"
)

foreach ($type in $required) {
    $svc = $null
    foreach ($s in $services) {
        if ($s.SelectSingleNode("*[local-name()='serviceType']").InnerText -eq $type) { $svc = $s; break }
    }

    Check ($null -ne $svc) "service $type is declared"
    if (-not $svc) { continue }

    $control = $svc.SelectSingleNode("*[local-name()='controlURL']").InnerText.Trim()
    $scpd = $svc.SelectSingleNode("*[local-name()='SCPDURL']").InnerText.Trim()
    $eventSub = $svc.SelectSingleNode("*[local-name()='eventSubURL']").InnerText.Trim()

    # Resolve the way a control point does: URLBase if present, otherwise the description URL.
    $controlUri = if ($control -like "http*") { $control } elseif ($control.StartsWith('/')) { $base + $control } else { "$base/$control" }
    $scpdUri = if ($scpd -like "http*") { $scpd } elseif ($scpd.StartsWith('/')) { $base + $scpd } else { "$base/$scpd" }

    try {
        $scpdResponse = Invoke-WebRequest -Uri $scpdUri -UseBasicParsing -TimeoutSec 10
        $scpdDoc = New-Object System.Xml.XmlDocument
        $scpdDoc.LoadXml($scpdResponse.Content)
        $actionCount = $scpdDoc.SelectNodes("//*[local-name()='action']").Count
        Check ($actionCount -gt 0) "SCPD for $type lists actions ($actionCount)"
    }
    catch {
        [void]$problems.Add("SCPD for $type is not retrievable at $scpdUri : $($_.Exception.Message)")
    }

    # The control endpoint must reject a GET with a client error, not hang or 500 silently.
    try {
        $probe = Invoke-WebRequest -Uri $controlUri -Method Post -Body "<not-soap/>" -ContentType "text/xml" -UseBasicParsing -TimeoutSec 10
        [void]$problems.Add("control endpoint $controlUri answered a malformed SOAP body with HTTP $($probe.StatusCode)")
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        Check ($status -ge 400 -and $status -lt 600) "control endpoint $controlUri rejects malformed SOAP with HTTP $status"
    }

    [void]$ok.Add("eventSubURL for $type is '$eventSub'")
}

Write-Output "=== PASSED ($($ok.Count)) ==="
$ok | ForEach-Object { "  ok   $_" }

Write-Output "=== FAILED ($($problems.Count)) ==="
$problems | ForEach-Object { "  FAIL $_" }

if ($problems.Count -gt 0) { exit 2 }
