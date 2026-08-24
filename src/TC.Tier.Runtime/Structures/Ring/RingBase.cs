using System.Buffers;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// Ring 基类（abstract，不带泛型，控制全流程）——4 基类之一。
/// <para>★ 本质：固定槽数环形页池 + 全局单调逻辑地址 + 8 地址指针状态机（mutable→readonly→flushed→evicted）。
/// 可原地 RCU + 环形驱逐复用的混合日志。</para>
/// <para>★ 核心能力（base.md §2）：页池(每页 AMM+PinnedBufferPool) + 寻址(TryAllocate/Seal/GetPhysicalAddress)
/// + 8 水位指针三层分层 + epoch 驱逐 + meta 持久化(IRingMetaPolicy) + 2PC(ITransactionParticipant)。</para>
/// <para>★ 变化点（base.md §3）：record 字节几何插槽 4 个 abstract + 扫描 IRingScanCursor + 恢复 IRingRecovery
/// + meta IRingMetaPolicy + codec IRingCodec + CreateMetaPolicy abstract。</para>
/// <para>★ 热路径 TryAllocate/Seal/GetPhysicalAddress/GetSpan/GetInfo 是 non-virtual（编译期静态分派，零虚分发）。
/// record 几何插槽 abstract 但子类 sealed override → JIT 去虚化。扫描/恢复/meta/2PC（冷路径）走接口/virtual。</para>
/// <para>实现类（BlittableRing）只填几何插槽 + K/V 字节读写，按场景取用基类能力。</para>
/// <para>参见 base.md 全文。</para>
/// </summary>
public abstract partial class RingBase<TKey> : LifecycleBase<RingRecoveryHints>, ITransactionParticipant,
    TC.Tier.Contracts.Transactions.IEpochProtected, TC.Tier.Contracts.Structures.IKeyResolver<TKey>
    where TKey : unmanaged, IEquatable<TKey>
{
    // ★ 结构层是内部使用者：引擎字段用具体类型（StorageEngine internal，Initialize 不在接口面——
    //   启动经两阶段：构造期建引擎、OnInitializeBegin 调 Initialize——结构层不走 Builder）。
    private readonly StorageEngine _engine;
    private readonly StorageEngine? _overflowEngine;

    private readonly LightEpoch _epoch;

    // === 页几何（从 Settings 派生）===
    /// <summary>页大小（字节，2 的幂）。</summary>
    public int PageSize { get; }
    /// <summary>页大小的二进制位数（log2(PageSize)）。</summary>
    public int PageSizeBits { get; }
    /// <summary>页大小掩码（PageSize - 1）。</summary>
    internal int PageSizeMask { get; }
    /// <summary>页槽数（= MemorySize / PageSize，2 的幂，构造时校验）。</summary>
    public int PageCount { get; }
    /// <summary>页槽数掩码（PageCount - 1，热路径 &amp; 寻址用）。</summary>
    internal int PageCountMask { get; }
    /// <summary>扇区大小（DIO 对齐）。</summary>
    private protected int SectorSize { get; }

    // === 设备读 + 冷页回源：见 RingBase.Read.cs ===

    /// <summary>段大小（扫描游标跨段寻址用）。</summary>
    internal long SegmentSize => _engine.SegmentGrowthLimit;

    // === 8 地址指针（base.md §2.3 三层分层，全程 LogicalAddress）===
    // ★ 新模型（engine-migration §2/§5）：水位全部 LogicalAddress——大小不参与地址，根除位打包毒点。
    //   BeginAddress 读引擎 MinAddress（照 LogBase.cs:112）；TailAddress 读引擎 AllocatedTail；
    //   其余 6 指针是 Ring 自管的内存页池语义层（mutable→readonly→flushed→evicted 状态机）。
    // 持久化层（meta 必存）
    private  LogicalAddress _beginAddress;
    private  LogicalAddress _flushedUntilAddress;
    private  LogicalAddress _safeReadOnlyAddress;
    private  LogicalAddress _readOnlyAddress;
    // 内存水位层（meta 可选存，恢复时从 Begin 重建）
    private  LogicalAddress _headAddress;
    private  LogicalAddress _safeHeadAddress;
    // 内存簿记层（永不落盘，恢复时初始化为 SafeHeadAddress）
    private  LogicalAddress _closedUntilAddress;
    // ★ 关页/驱逐协调游标（LogicalAddress）
    private  LogicalAddress _ongoingCloseUntilAddress;

    // === 环形满背压 + 自动驱逐协调协议（lag 用字节计数，非地址）===
    /// <summary>tail 落后 head 的字节数（HeadOffsetLag），决定 mutable 区大小 + 自动驱逐触发点。</summary>
    private long _headOffsetLagBytes;
    /// <summary>tail 落后 readonly 的字节数（ReadOnlyLag），决定 mutable→readonly 转换点。</summary>
    private long _readOnlyLagBytes;
    /// <summary>留空的页数（0 ~ PageCount-1），控制 lag 大小。默认 PageCount-1（最大 mutable 区）。</summary>
    private int _emptyPageCount;

    /// <summary>头截断边界（= engine.MinAddress，此地址前数据已回收）。</summary>
    public LogicalAddress BeginAddress => _beginAddress;
    /// <summary>落盘边界（此地址前已写引擎）。</summary>
    /// <remarks>★ LogicalAddress 是 16B struct，Volatile/Interlocked 不直接支持；
    ///   Ring 水位推进在单写者上下文（epoch drain / _tailLock），普通读 + 推进方负责可见性。</remarks>
    public LogicalAddress FlushedUntilAddress => _flushedUntilAddress;
    public LogicalAddress SafeReadOnlyAddress => _safeReadOnlyAddress;
    public LogicalAddress ReadOnlyAddress { get => _readOnlyAddress; private protected set => _readOnlyAddress = value; }
    public LogicalAddress TailAddress => _tailAddress;
    public LogicalAddress HeadAddress => _headAddress;
    public LogicalAddress SafeHeadAddress => _safeHeadAddress;
    public LogicalAddress ClosedUntilAddress => _closedUntilAddress;

    // === codec（注入的 record 三段式 codec）===
    /// <summary>注入的三段式 record codec（对齐 LogCodec/BlobCodec）。</summary>
    private protected IRingCodec RingCodec { get; }

    /// <summary>key 定长字节数——sizeof(TKey) 编译期常量（JIT 特化内联），key 长度契约的类型事实。</summary>
    private protected static int KeySize => Unsafe.SizeOf<TKey>();

    /// <summary>meta 策略——构造期装配（构造=配置，Core 完整生命周期），永非 null 纯读。</summary>
    public IMetaPolicy<RingMetaHeader, RingMetaPayload> MetaPolicy { get; }

    // === 可变接口（构造注入）===
    private readonly RingCursorFactory<IRingScanCursor>? _cursorFactory;
    private readonly IRingSnapshot _ringSnapshot;
    private readonly RingSnapshotReaderFactory? _snapshotReaderFactory;
    private readonly RingSnapshotWriterFactory? _snapshotWriterFactory;
    private readonly IMetaTransport? _metaTransport;

    private readonly IFileSystem _fs;
    /// <summary>Managed 模式的 meta 引擎（构造期 Create；启动在 OnInitializeBegin，就绪等待在恢复核心）。</summary>
    private readonly StorageEngine? _metaEngine;

    // 溢出配置（实现类读用）
    private readonly OverflowPolicy _overflowPolicy;
    private readonly int _minOverflowSize;

    /// <summary>溢出写游标（绝对地址），写入时自动递增。</summary>
    internal LogicalAddress OverflowTailAddress => _overflowTailAddress;

    // 溢出引擎访问器（实现类溢出读写用）
    /// <summary>溢出引擎（OverflowPolicy=Enabled 时非 null，实现类溢出读写用）。</summary>
    private protected IStorageEngine? OverflowEngine => _overflowEngine;

    // === 冷页缓存（ClockCache——GetRecord 冷区回源后缓存页内容，避免重复 I/O） ===
    // ★ 用 AlignedMemoryManager（pinned native 内存）替代 byte[]——零 GC 压力，淘汰时 Dispose native
    private ClockCache<LogicalAddress, AlignedMemoryManager>? _coldPageCache;
    /// <summary>
    /// ★ 冷页缓存容量（ClockCacheCapacity 优先，否则按 ColdReadRatio 派生）。
    /// </summary>
    private int _coldCacheCapacity;

    // ★ 原始 settings 引用（Validate 追加约束 + InitializePolicies 读 ClockCacheCapacity/ColdReadRatio 用）
    private readonly RingSettings _settings;

    protected RingBase(IRingCodec codec,
        IFileSystem fs,
        RingSettings settings,
        IRecovery<RingRecoveryHints>? recovery = null,
        RingCursorFactory<IRingScanCursor>? cursorFactory = null,
        IRingSnapshot? ringSnapshot = null,
        MetaPolicyFactory<RingMetaHeader, RingMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        LightEpoch? epoch = null,
        ILogger? logger = null)
        : base(recovery, logger)
    {
        RingCodec = codec;
        _settings = settings;
        _fs = fs;
        _ringSnapshot = ringSnapshot ?? new RingSnapshot<RingBase<TKey>>(this);
        // ★ 校验（base.md §2.8，fail-fast 绝不带病运行）
        Validate(settings);

        PageSize = settings.PageSize;
        PageSizeBits = System.Numerics.BitOperations.Log2((uint)PageSize);
        PageSizeMask = PageSize - 1;
        PageCount = (int)(settings.MemorySize / settings.PageSize);
        PageCountMask = PageCount - 1;

        // ★ 主引擎（构造期 Create 纯装配零 IO——对齐 LogBase；启动在 OnInitializeBegin，就绪等待在恢复核心）
        _engine = new StorageEngine(fs, settings.MainEngine);
        Resources.Add(_engine, ownership: ResourceOwnership.Owned);
        SectorSize = (int)_engine.SectorSize;
        if (PageSize < SectorSize)
            throw new ArgumentException($"PageSize {PageSize} < 扇区大小 {SectorSize}（DIO 对齐契约）");

        // ★ 溢出引擎（构造期建——WiscKey 分离 value 流，同主引擎几何；启动在 OnInitializeBegin）
        _overflowPolicy = settings.OverflowPolicy;
        _minOverflowSize = settings.MinOverflowSize;
        if (_overflowPolicy == OverflowPolicy.Enabled)
        {
            var ovOptions = new StorageEngineOptions(
                settings.MainEngine.EngineName + ".overflow",
                preallocateFile: settings.MainEngine.PreallocateFile,
                deleteOnClose: settings.MainEngine.DeleteOnClose)
                .WithSegment(Math.Max(settings.PageSize, settings.MainEngine.SegmentGrowthLimit),
                    enableSegmentation: true)
                .WithHints(settings.MainEngine.Hints);
            _overflowEngine = new StorageEngine(fs, ovOptions);
            Resources.Add(_overflowEngine, ownership: ResourceOwnership.Owned);
        }

        _epoch = epoch ?? new LightEpoch();
        Resources.Add(_epoch, ownership: epoch is null ? ResourceOwnership.Owned : ResourceOwnership.Referenced);

        _metaTransport = metaTransport;

        // ★ Managed meta 引擎构造期内联构建（纯 Create 零 IO——启动在 OnInitializeBegin，与主引擎并行）
        if (settings.MetaPolicyKind == MetaPolicyKind.Managed)
        {
            var metaOptions = new StorageEngineOptions(
                    settings.MainEngine.EngineName + ".meta",
                    preallocateFile: false,
                    deleteOnClose: settings.MainEngine.DeleteOnClose)
                .WithSegment(Math.Max(4096, 1L << 20), enableSegmentation: false)
                .WithHints(settings.MainEngine.Hints);
            _metaEngine = new StorageEngine(_fs, metaOptions);
            // ★ meta 引擎进 Resources（owned）——ManagedMetaPolicy.Dispose 只释放自身 buffer，不管引擎
            Resources.Add(_metaEngine, "metaEngine");
            logger?.LogInformation("Managed meta engine created: {MetaEngineName}", _metaEngine.EngineName);
        }

        // ★ meta 策略构造期装配（构造=配置，Core 完整生命周期）：工厂注入优先，否则默认映射。
        metaPolicyFactory ??= CreateMetaPolicyDefault;   // 方法组——与注入工厂同为 MetaPolicyFactory 委托
        MetaPolicy = metaPolicyFactory(settings.MetaPolicyKind);
        Resources.Add(MetaPolicy, "metaPolicy");

        _cursorFactory = cursorFactory;

        // ★ 页池初始化推迟到 Initialize()（需要引擎已 Initialize）

        // ★ 水位初始化（全程 LogicalAddress，照 Log）：
        //   引擎 Initialize 时自己恢复 MinAddress/AllocatedTail。
        //   Ring 的 8 指针初始值 = 引擎 MinAddress（空盘 = Empty，新文件首条 record 落在引擎首个 Allocate 处）。
        //   真实 tail 由 TryAllocate 推进 _tailAddress；恢复路径（Recover）会按 meta/扫盘覆盖这些初值。
        var initAddr = _engine.MinAddress;
        // ★ ★★ FASTER hybrid log 地址模型（照 AllocatorBase，仅 long→LogicalAddress）：
        //   地址单调递增，永远向前；内存固定 N 槽，slot = pageSeq & PageCountMask 循环复用；
        //   写满一圈淘汰旧页（head 推进），tail 继续 append（地址不回退，不需要 ReclaimTail）。
        //   ★ _dataStart = 数据区起点（GetDistance 锚点，构造时 Allocate 确定确定地址）。
        //   ★ 引擎地址空间提供 100% 确定的地址，Ring 基于 _dataStart + GetDistance 做 100% 正确寻址。
        //   ★ 不用 Log 的窗口/EnsureSpace/ReclaimTail——Ring 是固定页池，地址单调增长。
        LogicalAddress dataStart;
        if (_engine.AllocatedTail > _engine.MinAddress)
        {
            dataStart = _engine.MinAddress;   // 有历史数据：起点 = 头水位（恢复会校正）
            _dataCapacity = _engine.GetDistance(_engine.MinAddress, _engine.AllocatedTail);
        }
        else
        {
            // 新文件：不预 Allocate 大块（避免跨段时序问题）。_dataStart 首次写入时 EnsureSpace 确定。
            dataStart = _engine.MinAddress;
            _dataCapacity = 0;   // 未 Allocate，首次 TryAllocate 时 EnsureSpace 按需 Allocate
        }
        _dataStart = dataStart;
        _beginAddress = initAddr;
        _flushedUntilAddress = initAddr;
        _headAddress = initAddr;
        _safeHeadAddress = initAddr;
        _closedUntilAddress = initAddr;
        _safeReadOnlyAddress = initAddr;
        _readOnlyAddress = initAddr;
        _tailAddress = dataStart;

        // ★ 算 lag 字节数（自动驱逐触发点）——lag 现在是纯字节数，不再是位打包地址
        //   EmptyPageCount 默认 PageCount-1（最大 mutable 区）
        _emptyPageCount = PageCount - 1;
        // 默认让 head lag = (PageCount-1) 页（留满 mutable 区）
        int headOffsetLagPages = PageCount - 1;
        _headOffsetLagBytes = (long)headOffsetLagPages * PageSize;
        _readOnlyLagBytes = (long)(settings.MutableFraction * headOffsetLagPages) * PageSize;
    }

    /// <summary>
    /// ★ 校验 RingSettings（base.md §2.8 全量校验契约）。失败立即 throw。
    /// </summary>
    private static void Validate(RingSettings s)
    {
        // (1) PageSize 是 2 的幂
        if (!Utility.IsPowerOfTwo(s.PageSize))
            throw new ArgumentException(
                $"PageSize {s.PageSize} 必须是 2 的幂。建议用 AlignmentConst.AlignmentXxx（如 AlignmentConst.Alignment32M）");

        // (2) PageSize 范围 [4KB, 1GB]
        if (s.PageSize is < 4096 or > (1 << 30))
            throw new ArgumentException($"PageSize {s.PageSize} 越界，须 [4KB, 1GB]（Offset 为 int 的硬约束 + DIO 对齐）");

        // (3) MemorySize >= PageSize + 整除
        if (s.MemorySize < s.PageSize)
            throw new ArgumentException($"MemorySize {s.MemorySize} < PageSize {s.PageSize}（总容量必须 >= 页大小）");
        if (s.MemorySize % s.PageSize != 0)
            throw new ArgumentException(
                $"MemorySize {s.MemorySize} 必须整除 PageSize {s.PageSize}。建议两者都从 AlignmentConst 取值");

        // (4) PageCount 是 2 的幂（热路径 & (PageCount-1) 位掩码正确性前提）
        int pageCount = (int)(s.MemorySize / s.PageSize);
        if (!Utility.IsPowerOfTwo(pageCount))
            throw new ArgumentException(
                $"页数 {pageCount} = MemorySize/PageSize 必须是 2 的幂（热路径位掩码寻址的正确性前提）");

        // (5) 页数性能范围
        if (pageCount > s.MaxPageCount)
            throw new ArgumentException(
                $"页数 {pageCount} 过多（PageSize={s.PageSize/(1<<20)}MB 太小）——扫描/驱逐开销随页数线性增长。" +
                $"建议增大 PageSize 或调大 MaxPageCount。");

        // (6) 段大小 >= 页大小
        if (s.MainEngine.SegmentGrowthLimit < s.PageSize)
            throw new ArgumentException($"SegmentSize {s.MainEngine.SegmentGrowthLimit} 必须 >= PageSize {s.PageSize}");

        // (7) MutableFraction (0,1)
        if (s.MutableFraction is <= 0 or >= 1)
            throw new ArgumentException($"MutableFraction {s.MutableFraction} 须在 (0,1) 开区间");

        // (8) ColdRecordBufferLimit >= HeaderSize (28 bytes)
        if (s.ColdRecordBufferLimit < 28)
            throw new ArgumentException(
                $"ColdRecordBufferLimit {s.ColdRecordBufferLimit} 须 >= 28 (HeaderSize)");
    }

    protected void InitializePolicies()
    {
        ThrowIfDisposed();
        int cap;
        if (_settings.ClockCacheCapacity.HasValue)
        {
            cap = Math.Max(4, _settings.ClockCacheCapacity.Value);
            int p = 4; while (p < cap) p <<= 1; cap = p;
        }
        else
        {
            double ratio = Math.Clamp(_settings.ColdReadRatio, 0.0, 1.0);
            _coldCacheCapacity = (int)(PageCount * ratio);
            cap = 4; while (cap < _coldCacheCapacity) cap <<= 1;
            cap = Math.Max(4, cap);
        }
        _coldPageCache = new ClockCache<LogicalAddress, AlignedMemoryManager>(cap, (_, amm) => _pagePool.ReturnAligned(amm));
        RecoverOverflowTail(null);
    }

    protected override void OnInitializeBegin()
    {
        // ★ 双引擎并行启动（均非阻塞）：主引擎 + 溢出引擎 + meta 引擎（Managed，自恢复）。
        //   引擎就绪由 RingRecovery.RecoverAsync 开头 await engine.WaitForReadyAsync 保证；
        //   overflow 引擎就绪由 RecoverOverflowTail 内部保证（它读 overflow engine 时引擎已恢复）。
        //   水位线归结构层：引擎自恢复（结构水位走 meta/扫盘，不下传——同 LogBase）。
        _engine.Initialize();
        _overflowEngine?.Initialize();
        _metaEngine?.Initialize();
    }

    /// <summary>★ 恢复算法工厂——默认 DefaultRingRecovery。在 Initialize 的 CAS 闸门内被调一次
    /// （基类单一创建点）；注入实例经构造函数直接赋 _recovery，不经本工厂。</summary>
    protected override IRecovery<RingRecoveryHints> CreateRecovery()
        => new DefaultRingRecovery(this);

    /// <summary>派生类初始化钩子——引擎就绪后调用。</summary>
    protected virtual void OnInitialize() { }

    private void InitWatermarks()
    {
        var initAddr = _engine.MinAddress;
        LogicalAddress dataStart;
        if (_engine.AllocatedTail > _engine.MinAddress)
        {
            dataStart = _engine.MinAddress;
            _dataCapacity = _engine.GetDistance(_engine.MinAddress, _engine.AllocatedTail);
        }
        else
        {
            dataStart = _engine.MinAddress;
            _dataCapacity = 0;
        }
        _dataStart = dataStart;
        _beginAddress = initAddr;
        _flushedUntilAddress = initAddr;
        _headAddress = initAddr;
        _safeHeadAddress = initAddr;
        _closedUntilAddress = initAddr;
        _safeReadOnlyAddress = initAddr;
        _readOnlyAddress = initAddr;
        _tailAddress = dataStart;

        _emptyPageCount = PageCount - 1;
        int headOffsetLagPages = PageCount - 1;
        _headOffsetLagBytes = (long)headOffsetLagPages * PageSize;
        _readOnlyLagBytes = (long)(_settings.MutableFraction * headOffsetLagPages) * PageSize;
    }

    /// <summary>确保对象未被释放。</summary>
    private protected void EnsureNotDisposed() => ThrowIfDisposed();

    /// <summary>★ 创建默认恢复策略（实现类可 override 提供专属恢复）。</summary>
    protected virtual IRecovery<RingRecoveryHints> CreateDefaultRecovery() => new DefaultRingRecovery(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsureReady()
    {
        ThrowIfDisposed();
        if (!IsReady) ThrowNotReady();
    }

    /// <summary>★ meta KeySize 锚点校验——盘上记录的 key 长度与实例 TKey 不符即 fail-fast（0 = 旧盘/未写锚点时跳过）。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void ValidateKeySizeAnchor(int persistedKeySize)
    {
        if (persistedKeySize != 0 && persistedKeySize != KeySize)
            throw new InvalidOperationException(
                $"Key size mismatch: this ring instance expects TKey of {KeySize}B but the volume meta records " +
                $"{persistedKeySize}B — wrong ring specialization opened this volume, aborting to avoid silent key corruption.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ThrowNotReady()
        => throw new InvalidOperationException(
            "Call Initialize first — 读写须在恢复完成后调用（水位未恢复，读写可能操作脏数据）");

    /// <summary>
    /// ★ 写 meta record 到 ring 流末尾（Transport 策略 MetaHost 传输写入）。
    /// <para>★ 独立于 <see cref="BlittableRing.WriteRecordCore"/>——不复用用户 record 写入路径。
    ///   meta record 作为带 <see cref="RecordFlags.FLAG_ENTRY_IS_META"/> 的特殊 record 追加到 TailAddress 之后，
    ///   payload = 内层 [RingMetaHeader][RingMetaPayload][Crc32Footer] meta block。</para>
    /// <para>★ 对齐 <c>LogBase.WriteMetaPayload</c>（LogBase.LogMeta.cs:47）——独立 private protected 写流原语。</para>
    /// <para>纯内存写（分配 tail + 写 header + 写 payload + CRC + Seal，零 device IO）；落盘靠 Prepare 末尾 FlushUntil。</para>
    /// <para>★★ flags 顺序约定：WriteHeader 时就设全 IS_META|VALID|SEALED（对齐 WriteRecordCore:162-163），
    ///   再 FillCrc（CRC 覆盖含 flags 字段），最后 Seal 幂等。若 FillCrc 在 Seal 前、且 Seal 改 flags，
    ///   则 VerifyCrc 必失败（CRC 覆盖的 flags 字段与 Seal 后不一致）。</para>
    /// <para>★★ epoch 契约同 WriteRecordCore：整个分配+写周期包在 Resume/Suspend 内，防页被驱逐回收。</para>
    /// </summary>
    /// <param name="metaBlock">内层 meta block（[RingMetaHeader][RingMetaPayload][Crc32Footer]）。</param>
    /// <returns>meta record 的逻辑地址。</returns>
    private protected unsafe LogicalAddress WriteMetaRecord(ReadOnlySpan<byte> metaBlock)
    {
        int blockSize = metaBlock.Length;
        int hdrSize = RingCodec.HeaderSize;
        // ★ meta block 尺寸须 fit 在一页内（对齐 LogBase.LogMeta.cs:54-56 的单 entry 约束）
        if (blockSize + hdrSize > PageSize)
            throw new InvalidOperationException(
                $"meta block {blockSize}B + header {hdrSize}B 不 fit 一页 {PageSize}B");

        int unaligned = hdrSize + blockSize;
        int aligned = (unaligned + RingCodec.Alignment - 1) & ~(RingCodec.Alignment - 1);
        ushort paddingLen = (ushort)(aligned - unaligned);

        _epoch.Resume();
        try
        {
            LogicalAddress addr = Allocate(aligned);
            long phys = GetPhysicalAddress(addr);

            var fields = new RingRecordFields(
                (ushort)(RecordFlags.FLAG_ENTRY_IS_META | RecordFlags.FLAG_RINGRECORD_VALID | RecordFlags.FLAG_RINGRECORD_SEALED),
                (uint)blockSize, paddingLen, LogicalAddress.Empty);
            var headerSpan = new Span<byte>((void*)phys, hdrSize);
            RingCodec.WriteHeader(headerSpan, in fields);
            metaBlock.CopyTo(new Span<byte>((void*)(phys + hdrSize), blockSize));
            if (paddingLen > 0)
                new Span<byte>((void*)(phys + unaligned), paddingLen).Clear();
            var recordSpan = new Span<byte>((void*)phys, hdrSize + blockSize);
            RingCodec.FillCrc(recordSpan, hdrSize, blockSize);
            Seal(addr, aligned);
            return addr;
        }
        finally { _epoch.Suspend(); }
    }

    /// <summary>★ 异步写 meta record（纯内存写，同步包装合理——对齐 WriteRecordAsync 范式）。</summary>
    private protected ValueTask<LogicalAddress> WriteMetaRecordAsync(ReadOnlyMemory<byte> metaBlock, CancellationToken ct)
    {
        LogicalAddress addr = WriteMetaRecord(metaBlock.Span);
        return new ValueTask<LogicalAddress>(addr);
    }

    // === ★ LogicalAddress 原子操作 helper（16B struct，Volatile/Interlocked 不直接支持）===
    // LogicalAddress(int SegId@0 + int Extension@4 + long Offset@8) = 16B，与 NativeInt128(Lo@0+Hi@8) 同内存布局，
    // Unsafe.As reinterpret 后用 NativeAtomic128.CompareExchange（cmpxchg16b，~5ns）。
    // Ring 水位推进多在单写者上下文（epoch drain / _tailLock），CAS-loop 单调推进足够。
    /// <summary>原子读 LogicalAddress（经 NativeInt128 reinterpret，16B 原子）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static LogicalAddress VolatileRead(ref LogicalAddress location)
        => location;   // 单写者上下文，普通读足够（推进方负责可见性，epoch drain/lock 串行化）

    /// <summary>原子写 LogicalAddress。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static void VolatileWriteAddr(ref LogicalAddress location, LogicalAddress value)
        => location = value;

    /// <summary>★ CAS 单调推进 LogicalAddress 水位（仅 newValue &gt; 当前值才推进，不回退，同 <see cref="Utility.MonotonicUpdate"/> 语义；LogicalAddress 版因 16 字节复合结构不能直接复用 long 版）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected static bool MonotonicUpdateAddr(ref LogicalAddress variable, LogicalAddress newValue, out LogicalAddress oldValue)
    {
        oldValue = variable;
        if (newValue.CompareTo(oldValue) <= 0) return false;
        variable = newValue;   // 单写者上下文（epoch drain / lock）——直接赋值
        return true;
    }


}
