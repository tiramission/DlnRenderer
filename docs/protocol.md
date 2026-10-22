# DLNA 协议面与客户端兼容性

本文记录 DlnRenderer 对外暴露的 DLNA/UPnP 协议面，以及每个偏离"教科书实现"的地方**为什么**要这样写。
这些取舍不是风格偏好：每一条都对应一个真实控制端（尤其是抖音、腾讯视频这类国产投屏入口）在真机上
出现过的失败，参考实现是同机验证通过的 MiniCast（Python + cherrypy + libmpv）。

改动协议相关代码前请先读这一页，否则很容易把这些"多余"的宽容当成可以清理的冗余。

## 1. 设备发现（SSDP）

| 项 | 取值 | 原因 |
| --- | --- | --- |
| 组播地址/端口 | `239.255.255.250:1900` | 标准 |
| 绑定的 socket | `0.0.0.0:1900` + 每个网卡 `IP_ADD_MEMBERSHIP` | 单一通配 socket 才能与 SSDPSRV、其他渲染器共存 |
| 应答的 ST | `upnp:rootdevice`、`uuid:<udn>`、`MediaRenderer:1`、`AVTransport:1`、`RenderingControl:1`、`ConnectionManager:1` | 控制端有的按设备类型找，有的直接按服务类型找 |
| `ssdp:all` | 逐个 ST 各答一包 | 与参考实现一致 |
| `CACHE-CONTROL` | `max-age=66` | 与参考实现一致；短保活让崩溃/休眠的设备一分钟内从列表消失 |
| 保活间隔 | 30 s | 必须显著小于 max-age |
| `SERVER` | `Windows/10 UPnP/1.0 DlnRenderer/<ver>` | 部分客户端只据此判断"是不是设备" |

重复 M-SEARCH 去重窗口为 250 ms，只用于吞掉同一报文从多个已 join 网卡重复送达的情况，远小于客户端重试间隔。

## 2. 设备描述（`/description.xml`）

- 设备元素使用**默认命名空间**，不加 `d:` 前缀；`X_DLNADOC` 单独声明 `xmlns:dlna`。
- 字段集与参考实现一致：`deviceType`/`UDN`/`friendlyName`/`manufacturer`/`manufacturerURL`/
  `modelDescription`/`modelName`/`modelNumber`/`modelURL`/`serialNumber`/`X_DLNADOC`/`serviceList`。
  `serialNumber` 这类"可有可无"的字段一旦缺失，按固定字段序解析的客户端会直接丢掉这台设备。
- `controlURL`/`eventSubURL`/`SCPDURL` 均为**相对路径**（不以 `/` 开头），因此文档在任何网卡上
  访问都成立，多网卡机器不需要按接口重写描述。

## 3. 服务描述（SCPD）

- 根元素写成 `<scpd xmlns="urn:schemas-upnp-org:service-1-0">`，**不加 `s:` 前缀**。
  用前缀同样合法 XML，但按标签名遍历（而不是按命名空间解析）的客户端会找不到任何 action，
  于是"设备能发现、但所有投屏菜单里都没有它"。
- action 表按参考实现补全，包含本渲染器只做 no-op 的 `SetPlayMode`、`Next`、`Previous`。
  客户端是**读表**决定要不要发某个请求的，表里没有就当设备不会。
- 未实现的能力不声明：`SetNextAVTransportURI`（无播放队列）不在表内。
- `TransportState` 的允许值包含 `NO_MEDIA_PRESENT`；`A_ARG_TYPE_SeekMode` 含 `REL_TIME`/`ABS_TIME`/`TRACK_NR`。
- `ConnectionManager` 的 `SourceProtocolInfo`/`SinkProtocolInfo`/`CurrentConnectionIDs` 声明为
  `sendEvents="yes"`，订阅者能在首帧事件里直接拿到 sink 列表。

## 4. SOAP 控制

### 响应形状

```xml
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="...">
  <s:Body>
    <u:PlayResponse xmlns:u="urn:schemas-upnp-org:service:AVTransport:1">
      <InstanceID>0</InstanceID>
    </u:PlayResponse>
  </s:Body>
</s:Envelope>
```

