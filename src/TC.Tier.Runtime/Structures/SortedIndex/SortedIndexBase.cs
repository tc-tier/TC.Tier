using System.Runtime.CompilerServices;
using NativeInt128 = TC.Tier.Core.NativeInterop.UInt128Pair;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// 比较族基类（族 A：BTreeIndex/SkipListIndex）——条目物化 (TKey, 地址)，比较即路由，支持有序遍历。
/// <para>★ 族器官（设计稿 §3.2，从旧 IndexBase 收编）：AllocateNode/WriteNode/ReadNode/ReclaimNode
/// （引擎节点持久化原语——BTree 叶子/跳表节点的引擎寄生形态）。</para>
/// <para>★ 不持有探测族器官（判等 tag/CAS 桶/GrowIndex）；判等在条目内完成。
/// KeyResolver 可选注入——判等不需要（key 物化条目内），恢复重放需要（设计稿 §4 两族共需接口）：
/// 有重放窗口而无 resolver 者，恢复核心 fail-fast。</para>
/// <para>★ 线程契约（终态）：全部公开操作（读/写/游标单步/后台 dump）经内部<b>操作闸</b>互斥——
/// 单操作粒度全互斥，结构自保证任意并发组合安全（撕裂/成环消除——节点结构体大拷贝的写入
/// 非原子，无锁读者可下降成环活锁，2026-08-27 dotnet-stack 实锤）。粒度=单操作（游标=单步推进），
/// 长扫描经逐步持闸与写者交错。锁自由读（不可变节点/seqlock 形）为后续优化项。</para>
/// </summary>
public abstract partial class SortedIndexBase<TKey> : LifecycleBase<SortedIndexRecoveryHints>, ITransactionParticipant,
    IEpochProtected, IIndex<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private protected readonly StorageEngine _engine;
    private protected readonly LightEpoch _epoch;

    /// <summary>★ 操作闸（MonitorScope——段区锁同款先例：临界区 µs 级 O(log n) 下降无长 IO 段；竞争者 futex park 公平等待）。读写全互斥（单操作粒度）。</summary>
    private readonly object _opGate = new();

    /// <summary>进入操作闸（全部公开操作互斥——RAII using；后台 dump 编排同闸）。</summary>
    private protected MonitorScope EnterOp() => MonitorScope.Enter(_opGate);

    private protected readonly IKeyComparer<TKey> KeyComparer;

    /// <summary>★ 恢复重放数据面（可选注入——判等不需要，重放需要；窗口+null 者恢复期 fail-fast）。</summary>
    private protected readonly IKeyResolver<TKey>? KeyResolver;

    private readonly IFileSystem _fileSystem;
    private protected readonly SortedIndexSettings _settings;
    /// <summary>诊断可观测位（测试/运维）：上次恢复是否走了主存储载入路径（false = 全量重放 fail-safe）。</summary>
    public bool MainStorageAppliedLastRecovery { get; private protected set; }
    private protected int SectorSize { get; }
    internal long SegmentSize => _engine.SegmentGrowthLimit;

    private protected LogicalAddress _beginAddress;

    /// <summary>结构起始地址（引擎 MinAddress——比较族固定锚点槽所在地，节点分配在锚点之后）。</summary>
    public LogicalAddress BeginAddress => _beginAddress;

    /// <summary>
    /// ★ 基类注入标准顺序（对齐 LogBase/RingBase/MirrorBase/ProbingIndexBase）：codec → fs → settings → 族特有。
    /// </summary>
    protected SortedIndexBase(
        ISortedIndexCodec codec,
        IFileSystem fs,
        SortedIndexSettings settings,
        LightEpoch? epoch = null,
        IKeyComparer<TKey>? keyComparer = null,
        IKeyResolver<TKey>? keyResolver = null,
        TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;   // 时钟缝 件一 P2（持久化 dump 节奏）
        ArgumentNullException.ThrowIfNull(codec,
            "比较族主存储格式 codec 必注入（机制归基类、格式归 codec——族私有契约，禁跨族共用）");
        _fileSystem = fs;
        _settings = settings;
        KeyResolver = keyResolver;
        _codec = codec;

        // ★ 主引擎（构造期 Create 纯装配零 IO——对齐 RingBase/LogBase；启动在 OnInitializeBegin，就绪等待在恢复核心）
        _engine = new StorageEngine(fs, settings.MainEngine, workerScheduler: settings.WorkerScheduler);
        Resources.Add(_engine, ownership: ResourceOwnership.Owned);
        SectorSize = (int)_engine.SectorSize;

        _epoch = epoch ?? new LightEpoch();
        Resources.Add(_epoch, ownership: epoch is null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);

        KeyComparer = keyComparer ?? new KeyComparer<TKey>();
        _beginAddress = _engine.MinAddress;
    }

    /// <summary>启动主引擎（非阻塞——就绪由族恢复核心开头 await 保证；水位线归结构层，引擎自恢复不下传 hint）。</summary>
    protected override void OnInitializeBegin()
    {
        // ★ 引擎启动（非阻塞——就绪由族恢复核心开头 await 保证）。水位线归结构层：引擎自恢复不下传 hint。
        //   锚点槽预留归恢复核心（引擎就绪后、节点分配前——见 Recovery.cs/Persistence.cs）。
        _engine.Initialize();
    }

    /// <summary>创建默认恢复实现（RecoveryBase 模板派生——只填恢复算法：hints → 主存储帧 → 全量重放三级回退）。</summary>
    /// <returns>默认比较族恢复实例。</returns>
    protected override IRecovery<SortedIndexRecoveryHints>? CreateRecovery() => new DefaultSortedIndexBaseRecovery(this);

    // ══ 族器官：引擎节点持久化原语 ══

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected LogicalAddress AllocateNode(int nodeSize)
    {
        return _engine.Allocate(nodeSize).Start;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void WriteNode(LogicalAddress addr, ReadOnlySpan<byte> data)
    {
        _engine.Write(addr, data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected int ReadNode(LogicalAddress addr, Span<byte> buf)
    {
        return _engine.Read(addr, buf);
    }

    /// <summary>回收节点物理空间（删除/重平衡路径）。
    /// <para>★ 审计 #175 收口：物理回收走 <see cref="LightEpoch.DrainThen"/>——等既有读者
    ///   全部退出旧 epoch 后才执行 <c>engine.ReclaimTail</c>（防 use-after-free；
    ///   同 MetadataBase.Abort 物理回收范式）。原实现为裸 Suspend → ReclaimTail → Resume，
    ///   回收期间读者仍可能持有已释放节点。调用方无需持 epoch；回收可能延迟到任意读者线程触发。</para></summary>
    private protected void ReclaimNode(LogicalAddress addr)
    {
        _epoch.DrainThen(() => _engine.ReclaimTail(addr));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static bool CasSlot(ref LogicalAddress slot, LogicalAddress expected, LogicalAddress desired)
    {
        ref var loc = ref Unsafe.As<LogicalAddress, NativeInt128>(ref slot);
        var old = Unsafe.As<LogicalAddress, NativeInt128>(ref expected);
        var @new = Unsafe.As<LogicalAddress, NativeInt128>(ref desired);
        return NativeAtomic128.CompareExchange(ref loc, old, @new);
    }

    private protected void ResumeEpoch() => _epoch.Resume();
    private protected void SuspendEpoch() => _epoch.Suspend();
    private protected void BumpEpoch() => _epoch.BumpCurrentEpoch(() => { });

    // ══ 抽象面 ══

    /// <summary>
    /// ★ 不含 epoch 进出的查找——epoch 由调用方经 <see cref="EnterScope"/> / <see cref="FindBatch"/> 在外层持有。
    /// 子类实现零拷贝下降（跳表塔链 / B+树扁平缓存下降）。
    /// </summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    protected abstract LogicalAddress FindNoEpoch(TKey key);

    /// <summary>点查 key → value 逻辑地址（epoch 读保护内完成）。</summary>
    /// <param name="key">查找键。</param>
    /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
    public abstract LogicalAddress Find(TKey key);

    /// <summary>插入条目（key → valueAddress；同 key 覆写 value 不增计数），返回插入后地址（epoch 读保护内完成）。</summary>
    /// <param name="key">条目键。</param>
    /// <param name="valueAddress">条目 value 逻辑地址。</param>
    /// <param name="beginAddress">结构起始地址（重放路径约定参数——比较族插入不消费，保留接口对称）。</param>
    /// <returns>插入后地址。</returns>
    public abstract LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress);

    /// <summary>删除条目（epoch 读保护内完成）。</summary>
    /// <param name="key">条目键。</param>
    /// <returns>true = 真删到；false = 不存在。</returns>
    public abstract bool Delete(TKey key);

    /// <summary>
    /// 键序前缀批量删——删除全部 key &lt; <paramref name="boundExclusive"/> 的条目（retention trim 专用，
    /// 批量 O(覆盖区) 而非逐键 O(k·log n)；epoch 读保护内完成）。
    /// </summary>
    /// <param name="boundExclusive">边界键（不含）——全部 key &lt; 此值的条目被删除。</param>
    /// <returns>删除条数。</returns>
    public abstract long TruncatePrefix(TKey boundExclusive);

    /// <summary>条目数（写者维护计数——O(1)）。</summary>
    public abstract long EntryCount { get; }

    /// <summary>索引内存占用估算（字节——子类按结构形态估算）。</summary>
    public abstract long IndexSize { get; }

    /// <summary>有序遍历游标（比较族独有能力——range scan）。</summary>
    /// <param name="direction">遍历方向（Forward = 键升序，Backward = 键降序）。</param>
    /// <returns>新建的 <see cref="IIndexScanCursor{TKey}"/> 有序游标实例。</returns>
    public abstract IIndexScanCursor<TKey> CreateScanCursor(ReadDirection direction);

    /// <summary>
    /// 键序最大条目（Latest 语义——时序/版本链的"最新点查"；epoch 读保护内完成，O(log n)）。
    /// </summary>
    /// <param name="key">输出：最大条目的 key。</param>
    /// <param name="value">输出：最大条目的 value 逻辑地址。</param>
    /// <returns>false = 空索引。</returns>
    public abstract bool TryGetMax(out TKey key, out LogicalAddress value);

    /// <summary>
    /// 键序 ≤ <paramref name="key"/> 的最大条目（floor/采样语义——Prometheus @ 采样、前驱点查；
    /// epoch 读保护内完成，O(log n)）。
    /// </summary>
    /// <param name="key">查找键（含）。</param>
    /// <param name="floorKey">输出：命中条目的 key（≤ 查找键）。</param>
    /// <param name="value">输出：命中条目的 value 逻辑地址。</param>
    /// <returns>false = 无 ≤ key 的条目。</returns>
    public abstract bool TryGetFloor(TKey key, out TKey floorKey, out LogicalAddress value);

    /// <summary>
    /// 键序 &lt; <paramref name="key"/> 的最大条目（严格前驱——反向步进迭代原语：从 TryGetMax 起
    /// 逐步 TryGetPrev 即倒序遍历，O(k·log n)；两索引游标均仅 Forward（叶/层 0 链无 Prev 指针），
    /// 有序倒序面由本原语承担。epoch 读保护内完成，O(log n)）。
    /// </summary>
    /// <param name="key">查找键（不含——等值条目不算前驱）。</param>
    /// <param name="prevKey">输出：命中条目的 key（&lt; 查找键）。</param>
    /// <param name="value">输出：命中条目的 value 逻辑地址。</param>
    /// <returns>false = 无 &lt; key 的条目。</returns>
    public abstract bool TryGetPrev(TKey key, out TKey prevKey, out LogicalAddress value);

    // ══ 共享模板（各族自持——设计稿：不设公共基类）══

    /// <summary>进入读保护 scope（ref struct <see cref="IndexScope"/>——创建即 Resume epoch，Dispose 即 Suspend；scope 内 Find 省逐次 epoch 进出）。</summary>
    /// <returns>读保护 scope。</returns>
    public IndexScope EnterScope() => new(this);

    /// <summary>★ epoch 读保护协议实现（IEpochProtected——Session 读 scope 聚合入口；IndexScope 转发此真源）。</summary>
    public void EnterEpoch()
    {
        ThrowIfDisposed();
        _epoch.Resume();
    }

    /// <summary>退出 epoch 读保护（与 <see cref="EnterEpoch"/> 成对——Session 读 scope 聚合入口转发）。</summary>
    public void ExitEpoch() => _epoch.Suspend();

    /// <summary>批量点查（keys → results，单次操作闸 + 同一轮 epoch——零逐查 Resume/Suspend 开销）。</summary>
    /// <param name="keys">输入键集合。</param>
    /// <param name="results">输出地址数组（长度须 ≥ keys 长度，不足抛 ArgumentException）。</param>
    public void FindBatch(ReadOnlySpan<TKey> keys, Span<LogicalAddress> results)
    {
        if (keys.Length > results.Length)
            throw new ArgumentException($"results length({results.Length}) < keys length({keys.Length})", nameof(results));
        ThrowIfDisposed();
        using var _ = EnterOp();
        _epoch.Resume();
        try
        {
            for (int i = 0; i < keys.Length; i++)
                results[i] = FindNoEpoch(keys[i]);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>读保护 scope（ref struct——EnterScope 创建即 Resume epoch，Dispose 即 Suspend；Find 走 FindNoEpoch 零进出开销）。</summary>
    public readonly ref struct IndexScope
    {
        private readonly SortedIndexBase<TKey> _owner;
        internal IndexScope(SortedIndexBase<TKey> owner)
        {
            owner.EnterEpoch();
            _owner = owner;
        }

        /// <summary>scope 内单查（操作闸 + FindNoEpoch 转发）——epoch 已由 scope 持有，省逐次 Resume/Suspend（~10ns/op）。</summary>
        /// <param name="key">查找键。</param>
        /// <returns>命中 = value 逻辑地址；未命中 = <see cref="LogicalAddress.Empty"/>。</returns>
        public LogicalAddress Find(TKey key)
        {
            using var _ = _owner.EnterOp();
            return _owner.FindNoEpoch(key);
        }

        /// <summary>退出 scope（Suspend epoch——与 EnterScope 的 Resume 成对）。</summary>
        public void Dispose()
        {
            _owner?.ExitEpoch();
        }
    }
}
