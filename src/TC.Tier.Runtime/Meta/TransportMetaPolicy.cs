namespace TC.Tier.Runtime.Meta;

/// <summary>
/// Transport meta 策略——经 <see cref="TC.Tier.Contracts.Meta.IMetaTransport"/> 写块/读最后一条。
/// <para>★ 统一布局：[Header N B][Payload 结构化水位 + opaque 扩展][Crc32Footer 4B]——
///   块格式与完整性校验完全在策略内，传输只搬字节（单槽覆盖/主流追加/远程皆可）。</para>
/// <para>★ 变长块：Commit 只写 Header + 实际 PayloadLength + Footer（无 4K 填充——嵌入主流时
///   不污染数据流）；读写走 4K 对齐的就地缓冲。</para>
/// <para>★ 契约：未 Load 读返回 null/Empty；Commit 后同实例可读；重新 Load 全量重置；
///   Load false = 空/无数据/校验失败（不区分）；Dispose 幂等。</para>
/// </summary>
public sealed class TransportMetaPolicy<THeader, TPayload>(
    IMetaLayout<THeader, TPayload> metaLayout,
    IMetaTransport transport,
    ILogger? logger = null)
    : IMetaPolicy<THeader, TPayload>
    where THeader : struct
    where TPayload : struct
{
    private const int FooterSize = 4;   // Crc32FooterCodec.StructSize

    /// <summary>日志通道（CS9113 收口：捕获保留——当前无日志点，供后续诊断接入）。</summary>
    private readonly ILogger? _logger = logger;

    private readonly int _headerSize = metaLayout.HeaderSize;
    private readonly int _structPayloadSize = metaLayout.PayloadSize - metaLayout.PayloadOpaqueSize;
    private readonly AlignedMemoryManager _buffer = new(
        metaLayout.HeaderSize + metaLayout.PayloadSize + FooterSize, 4096);   // 4K 对齐（DIO 类传输可直接用）
    private bool _loaded;
    private bool _disposed;
    /// <summary>当前 opaque 实际长度（字段记账——从 header 倒推会读到旧值，opaque 永远记不上）。</summary>
    private int _opaqueLen;

    public int PayloadSize => metaLayout.PayloadSize;

    private int OpaqueOffset => _headerSize + _structPayloadSize;
    private int OpaqueCapacity => metaLayout.PayloadOpaqueSize;
    // === Header 读写（纯规范字段）===

    public void WriteHeader(THeader header)
        // 用调用方入参写（validate 由 layout 防御性补全规范字段）
        => metaLayout.WriteHeader(_buffer.GetSpan(0, _headerSize), in header, validate: true);

    public THeader? ReadHeader()
        => !_loaded ? null : metaLayout.ReadHeader(_buffer.GetSpan(0, _headerSize));

    // === 结构化水位 Payload 读写 ===

    public void WritePayload(in TPayload payload)
    {
        metaLayout.WritePayload(_buffer.GetSpan(_headerSize, _structPayloadSize), in payload);
        UpdatePayloadLength();
    }

    public TPayload? ReadMetaPayload()
        => !_loaded ? null : metaLayout.ReadPayload(_buffer.GetSpan(_headerSize, _structPayloadSize));

    // === Opaque 扩展读写 ===

    public void WritePayload(ReadOnlySpan<byte> opaque)
    {
        if (opaque.Length > OpaqueCapacity)
            throw new ArgumentException($"opaque data {opaque.Length} exceeds opaque capacity {OpaqueCapacity}");
        var opaqueSpan = _buffer.GetSpan(OpaqueOffset, OpaqueCapacity);
        opaqueSpan.Clear();
        opaque.CopyTo(opaqueSpan);
        _opaqueLen = opaque.Length;
        UpdatePayloadLength();
    }

    public ReadOnlySpan<byte> ReadPayload()
    {
        if (!_loaded || _opaqueLen <= 0 || _opaqueLen > OpaqueCapacity) return ReadOnlySpan<byte>.Empty;
        return _buffer.GetSpan(OpaqueOffset, _opaqueLen);
    }

    /// <summary>更新 Header.PayloadLength = 结构化水位 + 当前 opaque。</summary>
    private void UpdatePayloadLength()
    {
        var h = metaLayout.ReadHeader(_buffer.GetSpan(0, _headerSize));
        h = metaLayout.WithPayloadLength(h, (ushort)(_structPayloadSize + _opaqueLen));
        metaLayout.WriteHeader(_buffer.GetSpan(0, _headerSize), in h, validate: false);
    }

    // === 提交：变长块 [Header][实际 Payload][Footer CRC] → 传输 ===

    public void Commit()
    {
        ThrowIfDisposed();
        transport.WriteBlock(BuildBlock());
        _loaded = true;
    }

    public async ValueTask CommitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var block = BuildBlock();
        await transport.WriteBlockAsync(block, ct).ConfigureAwait(false);
        _loaded = true;
    }

    /// <summary>★ 从就地缓冲拼出精确长度的块（Header.PayloadLength 已随写更新，Footer 现算）。</summary>
    private byte[] BuildBlock()
    {
        int payloadLen = _structPayloadSize + _opaqueLen;
        int blockLen = _headerSize + payloadLen + FooterSize;
        var block = new byte[blockLen];
        var span = block.AsSpan();

        _buffer.GetSpan(0, _headerSize).CopyTo(span[.._headerSize]);
        _buffer.GetSpan(_headerSize, payloadLen).CopyTo(span[_headerSize..(_headerSize + payloadLen)]);
        uint crc = UnifiedCrc.ComputeCrc32C(span[..(_headerSize + payloadLen)]);
        Crc32FooterCodec.Write(span[(_headerSize + payloadLen)..], new Crc32Footer { Crc = crc });
        return block;
    }

    // === 加载：传输读最后一条 → 校验 → 全量重置缓存 ===

    public bool Load()
    {
        ThrowIfDisposed();
        var block = transport.ReadLastBlock();
        return !block.IsEmpty && TryLoadFrom(block);
    }

    public async ValueTask<bool> LoadAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var block = await transport.ReadLastBlockAsync(ct).ConfigureAwait(false);
        return !block.IsEmpty && TryLoadFrom(block.Span);
    }

    private bool TryLoadFrom(ReadOnlySpan<byte> block)
    {
        // ★ 全量重置缓冲与记账：上一轮的 opaque/PayloadLength 不得残留
        _buffer.GetSpan(0, _headerSize + metaLayout.PayloadSize + FooterSize).Clear();
        _loaded = false;
        _opaqueLen = 0;

        int minSize = _headerSize + _structPayloadSize + FooterSize;
        if (block.Length < minSize) return false;
        var span = block;

        var h = metaLayout.ReadHeader(span[.._headerSize]);
        if (metaLayout.GetMagicValue(h) != metaLayout.Magic) return false;
        if (metaLayout.GetVersion(h) != metaLayout.CurrentVersion) return false;

        int payloadLen = metaLayout.GetPayloadLength(h);
        if (payloadLen < _structPayloadSize) return false;                          // 下限：结构化水位必须完整
        if (payloadLen > _structPayloadSize + OpaqueCapacity) return false;         // 上限：opaque 区容量
        if (_headerSize + payloadLen + FooterSize > block.Length) return false;     // 块长度界

        // Footer CRC 校验（覆盖 Header + Payload）
        int coverEnd = _headerSize + payloadLen;
        uint computed = UnifiedCrc.ComputeCrc32C(span[..coverEnd]);
        if (computed != Crc32FooterCodec.Read(span[coverEnd..(coverEnd + FooterSize)]).Crc) return false;

        int copyLen = Math.Min(block.Length, _buffer.GetSpan().Length);   // 防御钳制：传输应给精确块，超缓冲截拷
        span[..copyLen].CopyTo(_buffer.GetSpan(0, copyLen));
        _opaqueLen = payloadLen - _structPayloadSize;
        _loaded = true;
        return true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _buffer.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}
