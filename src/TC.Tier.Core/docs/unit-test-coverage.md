# Core 单元测试覆盖标准与矩阵

> **标准（强制）**：`TC.Tier.Core` 的每一个组件都必须有完整的单元测试，**测试文件与源文件目录一一对应**
> （`src/TC.Tier.Core/Collections/X.cs` ↔ `tests/TC.Tier.Core.Tests/Collections/XTests.cs`）。
>
> 为什么（2026-08-14 事故教训）：**只要留给集成测试，问题就会被隐藏**——`LockWord`（SpinRWLock 前身）的 OR 置位 bug
> 单线程两次获取+两次释放即可捕获，却因为原语零单测被推到集成层，表现为"楔死/flaky"，
> 被误读为"压测不稳定"数月。原语 = 最高杠杆的测试投资点：一行实现的错误 × 所有上层依赖 = 系统性灾难。
>
> **并发 / 队列 / 锁是死锁高发区，特别注意**：
> 1. 并发原语必须有**契约测试**（计数语义、互斥、唤醒协议、无配对操作的绊线行为），不能只有功能测试；
> 2. 核心隐蔽场景必须有 **Debug 模式的验证与跟踪**（`#if DEBUG` 常设仪器，Release 零开销）——
>    SpinRWLock 的值示波器（最近 24 次原子操作环形记录 + 绊线携带历史）即为此范例；
> 3. **禁止用 SKIP 跳过并发测试**——跳过 = 把问题藏回集成层。并发测试 flaky 要修根因（轮询/事件对齐），
>    不是禁用。
> 4. **满套假红双保险（2026-08-20 三连假红后确立）**：①宿主层——测试项目各有一个
>    `TestHostThreadPool`（ModuleInitializer + `ThreadPool.SetMinThreads(64,64)`），消除并行
>    collection 打满池后 Task.Run <b>起跑</b>延迟秒级的注入节流（固定毫秒 Wait 假红的根因）；
>    ②用例层——对并行负载敏感的等待写 `SpinWait.SpinUntil` 轮询而非固定 <c>Wait(N)</c>（抗<b>完成慢</b>）。
>    新写并发测试两层的假设都要满足；生产代码禁止模仿①（掩盖真实饥饿是生产 bug）。

## 覆盖矩阵（2026-08-14 审计；2026-08-17 补录 IO/ 层与 AsyncPriorityQueueV3）

图例：✅ 完整契约测试 ｜ 🟡 部分覆盖（需加强） ｜ 🔴 无测试（**必修**） ｜ ⚪ 声明/P-Invoke/枚举，不适用单测（靠探针/使用方覆盖）

### Collections/（并发队列/缓存——死锁高发区）

| 源文件 | 测试（1:1 路径） | 状态 |
|---|---|---|
| AsyncPriorityQueue.cs | Collections/AsyncPriorityQueueTests.cs | ✅ **生产基线**（Route A marker 协议重写——直接引用+marker 节点+GC 回收；压力契约测试+DEBUG 链校验器+楔死看门狗。注：[lab/async-priority-queue-root-cause.md](lab/async-priority-queue-root-cause.md) 是 Route A **之前**旧实现的事故取证档案——解释 Route A 的由来，**与当前实现无关**） |
| AsyncPriorityQueueV2.cs | Collections/AsyncPriorityQueueV2Tests.cs | 🔬（实验线——2026-08-17 收敛为非生产，测试默认 Skip（标注含理由）；历史：18/19 稳定通过 + 零分配实证，残余竞态见 [lab/async-priority-queue-root-cause.md](lab/async-priority-queue-root-cause.md) §6） |
| AsyncPriorityQueueV3.cs | Collections/AsyncPriorityQueueV3Tests.cs | 🔬（实验线——原生 32B 槽位演进版，测试默认 Skip；见 [priority-queues.md](priority-queues.md)） |
| AsyncQueue.cs | Collections/AsyncQueueTests.cs | ✅ |
| BucketPriorityQueue.cs | Collections/BucketPriorityQueueTests.cs | ✅（多消费者 counting-semaphore 公平唤醒是引擎 worker 基石） |
| ClockCache.cs | Collections/ClockCacheTests.cs | ✅ |
| ClockCacheV2.cs | Collections/ClockCacheV2Tests.cs | ✅（组相联 CLOCK——miss 悬崖消除） |
| OverflowPool.cs | Collections/OverflowPoolTests.cs | ✅ |
| PinnedBufferPool.cs | Collections/PinnedBufferPoolTests.cs | ✅ |
| ShardLockWeakReference.cs | Collections/ShardLockWeakReferenceTests.cs | ✅（2026-08-14 补：弱语义/并发/清理） |
| SkipListPriorityQueue.cs | Collections/SkipListPriorityQueueTests.cs | ✅ |

