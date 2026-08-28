using TC.Tier.Contracts.Structures;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 内存异步传输面（测试双工——IAsyncTransferPersistence 家族现成实现为零，TierWAL 测试自建）。
/// <para>★ 语义对齐 WholeMirrorPersistence（同步面范本）：WriteFooter = 原子提交点（拷贝完整像）；
///   Complete(false)/未写尾 Dispose = Abort（像不可见）；读侧连续字节流，EOF 返回 0。</para>
/// <para>★ 并发双开写 → TryOpenWriteAsync false（单写者）。</para>
/// </summary>
internal sealed class MemoryAsyncTransferPersistence : IAsyncTransferPersistence
{
    private byte[]? _data;          // 已提交像（null = 无完整像）
    private bool _writeActive;
    private bool _disposed;

    /// <summary>最近一次提交的完整像（测试诊断/内容校验用）。</summary>
    public ReadOnlyMemory<byte>? CommittedImage => _data;

    /// <summary>直接注入完整像（测试用——模拟外部已提交的快照像，绕开写会话相位）。</summary>
    public void Seed(ReadOnlyMemory<byte> image)
    {
        _data = image.ToArray();
        _writeActive = false;
    }

    public ValueTask<bool> TryOpenWriteAsync(out IAsyncTransferWriter? writer, int maxTransferBytes = IAsyncTransferPersistence.DefaultMaxTransferBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_writeActive)
        {
            writer = null;
            return ValueTask.FromResult(false);
        }
        _writeActive = true;
        writer = new WriterSession(this, maxTransferBytes);
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryOpenReadAsync(out IAsyncTransferReader? reader, int maxTransferBytes = IAsyncTransferPersistence.DefaultMaxTransferBytes)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_data is null)
        {
            reader = null;
            return ValueTask.FromResult(false);
        }
        reader = new ReaderSession(_data, maxTransferBytes);
        return ValueTask.FromResult(true);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    // ═══ 写会话（三段式相位）═══

    private sealed class WriterSession(MemoryAsyncTransferPersistence owner, int maxTransferBytes) : IAsyncTransferWriter
    {
        private readonly MemoryStream _buf = new();
        private int _phase;   // 0=未开 1=写头后 2=已写尾
        private bool _disposed;

        public int MaxTransferBytes => maxTransferBytes;

        public void Complete(bool isSuccess = true)
        {
            if (_phase == 2 || _phase == 0) return;
            if (!isSuccess)
            {
                Abort();
                _phase = 2;
                return;
            }
            throw new InvalidOperationException("未写尾不可完成——先 WriteFooter（写尾 = 原子提交点）。");
        }

        public ValueTask WriteHeaderAsync(ReadOnlyMemory<byte> header, CancellationToken ct = default)
        {
            if (_phase != 0) throw new InvalidOperationException("WriteHeader 已调用——三段式相位不可重复。");
            Bound(header.Length);
            _buf.Write(header.Span);
            _phase = 1;
            return ValueTask.CompletedTask;
        }

        public ValueTask WritePayloadAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        {
            if (_phase == 0) throw new InvalidOperationException("先 WriteHeader 开相位（三段式协议）。");
            if (_phase == 2) throw new InvalidOperationException("已写尾——会话完成。");
            Bound(chunk.Length);
            _buf.Write(chunk.Span);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteFooterAsync(ReadOnlyMemory<byte> footer, CancellationToken ct = default)
        {
            if (_phase != 1) throw new InvalidOperationException("写尾前必须已写头（三段式协议）。");
            Bound(footer.Length);
            _buf.Write(footer.Span);
            owner._data = _buf.ToArray();   // ★ 写尾 = 原子提交点
            _phase = 2;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            if (_phase == 1) Abort();
            owner._writeActive = false;
            return ValueTask.CompletedTask;
        }

        private void Abort() => owner._data = null;

        private void Bound(int len)
            => ArgumentOutOfRangeException.ThrowIfGreaterThan(len, maxTransferBytes, "chunk");
    }

    // ═══ 读会话（连续字节流，EOF 返回 0）═══

    private sealed class ReaderSession(byte[] data, int maxTransferBytes) : IAsyncTransferReader
    {
        private int _cursor;
        private int _phase;   // 0=未读头 1=读头后 2=已读尾
        private bool _disposed;

        public int MaxTransferBytes => maxTransferBytes;

        public void Complete(bool isSuccess = true) => _phase = 2;

        public ValueTask<int> ReadHeaderAsync(Memory<byte> dst, CancellationToken ct = default)
        {
            if (_phase != 0) throw new InvalidOperationException("ReadHeader 已调用——三段式相位不可重复。");
            int got = ReadCore(dst.Span);
            _phase = 1;
            return ValueTask.FromResult(got);
        }

        public ValueTask<int> ReadPayloadAsync(Memory<byte> dst, CancellationToken ct = default)
        {
            if (_phase == 0) throw new InvalidOperationException("先 ReadHeader 开相位（三段式协议）。");
            if (_phase == 2) throw new InvalidOperationException("已读尾——会话完成。");
            return ValueTask.FromResult(ReadCore(dst.Span));
        }

        public ValueTask<int> ReadFooterAsync(Memory<byte> dst, CancellationToken ct = default)
        {
            if (_phase != 1) throw new InvalidOperationException("先 ReadHeader 开相位（三段式协议）。");
            int got = ReadCore(dst.Span);
            _phase = 2;
            return ValueTask.FromResult(got);
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private int ReadCore(Span<byte> dst)
        {
            Bound(dst.Length);
            int n = Math.Min(dst.Length, data.Length - _cursor);
            if (n <= 0) return 0;
            data.AsSpan(_cursor, n).CopyTo(dst);
            _cursor += n;
            return n;
        }

        private void Bound(int len)
            => ArgumentOutOfRangeException.ThrowIfGreaterThan(len, maxTransferBytes, "buffer");
    }
}

/// <summary>
/// 门控写会话（冻结机制测试用）——WriteHeaderAsync 后等待 gate（TaskCompletionSource）
/// 才继续，用于确定性模拟"导出挂起中"。
/// </summary>
internal sealed class GatedAsyncTransferWriter : IAsyncTransferWriter
{
    private readonly MemoryStream _buf = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _headerWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _phase;

    /// <summary>释放门控（导出继续）。</summary>
    public void Release() => _gate.TrySetResult();

    /// <summary>Header 已写 = 导出已挂起在门闩（N₀ 已冻结）——事件驱动等待（替代固定延时，消时序 flaky）。</summary>
    public Task HeaderWritten => _headerWritten.Task;

    /// <summary>已写字节（诊断）。</summary>
    public long WrittenBytes => _buf.Length;

    public int MaxTransferBytes => 128 * 1024;

    public ValueTask WriteHeaderAsync(ReadOnlyMemory<byte> header, CancellationToken ct = default)
    {
        _buf.Write(header.Span);
        _phase = 1;
        _headerWritten.TrySetResult();
        return new ValueTask(_gate.Task);   // ★ 门控：卡在 Header 后
    }

    public async ValueTask WritePayloadAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
    {
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        _buf.Write(chunk.Span);
    }

    public async ValueTask WriteFooterAsync(ReadOnlyMemory<byte> footer, CancellationToken ct = default)
    {
        await _gate.Task.WaitAsync(ct).ConfigureAwait(false);
        _buf.Write(footer.Span);
        _phase = 2;
    }

    public void Complete(bool isSuccess = true)
    {
        if (isSuccess && _phase != 2)
            throw new InvalidOperationException("未写尾不可完成——先 WriteFooter。");
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// 门控传输 persistence（并发语义测试用）——TryOpenWriteAsync 返回 <see cref="GatedAsyncTransferWriter"/>
/// （WriteHeaderAsync 后等 gate 才继续），用于确定性模拟"导出挂起中"。
/// </summary>
internal sealed class GatedAsyncTransferPersistence : IAsyncTransferPersistence
{
    private readonly GatedAsyncTransferWriter _writer = new();

    /// <summary>Header 已写 = 导出已挂起在门闩——事件驱动等待（替代固定延时，消时序 flaky）。</summary>
    public Task HeaderWritten => _writer.HeaderWritten;

    /// <summary>释放门控（导出继续）。</summary>
    public void Release() => _writer.Release();

    public ValueTask<bool> TryOpenWriteAsync(out IAsyncTransferWriter? writer, int maxTransferBytes = IAsyncTransferPersistence.DefaultMaxTransferBytes)
    {
        writer = _writer;
        return ValueTask.FromResult(true);
    }

    public ValueTask<bool> TryOpenReadAsync(out IAsyncTransferReader? reader, int maxTransferBytes = IAsyncTransferPersistence.DefaultMaxTransferBytes)
    {
        reader = null;
        return ValueTask.FromResult(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
