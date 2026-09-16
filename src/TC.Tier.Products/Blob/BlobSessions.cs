using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Products.Blob;

/// <summary>
/// 流式写会话——OpenWrite 直通结构层帧写入器（CRC64 增量流式累积，GB/TB 写路径内存 = 双缓冲页恒定）。
/// <para>★ Complete/Abort 显形（spec 裁定②）：<see cref="CompleteAsync"/> 收口登记（返回句柄）；
///   Dispose 未 Complete = Abort（尾截断回滚——结构层 append 可回滚语义），与 KV 会话 Dispose 收口惯例对齐。</para>
/// <para>★ 单会话契约：同一会话禁止并发写（结构层写尾单写者）；不同对象经产品写闸串行。</para>
/// </summary>
public sealed class BlobWriteSession : IDisposable, IAsyncDisposable
{
    private readonly TierBlob _owner;
    private readonly StreamSnapshot.StreamFrameWriter _writer;
    private readonly LogicalAddress _start;
    private readonly long _expectedLength;
    private readonly int _sectorSize;
    private long _written;
    private bool _completed;
    private bool _aborted;
    private bool _gateHeld;
    private bool _disposed;

    internal BlobWriteSession(TierBlob owner, StreamSnapshot.StreamFrameWriter writer,
        LogicalAddress start, long expectedLength, bool gateHeld, int sectorSize)
    {
        _owner = owner;
        _writer = writer;
        _start = start;
        _expectedLength = expectedLength;
        _gateHeld = gateHeld;
        _sectorSize = sectorSize;
    }

    /// <summary>对象句柄（帧流起始地址——Open 时即定，同址即同对象）。</summary>
    public LogicalAddress ObjectId => _start;

    /// <summary>已写入的用户数据字节数。</summary>
    public long BytesWritten => Volatile.Read(ref _written);

    /// <summary>定长契约目标（-1 = 不定长）。</summary>
    public long ExpectedLength => _expectedLength;

    /// <summary>异步写一段用户数据（CRC64 边写边累积；首次写自动加帧头）。</summary>
    /// <param name="data">数据字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">会话已收口。</exception>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ThrowIfClosed();
        await _writer.WriteAsync(data, ct).ConfigureAwait(false);
        Interlocked.Add(ref _written, data.Length);
    }

    /// <summary>同步写一段用户数据（单线程契约）。</summary>
    /// <param name="data">数据字节。</param>
    public void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfClosed();
        _writer.Write(data);
        Interlocked.Add(ref _written, data.Length);
    }

    /// <summary>
    /// 完成写入：帧尾收口（TotalLength/CRC64）→ 对象表登记（Prepare/Confirm 持久收口）→ 返回句柄。
    /// 完成后返回句柄 = 内存可见（表登记已持久）；数据面 fsync 走产品 <c>FlushAsync</c>（分配-持久分离）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象句柄 + 长度。</returns>
    /// <exception cref="InvalidOperationException">定长契约未写满 / 会话已收口 / 超 MaxBytes（不定长完成时判定）。</exception>
    public async ValueTask<BlobPutResult> CompleteAsync(CancellationToken ct = default)
    {
        ThrowIfClosed();
        if (_expectedLength >= 0 && Volatile.Read(ref _written) != _expectedLength)
            throw new InvalidOperationException(
                $"定长契约违约：expectedLength={_expectedLength}，实际写入 {Volatile.Read(ref _written)}——定长会话须恰好写满");

        // 数据区对齐补零（帧长 = 扇区整倍数——物理=逻辑不变式，重启恒等映射读回成立）
        var padLength = (int)(BlobObjectTable.FrameLengthOf(Volatile.Read(ref _written), _sectorSize)
                              - FrameOverhead - Volatile.Read(ref _written));
        if (padLength > 0)
            await _writer.WriteAsync(new byte[padLength], ct).ConfigureAwait(false);

        await _writer.CompleteAsync(ct).ConfigureAwait(false);

        var length = Volatile.Read(ref _written);
        var frameLength = BlobObjectTable.FrameLengthOf(length, _sectorSize);
        // 容量终判（不定长流唯一的容量观测点——超限即回滚，诚实拒绝）
        if (!_owner.CheckCapacityAfterWrite())
        {
            _aborted = true;
            await AbortRollbackAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"写入后超 MaxBytes 容量上限（{_owner.Options.MaxBytes}）——不定长流完成时判定，会话已回滚");
        }
        await _owner.RegisterObjectAsync(_start, length, frameLength, ct).ConfigureAwait(false);
        _completed = true;
        ReleaseGate();
        return new BlobPutResult(_start, length);
    }

    /// <summary>Abort（同步轨）——Dispose 未 Complete = Abort 语义的显式形态。</summary>
    public void Abort()
    {
        if (_completed || _aborted || _disposed) return;
        _aborted = true;
        AbortRollback();
        ReleaseGate();
    }

    /// <summary>同步释放——未 Complete 即 Abort（尾截断回滚）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed && !_aborted)
        {
            _aborted = true;
            AbortRollback();
        }
        ReleaseGate();
    }

    /// <summary>异步释放——未 Complete 即 Abort（尾截断回滚）。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_completed && !_aborted)
        {
            _aborted = true;
            await AbortRollbackAsync().ConfigureAwait(false);
        }
        ReleaseGate();
    }

    // —— 内部 ——

    private const int FrameOverhead = BlobObjectTable.FrameHeaderSize + BlobObjectTable.FrameFooterSize;

    /// <summary>Abort 回滚：底层帧写入器闭环（flush 悬干缓冲）→ 尾截断回退到会话起点（结构层 append 可回滚语义）。</summary>
    private async ValueTask AbortRollbackAsync()
    {
        await _writer.DisposeAsync().ConfigureAwait(false);   // 自动闭环未完成帧（含 flush）
        _owner.RollbackTail(_start);
    }

    private void AbortRollback()
    {
#pragma warning disable TCSG137 // 同步 Abort 轨：等底层帧闭环完成（结构层同步写路径同款惯例）
        _writer.DisposeAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore TCSG137
        _owner.RollbackTail(_start);
    }

    private void ReleaseGate()
    {
        if (_gateHeld)
        {
            _gateHeld = false;
            _owner.ReleaseWriteGate();
        }
    }

    private void ThrowIfClosed()
    {
        if (_completed || _aborted || _disposed)
            throw new InvalidOperationException("BlobWriteSession 已收口（Complete/Abort/Dispose）——不可再写");
    }
}

