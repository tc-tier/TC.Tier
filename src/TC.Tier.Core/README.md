# TC.Tier.Core

TC.Tier 的**内核基础设施**——生命周期骨架、并发原语、原生内存与 IO 底座。依赖 `TC.Tier.Contracts`。

## 包含

- **LifecycleBase / RecoveryBase**（统一生命周期骨架：Initialize → 恢复 → 就绪的固定模板）
- **并发原语**（LightEpoch / SpinRWLock / FairGate / MonitorScope / AsyncManualResetEvent 等）
- **原生内存**（AlignedMemoryManager / NativeArena / PinnedBufferPool——零 GC 热路径）
- **IO 底座**（TierFs 统一入口：local / memory / virtual 介质 + 文件系统抽象）
- **异步原语**（AsyncOperation / 优先级队列 / IsolatedTaskScheduler）

## 依赖

- TC.Tier.Contracts

## 文档

- 完整文档站：https://docs.mytzz.top/
- 生命周期模型：https://docs.mytzz.top/docs/src/TC.Tier.Core/docs/lifecycle.html
- 并发与锁：https://docs.mytzz.top/docs/src/TC.Tier.Core/docs/locking-and-epoch.html