### Primitives/（原语——最高杠杆）

| 源文件 | 测试 | 状态 |
|---|---|---|
| Atomic128.cs | Primitives/Atomic128Tests.cs | ✅ |
| **SpinRWLock.cs** | **Primitives/SpinRWLockTests.cs** | ✅（2026-08-20 自 LockWord 重构承接，v2 扩至 11 契约测试——OR 置位事故回归族 + 写偏向三契约【后到读者让位序号断言/pending 释放卫生】+ Try 变体两契约【TryExclusive 失败不挂闸】+ 无损伤绊线【触发后锁仍可用】） |
| **FairGate.cs** | **Primitives/FairGateTests.cs** | ✅（2026-08-20 下沉：4 契约测试——计数配对/唤醒活性/8 线程单槽长持窗口无饿死压测） |
| SpinLockScope.cs | Primitives/SpinLockScopeTests.cs | ✅（2026-08-14 补：互斥/异常释放/并发） |
| MicroTimer.cs | Primitives/MicroTimerTests.cs | ✅ |
| SectorAlignment.cs / AlignmentConst.cs | Primitives/SectorAlignmentTests.cs | ✅ |
| KeyComparer.cs / IKeyComparer.cs | Primitives/KeyComparerTests.cs | ✅ |
| UnifiedCrc.cs | Primitives/UnifiedCrcTests.cs | ✅ |
| ThrowHelper.cs | Primitives/ThrowHelperTests.cs | ✅ |
| AlignedMemoryManager.cs | Primitives/AlignedMemoryManagerTests.cs | ✅ |
| **NodeArena.cs** | **Primitives/NodeArenaTests.cs** | ✅（2026-08-23 自 Runtime/Structures 迁入归位——非托管分配原语本性归 Core；6 契约测试：8 对齐/指针恒稳跨块不搬移/块跨越/超块直通/并发 Alloc 无重叠/Dispose 幂等） |
| AsyncCountDown.cs | Primitives/AsyncCountDownTests.cs | ✅ |
| AsyncManualResetEvent.cs | Primitives/AsyncManualResetEventTests.cs | ✅ |
| **AsyncOperation.cs** | **Primitives/AsyncOperationTests.cs** | ✅（2026-08-18 新增：状态句柄原语——终态 CAS 单次/幂等、并发 Report 收敛、Wait 有界/超时/取消、WaitAsync 失败重抛、多 waiter 广播、完成先于等待零丢失、观察标记（泄漏绊线契约）24 例；设计见 [sync-async-bridge.md](sync-async-bridge.md)） |
| PooledValueTaskSource.cs | Primitives/PooledValueTaskSourceTests.cs | ✅ |
| NativeArena.cs | Primitives/NativeArenaTests.cs | ✅ |
| Utility.cs | Primitives/UtilityTests.cs | ✅ |

### Shared/（生命周期骨架）

| 源文件 | 测试 | 状态 |
|---|---|---|
| BackgroundWorkerLoop(.cs) | Shared/BackgroundWorkerLoopTests.cs + GenericTests.cs | ✅ |
| IsolatedTaskScheduler.cs | Shared/IsolatedTaskSchedulerTests.cs | ✅（性能基准另见 `benchmarks/TC.Tier.Core.Benchmarks/Shared/IsolatedTaskSchedulerBench.cs`，使用指南 [dedicated-task-scheduler.md](dedicated-task-scheduler.md)） |
| **SyncAsyncBridge.cs** | **Shared/SyncAsyncBridgeTests.cs** | ✅（2026-08-18 新增：同步-异步桥——Run 三轨（成功/失败/取消/超时现场）、Start 返回即 Running（可见性原则机器验证）、同池再入必抛+分池豁免、continuation 回流桥池私有线程、**池饿死回归**（打满公共池桥仍完成）、并发压测 14 例；设计见 [sync-async-bridge.md](sync-async-bridge.md)） |
| SyncBridgeOptions.cs | —（record，经 SyncAsyncBridgeTests 全路径使用） | ⚪ |
| IsolatedSchedulerOptions.cs | —（配置 record，经 IsolatedTaskSchedulerTests 全路径使用） | ⚪ |
| SchedulerRestartPolicy.cs | —（枚举，经 IsolatedTaskSchedulerTests Restart_* 覆盖） | ⚪ |
| LifecycleBase.cs | Shared/LifecycleBaseWorkerIntegrationTests.cs | 🟡（集成级；模板方法契约：CAS 门控/Dispose 顺序/异常聚合需加强） |
| RecoveryBase.cs | Shared/RecoverySkeletonTests.cs | ✅ |
| ResourceGroup.cs | Shared/ResourceGroupTests.cs | ✅ |
| InstanceTracker.cs | Shared/InstanceTrackerTests.cs | ✅（2026-08-14 补：注册/注销/子串过滤/弱跟踪） |
| CpuSampler.cs | Shared/CpuSamplerTests.cs | ✅（2026-08-14 数学重构：EMA 首样本标志/构造校验/Hub 折叠 + 可测性缝 ApplyEma/MapThrottleFactor，17 测试锁定契约） |
| ResourceInfo/ResourceOwnership/TrackedInstance/WorkerPriority | — | ⚪（record/枚举，经使用方覆盖） |

