namespace TC.Tier.Runtime.Meta;

/// <summary>
/// Managed meta 策略——独立 <c>IStorageEngine</c> + 四段自描述块 + Magic/CRC 校验。
/// <para>★ 统一布局：[统一 Header N B][内部水位线（结构化）][外部 opaque（实际用量）][统一 Footer CRC 4B]。</para>
/// <para>★ 自描述核心（设计决策 ）：header.PayloadLength = 水位字节数 + opaque <b>实际用量</b>；
///   水线位置 = HeaderSize（固定）；opaque 范围 = 其后至 PayloadLength；footer 位 = HeaderSize + PayloadLength。
///   <b>容量（可变的启动配置）零参与盘上几何</b>——跨重启容量随便调，读侧全按盘上自述定位，水位无条件恢复。</para>
/// <para>★ 容量（MetaOpaqueBytes）只做写侧约束：opaque 写入上限 + 缓冲分配 + 引擎段几何。</para>
/// <para>★ CRC32C 覆盖 Header + 水位 + 实际 opaque（到 footer 前），存于 Footer。</para>
/// </summary>
public sealed class ManagedMetaPolicy<THeader, TPayload>(
    IMetaLayout<THeader, TPayload> metaLayout,
    IStorageEngine metaStorage,
    ILogger? logger = null)
    : IMetaPolicy<THeader, TPayload>
    where THeader : struct
    where TPayload : struct
{
    private const int DioAlignment = 4096;

    /// <summary>日志通道（CS9113 收口：捕获保留——当前无日志点，供后续诊断接入）。</summary>
    private readonly ILogger? _logger = logger;

    private readonly int _headerSize = metaLayout.HeaderSize;
    /// <summary>写侧块几何（容量槽，4K 对齐）——写入长度/buffer 分配/引擎段容量用；盘上解读不用它。</summary>
    private readonly int _blockSize = (metaLayout.HeaderSize + metaLayout.PayloadSize + Crc32FooterCodec.StructSize).AlignUp(DioAlignment);
    private readonly int _bufferLength = (metaLayout.HeaderSize + metaLayout.PayloadSize + Crc32FooterCodec.StructSize).AlignUp(DioAlignment);
    private readonly AlignedMemoryManager _buffer = new(
        (metaLayout.HeaderSize + metaLayout.PayloadSize + Crc32FooterCodec.StructSize).AlignUp(DioAlignment),
        DioAlignment);
    private bool _loaded;
    private bool _disposed;
    /// <summary>写侧 opaque 实际长度（≤ 容量；字段记账——header 倒推会读到旧值）。</summary>
    private int _opaqueLen;
    /// <summary>盘上 opaque 超本启动容量的溢出交付（按盘自述读出；写侧不保——下次 Commit 按新容量覆写）。</summary>
    private byte[]? _opaqueOverflow;

    private int PayloadOffset => _headerSize;
    private int StructPayloadSize => metaLayout.PayloadSize - metaLayout.PayloadOpaqueSize;
    private int OpaqueOffset => _headerSize + StructPayloadSize;
    private int OpaqueCapacity => metaLayout.PayloadOpaqueSize;
    /// <summary>★ footer 位置 = HeaderSize + PayloadLength（水位+实际 opaque，盘上/写缓冲自述）。</summary>
    private int FooterOffset => _headerSize + UsedLen;
    private int CrcCoverLen => FooterOffset;
    /// <summary>当前块实际用量（写缓冲 header.PayloadLength = 水位 + 实际 opaque）。</summary>
    private int UsedLen => metaLayout.GetPayloadLength(metaLayout.ReadHeader(_buffer.GetSpan(0, _headerSize)));

    /// <summary>最小可读长度（header + footer）——Load 门槛不按当前配置的块几何预判（跨重启容量变更合法）。</summary>
    private int MinBlockLen => _headerSize + Crc32FooterCodec.StructSize;

    public int PayloadSize => metaLayout.PayloadSize;

    // ════════════════════════════════════════════════════════════
    // === Load（自描述：位置全由盘上 header.PayloadLength 推出）===
    // ════════════════════════════════════════════════════════════

    public bool Load()
    {
        ThrowIfDisposed();
        _loaded = false;
        _opaqueOverflow = null;
        if (metaStorage.AllocatedTail.Offset < MinBlockLen) return false;
        try
        {
            // ① 读 header 自述用量 → ② footer 位 = HeaderSize+PayloadLength → ③ 整块读 + 验 CRC
            if (metaStorage.Read(LogicalAddress.Empty, _buffer.GetSpan(0, _headerSize)) < _headerSize) return false;
            int needLen = _headerSize + UsedLen + Crc32FooterCodec.StructSize;
            if (needLen <= _bufferLength)
            {
                if (metaStorage.Read(LogicalAddress.Empty, _buffer.GetSpan(0, needLen)) < needLen) return false;
                if (!ValidateBlock(_buffer.GetSpan(0, needLen))) return false;
                AdoptLoaded(_buffer.GetSpan(0, needLen), fromBuffer: true);
                return true;
            }
            // 盘上块超本启动容量几何（前次启动更大容量写入）——临时对齐缓冲按盘自述全量读
            using var temp = new AlignedMemoryManager(needLen.AlignUp(DioAlignment), DioAlignment);
            if (metaStorage.Read(LogicalAddress.Empty, temp.GetSpan(0, needLen)) < needLen) return false;
            if (!ValidateBlock(temp.GetSpan(0, needLen))) return false;
            AdoptLoaded(temp.GetSpan(0, needLen), fromBuffer: false);
            return true;
        }
        catch { return false; }
    }

    public async ValueTask<bool> LoadAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        _loaded = false;
        _opaqueOverflow = null;
        if (metaStorage.AllocatedTail.Offset < MinBlockLen) return false;
        try
        {
            if (await metaStorage.ReadAsync(LogicalAddress.Empty, _buffer.Memory[.._headerSize], ct).ConfigureAwait(false) < _headerSize) return false;
            int needLen = _headerSize + UsedLen + Crc32FooterCodec.StructSize;
            if (needLen <= _bufferLength)
            {
                if (await metaStorage.ReadAsync(LogicalAddress.Empty, _buffer.Memory[..needLen], ct).ConfigureAwait(false) < needLen) return false;
                if (!ValidateBlock(_buffer.GetSpan(0, needLen))) return false;
                AdoptLoaded(_buffer.GetSpan(0, needLen), fromBuffer: true);
                return true;
            }
            using var temp = new AlignedMemoryManager(needLen.AlignUp(DioAlignment), DioAlignment);
            if (await metaStorage.ReadAsync(LogicalAddress.Empty, temp.Memory[..needLen], ct).ConfigureAwait(false) < needLen) return false;
            if (!ValidateBlock(temp.GetSpan(0, needLen))) return false;
            AdoptLoaded(temp.GetSpan(0, needLen), fromBuffer: false);
            return true;
        }
        catch { return false; }
    }

    /// <summary>验证整块：magic/version + CRC（覆盖 Header+水位+实际 opaque，到 footer 前）。</summary>
    private bool ValidateBlock(ReadOnlySpan<byte> block)
    {
        var header = metaLayout.ReadHeader(block[.._headerSize]);
        if (metaLayout.GetMagicValue(header) != metaLayout.Magic) return false;
        if (metaLayout.GetVersion(header) != metaLayout.CurrentVersion) return false;
        int footerPos = _headerSize + metaLayout.GetPayloadLength(header);
        uint computed = UnifiedCrc.ComputeCrc32C(block[..footerPos]);
        return computed == Crc32FooterCodec.Read(block[footerPos..]).Crc;
    }

    /// <summary>采纳已验证块：水位在固定几何位（无条件恢复）；opaque 按盘自述交付（超容量走溢出、写侧归零）。</summary>
    private void AdoptLoaded(ReadOnlySpan<byte> block, bool fromBuffer)
    {
        int usedLen = metaLayout.GetPayloadLength(metaLayout.ReadHeader(block[.._headerSize]));
        int diskOpaque = usedLen - StructPayloadSize;
        if (diskOpaque > OpaqueCapacity)
        {
            // 前次启动容量更大：header+水位拷回写缓冲（后续水位 Commit 可用）；opaque 溢出交付、写侧归零
            if (!fromBuffer)
                block[..(_headerSize + StructPayloadSize)].CopyTo(_buffer.GetSpan(0, _headerSize + StructPayloadSize));
            _opaqueOverflow = diskOpaque > 0 ? block.Slice(OpaqueOffset, diskOpaque).ToArray() : null;
            _opaqueLen = 0;
        }
        else
        {
            if (!fromBuffer)
                block[..(_headerSize + usedLen)].CopyTo(_buffer.GetSpan(0, _headerSize + usedLen));
            _opaqueOverflow = null;
            _opaqueLen = diskOpaque;
        }
        _loaded = true;
    }

    // === Header 读写（纯规范字段）===

    public void WriteHeader(THeader header)
    {
        ThrowIfDisposed();
        metaLayout.WriteHeader(_buffer.GetSpan(0, _headerSize), in header, validate: true);
    }

    public THeader? ReadHeader()
    {
        ThrowIfDisposed();
        if (!_loaded) return null;
        return metaLayout.ReadHeader(_buffer.GetSpan(0, _headerSize));
    }

    // === 结构化水位 Payload 读写 ===

    public void WritePayload(in TPayload payload)
    {
        ThrowIfDisposed();
        _opaqueOverflow = null;   // 写周期开始——盘上此后的 opaque 以缓冲为准
        metaLayout.WritePayload(_buffer.GetSpan(PayloadOffset, StructPayloadSize), in payload);
        UpdatePayloadLength();
    }

    public TPayload? ReadMetaPayload()
    {
        ThrowIfDisposed();
        if (!_loaded) return null;
        return metaLayout.ReadPayload(_buffer.GetSpan(PayloadOffset, StructPayloadSize));
    }

    // === Opaque 扩展读写（写在结构化水位之后；写入受本启动容量约束）===

    public void WritePayload(ReadOnlySpan<byte> opaque)
    {
        ThrowIfDisposed();
        if (opaque.Length > OpaqueCapacity)
            throw new ArgumentException($"opaque data {opaque.Length} exceeds opaque capacity {OpaqueCapacity}");
        var opaqueSpan = _buffer.GetSpan(OpaqueOffset, OpaqueCapacity);
        opaqueSpan.Clear();
        opaque.CopyTo(opaqueSpan);
        _opaqueLen = opaque.Length;
        _opaqueOverflow = null;
        UpdatePayloadLength();
    }

    public ReadOnlySpan<byte> ReadPayload()
    {
        ThrowIfDisposed();
        if (!_loaded) return ReadOnlySpan<byte>.Empty;
        if (_opaqueOverflow is { Length: > 0 } overflow) return overflow;   // 盘上自述交付（可超本启动容量）
        return _opaqueLen > 0 ? _buffer.GetSpan(OpaqueOffset, _opaqueLen) : ReadOnlySpan<byte>.Empty;
    }

    /// <summary>更新 header.PayloadLength = 水位 + 当前 opaque 实际用量（自描述锚点）。</summary>
    private void UpdatePayloadLength()
    {
        var h = metaLayout.ReadHeader(_buffer.GetSpan(0, _headerSize));
        h = metaLayout.WithPayloadLength(h, (ushort)(StructPayloadSize + _opaqueLen));
        metaLayout.WriteHeader(_buffer.GetSpan(0, _headerSize), in h, validate: false);
    }

    // === 提交（缓冲 → 算 CRC → 写引擎 + Flush）===

    public void Commit()
    {
        ThrowIfDisposed();
        ComputeCrc();
        if (metaStorage.AllocatedTail.Offset < _blockSize)
            metaStorage.Allocate(_blockSize);
        metaStorage.Write(LogicalAddress.Empty, _buffer.GetSpan(0, _blockSize));
        metaStorage.Flush();
        _loaded = true;
    }

    public async ValueTask CommitAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        ComputeCrc();
        if (metaStorage.AllocatedTail.Offset < _blockSize)
            metaStorage.Allocate(_blockSize);
        await metaStorage.WriteAsync(LogicalAddress.Empty, _buffer.Memory[.._blockSize], ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        metaStorage.Flush();
        _loaded = true;
    }

    // === CRC helper（footer 位 = HeaderSize+PayloadLength 实际用量——与读侧同一自描述规则）===

    private void ComputeCrc()
    {
        var span = _buffer.GetSpan(0, _blockSize);
        uint crc = UnifiedCrc.ComputeCrc32C(span[..CrcCoverLen]);
        Crc32FooterCodec.Write(span[FooterOffset..], new Crc32Footer { Crc = crc });
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
