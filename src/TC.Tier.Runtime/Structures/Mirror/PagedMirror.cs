using TC.Tier.Runtime.Structures.Mirror.Contracts;

namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>
/// PagedMirror——per-page 多链 checkpoint 镜像（统一帧格式，PerKey 链）。
/// <para>★ 子类实现面（COORDINATION §4 铁律 10）：codec（PMVH/PMFT、CRC32C）+ 页门面
///   （WritePage/ReadPage——每页一个帧，走基类帧三拍）+ per-page 字典钩子。
///   机制（帧写入/走链恢复/嵌入 meta/N=2/2PC）全部在 <see cref="MirrorBase"/>。</para>
/// <para>★ v2 帧化：页 record 从定长整块（头存长度+padding）改为同族帧（双魔术值推导长度、
///   padding 退役、帧长任意字节边界）——两种镜像一套格式一套机制。</para>
/// </summary>
public sealed partial class PagedMirror : MirrorBase
{
    private readonly PagedMirrorSettings _settings;

    // === per-page 链状态（多链：每页独立链头 + 第二新——N=2 保留窗口按所有页最小值）===
    private readonly Dictionary<long, LogicalAddress> _pageHeads = new();
    private readonly Dictionary<long, LogicalAddress> _pageSecond = new();
    private readonly Dictionary<long, LogicalAddress> _sessionWrites = new();   // 会话内写入（Confirm 转正）

    /// <summary>
    /// 初始化一个新的<see cref="PagedMirror"/>实例。
    /// </summary>
    /// <param name="fileSystem">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">分页镜像设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）。</param>
    public PagedMirror(
        IFileSystem fileSystem,
        PagedMirrorSettings settings,
        IRecovery<MirrorRecoveryHints>? recovery = null,
        MetaPolicyFactory<MirrorMetaHeader, MirrorMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null)
        : base(new Codec(), fileSystem, settings, recovery, metaPolicyFactory, metaTransport)
    {
        _settings = settings;
        PageSize = 1 << settings.LogPageSizeBits;
    }

    /// <summary>页大小（PageSize = 1 &lt;&lt; LogPageSizeBits，与源结构页对齐）。</summary>
    public int PageSize { get; }

    // ════════════════════════════════════════════════════════════
    // === 写路径：WritePage（无状态门面，可乱序——每页一个帧，走基类帧三拍）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 写一页（追加该页版本链新帧）。首个写入自动开启 checkpoint 会话。
    /// </summary>
    /// <param name="page">页标识（per-page 链键）。</param>
    /// <param name="startPage">会话起始页（调用方页区间语义锚点——链模型按 PageId 寻址，不参与地址计算）。</param>
    /// <param name="pageBytes">页数据（&gt; PageSize 截断；&lt; PageSize 记 FLAG_LAST_PARTIAL）。</param>
    /// <param name="logicalAddress">源页逻辑地址（随页透传）。</param>
    /// <returns>帧头地址。</returns>
    public LogicalAddress WritePage(long page, long startPage, ReadOnlySpan<byte> pageBytes, long logicalAddress = 0)
    {
        if (!_sessionActive) BeginCheckpointSession();

        int payloadLen = Math.Min(pageBytes.Length, PageSize);
        bool isLastPartial = payloadLen < PageSize;
        ushort flags = (ushort)(_codec.DefaultFlags
                     | (isLastPartial ? RecordFlags.FLAG_LAST_PARTIAL : (ushort)0));
        var previous = _pageHeads.TryGetValue(page, out var prev) ? prev : LogicalAddress.Invalid;

        // ★ Create()：ValidEquals 规范字段（Version）自动填常量——只填变化字段
        var header = MirrorFrameHeaderCodec.Create();
        header.Flags = flags;
        header.PageId = page;
        header.LogicalAddress = logicalAddress;
        header.MirrorVersion = _sessionVersion;
        var head = BeginFrame(in header);
        AppendFrameChunk(pageBytes[..payloadLen]);
        var footer = MirrorFrameFooterCodec.Create();
        footer.Flags = flags;
        footer.PreviousVersion = previous;   // 页内链指针（首版本 = Invalid——Empty 是合法地址不能当哨兵）
        footer.MirrorVersion = _sessionVersion;
        EndFrame(in footer);
        _sessionWrites[page] = head;
        return head;
    }