/// <summary>
/// 流式读会话——OpenRead 直通结构层帧读取器（CRC64 逐帧校验，双 buffer 预读，冷热透明）。
/// <para>★ 校验时机：读至对象末尾自动补读对齐补零触发帧尾解析——CRC64 校验失败抛 IOException。</para>
/// </summary>
public sealed class BlobReadSession : IAsyncDisposable
{
    private readonly StreamSnapshot.StreamFrameReader _reader;
    private readonly long _length;
    private readonly int _padLength;
    private long _delivered;
    private bool _verified;
    private bool _disposed;

    internal BlobReadSession(StreamSnapshot.StreamFrameReader reader, LogicalAddress objectId,
        long length, int padLength)
    {
        _reader = reader;
        ObjectId = objectId;
        _length = length;
        _padLength = padLength;
    }

    /// <summary>对象句柄。</summary>
    public LogicalAddress ObjectId { get; }

    /// <summary>对象长度（可读总字节数）。</summary>
    public long Length => _length;

    /// <summary>已交付字节数。</summary>
    public long Delivered => Volatile.Read(ref _delivered);

    /// <summary>帧校验是否已通过（读至对象末尾自动验帧；未读完 = false）。</summary>
    public bool IsVerified => Volatile.Read(ref _verified);

    /// <summary>顺序读一段（至多填满 dest；返回 0 = 对象读完，此时帧校验已收口）。</summary>
    /// <param name="dest">目标缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际读取字节数。</returns>
    /// <exception cref="IOException">帧尾 CRC64 校验失败。</exception>
    public async ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var remaining = _length - Volatile.Read(ref _delivered);
        if (remaining <= 0)
        {
            await DrainAndVerifyAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var toRead = (int)Math.Min(dest.Length, remaining);
        var n = await _reader.ReadDataAsync(dest[..toRead], ct).ConfigureAwait(false);
        if (n > 0)
            Interlocked.Add(ref _delivered, n);

        if (Volatile.Read(ref _delivered) >= _length)
            await DrainAndVerifyAsync(ct).ConfigureAwait(false);
        return n;
    }

    /// <summary>补读对齐补零触发帧尾解析 + CRC64 校验（幂等；补零 &lt; 1 扇区）。</summary>
    private async ValueTask DrainAndVerifyAsync(CancellationToken ct)
    {
        if (_verified) return;
        var scratch = new byte[Math.Max(_padLength, 1)];
        var total = 0;
        int got;
        while (total < _padLength
               && (got = await _reader.ReadDataAsync(scratch.AsMemory(total, _padLength - total), ct).ConfigureAwait(false)) > 0)
        {
            total += got;
        }
        EnsureVerified();
    }

    /// <summary>异步释放（幂等——未读完不校验，帧缓冲随结构层释放）。</summary>
    /// <returns>释放完成。</returns>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _reader.DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureVerified()
    {
        if (_verified) return;
        if (!_reader.IsFooterValid)
            throw new IOException($"对象 {ObjectId} 帧尾 CRC64 校验失败——数据损坏");
        if (_reader.TotalLength != _length + _padLength)
            throw new IOException($"对象 {ObjectId} 帧尾 TotalLength {_reader.TotalLength} ≠ 登记值 {_length + _padLength}——数据损坏");
        Volatile.Write(ref _verified, true);
    }
}