action 名通过 `u:` 前缀限定，**出参不带命名空间**。给响应元素加默认 `xmlns`（让出参也进服务命名空间）
同样符合设备架构，但按标签名取出参的客户端会读到空值并判定调用失败。

### 请求解析

- `SOAPAction` 头只用于日志，**不参与校验**。真实客户端会带着引号、尾随空白、不同服务版本甚至是空引号发过来；
  按它报 402 只会让设备看起来坏了。真正的 action 以 `Body` 里的元素为准。
- 只接受绝对 `http`/`https` URI（`SetAVTransportURI`）；其他 scheme 报 402。这是安全边界，不是兼容性取舍。

### 传输类 action 的宽容度

| 情况 | 做法 | 原因 |
| --- | --- | --- |
| 未设置媒体时 `Play`/`Pause`/`Seek` | 接受，记日志，不动作 | 客户端会在 `SetAVTransportURI` 之前恢复进度、续播上次会话；报 701 等于让手机立刻显示投屏失败 |
| `SetAVTransportURI` 加载失败 | 接受，记日志 | 媒体服务器可能还没就绪；URI 已保存，客户端可以再 `Play` |
| `Seek` 目标超过时长 | 夹到时长 | 直播流/仍在探测的媒体报短时长，客户端又常跳到片尾；报 601 会中断投屏 |
| `Seek` 的 `Unit=TRACK_NR` | 接受，不动作 | 单曲队列，任意轨号都是当前轨 |
| `Play` 的 `Speed` 非 `1` | 按 1x 播放 | 客户端记的是上一台设备的倍速 |
| `Next`/`Previous`/`SetPlayMode` | 接受，不动作 | 单条目队列；但请求本身不该失败 |

**不返回虚假状态**：这些宽容只作用在"命令"上，所有 `Get*` 的返回值仍然来自播放器实时快照。
命令被执行了吗、播放器在做什么，仍然如实反映在 `TransportState`/`TransportStatus`/位置信息里。

## 5. GENA 事件

- `AVTransport`/`RenderingControl` 用 `LastChange` 承载事件；`ConnectionManager` 用直接属性。
- `LastChange` 内层 `<Event>` 以事件命名空间作为**默认命名空间**，变量不带前缀
  （`<TransportState val="PLAYING"/>`）。写成 `<m:TransportState>` 会被按标签名读取的客户端跳过。
- `AVTransport` 事件变量集：`TransportState`、`TransportStatus`、`CurrentMediaDuration`、
  `CurrentTrackDuration`、`CurrentTrack`、`NumberOfTracks`。URI 不在事件里——那是
  `SetAVTransportURI` 已经发过的，需要时客户端调 `GetMediaInfo`。
- **首帧事件必须在 `SUBSCRIBE` 的 200 响应写完之后才发出。** 发早了客户端还不认识 SID，会回
  `412 Precondition Failed` 并丢弃刚建立的订阅（实测日志里就是这个现象）。实现上用
  `DlnHttpResponse.AfterSend` 钩子，由 HTTP 层在响应 flush 之后触发。
- **同一订阅的 NOTIFY 必须串行，SEQ 严格递增。** 并发投递会让慢速回调先收到 SEQ 5 再收到 SEQ 3，
  客户端按 SEQ 排序/去重后等于收不到事件。每个订阅有一个 pump：一次只发一条，积压时只保留最新状态
  （已过时的中间状态比不发更糟）。
- 未续订/被移除的订阅，pump 会先核对注册表再发送，避免给已取消的订阅"复活"事件。

## 6. 进度控制（Seek）

进度条是投屏里被操作最多的控件，也是失败表现得最明显的地方：seek 一旦变慢或报错，遥控器上的滑块
看起来就是"坏"的，而节目还在播。

规则：

- **`Seek` 立即返回。** 绝不为了执行 seek 去同步等待一次媒体重载：重载要走网络，慢的能到十几秒，
  客户端早就放弃了，而且拖动过程中每一步都会再排一次重载。
- **播放器空闲时把目标记下来。** 片子播完后 mpv 处于 idle，此时 seek 需要先重新 `loadfile`。渲染器
  记录目标位置并在后台重载，加载完成后立刻应用；如果随后来了 `Play`，则由 `Play` 的重载路径应用，
  保证从目标位置开始播。
