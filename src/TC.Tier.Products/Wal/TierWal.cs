using System.Runtime.CompilerServices;
using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Meta;
using TC.Tier.Contracts.Structures;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Core.Resources;
using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Structures.Snapshot;
using TC.Tier.Runtime.Storage;

using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL——协议中立 WAL 产品（raft/p2p 的存储中线）实现类。
/// <para>★ 心智模型（设计决策 2026-08-25）：<b>地址是事实</b>——entry 地址只在 Append 返回值记录，
///   绝不解析/推导逻辑地址结构（重启后段几何可能变，推导即错）；<b>不记录段</b>——段是底层的概念，
///   上层地址空间无限；index（long）= 顺序值，一条 entry 一个 index 一个地址（单调一一对应）。</para>
/// <para>★ TierWAL 自己的三段式（每层在自己的槽位定义自己的格式）：</para>
/// <para> - 头部 = opaque 容器（<see cref="WalOpaqueLayout"/>）：只记 [maxIndex→addr][minIndex→addr]
///   + raft 元数据预留区——搭 Meta 边车随显式提交原子落盘（CRC 完整性由底层 Meta 兜底）</para>
/// <para> - 数据 = EntryLog 公共方法（Append/OpenCursor/Truncate*/SetOpaqueMeta）——TierWAL 不碰底层帧结构</para>
/// <para> - 尾部/校验 = 底层 Meta 的原子持久化 + CRC32C（TierWAL 零机制知识）</para>
/// <para>★ 生命周期 = <see cref="LifecycleBase{THints}"/> 标准模板（恢复状态机/进度/取消/资源组）——
///   恢复核心 = <see cref="TierWalRecovery"/>（RecoveryBase 派生：层间 join EntryLog + opaque 解析
///   + 统一扫描重建锚点）。</para>
/// <para>★ 定位（给定 index）：内存稀疏锚点二分（append 时每 <see cref="AnchorInterval"/> 条记一个
///   [index→addr]，零 IO）→ 从锚点地址顺序扫帧（magic 有效帧）数到目标 → 有效停止。
///   恢复时从头顺序扫一遍重建锚点（= raft 恢复重放本身，不额外成本）。</para>
/// <para>★ 双水位：<see cref="AllocatedIndex"/>（内存计数，含攒批窗口）/ <see cref="PersistedIndex"/>
///   （最后显式提交水位——raft 语义的 last persisted index 可信下限）。</para>
/// </summary>
public sealed class TierWal : LifecycleBase<WalRecoveryHints>, ITierWal
{
    /// <summary>内存稀疏锚点间隔——每 N 条一个 [index→addr]（二分定位 O(log 锚点数) + 顺序扫 ≤N 帧）。</summary>
    private const int AnchorInterval = 1024;

    private readonly EntryLog _log;
    private readonly TierWalOptions _options;
    private readonly ILogger? _logger;
    private readonly LogRecoveryHints _logHints;   // ★ 构造期收的恢复 hints（OnInitializeBegin 透传 EntryLog）
    private readonly IncrementalSnapshot _snapshot;     // ★ 镜像快照部件（第三部件——本地存储恒有，raft 冷启动载入）
    private readonly IAsyncTransferPersistence? _snapshotPersistence;   // ★ 传输注入面（Export/Import——未注入回落抛）

