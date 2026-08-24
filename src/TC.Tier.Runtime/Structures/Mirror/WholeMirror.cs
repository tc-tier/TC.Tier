using TC.Tier.Runtime.Structures.Mirror.Contracts;

namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>
/// WholeMirror——整体单链 checkpoint 镜像（统一帧格式，Single 链）。
/// <para>★ 子类实现面（COORDINATION §4 铁律 10）：codec（WMHD/WMFT、CRC64）+ 会话门面
///   （三拍直通基类帧原语）+ 单链钩子（链头推进/保留窗口）。机制（帧写入/走链/尾锚/嵌入 meta/
///   N=2/2PC）全部在 <see cref="MirrorBase"/>。</para>
/// <para>★ 写会话真流式：BeginSession → AppendChunk × N → EndSession——不知尺寸、零缓冲、
///   CRC64 边写边累积收官落尾（全程不需要知道总长）。</para>
/// <para>★ 生命周期：new + Initialize()（后台恢复）+ WaitForReady()。详见 src/TC.Tier.Core/docs/lifecycle.md。</para>
/// </summary>
public sealed partial class WholeMirror : MirrorBase
{
    private readonly WholeMirrorSettings _settings;
    private LogicalAddress _sessionPrevious = LogicalAddress.Invalid;   // BeginSession 时的链头（EndSession 落进尾）

    /// <summary>
    /// 初始化一个新的<see cref="WholeMirror"/>实例。
    /// </summary>
    /// <param name="fileSystem">组合根文件系统（TierFs 构造的 IFileSystem）。</param>
    /// <param name="settings">整体镜像设置。</param>
    /// <param name="recovery">可选的恢复算法实例。</param>
    /// <param name="metaPolicyFactory">可选的 meta 策略工厂。</param>
    /// <param name="metaTransport">可选的 meta 传输（Transport 模式用）。</param>
    public WholeMirror(
        IFileSystem fileSystem,
        WholeMirrorSettings settings,
        IRecovery<MirrorRecoveryHints>? recovery = null,
        MetaPolicyFactory<MirrorMetaHeader, MirrorMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null)
        : base(new Codec(), fileSystem, settings, recovery, metaPolicyFactory, metaTransport)
    {
        _settings = settings;
    }

    // ════════════════════════════════════════════════════════════
    // === 写会话门面：BeginSession → AppendChunk × N → EndSession（三拍直通基类）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 开启 checkpoint 写会话：分配会话版本 + 帧三拍之一（圈头+写瘦头——不预留、不知尺寸）。
    /// </summary>
    /// <returns>帧头地址（调用方持有，用于后续读/定位）。</returns>
    public LogicalAddress BeginSession()
    {
        BeginCheckpointSession();
        _sessionPrevious = _hasCommittedVersion ? _highestVersionAddress : LogicalAddress.Invalid;
        // ★ Create()：ValidEquals 规范字段（Version）自动填常量——只填变化字段
        var header = MirrorFrameHeaderCodec.Create();
        header.Flags = _codec.DefaultFlags;
        header.PageId = 0;
        header.LogicalAddress = 0;
        header.MirrorVersion = _sessionVersion;
        return BeginFrame(in header);
    }

    /// <summary>
    /// 顺序追加镜像 chunk（单线程契约：逐段追加，CRC64 边写边累积——大镜像零缓冲不驻内存）。
    /// </summary>
    /// <param name="chunk">chunk 字节。</param>
    public void AppendChunk(ReadOnlySpan<byte> chunk) => AppendFrameChunk(chunk);

    /// <summary>
    /// 结束写会话：帧三拍之三（CRC 收官落尾）+ Flush。Confirm 仍走 <see cref="MirrorBase.ConfirmCommitted"/>
    /// （帧保持 pending 直到 Confirm/Abort）。
    /// </summary>
    /// <returns>帧尾末地址。</returns>
    public LogicalAddress EndSession()
    {
        // ★ Create()：ValidEquals 规范字段（Version）自动填常量——只填变化字段
        var footer = MirrorFrameFooterCodec.Create();
        footer.Flags = _codec.DefaultFlags;
        footer.PreviousVersion = _sessionPrevious;
        footer.MirrorVersion = _sessionVersion;
        var end = EndFrame(in footer);
        _engine.Flush(); // 数据落盘点（2PC Prepare 再 flush 是幂等加固；非 2PC 用法在此即持久）
        return end;
    }

    // ════════════════════════════════════════════════════════════
    // === 读门面（基类帧几何/CRC 机制的公开直通）===
    // ════════════════════════════════════════════════════════════

    /// <summary>读镜像 chunk（offset 相对 payload 起始）。大镜像恢复按 chunk 切片读。</summary>
    /// <param name="frameHead">帧头地址（BeginSession 返回值 / HighestVersionAddress）。</param>
    /// <param name="offsetInPayload">chunk 在 payload 内的偏移。</param>
    /// <param name="dst">目标缓冲。</param>
    /// <returns>实际读取字节数。</returns>
    public int ReadChunk(LogicalAddress frameHead, long offsetInPayload, Span<byte> dst)
    {
        EnsureNotDisposed();
        EnsureReady();
        return ReadFramePayload(frameHead, offsetInPayload, dst);
    }

    /// <summary>
    /// 帧几何账目（payload 长 = 尾位−头−头结构——零长度字段，长度是推导的事实）。
    /// </summary>
    /// <param name="frameHead">帧头地址。</param>
    public MirrorFrameInfo? GetFrameInfo(LogicalAddress frameHead)
    {
        EnsureNotDisposed();
        EnsureReady();
        return TryGetFrameInfo(frameHead, out var info) ? info : null;
    }

    /// <summary>帧 payload 长度（推导）。无有效帧尾返回 0。</summary>
    public long GetPayloadLength(LogicalAddress frameHead)
    {
        EnsureNotDisposed();
        EnsureReady();
        return GetFramePayloadLength(frameHead);
    }

    /// <summary>
    /// 验证整个帧的 CRC（头+体+尾前缀 对 尾 Crc 字段——流式分段重读重算，大帧不驻内存）。
    /// </summary>
    public bool Verify(LogicalAddress frameHead)
    {
        EnsureNotDisposed();
        EnsureReady();
        return TryGetFrameInfo(frameHead, out var info) && VerifyFrame(info.Head, info.FooterAddress);
    }

    // ════════════════════════════════════════════════════════════
    // === 会话链头推进/回退 + N=2 保留窗口（业务钩子）===
    // ════════════════════════════════════════════════════════════

    /// <summary>Confirm：pending→committed 单链头推进（旧链头降为第二新——存在性用标志，Empty 是合法地址）。</summary>
    private protected override void OnConfirmSession()
    {
        if (!_hasPendingFrame) return;
        if (_hasCommittedVersion) // 之前已有已提交版本（ConfirmCommitted 在本钩子后才置位）
        {
            _secondNewestAddress = _highestVersionAddress;
            _hasSecondNewest = true;
        }
        _highestVersionAddress = _pendingFrameHead;
        ClearPendingFrame();
    }

    /// <summary>Abort：丢弃 pending（committed 链头未被会话触碰；尾截断由基类 Abort 统一执行）。</summary>
    private protected override void OnAbortSession() => ClearPendingFrame();

    /// <summary>N=2 保留窗口：第二新帧头地址（无第二新回退链头自身=不回收；
    /// 第二新恰在 Empty 时由基类 keepAddr==Empty 守卫保守跳过，下轮补收——正确性优先）。</summary>
    private protected override LogicalAddress ComputeRetainFloor()
        => _hasSecondNewest ? _secondNewestAddress : _highestVersionAddress;
}
