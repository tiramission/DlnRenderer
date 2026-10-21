# 阶段 B 证据报告

- 日志：`C:\Users\jaign\AppData\Local\DlnRenderer\logs\service-20261021.log`
- 范围：最近一次服务会话（自第 1859 行）
- 服务启动：2026-10-21 18:09:20
- 观察窗口：1.2 分钟
- 真实客户端（非本机地址）：192.168.1.11, 192.168.1.12, 192.168.1.72
- 本机脚本（回环或本机网卡地址）：无

## 1. 按客户端汇总

| 客户端 | 本机 | M-SEARCH | 已应答 | 描述 XML | SCPD | SUBSCRIBE | NOTIFY 2xx | NOTIFY 失败 | SOAP 动作 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 192.168.1.11 | 否 | 5 | — | — | — | — | — | — |  |
| 192.168.1.12 | 否 | 8 | 2 | — | — | — | — | — |  |
| 192.168.1.72 | 否 | 60 | 15 | 1 | 3 | 3 | — | 3 | GetPositionInfo×22, GetProtocolInfo×1, GetTransportInfo×28, Pause×2, Play×3, Seek×1, SetAVTransportURI×1, Stop×1 |

## 2. 阶段 B 验收项（按日志自动判定）

判定规则：只有**非本机地址的客户端**留下的证据算「通过」；本机脚本只能得到「仅本地脚本」。

| 检查 | 结果 | 依据（全部客户端合计） |
| --- | --- | --- |
| 设备发现 | 通过（真实客户端） | M-SEARCH=73，已应答=17 |
| 设备描述 / SCPD | 通过（真实客户端） | description=1, SCPD=3 |
| 投送（SetAVTransportURI+Play） | 通过（真实客户端） | SetAVTransportURI=1, Play=3 |
| 暂停 | 通过（真实客户端） | Pause=2 |
| 停止 | 通过（真实客户端） | Stop=1 |
| Seek | 通过（真实客户端） | Seek=1 |
| 音量 | 客户端未发送 | GetVolume=0, SetVolume=0 |
| 静音 | 客户端未发送 | GetMute=0, SetMute=0 |
| 事件订阅 + NOTIFY 200 | 失败（SUBSCRIBE 3 次，NOTIFY 2xx 为 0，失败 3 次） | 真实客户端 SUBSCRIBE=3，NOTIFY 2xx=0，失败=3；本机 SUBSCRIBE=0，NOTIFY 2xx=0 |
| 网络变化恢复 | 本次未发生网络变化 | 日志中 `ssdp network change`=0 次 |

## 3. B4：客户端轮询行为

- `GetPositionInfo` 总调用：22（真实客户端 22，本机脚本 0）
- 全窗口平均：0.31 次/秒（窗口 70 秒）
- 结论：真实客户端在轮询位置，不推送位置事件的决定与其行为一致。

## 4. 往返证据摘录

```text
18:09:29 SUBSCRIBE RenderingControl from 192.168.1.72 accepted -> http://192.168.1.72:58645/dev/023cc902-a953-4ef3-afa4-af8d256dcc74/svc/upnp-org/RenderingControl/event/cb
18:09:31 NOTIFY failed (HTTP 412) sid=uuid:0903b795-baac-4b15-be26-8a0329cc45bb
18:10:04 SUBSCRIBE RenderingControl from 192.168.1.72 accepted -> http://192.168.1.72:58645/dev/023cc902-a953-4ef3-afa4-af8d256dcc74/svc/upnp-org/RenderingControl/event/cb
18:10:06 NOTIFY failed (HTTP 412) sid=uuid:f165e9df-c6c1-4fc3-aa77-7b7e82e290bc
18:10:19 SUBSCRIBE RenderingControl from 192.168.1.72 accepted -> http://192.168.1.72:58645/dev/023cc902-a953-4ef3-afa4-af8d256dcc74/svc/upnp-org/RenderingControl/event/cb
18:10:21 NOTIFY failed (HTTP 412) sid=uuid:65cf707d-6a02-4739-acf5-712b021a4bb2
```

## 5. 判定边界

- 本报告只依据服务日志，不包含手机截图或人耳确认。
- 回环（127.0.0.1）客户端来自本机脚本，不能替代第三方控制端。
- 未出现在日志中的操作只能记为「客户端未发送」，不能写成「渲染器不支持」。
- 仍需人工确认：画面是否出现、声音是否可听、关闭 UI 后是否继续播放、窗口是否全屏置顶。