### Epochs/ ✅（2026-08-14 补：LightEpoch 协议绊线 8 测试 + 常设 Debug 示波器）

LightEpoch/FastThreadLocal/EpochProtectedVersionScheme/VersionSchemeStateMachine/VersionSchemeState 均有对应测试。
`LightEpochTests` 2026-08-14 补齐**协议违反绊线测试**（未 Resume 就 Bump/Suspend、跨线程 Suspend、保护区重入、嵌套 bump、Dispose 持保护、drain action 异常）——Debug 构建断言立即抛异常（消息含协议操作历史），Release 构建断言零开销不抛。

### Logging / Metrics / Observability / Tracing ✅/🟡

LoggerExtensions、NullLogger、Metrics、ObservabilityHub、Tracing 有测试；各 ObservabilityHub.*.cs 分视图由 ObservabilityHubTests 覆盖（🟡 维度视图逐个加强属后续）。

### NativeInterop/ ⚪（P/Invoke 声明层）

Kernel32/LibC 等为声明，不适用进程内单测；行为由探针测试覆盖（FileNativePunchHole/Preallocate/FileMetadataProbe/CompactPrototype）。

### IO/（文件 IO 原语层——[io.md](io.md)，2026-08-17 补录）

| 源文件 | 测试（1:1 路径） | 状态 |
|---|---|---|
| TierSpec.cs（spec 解析/规范形——P1 协议层；StorageNature/AccessMode 枚举随之覆盖） | IO/TierSpecTests.cs | ✅（表驱动 76 用例：四本性头/二级首段/local 路径域四形态/快捷档/参数封闭集/×介质合法性/规范形往返稳定） |
| TierFs.cs（工厂——动词契约/位置落定/两级注册表）+ FileSystemOptions.cs（基类四成员 Access/Label/QuotaBytes/Exclusive）+ VolumeInfo.cs（九成员自描述）+ Shared/AccessGate.cs（G2 包络共享执法） | IO/TierFsTests.cs（spec 全参数四介质真执法：三态+包络构造期/惰性配额写前拒/label 往返/exclusive 挂载持有与释放/VolumeInfo 自描述/动词契约/CWD 固化/fake 协议注册表） | ✅ |
| IFileSystem.cs / IFileHandle.cs（公开契约 + 能力协商） | IO/FileSystemContractTests.cs | ✅ |
| FileOpenOptions.cs（+ 枚举族 FileOpenMode/Access/Sharing/Hints、FileAdvise、MapAccess、FileLockMode） | IO/FileOpenOptionsTests.cs | ✅ |
| FileHandlePool.cs | IO/FileHandlePoolTests.cs | ✅（池归还协议 + MappedSection 间接涉及） |
| Disk/DiskFileHandle.cs | IO/Disk/DiskFileHandleTests.cs | ✅ |
| Disk/DiskFileSystem.cs | IO/Disk/DiskFileSystemTests.cs | ✅ |
| Disk/DiskMappedSection.cs / Mem/MemMappedSection.cs | —（经 FileSystemContractTests/FileHandlePoolTests 间接覆盖） | 🟡（映射生命周期 1:1 测试属后续） |
| Mem/MemFileHandle.cs / MemoryFileSystem.cs | IO/Mem/MemoryFileSystemTests.cs + MemoryFileSystemConcurrencyTests.cs | ✅（双分配模式 + 并发契约专测） |
| Testing/FaultInjectingFileSystem.cs | IO/Testing/FaultInjectingFileSystemTests.cs | ✅ |
| IObjectStore.cs（接口本体 + 流式 List DIM）/ IMultipartUpload.cs（multipart 族伴生：接口 + UploadPartResult + MultipartUploadSession）/ ObjectMetadata.cs / ObjectInfo.cs（含 ObjectEntry）/ CopyMetadata.cs / ObjectKeyValidator.cs / ObjectStoreExtensions.cs / ObjectStoreCapabilities.cs（含条件结构体）——一类型族一文件（B3.0② + 增补拆分 159caea2） | IO/ObjectStoreContractTypesTests.cs（类型校验：元数据键集/2KB 超限/键长字节口径/能力位冻结——跨契约族类型级测试） | ✅ |
| Testing/MemoryObjectStore.cs（对象层内存替身——B3.1） | IO/ObjectStoreContractTests.cs（参数化契约平权套，public 跨工程）+ IO/MemoryObjectStoreTests.cs（仪器/并发抢建/会话生命周期） | ✅ |
| Remote/RemoteFileSystem.cs / RemoteFileHandle.cs / StagingBuffer.cs / RemoteFileSystemOptions.cs（B3.3/B3.4 + 增补 P1-P5：孤儿扫描/增量 Flush/洞元数据/内存 spill） | IO/Remote/RemoteFileSystemTests.cs（§7.1 全项：staging/H1 回填/H2 池协议/M4 游标/H3 打洞/延迟加载计数/路径穿越/CopyRange 双路径/元数据/L5 差异专项/恢复/spill/孤儿清理/增量 Flush 计数断言/洞读零 GET/SpillToMemory）+ IO/Remote/RemoteFencingTests.cs（互斥/接管/token 防误删/降级） | ✅ |
| FileIOException.cs / IOError.cs / VolumeInfo.cs / FileSystemCapabilities.cs | — | ⚪（异常/枚举/记录，经使用方覆盖） |
| Shared/（internal：HandlePoolAttachment/IOExceptionMapper/PathValidator/PathPattern/AppendCursor/IPoolAttachable） | PathValidator 单测在 IO/FileOpenOptionsTests.cs（ValidateRelative 规则集）；PathPattern 1:1 在 IO/Shared/PathPatternTests.cs；其余经 FileHandlePoolTests/Disk 测试间接覆盖 | 🟡→🟢（PathValidator/PathPattern 已专测；HandlePoolAttachment 的 Debug 绊线在源内常设） |

