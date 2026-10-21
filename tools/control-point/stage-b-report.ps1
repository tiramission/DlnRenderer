# Stage B evidence report: turn a service log into the acceptance table.
#
# Stage B is verified with real control points, so the only durable evidence is what the service
# logged while they were talking to it: which address searched, which SOAP actions arrived, which
# subscription was created, and whether the NOTIFY that followed was answered with 200.
#
# This script reads that evidence instead of asking anyone to scroll a log. It is read-only.
#
#   pwsh tools/control-point/stage-b-report.ps1
#   pwsh tools/control-point/stage-b-report.ps1 -LogPath "$env:LOCALAPPDATA\DlnRenderer\logs\service-20261021.log"
#   pwsh tools/control-point/stage-b-report.ps1 -OutputPath docs/stage-b-evidence.md
#
# Loopback clients are reported separately. They prove protocol behaviour but they are this
# machine's own scripts, not a third-party control point.

[CmdletBinding()]
param(
    # A service log file, a log directory (newest service-*.log wins), or nothing for the default.
    [string]$LogPath,

    # Write the Markdown report here as well as printing it.
    [string]$OutputPath,

    # Ignore lines older than this, for a long-running service whose log spans several sessions.
    [datetime]$Since,

    # Include every session in the file. By default only the most recent service start is reported:
    # a per-day log accumulates sessions, and mixing them makes a stale cast look like a fresh one.
    [switch]$AllSessions
)

$ErrorActionPreference = "Stop"
try {
    $OutputEncoding = [System.Text.Encoding]::UTF8
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

function Resolve-ServiceLog([string]$path) {
    if (-not $path) {
        $path = Join-Path $env:LOCALAPPDATA "DlnRenderer\logs"
    }

    if (Test-Path -LiteralPath $path -PathType Container) {
        $candidate = Get-ChildItem -LiteralPath $path -Filter "service-*.log" -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if (-not $candidate) { throw "no service-*.log in '$path'" }
        return $candidate.FullName
    }

    if (-not (Test-Path -LiteralPath $path)) { throw "log not found: '$path'" }
    return (Resolve-Path -LiteralPath $path).Path
}

# M-SEARCH logs carry "ip:port"; HTTP and SOAP logs carry a bare address. Grouping by the raw
# value would give one client a row per ephemeral port.
function Get-HostKey([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) { return "" }
    $text = $value.Trim()
    if ($text -match '^\[(?<host>[^\]]+)\](?::\d+)?$') { return $Matches.host }
    if ($text -match '^(?<host>\d{1,3}(?:\.\d{1,3}){3})(?::\d+)?$') { return $Matches.host }
    return $text
}

# A local script that targets the machine's LAN address is still this machine, not a third-party
# control point; the address list is what distinguishes the two.
$localAddresses = @()
try {
    $localAddresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        ForEach-Object { $_.IPAddress })
} catch { }

function Test-LocalAddress([string]$ip) {
    if ($ip -eq "::1" -or $ip -match '^127\.') { return $true }
    return ($localAddresses -contains $ip)
}

function New-Client([string]$ip) {
    [pscustomobject]@{
        Ip           = $ip
        IsLocal      = (Test-LocalAddress $ip)
        Searches     = 0
        Answered     = 0
        Descriptions = 0
        Scpds        = 0
        Subscribes   = 0
        NotifyOk     = 0
        NotifyFailed = 0
        Actions      = @{}
        Callbacks    = [System.Collections.Generic.HashSet[string]]::new()
        FirstSeen    = $null
        LastSeen     = $null
    }
}

function Get-Client($clients, [string]$ip) {
    if (-not $clients.ContainsKey($ip)) { $clients[$ip] = New-Client $ip }
    return $clients[$ip]
}

function Add-Action($client, [string]$action) {
    $client.Actions[$action] = 1 + [int]($client.Actions[$action])
}

function Add-Seen($client, [datetime]$timestamp) {
    if ($null -eq $client.FirstSeen -or $timestamp -lt $client.FirstSeen) { $client.FirstSeen = $timestamp }
    if ($null -eq $client.LastSeen -or $timestamp -gt $client.LastSeen) { $client.LastSeen = $timestamp }
}

$logFile = Resolve-ServiceLog $LogPath

$allLines = Get-Content -LiteralPath $logFile -Encoding utf8
$scopeLine = 0
$scope = "最近一次服务会话"
if (-not $AllSessions -and -not $Since) {
    for ($i = $allLines.Count - 1; $i -ge 0; $i--) {
        if ($allLines[$i] -match '\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} service starting') { $scopeLine = $i; break }
    }
}
elseif ($AllSessions) {
    $scope = "当日全部会话"
}
else {
    $scope = "自 $($Since.ToString('yyyy-MM-dd HH:mm:ss')) 起"
}

