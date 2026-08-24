using System.Collections.Concurrent;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Testing;

/// <summary>
/// 故障注入装饰器——按「路径 × 操作 × 概率」三维组合注入 <see cref="FileIOException"/>（测试替身）。
/// <para>★ 概率注入（seeded Random——可复现）与确定性注入（第 N 次匹配调用时注入——部分失败测试用）双模式。</para>
/// <para>★ 路径匹配：精确名或 <c>"*"</c>（全部）；操作匹配：操作名（"Read"/"Write"/"CopyRange"/…）或 <c>"*"</c>。</para>
/// <para>★ 注入点：fs 命名空间操作 + 句柄数据面操作（句柄被包装为 FaultInjectingFileHandle）。</para>
/// <para>★ Dispose 转发内层 fs（装饰器持有内层——典型用法为测试自建内层）。</para>
/// </summary>
internal sealed class FaultInjectingFileSystem : IFileSystem
{
    /// <summary>注入规则。</summary>
    public sealed class FaultRule
    {
        /// <summary>路径匹配（精确名或 "*"）。</summary>
        public required string PathPattern { get; init; }

        /// <summary>操作匹配（操作名或 "*"）。</summary>
        public required string OperationPattern { get; init; }

        /// <summary>注入的错误码。</summary>
        public required IOError Error { get; init; }

        /// <summary>注入概率 [0,1]（与 <see cref="FailAtCallIndex"/> 互斥使用——两者都设时确定性优先）。</summary>
        public double Probability { get; init; }

        /// <summary>确定性注入：第 N 次（1 起）匹配调用时注入一次（部分失败/中途失败测试用）。</summary>
        public long? FailAtCallIndex { get; init; }

        /// <summary>附加上下文（进异常消息）。</summary>
        public string? Detail { get; init; }

        internal long MatchCount;
    }

    private readonly IFileSystem _inner;
    private readonly Random _random;
    private readonly ConcurrentQueue<FaultRule> _rules = new();
    private int _disposed;

    /// <summary>构造——包装内层 fs（seed 固定则故障序列可复现）。</summary>
    public FaultInjectingFileSystem(IFileSystem inner, int seed = 12345)
    {
        _inner = inner;
        _random = new Random(seed);
    }

    /// <summary>添加注入规则（返回规则实例——调用方可观测 MatchCount）。</summary>
    public FaultRule AddRule(string pathPattern, string operationPattern, IOError error,
        double probability = 1.0, long? failAtCallIndex = null, string? detail = null)
    {
        if (probability is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(probability));
        var rule = new FaultRule
        {
            PathPattern = pathPattern,
            OperationPattern = operationPattern,
            Error = error,
            Probability = probability,
            FailAtCallIndex = failAtCallIndex,
            Detail = detail,
        };
        _rules.Enqueue(rule);
        return rule;
    }

    /// <summary>清空全部规则。</summary>
    public void ClearRules() => _rules.Clear();

    private void MaybeInject(string? path, string operation)
    {
        foreach (var rule in _rules)
        {
            if (!Matches(rule.PathPattern, path) || !Matches(rule.OperationPattern, operation)) continue;
            var n = Interlocked.Increment(ref rule.MatchCount);
            if (rule.FailAtCallIndex is { } idx)
            {
                if (n != idx) continue;
            }
            else if (_random.NextDouble() >= rule.Probability)
            {
                continue;
            }
            throw new FileIOException(rule.Error,
                $"[FaultInject] 注入 {rule.Error}（path={path}, op={operation}, match#{n}{(rule.Detail is null ? null : $", {rule.Detail}")}）",
                path, operation);
        }
    }

    private static bool Matches(string pattern, string? value)
        => pattern == "*" || string.Equals(pattern, value, StringComparison.Ordinal);

    /// <inheritdoc/>
    public FileSystemCapabilities Capabilities => _inner.Capabilities;

    /// <inheritdoc/>
    public VolumeInfo Volume => _inner.Volume;

    /// <inheritdoc/>
    public IFileHandle Open(string path, FileOpenOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Open");
        return new FaultInjectingFileHandle(this, _inner.Open(path, options), path);
    }

