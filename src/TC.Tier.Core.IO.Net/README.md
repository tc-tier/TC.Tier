# TC.Tier.Core.IO.Net

TC.Tier 文件系统层的**根空间镜像 TCP 流式收发**——把本地 TierFs 根空间（TCA1 结构化流）经 TCP 推送到对端落盘，带逐帧 CRC 与尾对账。Core.IO 镜像能力（`TC.Tier.Core` Image）的网络发射端。

## 能力

- **TIN1 协议**：握手（magic/版本/mode）→ TCA1 载荷 → 回执帧（帧数/字节/聚合 CRC/状态）
- **双向对称**：同一 TCP 连接，发起端既可 `Send`（采集推送）也可 `ReceiveTo`（接收落盘）——对端运行互补方法
- **端到端确认**：TCA1 逐帧 CRC + 尾对账之上，接收端回执回传摘要——发送端确认对端落盘一致（不符抛异常）
- **模式可选**：Structural（默认，跨介质可转、逐帧校验）∥ Raw（裸字节流，对接外部 dd/nc——中断即报废，显式选择）

## 快速开始

```csharp
using TC.Tier.Core.IO.Net;

// 接收端：监听端口并阻塞至传送完成（对端 Send 回执对账一致才返回）
var recv = NetworkImageTransfer.ReceiveTo(targetFs, port, ct: ct);

// 发送端：采集本地根空间推送，阻塞至对端回执对账一致
var result = NetworkImageTransfer.Send(sourceFs, host, port);
Console.WriteLine($"verified={result.Verified} bytes={result.RawBytes}");
```

## 依赖

- TC.Tier.Core（IFileSystem/镜像采集）

## 状态

Beta——v1 边界：单连接单传送（无续传/多路复用，帧级断点续传为后续增强）。