    // ════════════════════════════════════════════════════════════
    // === 读路径：ReadPage（读该页 committed 链头）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 读一页（该页 committed 链头）：帧几何推导 payload 长 → 读 payload → 全帧 CRC 验证。
    /// </summary>
    /// <param name="page">页标识。</param>
    /// <param name="startPage">会话起始页（语义锚点，不参与寻址）。</param>
    /// <param name="dest">目标缓冲。</param>
    /// <returns>(实际读取字节数, CRC 是否有效)。无该页/空链/帧损毁返回 (0, false)。</returns>
    public (int bytesRead, bool isValid) ReadPage(long page, long startPage, Span<byte> dest)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (!_pageHeads.TryGetValue(page, out var head)) return (0, false);
        if (!TryGetFrameInfo(head, out var info)) return (0, false);

        int payloadLen = (int)Math.Min(
            _engine.GetDistance(_engine.CalculationAddress(head, _codec.HeaderSize), info.FooterAddress),
            (long)dest.Length);
        int got = _engine.Read(_engine.CalculationAddress(head, _codec.HeaderSize), dest[..payloadLen]);
        bool valid = VerifyFrame(info.Head, info.FooterAddress);
        return (got, valid);
    }

    // ════════════════════════════════════════════════════════════
    // === 恢复扫盘钩子（业务钩子：per-page 链重建）===
    // ════════════════════════════════════════════════════════════

    /// <summary>扫盘重建：先调 base（全局水位），再按 PageId 重建页链（最高地址 = 链头）。</summary>
    private protected override void OnScanFrame(in MirrorFrameHeader header, LogicalAddress head, LogicalAddress footerAddress)
    {
        base.OnScanFrame(in header, head, footerAddress);
        if (_pageHeads.TryGetValue(header.PageId, out var old))
            _pageSecond[header.PageId] = old;
        _pageHeads[header.PageId] = head;
    }

    // ════════════════════════════════════════════════════════════
    // === 会话链头推进/回退 + N=2 保留窗口（业务钩子）===
    // ════════════════════════════════════════════════════════════

    /// <summary>Confirm：逐页推进链头（旧头降为该页第二新）+ 全局水位推到会话最高地址。</summary>
    private protected override void OnConfirmSession()
    {
        foreach (var (page, addr) in _sessionWrites)
        {
            _pageHeads.TryGetValue(page, out var old);
            _pageSecond[page] = old; // 字典存在性即标志——old 可为 Empty（页首 record 在地址 0，合法）
            _pageHeads[page] = addr;
            if (addr.CompareTo(_highestVersionAddress) > 0 || !_hasCommittedVersion)
                _highestVersionAddress = addr;
        }
        _sessionWrites.Clear();
    }

    /// <summary>Abort：丢弃会话写入（committed 页链头未被会话触碰）。</summary>
    private protected override void OnAbortSession()
    {
        _sessionWrites.Clear();
    }

    /// <summary>
    /// N=2 保留窗口：所有页 keepAddr 的最小值（引擎 MinAddress 是全局水位，
    /// 只能推进到所有页都同意回收的地址——否则误删某页在用版本）。
    /// 字典存在性判"有第二新"；keepAddr 恰为 Empty 时由基类守卫保守跳过本轮。
    /// </summary>
    private protected override LogicalAddress ComputeRetainFloor()
    {
        LogicalAddress floor = LogicalAddress.Empty;
        bool any = false;
        foreach (var (page, head) in _pageHeads)
        {
            var keep = _pageSecond.TryGetValue(page, out var second) ? second : head;
            if (!any || keep.CompareTo(floor) < 0)
            {
                floor = keep;
                any = true;
            }
        }
        return floor;
    }
}
