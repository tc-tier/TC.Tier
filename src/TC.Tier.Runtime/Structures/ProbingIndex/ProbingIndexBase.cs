using System.Runtime.CompilerServices;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Structures.ProbingIndex;

/// <summary>
/// 探测族基类（族 B：HashIndex）——条目 = 地址 + tag（不物化 key），判等经 <see cref="IKeyResolver{TKey}"/> 回读。
/// <para>★ 族器官（设计稿 §3.2，从旧 IndexBase 收编）：ComputeTag/ComputeHash（hash 路由）+
///   CasSlot（128bit 桶并发）+ GrowIndex（resize 语义）。KeyResolver 构造期必注入非空——判等闭环硬依赖显性化（废 null 断言）。
///   KeyResolver 同时是恢复数据面：恢复核心拉 ScanAsync 流自填桶（设计稿 §4，索引=派生数据、真相源在 record 流）。</para>
/// <para>★ 自建主存储（设计稿 V2/index-persistence-evolution-design.md，机制在 <c>ProbingIndexBase.Persistence.cs</c>）：
///   基类=机制容器（后台 dump 编排/版本链 N 版轮替/帧走链恢复载入/策略触发），子类只实现格式布局
///   （几何 + 桶区/溢出池逐槽拷贝 + 物化——铁律 10，对齐 LogBase/RingBase/MirrorBase）。
///   持久化是结构核心能力（可关闭 PersistenceKind=None），镜像通道注入面已退役。</para>
/// <para>★ 不持有比较族器官（节点引擎读写）；无有序遍历（CreateScanCursor 是比较族能力）。</para>
/// </summary>
public abstract partial class ProbingIndexBase<TKey> : LifecycleBase<ProbingIndexRecoveryHints>, ITransactionParticipant,
    TC.Tier.Contracts.Transactions.IEpochProtected, IIndex<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    private protected readonly StorageEngine _engine;
    private protected readonly LightEpoch _epoch;

    /// <summary>★ 判等闭环数据面（构造期必注入，永非 null）——tag 命中后回读真 key 校验 + 恢复拉流。</summary>
    private protected readonly IKeyResolver<TKey> KeyResolver;
    private protected readonly IKeyComparer<TKey> KeyComparer;

    private readonly IFileSystem _fileSystem;
    private protected readonly ProbingIndexSettings _settings;
    /// <summary>测试可观测位：上次恢复是否走了主存储载入路径（false=全量重放 fail-safe）。</summary>
    internal bool MainStorageAppliedLastRecovery { get; private protected set; }
    private protected int SectorSize { get; }

    private protected LogicalAddress _beginAddress;
    public LogicalAddress BeginAddress => _beginAddress;

    /// <summary>
    /// ★ 基类注入标准顺序（对齐 LogBase/RingBase/MirrorBase）：codec → fs → settings → 族特有。
    /// </summary>
    protected ProbingIndexBase(
        IProbingIndexCodec codec,
        IFileSystem fs,
        ProbingIndexSettings settings,
        IKeyResolver<TKey> keyResolver,
        LightEpoch? epoch = null,
        IKeyComparer<TKey>? keyComparer = null)
    {
        ArgumentNullException.ThrowIfNull(keyResolver,
            "探测族判等闭环强依赖 IKeyResolver（tag-only 桶 tag 命中后必须读回真 key 校验）——构造期必注入");
        ArgumentNullException.ThrowIfNull(codec,
            "探测族主存储格式 codec 必注入（机制归基类、格式归 codec——对齐 IMirrorCodec 律）");
        _fileSystem = fs;
        _settings = settings;
        KeyResolver = keyResolver;
        ProbingIndexCodec = codec;

        // ★ 主引擎（构造期 Create 纯装配零 IO——对齐 RingBase/LogBase；启动在 OnInitializeBegin，就绪等待在恢复核心）
        _engine = new StorageEngine(fs, settings.MainEngine);
        Resources.Add(_engine, ownership: ResourceOwnership.Owned);
        SectorSize = (int)_engine.SectorSize;

        _epoch = epoch ?? new LightEpoch();
        Resources.Add(_epoch, ownership: epoch is null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);

        KeyComparer = keyComparer ?? new KeyComparer<TKey>();
        _beginAddress = _engine.MinAddress;
    }

    protected override void OnInitializeBegin()
    {
        // ★ 引擎启动（非阻塞——就绪由族恢复核心开头 await 保证）。水位线归结构层：引擎自恢复不下传 hint。
        _engine.Initialize();
    }

    protected override IRecovery<ProbingIndexRecoveryHints>? CreateRecovery() => new DefaultProbingIndexBaseRecovery(this);

    // ══ 族器官：hash 路由 + 桶并发 ══

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ushort ComputeTag(TKey key)
    {
        ulong hash = KeyComparer.GetHashCode64(key);
        return (ushort)(hash >> 50);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected ulong ComputeHash(TKey key) => KeyComparer.GetHashCode64(key);

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

    protected abstract LogicalAddress FindNoEpoch(TKey key);

    public abstract LogicalAddress Find(TKey key);
    public abstract LogicalAddress Insert(TKey key, LogicalAddress valueAddress, LogicalAddress beginAddress);
    public abstract bool Delete(TKey key);
    public abstract long EntryCount { get; }
    public abstract long IndexSize { get; }

    public abstract void GrowIndex();

    // ══ 共享模板（各族自持——设计稿：不设公共基类）══

    public IndexScope EnterScope() => new(this);

    /// <summary>★ epoch 读保护协议实现（IEpochProtected——Session 读 scope 聚合入口；IndexScope 转发此真源）。</summary>
    public void EnterEpoch()
    {
        ThrowIfDisposed();
        ResumeEpoch();
    }

    public void ExitEpoch() => SuspendEpoch();

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

    public readonly ref struct IndexScope
    {
        private readonly ProbingIndexBase<TKey> _owner;
        internal IndexScope(ProbingIndexBase<TKey> owner)
        {
            owner.EnterEpoch();
            _owner = owner;
        }

        /// <summary>scope 内单查（FindNoEpoch 转发）——epoch 已由 scope 持有，省逐次 Resume/Suspend（~10ns/op）。</summary>
        public LogicalAddress Find(TKey key) => _owner.FindNoEpoch(key);

        public void Dispose()
        {
            _owner?.ExitEpoch();
        }
    }
}