### TC.Tier.Core.IO.S3（S3 兼容对象层——B3.2，独立程序集）

| 源文件 | 测试（1:1 路径，tests/TC.Tier.Core.IO.S3.Tests/） | 状态 |
|---|---|---|
| SigV4.cs | SigV4GoldenVectorTests.cs（AWS 官方黄金向量逐字节 + RFC3986 编码器 + canonical query 排序） | ✅ |
| S3ObjectStore.cs / S3Xml.cs | S3ObjectStoreFakeServerTests.cs（进程内假 S3 服务器：服务端重算签名验证 + 真实 XML/分页/条件/multipart/503 注入） | ✅ |
| S3ObjectStore.cs（真协议终验） | S3ObjectStoreMinioContractTests.cs（继承 ObjectStoreContractTests，环境门控 TIER_S3_TEST_ENDPOINT/TIER_S3_TEST_VHOST——`scripts/run-minio-tests.sh`） | ✅（MinIO 25/25；**真实腾讯 COS 28/28 全功能面**——vhost 寻址，含会话治理/流式 List/chunked PUT） |
| ChunkedSignedStream.cs / SigV4 chunked 链（增补 P4） | ChunkedSignedStream_ProducesFramedBytes + PutChunked_*（假服务器链式签名独立重算） | ✅ |
| S3ClientOptions.cs / 凭证源 | 经上述测试构造覆盖（校验器 + Host 头派生在黄金向量/假服务器断言中触达） | 🟡 |
| TierFsS3Binding.cs（s3 协议构建器 + ModuleInitializer 自动注册——P1-3） | TierFsS3BindingTests.cs | ✅（自动注册/组装映射（prefix/spill 二态）/cred env: 解析（缺失/畸形/缺席 fail-fast）/阶段号指针——Create 零网络无需真实端点） |

## 维护规则

1. 新增 Core 源文件 → 同 PR 必须带同路径测试文件，否则不许合入。
2. 并发/锁/队列组件的测试必须含：契约语义（计数/互斥/唤醒）+ 无配对操作绊线 + Debug 跟踪设施。
3. 本矩阵随覆盖变化更新（审计命令：比对 `src/TC.Tier.Core/**` 与 `tests/TC.Tier.Core.Tests/**` 的文件名集合）。