    /// <inheritdoc/>
    public void EnsureRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnsureRoot");
        _inner.EnsureRoot();
    }

    /// <inheritdoc/>
    public void FlushRoot()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "FlushRoot");
        _inner.FlushRoot();
    }

    /// <inheritdoc/>
    public bool Exists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Exists");
        return _inner.Exists(path);
    }

    /// <inheritdoc/>
    public void Delete(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Delete");
        _inner.Delete(path);
    }

    /// <inheritdoc/>
    public void Move(string source, string dest, bool overwrite = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(source, "Move");
        _inner.Move(source, dest, overwrite);
    }

    // ═══════════════ 根空间新成员转发（目录族/创建解耦/元数据/枚举族——注入操作名同名）═══════════════

    /// <inheritdoc/>
    public void CreateDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "CreateDirectory");
        _inner.CreateDirectory(path);
    }

    /// <inheritdoc/>
    public void DeleteDirectory(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "DeleteDirectory");
        _inner.DeleteDirectory(path);
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "DirectoryExists");
        return _inner.DirectoryExists(path);
    }

    /// <inheritdoc/>
    public void MoveDirectory(string source, string dest)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(source, "MoveDirectory");
        _inner.MoveDirectory(source, dest);
    }

    /// <inheritdoc/>
    public void CreateFile(string path, long preallocateSize = 0, ReadOnlyMemory<byte> metadata = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "CreateFile");
        _inner.CreateFile(path, preallocateSize, metadata);
    }

    /// <inheritdoc/>
    public FsEntryInfo Stat(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "Stat");
        return _inner.Stat(path);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string pattern = "*", bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnumerateFiles");
        return _inner.EnumerateFiles(pattern, recursive);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateFiles(string path, string pattern, bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "EnumerateFiles");
        return _inner.EnumerateFiles(path, pattern, recursive);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string pattern = "*", bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnumerateDirectories");
        return _inner.EnumerateDirectories(pattern, recursive);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateDirectories(string path, string pattern, bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "EnumerateDirectories");
        return _inner.EnumerateDirectories(path, pattern, recursive);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string pattern = "*", bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnumerateEntries");
        return _inner.EnumerateEntries(pattern, recursive);
    }

    /// <inheritdoc/>
    public IEnumerable<FsEntry> EnumerateEntries(string path, string pattern, bool recursive = false)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(path, "EnumerateEntries");
        return _inner.EnumerateEntries(path, pattern, recursive);
    }

    /// <inheritdoc/>
    /// <inheritdoc/>
    public IDisposable EnterMaintenance(string reason, MaintenanceScope scope, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "EnterMaintenance");
        return _inner.EnterMaintenance(reason, scope, ct);
    }

    public IDisposable AcquireExclusive(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        MaybeInject(null, "AcquireExclusive");
        return _inner.AcquireExclusive(timeout);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _inner.Dispose();
    }

    /// <summary>句柄级注入包装——数据面每个操作入口评估规则。</summary>
    private sealed class FaultInjectingFileHandle(FaultInjectingFileSystem owner, IFileHandle inner, string path)
        : IFileHandle, IPoolAttachable
    {
        private void Inject(string operation) => owner.MaybeInject(path, operation);

        public string Path => inner.Path;
        public UnbufferedIoSupport UnbufferedSupport => inner.UnbufferedSupport;
        public long RequiredAlignment => inner.RequiredAlignment;

        public void Write(long offset, ReadOnlySpan<byte> source)
        {
            Inject("Write");
            inner.Write(offset, source);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            Inject("Write");
            return inner.WriteAsync(offset, source, ct);
        }

        public int Read(long offset, Span<byte> destination)
        {
            Inject("Read");
            return inner.Read(offset, destination);
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> destination, CancellationToken ct)
        {
            Inject("Read");
            return inner.ReadAsync(offset, destination, ct);
        }

        public long Position => inner.Position;

        public long Append(ReadOnlySpan<byte> source)
        {
            Inject("Append");
            return inner.Append(source);
        }

        public ValueTask<long> AppendAsync(ReadOnlyMemory<byte> source, CancellationToken ct)
        {
            Inject("Append");
            return inner.AppendAsync(source, ct);
        }

        public long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public void Preallocate()
        {
            Inject("Preallocate");
            inner.Preallocate();
        }

        public long Length => inner.Length;
        public long AllocatedSize => inner.AllocatedSize;

        public void SetLength(long length)
        {
            Inject("SetLength");
            inner.SetLength(length);
        }

        public void PunchHole(long offset, long length)
        {
            Inject("PunchHole");
            inner.PunchHole(offset, length);
        }

        public IReadOnlyCollection<(long Start, long End)> EnumerateAllocatedRanges() => inner.EnumerateAllocatedRanges();

        public void CollapseRange(long offset, long length)
        {
            Inject("CollapseRange");
            inner.CollapseRange(offset, length);
        }

        public void InsertRange(long offset, long length)
        {
            Inject("InsertRange");
            inner.InsertRange(offset, length);
        }

        public long CopyRange(IFileHandle destination, long sourceOffset, long destinationOffset, long length)
        {
            Inject("CopyRange");
            return inner.CopyRange(destination, sourceOffset, destinationOffset, length);
        }

        public long CloneRange(IFileHandle destination)
        {
            Inject("CloneRange");
            return inner.CloneRange(destination);
        }

        public void WriteVector(long offset, ReadOnlySpan<ReadOnlyMemory<byte>> sources)
        {
            Inject("WriteVector");
            inner.WriteVector(offset, sources);
        }

        public ValueTask WriteVectorAsync(long offset, ReadOnlyMemory<ReadOnlyMemory<byte>> sources, CancellationToken ct)
        {
            Inject("WriteVector");
            return inner.WriteVectorAsync(offset, sources, ct);
        }

        public int ReadVector(long offset, ReadOnlySpan<Memory<byte>> destinations)
        {
            Inject("ReadVector");
            return inner.ReadVector(offset, destinations);
        }

        public ValueTask<int> ReadVectorAsync(long offset, Memory<Memory<byte>> destinations, CancellationToken ct)
        {
            Inject("ReadVector");
            return inner.ReadVectorAsync(offset, destinations, ct);
        }

        public void Flush()
        {
            Inject("Flush");
            inner.Flush();
        }

        public void FlushData()
        {
            Inject("FlushData");
            inner.FlushData();
        }

        public void Advise(FileAdvise advise) => inner.Advise(advise);

        public void Lock(long offset, long length, FileLockMode mode)
        {
            Inject("Lock");
            inner.Lock(offset, length, mode);
        }

        public bool TryLock(long offset, long length, FileLockMode mode)
        {
            Inject("TryLock");
            return inner.TryLock(offset, length, mode);
        }

        public void Unlock(long offset, long length) => inner.Unlock(offset, length);

        public IMappedSection Map(long offset, long length, AccessMode access)
        {
            Inject("Map");
            return inner.Map(offset, length, access);
        }

        public ReadOnlyMemory<byte> FileExtra => inner.FileExtra;

        public int ReadFileExtra(long offset, Span<byte> destination) => inner.ReadFileExtra(offset, destination);

        public void WriteFileExtra(long offset, ReadOnlySpan<byte> data) => inner.WriteFileExtra(offset, data);

        public void SetFileExtra(ReadOnlyMemory<byte> extra) => inner.SetFileExtra(extra);

        public void Dispose() => inner.Dispose();

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        // ═══ 池挂载协议转发 ═══
        // ★ 装饰器与 FileHandlePool 组合（引擎消费面必经）：池以本装饰器为池化对象，
        //   挂载/归还/真关闭整体转发内层——四介质句柄全实现 IPoolAttachable，内层必可转。
        //   （曾缺席：池 Acquire 强转 IPoolAttachable 直接 InvalidCastException——任何句柄装饰器
        //   与池不组合，故障注入测试首跑即炸实锤。）
        HandlePoolAttachment? IPoolAttachable.PoolAttachment => ((IPoolAttachable)inner).PoolAttachment;

        HandlePoolAttachment IPoolAttachable.AttachPool(FileHandlePool pool)
            => ((IPoolAttachable)inner).AttachPool(pool);

        void IPoolAttachable.CloseUnderlying() => ((IPoolAttachable)inner).CloseUnderlying();
    }
}
