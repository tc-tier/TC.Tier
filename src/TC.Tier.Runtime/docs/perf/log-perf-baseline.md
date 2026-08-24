# Log（EntryLog）性能基线

> 现行版 Log 独有压测报表（2026-08-24 首建——旧架构基准套件 §4 Log 数字出自旧 Structures/{Index,Log,Ring}
> 40 文件套件（旧 API 形态），2026-08-22 随缺口收尾清理，已过时。注：Log 非泛型——entry 为原始字节流，
> 无 key 类型参数）。数字实测于本机
> （i5-12400 / .NET 8.0.30 / x64 RyuJit AVX2），介质=mem（写路径纯 CPU 口径，引擎 IO 噪声为零；
> 磁盘介质另行对照）。

## 配置

- EntryLog（PageSize=8K / Managed meta / 组提交默认策略——字节量/条数/时间间隔三维阈值自动落盘）
- entry 64B；count 50 万；批变体 512/批
- 运行：
  ```bash
  dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --log-write-probe 500000 64
  ```

## 数字

| 变体 | 吞吐 | 备注 |
|---|---:|---|
| single（单条 Append） | **1.34M op/s（~745ns/op）** | 2026-08-24 并发安全化（写路径粗锁）：相对旧无锁版 1.38M **-3%**（Monitor 快速路径 ~20ns/entry）——单写者语义完全保持（水位零改动） |
| batch（BeginAppendBatch 512） | **5.49M op/s（~182ns/op）** | 批持锁（Begin/Dispose 各一次）——批内本地游标零锁——与旧版 5.4M **持平** |
| concurrent（8 写者并发 Append） | **~2.1M op/s** | 锁竞争下吞吐（正确性确定——串行语义）；并发价值 = API 契约安全（public Append 多线程调用不损坏），非吞吐扩展 |
| recovery（50 万条重开） | **9 ms** | 页帧扫盘 + meta 水位恢复 |

## 对照（同机同形，mem）

| 项 | Log | Ring | 判定 |
|---|---:|---:|---|
| 单条写 | 720ns | 906ns（lock 分配） | Log 略快（单写者无锁） |
| 批量写 | 5.40M op/s | 6.07M op/s（8 写者批窗口） | 同量级 |
| 恢复 | 9ms/50 万 | 45-60ms 底盘 | Log 快一个量级（无索引重建） |

## 结论

- **并发安全已交付（2026-08-24 终案——写路径粗锁）**：public Append/Flush/TruncateSuffix 多线程并发调用
  安全（串行化——不损坏）；API 契约对齐 Ring。代价 = 单写者 -3%（Monitor 快速路径）、批持平
  （批持锁摊薄）——**最小损失方案**。
- **模型演进复盘（用户批评驱动的收敛）**：窗口模型（双页 ping-pong 强套并发——页状态机/原子段分配/
  换页仲裁）flaky 未定位 + 负优化（-29%/-64%）弃；每写者页缓冲（写者零共享）适配面大（水位/
  TailAddress/TruncateSuffix 是单写者语义深度耦合——并发化需全面语义改造）弃；粗锁（单写者语义保持、
  水位零改动、零新竞态面）为终案——**并发安全以正确性确定为前提，不以吞吐扩展为目标**。
- 全量 855 绿 ×6 连过（零 flaky）。

## 维护

- 改动 Log 写路径（Append/页缓冲/flush/组提交）后必须重跑本探针并更新数字（性能论断须实测教义）。