- **播放器拒绝 seek 不算协议错误。** 例如 `Stop` 与 `Seek` 竞争导致 mpv 已 idle：记日志、转后台路径，
  不返回 SOAP fault。滑块已经移动了，报错只会让它弹回去并禁用。
- **越界夹取**、`TRACK_NR` 忽略、未知 `Unit` 按时间处理（见 §4）。
- **`Target` 同时接受 `H+:MM:SS` 和裸秒数。** 规范只定义前者，但按偏移量算的客户端会发后者，mpv 也接受。

### mpv 的 pause 陷阱

`loadfile` 会重置 `pause` 属性。以"加载后暂停"为意图调用 `loadfile`，实际会得到一个正在播放或者
卡在 buffering 的播放器：文件已经打开，但核心没有开始解码，状态既不是 Playing 也不是 Paused。
所有等待"加载完成"的调用方（重载、seek、Play）都会因此超时。

实现上在 `FILE_LOADED` 里重新应用一次暂停意图（`MpvPlayer._desiredPause`），并且 `Play` 会把该意图
改成"播放"，这样"先 `SetAVTransportURI` 再立刻 `Play`"的客户端不会被迟到的暂停打断。

## 7. 播放器参数（与协议面的耦合）

- `image-display-duration=inf`：抖音等客户端的图文/照片投屏是一张一张推 `.jpg`。mpv 默认整秒后结束
  一张静态图，于是 `Play` 之后立刻回落 idle，手机侧判定投屏失败。参考实现把图片时长钉成无限。
- `keep-open=no`、`idle=yes`：与参考实现一致，播放结束后回到 idle 并如实上报状态。

## 8. 与参考实现（Macast / MiniCast）的差异

`Macast` 是上游（xfangfang/Macast），`MiniCast` 是它的分支，两者协议面基本一致；本项目的对齐基线与
真机验证都以这条线为准。以下是**有意不跟随**或**有意加强**的地方：

| 参考实现 | 本实现 | 原因 |
| --- | --- | --- |
| RenderingControl 事件里也用 `AVT` 命名空间 | 用 `RCS` 命名空间 | 参考实现是笔误，严格解析器会拒收 |
| 所有失败都返回 200 | 未实现 action 返回 401，非法参数返回 402 | 保持可诊断性，只是不再因时序问题报错 |
| `TIMEOUT` 头返回裸秒数 | 返回 `Second-N` | 规范形式，客户端两者都接受 |
| seek 直接把字符串丢给 mpv，失败静默 | 解析 → 夹取 → 空闲时延后执行 → 失败只记日志 | 可测、可日志化；顺带修掉"重载卡死" |
| `A_ARG_TYPE_SeekMode` 只有 REL_TIME / TRACK_NR | 加 ABS_TIME | 我们确实支持绝对时间 seek |
| 位置状态由渲染器自己维护并回写 | 每次请求读播放器实时快照 | 不会出现"状态说 5:00、实际在 0:30" |
| 单线程串行处理所有请求 | 每请求一个任务，但同订阅事件串行 | 事件顺序必须保证，SOAP 不该互相阻塞 |

## 9. 真机排查顺序

1. `ssdp M-SEARCH received` + `ssdp answered`：设备是否被发现，LOCATION 是否是手机所在网段的地址。
2. `http GET /description.xml` / `scpd/*.xml`：客户端是否真的取了描述，取到的是不是 200。
3. `soap action <Name>`：客户端走到哪一步。缺 `SetAVTransportURI` 说明它还在协商。
4. `soap fault <code>`：401 未实现、402 参数（701/716 不再因时序问题上报）。
5. `subscription created` → `notify ... seq 0 ... 200`：事件通道是否健康。出现 `412` 说明首帧发早了，
   出现 `timed out` 说明串行 pump 被慢回调阻塞（可调 `NotifyRequestTimeout`）。
6. `seek deferred until the media is reloaded` / `seek applied after reload`：进度控制是否走的是
   延后路径；若一直只有前者，说明媒体重载本身失败（看 `reload attempt N did not settle`）。

