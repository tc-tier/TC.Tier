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

    /// <summary>探测起始地址（引擎 MinAddress——探测索引只在内存，不锚定固定槽，只锚定地址空间起点）。</summary>
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

    /// <summary>启动主引擎（非阻塞——就绪由族恢复核心开头 await 保证；水位线归结构层，引擎自恢复不下传 hint）。</summary>
    protected override void OnInitializeBegin()
    {
        // ★ 引擎启动（非阻塞——就绪由族恢复核心开头 await 保证）。水位线归结构层：引擎自恢复不下传 hint。
        _engine.Initialize();
    }

    /// <summary>创建默认恢复实现（RecoveryBase 模板派生——只填恢复算法：hints → 主存储帧 → 全量重放三级回退）。</summary>
    /// <returns>默认探测族恢复实例。</returns>
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

    /// <summary>
    /// ★ 不含 epoch 进出的查找——epoch 由调用方经 <see cref="EnterScope"/> / <see cref="FindBatch"/> 在外层持有。
    /// 子类实现 tag 命中 + KeyResolver 判等闭环。
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
    /// <param name="beginAddress">探测下限地址（重放路径约定参数）——槽内旧条目地址小于它视为陈旧，可覆写落位。</param>
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

    /// <summary>扩容（探测族独有能力——子类实现表代函数式重建，装载超阈值由 Insert 触发）。</summary>
    public abstract void GrowIndex();

    // ══ 共享模板（各族自持——设计稿：不设公共基类）══

    /// <summary>进入读保护 scope（ref struct <see cref="IndexScope"/>——创建即 Resume epoch，Dispose 即 Suspend；scope 内 Find 省逐次 epoch 进出）。</summary>
    /// <returns>读保护 scope。</returns>
    public IndexScope EnterScope() => new(this);

    /// <summary>★ epoch 读保护协议实现（IEpochProtected——Session 读 scope 聚合入口；IndexScope 转发此真源）。</summary>
    public void EnterEpoch()
    {
        ThrowIfDisposed();
        ResumeEpoch();
    }

    /// <summary>退出 epoch 读保护（与 <see cref="EnterEpoch"/> 成对——Session 读 scope 聚合入口转发）。</summary>
    public void ExitEpoch() => SuspendEpoch();

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
        private readonly ProbingIndexBase<TKey> _owner;
        internal IndexScope(ProbingIndexBase<TKey> owner)
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