    // ═══ index 簿记（写路径单写者串行——EntryLog 写锁语义保持；读路径经 _stateLock 快照）═══
    private readonly object _stateLock = new();
    private long _allocatedIndex;        // 已分配尾 index（含未持久化缓冲——raft 攒批窗口）
    private long _persistedIndex;        // 最后显式提交水位（opaque TailIndex，raft 可应答下限）
    private long _headIndex;             // 头截断边界（第一条存活 entry 的 index；空 WAL = 0）
    private LogicalAddress _headAddress; // 头截断边界地址（第一条存活 entry 起点）
    private LogicalAddress _tailAddress; // 最后一条已分配 entry 的起点（opaque TailAddress）
    private byte[]? _raftMeta;           // raft 元数据预留区内存副本（null = 未写）
    private byte[]? _lastStaged;         // 上次实际 SetOpaqueMeta 的容器字节（_stateLock 内读——内容去重基准）
    private long _snapshotIndex;         // 快照覆盖点 N₀（快照后/导入后）
    private long _snapshotHeadIndex;     // 快照覆盖起点（镜像帧流第一条 index——重建 index 对齐）
    private TaskCompletionSource _persistedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);   // ★ 提交信号（显式 CommitAsync 完成后 Set）
    private bool _autoCommitPending;     // ★ OnAppended 内自动提交先于 RecordAppended——persisted 补推标记

    /// <summary>内存稀疏锚点（升序）：(index, 该条 entry 起点地址)——二分定位用。</summary>
    private readonly List<(long Index, LogicalAddress Address)> _anchors = [];

    /// <summary>★ 尾部 (index→addr) 环表（热读直达——spec-10 D0.5 第三层根因）：复制/apply 读皆
    /// 尾部形态（nextIndex/applied+1 距尾 ≤ 批窗口），锚点定位（AnchorInterval 间隔二分）之后仍需
    /// 从锚点跳过扫描逐条付账（实测 ~0.5µs/条，尾条读最多千条）；环表覆盖最近 2048 条，命中即
    /// 精确直达（skip=0）。写侧 <see cref="RecordAppended"/> 每 entry 一次槽存（_stateLock 内零额外
    /// 同步）；自校验 = 槽 Index 匹配才命中（回绕陈旧槽 Index 不匹配自然落锚点；截尾后被删 index
    /// 不可达——读侧先拒 startIndex &gt; allocated；截尾后重 append 同 index 自然覆写新地址）。
    /// 导入重锚全量清表（<see cref="ReAnchorMainDataAsync"/>——旧地址全部作废）。</summary>
    private readonly (long Index, LogicalAddress Address)[] _tailMap = new (long, LogicalAddress)[TailMapCapacity];
    private const int TailMapCapacity = 2048;   // 2× AnchorInterval——写侧环覆写自回收
    private const int TailMapMask = TailMapCapacity - 1;

    /// <summary>构造 internal——外部只能经 <see cref="TierWalBuilder.StartAsync"/>。</summary>
    /// <param name="log">已配置的 EntryLog（底层追加/截断/游标/提交面——TierWAL 不碰帧结构）。</param>
    /// <param name="fs">文件系统抽象（介质面——镜像快照部件用）。</param>
    /// <param name="options">TierWAL 选项（名/容量/提交策略/hints 透传）。</param>
    /// <param name="hints">恢复 hints（地址级已知水位——透传 EntryLog）。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    /// <param name="snapshotPersistence">快照传输面（Export/Import——未注入回落抛；单机本地快照）。</param>
    internal TierWal(EntryLog log, IFileSystem fs, TierWalOptions options, WalRecoveryHints hints, ILogger? logger,
        IAsyncTransferPersistence? snapshotPersistence = null)
        : base(recovery: null, logger)
    {
        _log = log;
        _options = options;
        _logger = logger;
        _logHints = ToLogHints(hints);
        // ★ 镜像快照部件（第三部件）——本地存储恒为 IncrementalSnapshot（raft 冷启动必须本地载入）：
        //   SnapshotAsync 写它（增量段——方案 A 落地面）；Export/Import 经注入传输面（默认无注入 = 单机不导出）。
        //   ★ hints 随主引擎（DIO 最优默认——镜像写同为顺序大块形态，见 TierWalOptions.Hints 文档）。
        //   ★ 旋钮透传（#505）：快照几何/Optimization/耐久化泵周期/时钟随 Options——与主引擎同源。
        _snapshot = new IncrementalSnapshot(fs, new IncrementalSnapshotSettings(
            new StorageEngineOptions($"{options.WalName}.snapshot", options.SnapshotSegmentGrowthLimit,
                enableSegmentation: true, preallocateFile: false)
                .WithHints(options.Hints)
                .WithOptimization(options.Optimization)
                .WithMetaTupleFlushInterval(options.MetaTupleFlushInterval)
                .WithClock(options.Clock)
                .WithWorkerScheduler(options.WorkerSchedulerOptions))
        {
            MetaPolicyKind = MetaPolicyKind.Managed,
            MetaOpaqueBytes = options.MetaOpaqueBytes,
            // ★ worker 调度器共享注入随 Options——快照引擎（+快照 meta）与主日志侧同组线程
            WorkerScheduler = options.WorkerScheduler,
        });
        _snapshotPersistence = snapshotPersistence;
        // ★ EntryLog 与快照进资源组（Owned）——TierWal Dispose 随析构（生命周期统一）
        Resources.Add(log, ownership: ResourceOwnership.Owned);
        Resources.Add(_snapshot, ownership: ResourceOwnership.Owned);
    }

    /// <summary>★ Initialize 第一阶段钩子：启动 EntryLog + 镜像快照恢复（并行后台）——本层恢复核心随后 join。</summary>
    protected override void OnInitializeBegin()
    {
        _log.Initialize(_logHints);
        _snapshot.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点（Initialize 的 CAS 闸门内调一次）——恢复核心 = TierWalRecovery。</summary>
    /// <returns>恢复算法实例（由基类持有并在 Initialize 中执行）。</returns>
    protected override IRecovery<WalRecoveryHints> CreateRecovery() => new TierWalRecovery(this);

    /// <summary>内部诊断（测试/白盒）——底层 EntryLog 观测。</summary>
    internal EntryLog DiagnosticLog => _log;

    /// <summary>内部诊断——头边界地址。</summary>
    internal LogicalAddress DiagnosticHeadAddress { get { lock (_stateLock) return _headAddress; } }

    /// <inheritdoc/>
    public long AllocatedIndex => Volatile.Read(ref _allocatedIndex);

    /// <inheritdoc/>
    public long PersistedIndex => Volatile.Read(ref _persistedIndex);

    /// <inheritdoc/>
    public long SnapshotIndex => Volatile.Read(ref _snapshotIndex);

    /// <inheritdoc/>
    public long SnapshotHeadIndex => Volatile.Read(ref _snapshotHeadIndex);

    /// <inheritdoc/>
    public bool IsPersisted(long index) => index <= Volatile.Read(ref _persistedIndex);

    // ═══════════════════════════════════════════════════════════════════
    // 追加（分配与持久化分离——raft 控制 fsync 时机）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public WalAppendLease BeginAppendBatch()
    {
        var batch = _log.BeginAppendBatch();   // EntryLog 写锁由批持有至 lease Dispose
        try
        {
            // ★ StartIndex 在写锁内取（AllocatedIndex 不可能并发推进——单写者纪律）
            return new WalAppendLease(this, batch, Volatile.Read(ref _allocatedIndex) + 1);
        }
        catch
        {
            batch.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public ValueTask<WalAppendResult> AppendBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> entries, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
            throw new ArgumentException("entries 不能为空——空批无意义（AppendSingleAsync 供单条）。", nameof(entries));

        // ★ 集合形态 = lease 包装（单一真源——追加/簿记路径与消费方控制形态完全一致）
        WalAppendResult result;
        using (var lease = BeginAppendBatch())
        {
            for (int i = 0; i < entries.Count; i++)
                _ = lease.Append(entries[i].Span);
            result = new WalAppendResult(lease.StartIndex, entries.Count);
        }

        return new ValueTask<WalAppendResult>(result);
    }

    /// <inheritdoc/>
    public ValueTask<WalAppendResult> AppendSingleAsync(ReadOnlyMemory<byte> entry, CancellationToken ct)
    {
        long index = Volatile.Read(ref _allocatedIndex) + 1;
        var addr = _log.Append(entry.Span);
        RecordAppended(index, addr);
        return new ValueTask<WalAppendResult>(new WalAppendResult(index, 1));
    }

    /// <summary>写路径簿记（单写者串行——BeginAppendBatch 持写锁期间/Append 单条；锁短临界）。
    /// internal = <see cref="WalAppendLease"/> 逐条簿记入口（同程序集租约）。</summary>
    /// <param name="index">本次追加 entry 的业务 index（单调递增，从 1 起）。</param>
    /// <param name="addr">本次追加 entry 的起点逻辑地址。</param>
    internal void RecordAppended(long index, LogicalAddress addr)
    {
        lock (_stateLock)
        {
            _allocatedIndex = index;
            _tailAddress = addr;
            if (_headIndex == 0)
            {
                _headIndex = 1;          // 首条 entry——头边界就位
                _headAddress = addr;
            }

            // ★ 稀疏锚点（内存，零 IO）：每 AnchorInterval 条记一个 [index→addr]——二分定位用
            if (index % AnchorInterval == 0)
                _anchors.Add((index, addr));
            // ★ 尾部环表槽存（每 entry 一次——热读 skip=0 直达，见 _tailMap 字段注释）
            _tailMap[index & TailMapMask] = (index, addr);

            // ★ 自动提交补推（OnAppended 内提交时 allocated 旧——簿记更新后推正）。
            //   ★ 条件化（V0.1 诚实性）：只在本条已被提交边界覆盖（自身 append 触发的同步提交
            //   已完成）时才宣称——循环轨（时间维度提交）也会置 pending，但它的提交目标
            //   = FlushedTail 不覆盖本条；无条件宣称会让下一次 commit 谎报未落盘条目。
            //   未覆盖时保留 pending（后续提交覆盖后由下一条 RecordAppended 一并推正）。
            if (_autoCommitPending && addr <= _log.CommittedOffset)
            {
                _autoCommitPending = false;
                if (index > _persistedIndex)
                {
                    _persistedIndex = index;
                    SerializeAndStage();   // ★ 再 stage：OnAppended 的 stage 用了旧 TailIndex——补推最新（恢复水位）
                    var old = Interlocked.Exchange(ref _persistedSignal,
                        new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                    old.TrySetResult();
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 显式提交（raft 同步点）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask CommitAsync(CancellationToken ct)
    {
        // ★ 持久化水位界 = 提交起点时点的已分配尾（保守正确）：W4 下提交任务与追加并发
        //   （raft 循环在途提交期间继续追加），提交起点之后追加的条目不被本次 flush 覆盖——
        //   照抄 `_allocatedIndex`（提交返回时的值）会把未落盘条目标记为已持久化（持久化谎报）。
        //   起点前已分配的条目必在提交目标内（EntryLog.CommitAsync 捕获 TailAddress 晚于本捕获）；
        //   窗口内新追加由下一次提交的水位脉冲补报（延迟标记=安全方向）。
        var allocatedAtStart = Volatile.Read(ref _allocatedIndex);
        StageOpaque();   // stateLock 内序列化容器 → SetOpaqueMeta（随本次水位提交原子落盘）
        await _log.CommitAsync(ct).ConfigureAwait(false);
        lock (_stateLock)
        {
            // ★ 单调防回退：并发自动提交轨可能已把 persisted 推到更高（本提交起点捕获过期）——
            //   无条件赋值会把已宣称水位拉回（等待者重等、raft 判定保守抖动）。
            if (allocatedAtStart > _persistedIndex)
                _persistedIndex = allocatedAtStart;
            // ★ 换新信号 + 唤醒等待者（先换后 Set——等待者读到的信号必是已 Set 的或下一个）
            var old = Interlocked.Exchange(ref _persistedSignal,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            old.TrySetResult();
        }
    }

    /// <inheritdoc/>
    public async ValueTask WaitForPersistedAsync(long index, CancellationToken ct)
    {
        if (index < 1 || index > Volatile.Read(ref _allocatedIndex))
            throw new ArgumentOutOfRangeException(nameof(index), index,
                $"index {index} 超出已分配区间 [1, {AllocatedIndex}]。");

        // ★ 信号等待（非阻塞）：persisted 在显式 CommitAsync / 自动提交（OnAutoCommitted）推进——
        //   每次提交 Set 信号，等待者醒来重查（防信号丢失循环）。不依赖地址定位
        //   （未提交数据在内存页——盘上不可读，地址定位会错位）。
        while (Volatile.Read(ref _persistedIndex) < index)
        {
            var signal = Volatile.Read(ref _persistedSignal);
            await signal.Task.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 重放（随机起点顺序读）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async IAsyncEnumerable<WalEntry> ReadFromAsync(long startIndex,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long head = Volatile.Read(ref _headIndex);
        if (startIndex < head)
            throw new ArgumentOutOfRangeException(nameof(startIndex), startIndex,
                $"startIndex {startIndex} < HeadIndex {head}——该区间已被 TruncatePrefix 截断。");
        if (startIndex > Volatile.Read(ref _allocatedIndex)) yield break;

        var (fromAddr, fromIndex) = LocateIndexAddress(startIndex);
        // ★ 重放边界 = EntryLog.CommittedOffset（底层 TruncateSuffix 已夹回物理尾——
        //   配合 PageFrameCursor 物理截断帧容忍，读到截断点精确停止）
        var committed = _log.CommittedOffset;

        // ★ 游标锁（spec-10 根治）：复制读用 DirtyRead（逐段短暂共享锁）——Consistent 的区间段
        //   共享锁全程持有到 Dispose，end=committed 尾随日志增长，与写侧段排他（ReclaimTail drain/
        //   TruncateSuffix）互斥——读写多线程并发（WAL 直排）形态下互锁；raft 复制读语义
        //   （读已提交即发、prevLog/term 校验兜底）不需要区间快照隔离
        await using var cursor = _log.OpenCursor(fromAddr, committed, snapshotMode: SnapshotMode.DirtyRead);
        long index = fromIndex;
        long skip = startIndex - fromIndex;
        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            // ★ 空洞/无效帧处 cursor 安静停止（PageFrameCursor 对无效 entry 头返回 false）——
            //   有效停止 = 数据完整性边界
            if (skip > 0) { skip--; index++; continue; }
            // ★ meta entry 不占业务 index（嵌入式 meta 回落路径——与恢复扫描/运行时 Append 口径一致）
            if (cursor.CurrentIsMeta) { index++; continue; }
            yield return new WalEntry(index, cursor.CurrentPayload.ToArray());
            index++;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<WalEntry> ReadFromSync(long startIndex)
    {
        long head = Volatile.Read(ref _headIndex);
        if (startIndex < head)
            throw new ArgumentOutOfRangeException(nameof(startIndex), startIndex,
                $"startIndex {startIndex} < HeadIndex {head}——该区间已被 TruncatePrefix 截断。");
        if (startIndex > Volatile.Read(ref _allocatedIndex)) yield break;

        var (fromAddr, fromIndex) = LocateIndexAddress(startIndex);
        var committed = _log.CommittedOffset;

        // ★ 同 ReadFromAsync——复制读游标锁（DirtyRead）。
        // ★ lease 页租借（零拷贝——2026-08-29 性能：apply 读热路径 ToArray 消除）：换页不还池，
        //   游标持有全部已读页至枚举结束（using Dispose 统一归还）——产出 Data = 页缓冲切片，
        //   <b>仅在枚举存活期有效</b>（含循环体内 await），跨枚举持有须自行拷贝。
        using var cursor = _log.OpenCursor(fromAddr, committed, snapshotMode: SnapshotMode.DirtyRead, leasePages: true);
        long index = fromIndex;
        long skip = startIndex - fromIndex;
        while (cursor.MoveNext())
        {
            if (skip > 0) { skip--; index++; continue; }
            if (cursor.CurrentIsMeta) { index++; continue; }
            yield return new WalEntry(index, cursor.CurrentPayloadMemory);
            index++;
        }
    }

    /// <inheritdoc/>
    public IWalBatchLease ReadBatchLeaseSync(long startIndex, int maxCount, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCount);
        long head = Volatile.Read(ref _headIndex);
        if (startIndex < head)
            throw new ArgumentOutOfRangeException(nameof(startIndex), startIndex,
                $"startIndex {startIndex} < HeadIndex {head}——该区间已被 TruncatePrefix 截断。");
        if (startIndex > Volatile.Read(ref _allocatedIndex))
            return new WalBatchLease(null, startIndex, startIndex, maxCount, ct);   // 越上界——惰性空租约

        var (fromAddr, fromIndex) = LocateIndexAddress(startIndex);
        var committed = _log.CommittedOffset;
        // ★ lease 游标（页租借——切片视图有效至 Dispose；真流式：调用方 MoveNextAsync 驱动逐条产出，
        //   不预物化 List 压内存——底层游标本就是流式，上层伪装批量=全区间页钉死）
        var cursor = _log.OpenCursor(fromAddr, committed, snapshotMode: SnapshotMode.DirtyRead, leasePages: true);
        return new WalBatchLease(cursor, startIndex, fromIndex, maxCount, ct);
    }

    /// <summary>批租约实现——真流式（游标惰性驱动逐条产出页切片视图；Dispose 归还页 → 视图失效）。
    /// ★ Entries 单次枚举（游标前进不可重放）；枚举期间页钉住，结束/Dispose 释放。</summary>
    /// <param name="cursor">底层日志游标（null = 越上界空租约——惰性空结果）。</param>
    /// <param name="startIndex">调用方请求的起始 index（校验已过——用于 skip 计数）。</param>
    /// <param name="fromIndex">游标定位的真实起点 index（≤ startIndex——skip 跳到 startIndex）。</param>
    /// <param name="maxCount">最大返回条数（防无界读）。</param>
    /// <param name="ct">取消令牌（贯通游标 MoveNextAsync）。</param>
    private sealed class WalBatchLease(ILogCursor? cursor, long startIndex, long fromIndex, int maxCount, CancellationToken ct) : IWalBatchLease
    {
        public IAsyncEnumerable<WalEntry> Entries => Iterate(ct);

        /// <summary>异步游标驱动（MoveNextAsync——页 IO 不阻塞调用线程；ct 贯通取消）。</summary>
        /// <param name="cancellationToken">取消令牌（贯通游标 MoveNextAsync）。</param>
        /// <returns>逐条产出的 WalEntry（页切片视图——枚举存活期有效）。</returns>
        private async IAsyncEnumerable<WalEntry> Iterate(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (cursor is null) yield break;
            long index = fromIndex;
            long skip = startIndex - fromIndex;
            var taken = 0;
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false) && taken < maxCount)
            {
                if (skip > 0) { skip--; index++; continue; }
                if (cursor.CurrentIsMeta) { index++; continue; }
                yield return new WalEntry(index, cursor.CurrentPayloadMemory);
                taken++; index++;
            }
        }

        /// <summary>归还底层读游标（Entries 页切片视图随之失效，不可再枚举）。</summary>
        public void Dispose() => cursor?.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 截断（raft 冲突修正 / 日志压缩）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask TruncateSuffixAsync(long newTailIndex, CancellationToken ct)
    {
        long head = Volatile.Read(ref _headIndex);
        // ★ 清空主数据区合法（newTailIndex == head-1：raft 冲突回退到哨兵——leader prev=0
        //   要求 follower 从头接受日志）；越过 head-1（截掉快照已覆盖区）才越界。
        if (newTailIndex < head - 1)
            throw new ArgumentOutOfRangeException(nameof(newTailIndex), newTailIndex,
                $"newTailIndex {newTailIndex} < HeadIndex-1 {head - 1}——截断边界越过头部（日志区间 [head, tail] 之外）。");
        if (newTailIndex >= Volatile.Read(ref _allocatedIndex)) return;   // no-op

        // ★ 边界定位前落盘：未提交数据在内存页（盘上不可读）——定位会错位（停在最后已提交）
        await _log.FlushAsync(ct).ConfigureAwait(false);

        // 截断边界 = 第 newTailIndex+1 条 entry 的起点（第 newTailIndex 条保留，其后全删）
        var boundaryAddr = LocateExactAddress(newTailIndex + 1);
        if (!_log.TruncateSuffix(boundaryAddr))
            throw new InvalidOperationException($"TruncateSuffix 拒绝地址 {boundaryAddr}（底层校验失败）。");

        lock (_stateLock)
        {
            _allocatedIndex = newTailIndex;
            if (_persistedIndex > newTailIndex) _persistedIndex = newTailIndex;
            _anchors.RemoveAll(a => a.Index > newTailIndex);
        }

        // ★ 提交（stage + meta 落盘——重启恢复不读越界；截断 = raft 冲突修正（低频）——fsync 可接受）
        await CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask TruncatePrefixAsync(long newHeadIndex, CancellationToken ct)
    {
        if (newHeadIndex <= Volatile.Read(ref _headIndex)) return;   // no-op
        // ★ 越界校验优先（参数合法性）：允许截空（newHeadIndex = AllocatedIndex + 1）——
        //   raft 快照覆盖全部日志后日志可为空（SnapshotAsync 一体压缩截到 N₀+1）。
        long allocated = Volatile.Read(ref _allocatedIndex);
        if (newHeadIndex > allocated + 1)
            throw new ArgumentOutOfRangeException(nameof(newHeadIndex), newHeadIndex,
                $"newHeadIndex {newHeadIndex} > AllocatedIndex + 1 {allocated + 1}——头部不能越过尾部。");
        // ★ 镜像快照覆盖校验（raft 日志压缩 = 先快照后截断）：截断 [head, newHeadIndex) 物理删除
        //   被截区——只有镜像快照能恢复——截断前被截区必须已快照覆盖（SnapshotIndex ≥ newHeadIndex - 1）
        if (newHeadIndex - 1 > Volatile.Read(ref _snapshotIndex))
            throw new InvalidOperationException(
                $"TruncatePrefix 越过镜像快照覆盖点 {SnapshotIndex}：被截区 [head, {newHeadIndex - 1}] 无快照覆盖——" +
                "截断后不可恢复。请先 SnapshotAsync 持久化 raft 状态镜像（先快照后截断）。");

        // ★ 边界定位前落盘（同 TruncateSuffix——未提交数据可定位）
        await _log.FlushAsync(ct).ConfigureAwait(false);

        // ★ 截空（newHeadIndex = AllocatedIndex+1）时第 newHeadIndex 条不存在——head 边界 = 写尾
        //   （恢复扫描从写尾起 = 空；EOF 停驻的最后一条会把残留多算一条）
        var entryAddr = newHeadIndex > allocated ? _log.TailAddress : LocateExactAddress(newHeadIndex);
        // ★ 头截断边界对齐到段起点（设计决策层叠）：段内打洞后 PageFrameCursor 从段首读
        //   会撞洞（帧头为零 → 停）——底层扫描机制限制；段对齐 = 整段删除，cursor 从新段首
        //   读正常（同段内 < newHeadIndex 的 entry 物理保留、逻辑已删——ReadFrom 从 newHeadIndex 起）。
        var boundaryAddr = new LogicalAddress(entryAddr.SegId, 0);
        if (boundaryAddr > _log.BeginAddress)
            _log.TruncatePrefix(boundaryAddr);
        // ★ 同段/段边界 no-op 也推进逻辑 head（物理不删——ReadFrom 从 newHeadIndex 起，语义不变）

        lock (_stateLock)
        {
            _headIndex = newHeadIndex;
            _headAddress = entryAddr;   // ★ head 地址 = 精确 entry 起点（重放/恢复定位用）
            // ★ 锚点修剪：删除 index < 新头的锚点（首个剩余锚点可能 > newHeadIndex——
            //   定位 [head, 首锚点) 从 headAddress 顺序扫，≤ AnchorInterval 帧）
            _anchors.RemoveAll(a => a.Index < newHeadIndex);
        }

        // ★ 提交（stage 含新 headIndex——opaque 落盘；重启恢复 headIndex 正确）。
        //   截断 = raft 日志压缩（低频）——一次 fsync 可接受。
        await CommitAsync(ct).ConfigureAwait(false);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 元数据 Opaque 槽（原子替换，内容零知识）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask WriteMetaAsync(ReadOnlyMemory<byte> opaque, CancellationToken ct)
    {
        lock (_stateLock) { _raftMeta = opaque.ToArray(); }
        // ★ raft 投票/任期变更必须持久化后才应答（契约①）——立即显式提交（一次 fsync）
        await CommitAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> ReadMeta()
    {
        lock (_stateLock) { return _raftMeta ?? ReadOnlyMemory<byte>.Empty; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 镜像快照（第三部件——主数据镜像 + 增量压缩 + 注入传输面）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<long> SnapshotAsync(CancellationToken ct)
    {
        // ★ 一体（raft 日志压缩惯例）：N₀ = 当前 PersistedIndex（只含已持久化）——
        //   镜像 [Head..N₀] 主数据条目帧流 → 本地快照（增量段）→ 截断（先快照后截断——校验自洽）
        long n0 = Volatile.Read(ref _persistedIndex);
        Volatile.Write(ref _snapshotHeadIndex, Volatile.Read(ref _headIndex));   // ★ 镜像起点（帧流第一条 index）
        await _snapshot.AppendSegmentAsync(n0, MirrorFramesAsync(n0, ct), ct).ConfigureAwait(false);
        Volatile.Write(ref _snapshotIndex, n0);
        await TruncatePrefixAsync(n0 + 1, ct).ConfigureAwait(false);
        return n0;
    }

    /// <summary>
    /// 主数据 [Head..N₀] → 条目帧流（每条 = [len 4B][payload]——WalSnapshotFormat 帧；
    /// WAL 自己从主数据生成——非 raft 状态机镜像；攒批 128KB 减少逐条枚举）。
    /// </summary>
    /// <param name="n0">快照覆盖点（镜像到 N₀——(N₀, 尾] 保留供增量重放）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>攒批 128KB 的帧流块（每块含若干 [len 4B][payload] 帧）。</returns>
    private async IAsyncEnumerable<ReadOnlyMemory<byte>> MirrorFramesAsync(long n0,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buf = new byte[128 * 1024];
        int filled = 0;
        await foreach (var e in ReadFromAsync(Volatile.Read(ref _headIndex), ct).ConfigureAwait(false))
        {
            if (e.Index > n0) break;   // 只镜像到 N₀（(N₀, 尾] 保留供增量重放）
            if (filled + WalSnapshotFormat.FrameHeaderSize + e.Data.Length > buf.Length)
            {
                yield return buf.AsMemory(0, filled);
                filled = 0;
            }
            WalSnapshotFormat.WritePayloadFrame(buf.AsSpan(filled, WalSnapshotFormat.FrameHeaderSize + e.Data.Length), e.Data.Span);
            filled += WalSnapshotFormat.FrameHeaderSize + e.Data.Length;
        }
        if (filled > 0) yield return buf.AsMemory(0, filled);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadSnapshotEntriesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ★ 流式读回镜像条目帧流（raft 重建状态机分块消费；段级 CRC64 读时校验）
        await foreach (var chunk in _snapshot.ReadAllChunksAsync(ct).ConfigureAwait(false))
            yield return chunk;
    }

    /// <inheritdoc/>
    public async ValueTask ExportSnapshotAsync(CancellationToken ct)
    {
        EnsureSnapshotPersistence();
        var writer = await OpenWriterOrThrowAsync(ct).ConfigureAwait(false);
        var n0 = Volatile.Read(ref _snapshotIndex);
        try
        {
            // ★ 导出内容 = 压缩后的镜像（内部快照条目帧流）——Header N₀ + 帧流 + Footer CRC32C
            var header = new byte[WalSnapshotFormat.HeaderSize];
            WalSnapshotFormat.WriteHeader(header, n0);
            await writer.WriteHeaderAsync(header, ct).ConfigureAwait(false);

            uint crc = 0;
            var frameStats = new FrameStats();
            await foreach (var chunk in _snapshot.ReadAllChunksAsync(ct).ConfigureAwait(false))
            {
                await writer.WritePayloadAsync(chunk, ct).ConfigureAwait(false);
                crc = CrcAppend(crc, chunk.ToArray(), chunk.Length);   // C#12 async 禁 span 局部——拷贝帧
                frameStats.Accumulate(chunk.Span);   // ★ 跨块帧计数（帧可能跨交付块边界）
            }
            var count = frameStats.Frames;
            var totalPayload = frameStats.PayloadBytes;

            var footer = new byte[WalSnapshotFormat.FooterSize];
            WalSnapshotFormat.WriteFooter(footer, count, totalPayload, crc);
            await writer.WriteFooterAsync(footer, ct).ConfigureAwait(false);
            writer.Complete(true);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async ValueTask ImportSnapshotAsync(CancellationToken ct)
    {
        EnsureSnapshotPersistence();
        var reader = await OpenReaderOrThrowAsync(ct).ConfigureAwait(false);
        try
        {
            // 1. Header——恢复一致性点 N₀
            var headerBuf = new byte[WalSnapshotFormat.HeaderSize];
            if (await reader.ReadHeaderAsync(headerBuf, ct).ConfigureAwait(false) < WalSnapshotFormat.HeaderSize
                || !WalSnapshotFormat.TryReadHeader(headerBuf, out long n0))
                throw new InvalidOperationException("快照流 Header 非法/损坏（非 TierWAL 快照格式）。");

            // 2. ★ 流式安装（O(单帧) 内存——GB/TB 级不驻留）：帧流迭代器直接喂事务式段写——
            //    Footer 校验在迭代器尾部（校验失败抛 = 事务 Abort 回滚——新段物理清除，旧快照完好）；
            //    通过 = 2PC 提交（段表替换 + 旧段回收）。
            var counted = CountedFrames(ReadImportFramesAsync(reader, ct), ct);
            await _snapshot.ImportSegmentAsync(n0, counted.Frames, ct).ConfigureAwait(false);
            // ★ 快照区 index 对齐：帧流 [head..N₀] 共 count 帧 → head = N₀ - count + 1（帧内零 index——
            //   冷启动重建状态机须从真实 head 计数，否则业务 apply 的 index 错位）
            if (counted.Counter.Count > 0)
                Volatile.Write(ref _snapshotHeadIndex, n0 - counted.Counter.Count + 1);
            Volatile.Write(ref _snapshotIndex, n0);

            // ★ 主数据重锚（raft 安装语义——2026-08-26 楔死修复）：快照覆盖 [head..N₀]——本地主数据
            //   全部进入快照覆盖区，追加位对齐 N₀+1（全局 index 续接——协议层 nextIndex = N₀+1 推增量
            //   直接落位，本地与全局 index 恒等）。旧行为本地重编号（追加位 = 本地尾+1）：raft 全局
            //   index 与存储 index 错位，AppendEntries/冲突回退/快照重导全链楔死（实测：增量追加落 1
            //   而非 N₀+1；重编号节点当选 leader 后自快照还会错标 N₀——协议层外部无法修正）。
            //   ★ 被清的主数据全部未提交（raft 安全：安装触发时 nextIndex ≤ leader 快照覆盖点，
            //   leader 的 commitIndex ≤ PersistedIndex = N₀——follower 本地已提交条目必在快照覆盖内）。
            if (n0 > 0)
                await ReAnchorMainDataAsync(n0, ct).ConfigureAwait(false);
        }
        finally
        {
            await reader.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 主数据重锚（快照安装后——追加位对齐 N₀+1）：物理清除主数据（全部被快照覆盖——镜像 = 唯一权威）
    /// + 簿记 head/allocated/persisted 对齐 N₀ + 提交（opaque 落盘——重启恢复 head=N₀+1、allocated=N₀）。
    /// </summary>
    /// <param name="n0">快照覆盖点（重锚基准——head=N₀+1、allocated/persisted=N₀）。</param>
    /// <param name="ct">取消令牌。</param>
    private async ValueTask ReAnchorMainDataAsync(long n0, CancellationToken ct)
    {
        // 1. 物理清除（含未提交内存数据——flush 兜底；段对齐整删，同 TruncatePrefixAsync 口径）
        await _log.FlushAsync(ct).ConfigureAwait(false);
        var tail = _log.TailAddress;
        var boundaryAddr = new LogicalAddress(tail.SegId, 0);
        if (boundaryAddr > _log.BeginAddress)
            _log.TruncatePrefix(boundaryAddr);

        // 2. 簿记：head = N₀+1（第一条增量落位）；allocated/persisted = N₀（快照 = 已持久化 N₀ 条）
        lock (_stateLock)
        {
            _headIndex = n0 + 1;
            _headAddress = tail;
            _allocatedIndex = n0;
            _persistedIndex = n0;
            _anchors.Clear();
            Array.Clear(_tailMap);   // ★ 旧主数据地址全部作废——环表必清（陈旧槽 Index 巧合命中=读已删数据）
        }

        // 3. 提交（stage 含新 headIndex/水位——重启恢复正确）
        await CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>帧流计数包装（导入时统计帧数——快照区 head 反推用；流式零额外成本）。
    /// ★ 迭代器惰性执行——计数经共享可变容器带回（返回时闭包计数尚未运行）。</summary>
    /// <param name="frames">原始帧流（未消费——计数包装惰性驱动）。</param>
    /// <param name="ct">取消令牌（贯通帧流迭代）。</param>
    /// <returns>(包装后的帧流, 帧计数器——迭代完成后读 Count)。</returns>
    private static (IAsyncEnumerable<ReadOnlyMemory<byte>> Frames, FrameCounter Counter) CountedFrames(
        IAsyncEnumerable<ReadOnlyMemory<byte>> frames, CancellationToken ct)
    {
        var counter = new FrameCounter();
        return (Iterate(), counter);

        async IAsyncEnumerable<ReadOnlyMemory<byte>> Iterate()
        {
            await foreach (var chunk in frames.WithCancellation(ct).ConfigureAwait(false))
            {
                // 块可能含多帧——按 [len 4B][payload] 拆帧计数（索引访问——async 迭代器禁 span）
                int p = 0;
                while (p + WalSnapshotFormat.FrameHeaderSize <= chunk.Length)
                {
                    int len = BitConverter.ToInt32(chunk.Span.Slice(p, WalSnapshotFormat.FrameHeaderSize));
                    if (!WalSnapshotFormat.IsValidFrameLength(len)) break;
                    p += WalSnapshotFormat.FrameHeaderSize + len;
                    counter.Count++;
                }
                yield return chunk;
            }
        }
    }

    /// <summary>帧计数共享容器（迭代器闭包写、调用方读——惰性执行安全）。</summary>
    private sealed class FrameCounter
    {
        public long Count;
    }

    /// <summary>
    /// ★ 快照导入帧流迭代器（流式——攒批 128KB 有界内存，不驻留整像）：
    /// 帧循环 [len 4B][payload] → yield 帧流块；帧流结束（EOF/footer magic）→ 补齐 Footer 校验
    /// （条数/总长/CRC——校验失败抛 = 事务 Abort 触发，安装不落盘）。
    /// </summary>
    /// <param name="reader">快照传输读会话（ReadPayloadAsync/ReadFooterAsync）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>攒批 128KB 的帧流块（校验失败抛 InvalidOperationException）。</returns>
    /// <exception cref="InvalidOperationException">Payload 截断或 Footer 校验失败。</exception>
    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadImportFramesAsync(
        IAsyncTransferReader reader, [EnumeratorCancellation] CancellationToken ct)
    {
        long count = 0;
        long totalPayload = 0;
        uint crc = 0;
        var lenBuf = new byte[WalSnapshotFormat.FrameHeaderSize];
        var payloadBuf = new byte[64 * 1024];
        var buf = new byte[128 * 1024];
        var filled = 0;

        while (true)
        {
            var got = await reader.ReadPayloadAsync(lenBuf, ct).ConfigureAwait(false);
            if (got < WalSnapshotFormat.FrameHeaderSize) break;   // EOF/不足——Footer 区
            var len = BitConverter.ToInt32(lenBuf);
            if (!WalSnapshotFormat.IsValidFrameLength(len)) break;   // Footer magic——Footer 区
            if (payloadBuf.Length < len) payloadBuf = new byte[len];
            if (await reader.ReadPayloadAsync(payloadBuf.AsMemory(0, len), ct).ConfigureAwait(false) < len)
                throw new InvalidOperationException("快照流 Payload 截断（len 与实际字节不符）。");

            crc = CrcAppend(crc, lenBuf, WalSnapshotFormat.FrameHeaderSize);
            crc = CrcAppend(crc, payloadBuf, len);
            count++;
            totalPayload += len;

            // 攒批帧流块（128KB 有界——GB/TB 级不驻内存）
            int frameLen = WalSnapshotFormat.FrameHeaderSize + len;
            if (filled + frameLen > buf.Length)
            {
                yield return buf.AsMemory(0, filled);
                filled = 0;
            }
            WalSnapshotFormat.WritePayloadFrame(buf.AsSpan(filled, frameLen), payloadBuf.AsSpan(0, len));
            filled += frameLen;
        }
        if (filled > 0) yield return buf.AsMemory(0, filled);

        // 3. Footer——已读 lenBuf 前 4B（footer magic 开头）补齐校验（失败抛 = 事务 Abort）
        var footerBuf = new byte[WalSnapshotFormat.FooterSize];
        lenBuf.CopyTo(footerBuf, 0);
        int footerGot = await reader.ReadFooterAsync(footerBuf.AsMemory(WalSnapshotFormat.FrameHeaderSize), ct)
            .ConfigureAwait(false);
        if (WalSnapshotFormat.FrameHeaderSize + footerGot < WalSnapshotFormat.FooterSize
            || !WalSnapshotFormat.TryValidateFooter(footerBuf, count, totalPayload, crc))
            throw new InvalidOperationException("快照流 Footer 校验失败（条数/总长/CRC 不一致——快照损坏或 Abort）。");
    }

    /// <summary>
    /// 跨块帧计数状态机（[len 4B][payload] 帧流——帧可能跨交付块边界；
    /// Footer 语义：帧数 + 纯 payload 字节不含 len 头——与导入侧逐帧计数一致）。
    /// </summary>
    private sealed class FrameStats
    {
        private readonly byte[] _lenBuf = new byte[WalSnapshotFormat.FrameHeaderSize];
        private int _lenGot;       // 已读的 len 头字节（state=0）
        private int _frameLen;     // 当前帧 payload 长度（state=1）
        private int _frameGot;     // 已读的 payload 字节（state=1）
        private int _state;        // 0=等 len 头 1=读 payload

        public long Frames { get; private set; }
        public long PayloadBytes { get; private set; }

        /// <summary>累加交付块到帧计数状态机（帧可能跨块边界——状态机续接处理）。</summary>
        /// <param name="chunk">交付块（含若干 [len 4B][payload] 帧的切片，可能跨帧/跨块）。</param>
        /// <exception cref="InvalidDataException">帧 len 头非法（损坏/非本格式）。</exception>
        public void Accumulate(ReadOnlySpan<byte> chunk)
        {
            int off = 0;
            while (off < chunk.Length)
            {
                if (_state == 0)
                {
                    int take = Math.Min(WalSnapshotFormat.FrameHeaderSize - _lenGot, chunk.Length - off);
                    chunk.Slice(off, take).CopyTo(_lenBuf.AsSpan(_lenGot, take));
                    off += take;
                    _lenGot += take;
                    if (_lenGot == WalSnapshotFormat.FrameHeaderSize)
                    {
                        _frameLen = BitConverter.ToInt32(_lenBuf);
                        if (!WalSnapshotFormat.IsValidFrameLength(_frameLen))
                            throw new InvalidDataException("镜像帧流损坏——len 头非法。");
                        _state = 1;
                        _lenGot = 0;
                        PayloadBytes += _frameLen;
                    }
                }
                else
                {
                    int take = Math.Min(_frameLen - _frameGot, chunk.Length - off);
                    off += take;
                    _frameGot += take;
                    if (_frameGot == _frameLen)
                    {
                        Frames++;
                        _state = 0;
                        _frameGot = 0;
                    }
                }
            }
        }
    }

    /// <summary>传输面守卫：未注入（单机）抛——Export/Import 是多节点功能。</summary>
    private void EnsureSnapshotPersistence()
    {
        if (_snapshotPersistence is null)
            throw new InvalidOperationException(
                "未注入快照传输面——Export/Import 需要 TierWalBuilder.WithSnapshotPersistence（跨节点传输/远端存储）；"
                + "单机场景快照已本地化（SnapshotAsync + 冷启动自动载入），无需导出。");
    }

    /// <summary>打开快照传输写会话（失败抛——双写者冲突/存储不可用）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已打开的写会话。</returns>
    /// <exception cref="InvalidOperationException">传输面打开写会话失败。</exception>
    private async ValueTask<IAsyncTransferWriter> OpenWriterOrThrowAsync(CancellationToken ct)
    {
        if (!await _snapshotPersistence!.TryOpenWriteAsync(out var writer).ConfigureAwait(false) || writer is null)
            throw new InvalidOperationException("快照传输面打开写会话失败（双写者冲突/存储不可用）。");
        return writer;
    }

    /// <summary>打开快照传输读会话（失败抛——账面无完整像）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>已打开的读会话。</returns>
    /// <exception cref="InvalidOperationException">传输面打开读会话失败。</exception>
    private async ValueTask<IAsyncTransferReader> OpenReaderOrThrowAsync(CancellationToken ct)
    {
        if (!await _snapshotPersistence!.TryOpenReadAsync(out var reader).ConfigureAwait(false) || reader is null)
            throw new InvalidOperationException("快照传输面打开读会话失败（账面无完整像）。");
        return reader;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 定位（★ 地址顺序性保证：index 单调 ⇔ 地址单调——二分锚点 + 顺序扫帧）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 定位 index 所在区间起点：二分内存稀疏锚点（last anchor ≤ index）→ 从锚点地址开始扫；
    ///   目标在头部与首锚点之间 → 从 headAddress 扫（≤ AnchorInterval 帧）。
    /// </summary>
    /// <param name="index">目标 entry 的业务 index。</param>
    /// <returns>(起点地址, 该地址处 entry 的 index)——调用方据此跳过/计数。</returns>
    private (LogicalAddress Address, long Index) LocateIndexAddress(long index)
    {
        lock (_stateLock)
        {
            // ★ 尾部环表命中——精确直达（skip=0，免锚点跳过扫描；槽 Index 自校验防回绕陈旧）
            var slot = _tailMap[index & TailMapMask];
            if (slot.Index == index) return (slot.Address, index);

            int lo = 0, hi = _anchors.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (_anchors[mid].Index <= index) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }

            if (found >= 0)
                return (_anchors[found].Address, _anchors[found].Index);
            return (_headAddress, _headIndex);   // 头部与首锚点之间——从 head 扫
        }
    }

    /// <summary>定位 index 对应 entry 的<b>精确</b>起点地址（从锚点/头部顺序扫帧数到目标）。
    /// ★ cursor 第一条 = anchorIndex 条（断点续传）——要 index 条需 (index − anchorIndex + 1) 次 MoveNext。</summary>
    /// <param name="index">目标 entry 的业务 index。</param>
    /// <returns>该 entry 的精确起点逻辑地址。</returns>
    private LogicalAddress LocateExactAddress(long index)
    {
        var (anchorAddr, anchorIndex) = LocateIndexAddress(index);
        if (anchorIndex == index) return anchorAddr;

        long count = index - anchorIndex + 1;
        using var cursor = _log.OpenCursor(anchorAddr, _log.CommittedOffset, snapshotMode: SnapshotMode.DirtyRead);
        LogicalAddress target = anchorAddr;
        while (count-- > 0 && cursor.MoveNext()) target = cursor.CurrentAddress;
        return target;
    }

    // ═══════════════════════════════════════════════════════════════════
    // opaque 容器（TierWAL 三段式头部——搭 Meta 边车随水位原子落盘）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>stage：序列化容器 → SetOpaqueMeta（随下次水位提交原子落盘）。
    /// 由显式 CommitAsync 与包装提交策略（EntryLog 自动提交路径）调用。</summary>
    internal void StageOpaque()
    {
        lock (_stateLock) { SerializeAndStage(); }
    }

    /// <summary>序列化 + stage（调用方须持 _stateLock）。</summary>
    private void SerializeAndStage()
    {
        int raftLen = _raftMeta?.Length ?? 0;
        var buf = new byte[WalOpaqueLayout.ContainerHeaderSize + raftLen];
        WalOpaqueLayout.Serialize(buf, _allocatedIndex, _tailAddress, _headIndex, _headAddress, _raftMeta);
        // ★ 内容去重（判例 2026-09-04——raft×TierWal 多提交流停顿根因）：容器自上次 stage 后
        //   一字未变（纯冗余提交——复制车道读门/apply 周期持久化）→ 跳过 SetOpaqueMeta、不置脏
        //   → EntryLog.CommitCore 合并快路径（target≤committed && !dirty = 零成本）生效。
        //   语义安全：内容已随上次水位原子落盘，无新状态需携带；上次 stage 未落盘时 dirty 仍
        //   true → 本次提交照常写盘（"至少一次落盘"保证不变）。
        if (_lastStaged is { } last && last.AsSpan().SequenceEqual(buf))
            return;
        _log.SetOpaqueMeta(buf);
        _lastStaged = buf;
    }

    /// <summary>★ 自动提交完成（包装策略触发——EntryLog 同步提交链，stage 后紧随落盘）：
    /// 推进 persisted（单条强制形态 = 每条 append 即持久化——raft 可应答水位）。
    /// ★ 诚实边界（V0.1 根修）：只宣称已被提交边界（EntryLog.CommittedOffset——推进即含
    /// meta fsync，STORAGE-005）覆盖的已记录条目。循环轨（时间维度提交）目标 = FlushedTail，
    /// 页缓冲内末页数据不在其内——无条件宣称 allocated 尾 = 持久化谎报（raft 会把未落盘
    /// 条目提交给客户端，崩溃丢已确认写）。快路径：全部已记录条目已覆盖 → 宣称 allocated；
    /// 尾部有未提交数据 → 不宣称（保守，由 RecordAppended 条件补推/显式提交推进——
    /// 延迟标记=安全方向）。
    /// ★ OnAppended 在 RecordAppended 之前触发——此处 allocated/tail 是旧值（刚追加条目
    /// 尚未入簿），宣称天然不含它；同步轨随后 RecordAppended 条件补推收口。</summary>
    internal void OnAutoCommitted()
    {
        lock (_stateLock)
        {
            _autoCommitPending = true;
            if (_allocatedIndex > _persistedIndex && _tailAddress <= _log.CommittedOffset)
            {
                _persistedIndex = _allocatedIndex;
                var old = Interlocked.Exchange(ref _persistedSignal,
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                old.TrySetResult();
            }
        }
    }

    /// <summary>增量 CRC（同步 helper——C# 12 async 方法禁 span 表达式）。</summary>
    /// <param name="crc">前一阶段 CRC32C 种子。</param>
    /// <param name="buf">待计入的字节缓冲。</param>
    /// <param name="length">实际计入长度（≤ buf.Length）。</param>
    /// <returns>累计后的 CRC32C 值。</returns>
    private static uint CrcAppend(uint crc, byte[] buf, int length)
        => UnifiedCrc.ComputeCrc32C(crc, buf.AsSpan(0, length));

    /// <summary>拷贝 opaque（同步 helper——ReadOpaqueMeta 返回 span）。</summary>
    /// <param name="log">底层 EntryLog（ReadOpaqueMeta 返回当前 opaque span）。</param>
    /// <returns>opaque 字节副本（数组——async 方法禁 span 表达式）。</returns>
    private static byte[] CopyOpaque(EntryLog log) => log.ReadOpaqueMeta().ToArray();

    /// <summary>WalRecoveryHints → LogRecoveryHints（构造期转换——EntryLog 透传）。</summary>
    /// <param name="hints">TierWAL 恢复 hints（地址级已知水位）。</param>
    /// <returns>转换后的底层 LogRecoveryHints（透传 EntryLog 恢复面）。</returns>
    private static LogRecoveryHints ToLogHints(WalRecoveryHints hints) => new()
    {
        TailAddress = hints.TailAddress,
        BeginAddress = hints.BeginAddress,
        CommittedOffset = hints.CommittedOffset,
    };

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复核心（RecoveryBase 模板派生——层间 join EntryLog + opaque 解析 + 统一扫描重建锚点）
    // ═══════════════════════════════════════════════════════════════════

    private sealed class TierWalRecovery(TierWal owner) : RecoveryBase<WalRecoveryHints>
    {
        /// <summary>层间 join——EntryLog + 镜像快照恢复（OnInitializeBegin 已并行启动）此处只等待。</summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>任务在 EntryLog 与镜像快照均 Ready 后完成。</returns>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._log.WaitForReadyAsync(ct).ConfigureAwait(false);
            await owner._snapshot.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心：读 opaque 容器（O(1)）→ 解析 [maxIndex→addr][minIndex→addr] + raft 元数据 →
        /// 统一扫描重建锚点（= raft 恢复重放本身，不额外成本）→ 计真实 allocated
        /// （EntryLog 尾可能超前 opaque——自动提交提前落盘的崩溃窗口）。
        /// ★ 镜像快照：内部快照恢复已完成（段表 O(1)）——SnapshotIndex = LatestN0（raft 从 N₀+1 回放增量；
        ///   内容 raft 经 ReadSnapshotEntriesAsync 流式读回——不驻留）。
        /// </summary>
        /// <param name="hints">恢复 hints（外部注入已知水位——default 让底层自恢复）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>恢复完成（异常时抛——RecoveryBase 转为 Failed）。</returns>
        protected override async ValueTask OnRecoveryCoreAsync(WalRecoveryHints hints, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RaiseProgress(10, "opaque/scan");

            // ★ 载快照（第三部件）：内部快照恢复产物 = 段表 + LatestN₀（raft 快照覆盖点）
            owner._snapshotIndex = owner._snapshot.LatestN0;

            var opaque = CopyOpaque(owner._log);   // span → 数组（C#12 async 禁 span 表达式）
            var parsed = WalOpaqueLayout.TryParse(opaque, out var persisted, out _,
                out var headIndex, out var headAddr, out var raftMeta);

            // ★ 统一扫描重建（恢复 = raft 重放成本）：从 head 起扫到 EntryLog 真实尾——
            //   补锚点 + 计真实 allocated（含从未 stage 的页契约提交路径：opaque 空但数据存在）。
            var anchors = new List<(long, LogicalAddress)>();
            var index = headIndex > 0 ? headIndex - 1 : 0;   // 扫描第一条 = headIndex 条（head=0 时 = 第 1 条）
            var lastAddr = LogicalAddress.Empty;
            var firstAddr = LogicalAddress.Empty;
            var lastCommittedIndex = persisted;   // ★ 扫描中最后一条 ≤ EntryLog.CommittedOffset 的 index
            var committedBound = owner._log.CommittedOffset;
            var start = headAddr.IsValid ? headAddr : LogicalAddress.Empty;
            await using (var cursor = owner._log.OpenCursor(start, owner._log.TailAddress))
            {
                while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                {
                    // ★ meta entry 不占业务 index（嵌入式 meta 回落路径——与运行时 Append 推进口径一致，
                    //   否则恢复水位含 meta、重启后 raft index 跳变）
                    if (cursor.CurrentIsMeta) continue;
                    index++;
                    lastAddr = cursor.CurrentAddress;
                    if (firstAddr == LogicalAddress.Empty) firstAddr = lastAddr;
                    if (index % AnchorInterval == 0) anchors.Add((index, lastAddr));
                    owner._tailMap[index & TailMapMask] = (index, lastAddr);   // 恢复期填充（单线程——ready 前无读者）
                    if (lastAddr <= committedBound) lastCommittedIndex = index;
                }
            }
            ct.ThrowIfCancellationRequested();

            // ★ head 就位（opaque 未记头但数据存在）
            if (headIndex == 0 && index > 0)
            {
                headIndex = 1;
                headAddr = firstAddr;
            }

            // ★ persisted = opaque 显式水位（raft 可应答下限）；opaque 存在时用扫描中最后已提交
            //   index 提升（自动提交/单条强制的 stage 可能落后 1 条——EntryLog.CommittedOffset 是
            //   真实落盘边界）。opaque 为空（从未策略提交——只有页契约 flush）→ persisted = 0
            //   （raft 语义：从未显式提交，页契约落盘不算可应答水位）。
            if (parsed && lastCommittedIndex > persisted) persisted = lastCommittedIndex;

            lock (owner._stateLock)
            {
                owner._allocatedIndex = index;
                owner._persistedIndex = persisted;
                owner._headIndex = headIndex;
                owner._headAddress = headAddr;
                owner._tailAddress = lastAddr;
                owner._raftMeta = raftMeta;
                owner._anchors.Clear();
                owner._anchors.AddRange(anchors);
            }

            RaiseProgress(90, $"tail={index} persisted={persisted} head={headIndex} anchors={anchors.Count}");
            // 完成由模板 MarkReady（RecoveryBase）
        }
    }
}