if ($scopeLine -gt 0) { $allLines = $allLines[$scopeLine..($allLines.Count - 1)] }

$clients = @{}
$sidOwner = @{}
$events = [System.Collections.Generic.List[string]]::new()
$networkChanges = 0
$startedAt = $null
$lastHttpIp = ""

foreach ($raw in $allLines) {
    if ($raw -notmatch '^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) (?<msg>.*)$') { continue }

    $timestamp = [datetime]::ParseExact($Matches.ts, "yyyy-MM-dd HH:mm:ss.fff", $null)
    if ($Since -and $timestamp -lt $Since) { continue }

    $message = $Matches.msg

    if ($message.StartsWith("service starting")) {
        if ($null -eq $startedAt -or $timestamp -gt $startedAt) { $startedAt = $timestamp }
        continue
    }

    if ($message -match '^ssdp M-SEARCH received from (?<from>\S+) ST=(?<st>\S+)') {
        $ip = Get-HostKey $Matches.from
        $client = Get-Client $clients $ip
        $client.Searches++
        Add-Seen $client $timestamp
        continue
    }

    if ($message -match '^ssdp answered .* to (?<to>\S+) LOCATION=') {
        $client = Get-Client $clients (Get-HostKey $Matches.to)
        $client.Answered++
        Add-Seen $client $timestamp
        continue
    }

    if ($message -match '^http (?<method>\S+) (?<path>\S+) from (?<ip>\S+)') {
        $ip = Get-HostKey $Matches.ip
        $lastHttpIp = $ip
        $client = Get-Client $clients $ip
        Add-Seen $client $timestamp
        if ($Matches.path -match 'description') { $client.Descriptions++ }
        elseif ($Matches.path -match 'scpd') { $client.Scpds++ }
        continue
    }

    if ($message -match '^soap action (?<action>\S+) from (?<ip>\S+)') {
        $client = Get-Client $clients (Get-HostKey $Matches.ip)
        Add-Action $client $Matches.action
        Add-Seen $client $timestamp
        continue
    }

    if ($message -match '^subscription created (?<sid>\S+) for (?<svc>\S+) -> (?<cb>\S+)') {
        $sid = $Matches.sid
        $owner = if ($lastHttpIp) { $lastHttpIp } else { Get-HostKey $Matches.cb }
        $sidOwner[$sid] = $owner
        $client = Get-Client $clients $owner
        $client.Subscribes++
        $client.Callbacks.Add($Matches.cb) | Out-Null
        $events.Add("$($timestamp.ToString('HH:mm:ss')) SUBSCRIBE $($Matches.svc) from $owner accepted -> $($Matches.cb)")
        continue
    }

    if ($message -match '^subscribe rejected from (?<ip>[^ :]+)') {
        $events.Add("$($timestamp.ToString('HH:mm:ss')) SUBSCRIBE rejected from $(Get-HostKey $Matches.ip)")
        continue
    }

    if ($message -match '^notify (?<sid>\S+) seq (?<seq>\d+) -> (?<cb>http://(?<host>[^:/]+)(?::\d+)?\S*) (?<status>\d+) in (?<ms>\d+) ms') {
        $sid = $Matches.sid
        $owner = if ($sidOwner.ContainsKey($sid)) { $sidOwner[$sid] } else { Get-HostKey $Matches.host }
        $client = Get-Client $clients $owner
        Add-Seen $client $timestamp
        if ([int]$Matches.status -ge 200 -and [int]$Matches.status -lt 300) {
            $client.NotifyOk++
            if ($events.Count -lt 200) {
                $events.Add("$($timestamp.ToString('HH:mm:ss')) NOTIFY $owner seq $($Matches.seq) -> $($Matches.status) in $($Matches.ms) ms")
            }
        } else {
            $client.NotifyFailed++
            $events.Add("$($timestamp.ToString('HH:mm:ss')) NOTIFY $owner seq $($Matches.seq) -> HTTP $($Matches.status)")
        }
        continue
    }

    if ($message -match '^notify failed for (?<sid>\S+) \((?<reason>[^)]*)\)') {
        $owner = if ($sidOwner.ContainsKey($Matches.sid)) { $sidOwner[$Matches.sid] } else { 'unknown' }
        $client = Get-Client $clients $owner
        $client.NotifyFailed++
        Add-Seen $client $timestamp
        $events.Add("$($timestamp.ToString('HH:mm:ss')) NOTIFY failed ($($Matches.reason)) sid=$($Matches.sid)")
        continue
    }

    if ($message -match '^ssdp network change') { $networkChanges++ }
}

