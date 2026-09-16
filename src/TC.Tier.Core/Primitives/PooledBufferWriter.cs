using System.Buffers;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 池化增长缓冲写器（<see cref="IBufferWriter{T}"/> 的租借形态）——背板数组经
/// <see cref="ArrayPool{T}"/> 租借、按需倍增（旧板归还），稳态零分配。
/// <para>★ 与 BCL ArrayBufferWriter 的差别：背板不入 LOH 常驻——Rent/Return 生命周期显式
/// （<see cref="Reset"/> 保留背板复用、<see cref="Dispose"/> 归还池）；写完即发送的帧场景
/// （raft AppendEntries 条目区直写）由调用方持实例跨 await，发送完成后才可 <see cref="Reset"/>。</para>
/// <para>★ 非线程安全（单写者——与 IBufferWriter 通用契约一致）。</para>
/// </summary>
public sealed class PooledBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly ArrayPool<byte> _pool;
    private byte[] _buffer;
    private int _written;

    /// <summary>构造（零分配——背板在首次 <see cref="GetSpan"/> 惰性租借）。</summary>
    /// <param name="pool">背板池（null = <see cref="ArrayPool{Byte}.Shared"/>）。</param>
    /// <param name="initialCapacity">首板容量下界（0 = 惰性——首次 GetSpan 按需租）。</param>
    public PooledBufferWriter(ArrayPool<byte>? pool = null, int initialCapacity = 0)
    {
        _pool = pool ?? ArrayPool<byte>.Shared;
        _buffer = initialCapacity > 0 ? _pool.Rent(initialCapacity) : [];
    }

    /// <summary>已写字节数（GetSpan/Advance 累计）。</summary>
    public int WrittenCount => _written;

    /// <summary>已写区间（背板前缀视图——<see cref="Advance"/> 后内容确定）。</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>已写区间（背板前缀视图——背板为租借数组，消费完成后才可复用/归还本写器）。</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <inheritdoc/>
    /// <param name="sizeHint">本批需要的额外字节数（可为 0——仅取剩余可写区间）；容量不足时倍增扩板。</param>
    /// <returns>可写 Memory 窗口（从已写末尾到背板末尾）；写入后须调用 <see cref="Advance"/> 确认。</returns>
    public Memory<byte> GetMemory(int sizeHint)
    {
        GetSpan(sizeHint);   // 确保容量（增长含旧板内容搬迁）
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc/>
    /// <param name="sizeHint">本批需要的额外字节数（可为 0——仅取剩余可写区间）；容量不足时倍增扩板。</param>
    /// <returns>可写 Span 窗口（从已写末尾到背板末尾），长度 = 背板容量 - <see cref="WrittenCount"/>；写入后须调用 <see cref="Advance"/> 确认。</returns>
    public Span<byte> GetSpan(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        var needed = _written + sizeHint;
        if (needed > _buffer.Length)
        {
            var newSize = Math.Max(needed, Math.Max(_buffer.Length * 2, 512));
            var grown = _pool.Rent(newSize);
            _buffer.AsSpan(0, _written).CopyTo(grown);
            if (_buffer.Length > 0) _pool.Return(_buffer);
            _buffer = grown;
        }
        return _buffer.AsSpan(_written, _buffer.Length - _written);
    }

    /// <inheritdoc/>
    /// <param name="count">本次实际写入的字节数，非负且不得超过 <see cref="GetSpan(int)"/> 给出窗口的长度。</param>
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written + count > _buffer.Length)
            throw new ArgumentOutOfRangeException(nameof(count), count, "Advance 超背板容量（GetSpan 给出的窗口）。");
        _written += count;
    }

    /// <summary>清零已写计数、保留背板复用（稳态零分配——下一批 GetSpan 直写）。
    /// ★ 仅当此前 WrittenMemory 的消费已全部完成（发送/拷贝落定）才可调用。</summary>
    public void Reset() => _written = 0;

    /// <summary>归还背板（终态——此后不可再写；幂等）。</summary>
    public void Dispose()
    {
        if (_buffer.Length > 0)
        {
            _pool.Return(_buffer);
            _buffer = [];
        }
        _written = 0;
    }
}
