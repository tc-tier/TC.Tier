using System.Runtime.CompilerServices;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;

namespace TC.Tier.Runtime.Structures.SortedIndex;

/// <summary>
/// 比较族基类（族 A：BTreeIndex/SkipListIndex）——条目物化 (TKey, 地址)，比较即路由，支持有序遍历。
/// <para>★ 族器官（设计稿 §3.2，从旧 IndexBase 收编）：AllocateNode/WriteNode/ReadNode/ReclaimNode
///   （引擎节点持久化原语——BTree 叶子/跳表节点的引擎寄生形态）。</para>
/// <para>★ 不持有探测族器官（判等 tag/CAS 桶/GrowIndex）；判等在条目内完成。
///   KeyResolver 可选注入——判等不需要（key 物化条目内），恢复重放需要（设计稿 §4 两族共需接口）：
///   有重放窗口而无 resolver 者，恢复核心 fail-fast。</para>
/// </summary>
public abstract partial class SortedIndexBase<TKey> : LifecycleBase<SortedIndexRecoveryHints>, ITransactionParticipant,
    IEpochProtected, IIndex<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private protected readonly StorageEngine _engine;
    private protected readonly LightEpoch _epoch;

    private protected readonly IKeyComparer<TKey> KeyComparer;

    /// <summary>★ 恢复重放数据面（可选注入——判等不需要，重放需要；窗口+null 者恢复期 fail-fast）。</summary>
    private protected readonly IKeyResolver<TKey>? KeyResolver;

    private readonly IFileSystem _fileSystem;
    private protected readonly SortedIndexSettings _settings;
    /// <summary>测试可观测位：上次恢复是否走了主存储载入路径（false=全量重放 fail-safe）。</summary>
    internal bool MainStorageAppliedLastRecovery { get; private protected set; }
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
        IKeyResolver<TKey>? keyResolver = null)
    {
        ArgumentNullException.ThrowIfNull(codec,
            "比较族主存储格式 codec 必注入（机制归基类、格式归 codec——族私有契约，禁跨族共用）");
        _fileSystem = fs;
        _settings = settings;
        KeyResolver = keyResolver;
        _codec = codec;

        // ★ 主引擎（构造期 Create 纯装配零 IO——对齐 RingBase/LogBase；启动在 OnInitializeBegin，就绪等待在恢复核心）
        _engine = new StorageEngine(fs, settings.MainEngine);
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

    private protected void ReclaimNode(LogicalAddress addr)
    {
        _epoch.Suspend();
        try { _engine.ReclaimTail(addr); }
        finally { _epoch.Resume(); }
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

    /// <summary>条目数（写者维护计数——O(1)）。</summary>
    public abstract long EntryCount { get; }

    /// <summary>索引内存占用估算（字节——子类按结构形态估算）。</summary>
    public abstract long IndexSize { get; }

    /// <summary>有序遍历游标（比较族独有能力——range scan）。</summary>
    public abstract IIndexScanCursor<TKey> CreateScanCursor(ReadDirection direction);

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

    /// <summary>批量点查（keys → results，同一轮 epoch 内完成——零逐查 Resume/Suspend 开销）。</summary>
    /// <param name="keys">输入键集合。</param>
    /// <param name="results">输出地址数组（长度须 ≥ keys 长度，不足抛 ArgumentException）。</param>
    public void FindBatch(ReadOnlySpan<TKey> keys, Span<LogicalAddress> results)
    {
        if (keys.Length > results.Length)
            throw new ArgumentException($"results length({results.Length}) < keys length({keys.Length})", nameof(results));
        ThrowIfDisposed();
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

        /// <summary>scope 内单查（FindNoEpoch 转发）——epoch 已由 scope 持有，省逐次 Resume/Suspend（~10ns/op）。</summary>
        public LogicalAddress Find(TKey key) => _owner.FindNoEpoch(key);

        /// <summary>退出 scope（Suspend epoch——与 EnterScope 的 Resume 成对）。</summary>
        public void Dispose()
        {
            _owner?.ExitEpoch();
        }
    }
}