function Format-Count($value) { if ($value -eq 0) { "—" } else { $value } }

function Total-Of($set, [string]$action) {
    if (-not $set) { return 0 }
    $sum = ($set | ForEach-Object { [int]$_.Actions[$action] } | Measure-Object -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [int]$sum
}

function Sum-Of($set, [string]$property) {
    if (-not $set) { return 0 }
    $sum = ($set | ForEach-Object { [int]$_.$property } | Measure-Object -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [int]$sum
}

$all = @($clients.Values)
$remote = @($clients.Values | Where-Object { -not $_.IsLocal })
$local = @($clients.Values | Where-Object { $_.IsLocal })

$window = $null
if ($startedAt) {
    $last = ($all | ForEach-Object { $_.LastSeen } | Where-Object { $_ } | Sort-Object | Select-Object -Last 1)
    if ($last) { $window = $last - $startedAt }
}

# A check passes only on a non-loopback client: that is the whole point of Stage B.
function Verdict([int]$remoteCount, [int]$localCount, [string]$missing) {
    if ($remoteCount -gt 0) { "通过（真实客户端）" }
    elseif ($localCount -gt 0) { "仅本地脚本" }
    else { $missing }
}

function Actions-Row([string[]]$names, [string]$label, [string]$missing) {
    $remoteCount = 0
    $localCount = 0
    foreach ($name in $names) {
        $remoteCount += Total-Of $remote $name
        $localCount += Total-Of $local $name
    }
    $detail = ($names | ForEach-Object { "$_=$(Total-Of $all $_)" }) -join ', '
    "| $label | $(Verdict $remoteCount $localCount $missing) | $detail |"
}

$lines = [System.Collections.Generic.List[string]]::new()
function Out-Line([string]$text = "") { $lines.Add($text) }

Out-Line "# 阶段 B 证据报告"
Out-Line ""
Out-Line "- 日志：``$logFile``"
Out-Line "- 范围：$scope$(if ($scopeLine -gt 0) { "（自第 $($scopeLine + 1) 行）" })"
Out-Line "- 服务启动：$(if ($startedAt) { $startedAt.ToString('yyyy-MM-dd HH:mm:ss') } else { '（未找到启动行）' })"
Out-Line "- 观察窗口：$(if ($window) { "$([math]::Round($window.TotalMinutes, 1)) 分钟" } else { '未知' })"
Out-Line "- 真实客户端（非本机地址）：$(if ($remote.Count) { ($remote.Ip | Sort-Object) -join ', ' } else { '无' })"
Out-Line "- 本机脚本（回环或本机网卡地址）：$(if ($local.Count) { ($local.Ip | Sort-Object) -join ', ' } else { '无' })"
Out-Line ""

if ($clients.Count -eq 0) {
    Out-Line "> 日志中没有任何控制端请求。这只能说明服务没被访问过，不能作为验收证据。"
    Out-Line ""
}

Out-Line "## 1. 按客户端汇总"
Out-Line ""
Out-Line "| 客户端 | 本机 | M-SEARCH | 已应答 | 描述 XML | SCPD | SUBSCRIBE | NOTIFY 2xx | NOTIFY 失败 | SOAP 动作 |"
Out-Line "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"
foreach ($client in ($all | Sort-Object Ip)) {
    $actions = ($client.Actions.Keys | Sort-Object | ForEach-Object { "$_×$($client.Actions[$_])" }) -join ', '
    Out-Line "| $($client.Ip) | $(if ($client.IsLocal) { '是' } else { '否' }) | $(Format-Count $client.Searches) | $(Format-Count $client.Answered) | $(Format-Count $client.Descriptions) | $(Format-Count $client.Scpds) | $(Format-Count $client.Subscribes) | $(Format-Count $client.NotifyOk) | $(Format-Count $client.NotifyFailed) | $actions |"
}
Out-Line ""

Out-Line "## 2. 阶段 B 验收项（按日志自动判定）"
Out-Line ""
Out-Line "判定规则：只有**非本机地址的客户端**留下的证据算「通过」；本机脚本只能得到「仅本地脚本」。"
Out-Line ""
Out-Line "| 检查 | 结果 | 依据（全部客户端合计） |"
Out-Line "| --- | --- | --- |"
Out-Line "| 设备发现 | $(Verdict (Sum-Of $remote 'Searches') (Sum-Of $local 'Searches') '未发生') | M-SEARCH=$(Sum-Of $all 'Searches')，已应答=$(Sum-Of $all 'Answered') |"
Out-Line "| 设备描述 / SCPD | $(Verdict ((Sum-Of $remote 'Descriptions') + (Sum-Of $remote 'Scpds')) ((Sum-Of $local 'Descriptions') + (Sum-Of $local 'Scpds')) '未发生') | description=$(Sum-Of $all 'Descriptions'), SCPD=$(Sum-Of $all 'Scpds') |"
Out-Line (Actions-Row @('SetAVTransportURI', 'Play') '投送（SetAVTransportURI+Play）' '未发生')
Out-Line (Actions-Row @('Pause') '暂停' '客户端未发送')
Out-Line (Actions-Row @('Stop') '停止' '客户端未发送')
Out-Line (Actions-Row @('Seek') 'Seek' '客户端未发送')
Out-Line (Actions-Row @('GetVolume', 'SetVolume') '音量' '客户端未发送')
Out-Line (Actions-Row @('GetMute', 'SetMute') '静音' '客户端未发送')
$remoteSubs = Sum-Of $remote 'Subscribes'
$remoteOk = Sum-Of $remote 'NotifyOk'
$remoteFailed = Sum-Of $remote 'NotifyFailed'
$localSubs = Sum-Of $local 'Subscribes'
$localOk = Sum-Of $local 'NotifyOk'
# A subscription that was accepted but whose NOTIFY never got a 2xx is a failure, not a pass: that
# is exactly the case a real client exposes and a unit test cannot.
$eventVerdict = if ($remoteSubs -gt 0 -and $remoteOk -gt 0) { '通过（真实客户端）' }
    elseif ($remoteSubs -gt 0) { "失败（SUBSCRIBE $remoteSubs 次，NOTIFY 2xx 为 0，失败 $remoteFailed 次）" }
    elseif ($localSubs -gt 0 -and $localOk -gt 0) { '仅本地脚本' }
    else { '未发生' }
Out-Line "| 事件订阅 + NOTIFY 200 | $eventVerdict | 真实客户端 SUBSCRIBE=$remoteSubs，NOTIFY 2xx=$remoteOk，失败=$remoteFailed；本机 SUBSCRIBE=$localSubs，NOTIFY 2xx=$localOk |"
Out-Line "| 网络变化恢复 | $(Verdict $networkChanges 0 '本次未发生网络变化') | 日志中 ``ssdp network change``=$networkChanges 次 |"
Out-Line ""

Out-Line "## 3. B4：客户端轮询行为"
Out-Line ""
$positionPolls = Total-Of $all 'GetPositionInfo'
$remotePolls = Total-Of $remote 'GetPositionInfo'
$localPolls = Total-Of $local 'GetPositionInfo'
Out-Line "- ``GetPositionInfo`` 总调用：$positionPolls（真实客户端 $remotePolls，本机脚本 $localPolls）"
if ($window -and $window.TotalSeconds -gt 0) {
    Out-Line "- 全窗口平均：$([math]::Round($positionPolls / $window.TotalSeconds, 2)) 次/秒（窗口 $([math]::Round($window.TotalSeconds, 0)) 秒）"
}
Out-Line "- 结论：$(if ($remotePolls -gt 0) { '真实客户端在轮询位置，不推送位置事件的决定与其行为一致。' } elseif ($localPolls -gt 0) { '只有本机脚本轮询了位置；真实客户端尚未留下轮询记录，B4 仍需真机验证。' } else { '本次没有任何位置轮询，无法判断客户端是否需要位置事件。' })"
Out-Line ""

Out-Line "## 4. 往返证据摘录"
Out-Line ""
if ($events.Count -eq 0) {
    Out-Line "（无 SUBSCRIBE / NOTIFY 记录）"
} else {
    Out-Line '```text'
    foreach ($event in ($events | Select-Object -First 40)) { Out-Line $event }
    Out-Line '```'
}
Out-Line ""

Out-Line "## 5. 判定边界"
Out-Line ""
Out-Line "- 本报告只依据服务日志，不包含手机截图或人耳确认。"
Out-Line "- 回环（127.0.0.1）客户端来自本机脚本，不能替代第三方控制端。"
Out-Line "- 未出现在日志中的操作只能记为「客户端未发送」，不能写成「渲染器不支持」。"
Out-Line "- 仍需人工确认：画面是否出现、声音是否可听、关闭 UI 后是否继续播放、窗口是否全屏置顶。"

$report = $lines -join [Environment]::NewLine

if ($OutputPath) {
    $directory = Split-Path -Parent $OutputPath
    if ($directory -and -not (Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Set-Content -LiteralPath $OutputPath -Value $report -Encoding utf8
    Write-Host "report written to $OutputPath"
}

$report
