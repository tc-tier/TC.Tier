using System.Collections.Concurrent;
using System.Text;
using TC.Tier.Core.IO.Remote;

namespace TC.Tier.Core.IO.Testing;

/// <summary>
/// 内存对象存储——<see cref="IObjectStore"/> 的进程内替身（契约测试平权 + CI 无网测试，B3.1）。
/// <para>★ 全能力位置位（ConditionalPut/ConditionalDelete/ServerSideCopy/StrongList/Multipart/RangeGet）——
///   契约测试的语义全集基准实现；S3 实现与其同跑同一套断言（三介质平权哲学在对象层的投影）。</para>
/// <para>★ 语义细节对齐 S3：ETag = 内容 XxHash128（hex，不带引号——归一化口径，S3 实现侧负责引号剥离）；
///   条件写失配 = <see cref="IOError.PreconditionFailed"/>；multipart 会话 Complete 后失效
///   （后续操作 NotFound = NoSuchUpload 归一）。</para>
/// <para>★ 测试设施仪器：调用计数器（桥层"Open 零下载/纯追加零历史拉取"类断言的依据）+
///   活跃 multipart 会话数（碎片回收断言的依据）。Release 零依赖约束下 Internal 常设。</para>
/// <para>★ 并发模型：全局锁（替身不走性能路线——确定性优先于吞吐）。</para>
/// </summary>
internal sealed class MemoryObjectStore : IObjectStore
{
    private sealed class ObjectState
    {
        public required byte[] Data;
        public required ObjectMetadata Metadata;
        public required string ETag;
        public required DateTimeOffset LastModifiedUtc;   // 写入/替换时间（对象替身的时间戳诚实化）
    }

    private sealed class UploadSession : IMultipartUpload
    {
        public required MemoryObjectStore Owner;
        public required string Key;
        public required string UploadId;             // 会话治理原语的句柄（Guid）
        public DateTimeOffset InitiatedUtc;          // 孤儿判定基准（测试仪器可回拨）
        /// <summary>回拨会话发起时间（孤儿清理场景注入）。</summary>
        /// <param name="utc">回拨后的发起时刻（UTC）。</param>
        public void BackdateForTest(DateTimeOffset utc) => InitiatedUtc = utc;
        public ObjectMetadata? Metadata;
        public readonly Dictionary<int, byte[]> Parts = new();
        public bool Terminated;   // Complete 或 Abort 之后失效（NoSuchUpload 归一）

        /// <summary>上传一个分片（会话内缓存——Complete 时按 PartNumber 拼接）。</summary>
        /// <param name="partNumber">分片号（≥1）。</param>
        /// <param name="data">分片数据。</param>
        /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
        /// <returns>完成后得到分片结果（分片号 + 内容 ETag）。</returns>
        /// <exception cref="ArgumentOutOfRangeException">partNumber &lt;1。</exception>
        public ValueTask<UploadPartResult> UploadPartAsync(int partNumber, ReadOnlyMemory<byte> data,
                                                           CancellationToken ct = default)
        {
            if (partNumber < 1) throw new ArgumentOutOfRangeException(nameof(partNumber), "partNumber ≥ 1。");
            Interlocked.Add(ref Owner.Counters.UploadPartBytes, data.Length);
            lock (Owner._lock)
            {
                Owner.SessionOp(this, nameof(UploadPartAsync));
                var copy = data.ToArray();
                Parts[partNumber] = copy;
                return ValueTask.FromResult(new UploadPartResult(partNumber, ETagOf(copy)));
            }
        }

        /// <summary>从既有对象服务端拷贝一个分片（内存切片）。</summary>
        /// <param name="partNumber">分片号（≥1）。</param>
        /// <param name="sourceKey">源对象键。</param>
        /// <param name="sourceOffset">源对象内起始偏移（字节，≥0）。</param>
        /// <param name="length">拷贝字节数（超出源剩余部分按实际可得截取）。</param>
        /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
        /// <returns>完成后得到分片结果（分片号 + 内容 ETag）。</returns>
        /// <exception cref="FileIOException">源对象不存在。</exception>
        public ValueTask<UploadPartResult> UploadPartCopyAsync(int partNumber, string sourceKey,
                                                               long sourceOffset, long length,
                                                               CancellationToken ct = default)
        {
            if (partNumber < 1) throw new ArgumentOutOfRangeException(nameof(partNumber), "partNumber ≥ 1。");
            Interlocked.Increment(ref Owner.Counters.UploadPartCopies);
            ObjectKeyValidator.Validate(sourceKey);
            lock (Owner._lock)
            {
                Owner.SessionOp(this, nameof(UploadPartCopyAsync));
                if (!Owner._objects.TryGetValue(sourceKey, out var source))
                    throw new FileIOException(IOError.NotFound, $"源对象不存在: {sourceKey}", sourceKey, nameof(UploadPartCopyAsync));
                var n = (int)Math.Min(length, Math.Max(0, source.Data.LongLength - sourceOffset));
                var slice = source.Data.AsSpan((int)sourceOffset, n).ToArray();
                Parts[partNumber] = slice;
                return ValueTask.FromResult(new UploadPartResult(partNumber, ETagOf(slice)));
            }
        }

        /// <summary>提交会话——按 PartNumber 升序拼接分片落对象，会话失效。</summary>
        /// <param name="parts">分片结果清单（至少一项；须与已上传分片一致）。</param>
        /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
        /// <returns>对象落档完成后即完成。</returns>
        /// <exception cref="ArgumentException">parts 为空。</exception>
        public ValueTask CompleteAsync(IReadOnlyList<UploadPartResult> parts, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(parts);
            if (parts.Count == 0) throw new ArgumentException("Complete 至少一个 part。", nameof(parts));
            lock (Owner._lock)
            {
                Owner.SessionOp(this, nameof(CompleteAsync));
                // 校验 part 结果与本地会话一致，按 PartNumber 升序拼接（S3 complete 语义）
                var ordered = parts.OrderBy(p => p.PartNumber, Comparer<int>.Default).ToArray();
                var total = 0L;
                foreach (var p in ordered) total += Parts[p.PartNumber].LongLength;
                var assembled = new byte[total];
                var pos = 0;
                foreach (var p in ordered)
                {
                    var buf = Parts[p.PartNumber];
                    buf.CopyTo(assembled, pos);
                    pos += buf.Length;
                }
                Owner._objects[Key] = new ObjectState
                {
                    Data = assembled,
                    Metadata = Metadata ?? ObjectMetadata.Empty,
                    ETag = ETagOf(assembled),
                    LastModifiedUtc = DateTimeOffset.UtcNow,
                };
                Owner.EndSession(this);
            }
            return ValueTask.CompletedTask;
        }

        /// <summary>中止会话——丢弃全部分片并失效（幂等）。</summary>
        /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
        /// <returns>会话终止后即完成。</returns>
        public async ValueTask AbortAsync(CancellationToken ct = default)
        {
            lock (Owner._lock)
            {
                if (!Terminated && Owner._sessions.Contains(this))
                    Owner.EndSession(this);
            }
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>释放会话——异常安全兜底，语义 ≡ <see cref="AbortAsync"/>。</summary>
        /// <returns>中止完成后即完成。</returns>
        public async ValueTask DisposeAsync()
        {
            await AbortAsync(ct: default).ConfigureAwait(false);   // 异常安全兜底 ≡ Abort
        }

    }

    /// <summary>调用计数（测试断言仪器——桥层延迟加载/零网络断言的依据）。</summary>
    public sealed class StoreCounters
    {
        public long Puts, Gets, Heads, Deletes, Lists, Copies, CopyRanges, CopyMetadatas, MultipartCreates;
        public long GetBytes, PutBytes, UploadPartBytes, UploadPartCopies;

        /// <summary>全部网络语义调用的总和（排除 Counters 自身）。</summary>
        public long TotalOps => Volatile.Read(ref Puts) + Volatile.Read(ref Gets) + Volatile.Read(ref Heads)
                                + Volatile.Read(ref Deletes) + Volatile.Read(ref Lists) + Volatile.Read(ref Copies)
                                + Volatile.Read(ref CopyRanges) + Volatile.Read(ref CopyMetadatas);
    }

    private MemoryStream? _unknownSink;   // 未知长度流的中转（复用减分配）
    private readonly object _lock = new();
    private readonly Dictionary<string, ObjectState> _objects = new(StringComparer.Ordinal);
    private readonly List<UploadSession> _sessions = [];
    private int _disposed;

    /// <summary>调用计数器（Interlocked 递增——并发断言安全）。</summary>
    public StoreCounters Counters { get; } = new();

    /// <summary>测试仪器：回拨指定键会话的发起时间（孤儿清理场景注入——真实实现不可回拨）。</summary>
    internal void BackdateUploadSessionForTest(string key, DateTimeOffset initiatedUtc)
    {
        lock (_lock)
        {
            foreach (var x in _sessions.Where(x => x.Key == key))
                x.BackdateForTest(initiatedUtc);
        }
    }

    /// <summary>当前活跃（未 Complete/Abort）multipart 会话数——碎片回收断言依据（ListMultipartUploads 归一）。</summary>
    public int ActiveUploadSessions
    {
        get
        {
            lock (_lock) return _sessions.Count;
        }
    }

    /// <inheritdoc/>
    public ObjectStoreCapabilities Capabilities =>
        ObjectStoreCapabilities.ConditionalPut
        | ObjectStoreCapabilities.ConditionalDelete
        | ObjectStoreCapabilities.ServerSideCopy
        | ObjectStoreCapabilities.StrongList
        | ObjectStoreCapabilities.Multipart
        | ObjectStoreCapabilities.RangeGet;

    // ═════════════════════════════ 六件套 ═════════════════════════════

    /// <inheritdoc/>
    /// <summary>写入对象（整体替换；条件写失配抛 PreconditionFailed）。</summary>
    /// <param name="key">对象键。</param>
    /// <param name="data">对象数据。</param>
    /// <param name="metadata">用户元数据快照（null = 空）。</param>
    /// <param name="condition">写入条件（null = 无条件）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>落档完成后即完成。</returns>
    /// <exception cref="FileIOException">条件失配（PreconditionFailed/NotFound）。</exception>
    public ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, ObjectMetadata? metadata = null,
                              PutCondition? condition = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        Interlocked.Increment(ref Counters.Puts);
        Interlocked.Add(ref Counters.PutBytes, data.Length);
        var state = new ObjectState
        {
            Data = data.ToArray(),
            Metadata = metadata ?? ObjectMetadata.Empty,
            ETag = ETagOf(data.Span),
            LastModifiedUtc = DateTimeOffset.UtcNow,
        };
        lock (_lock)
        {
            CheckPutConditionNoLock(key, condition);
            _objects[key] = state;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>从流写入对象（长度已知契约——流提前结束抛 IOFailure；不可寻零长按未知处理）。</summary>
    /// <param name="key">对象键。</param>
    /// <param name="data">对象数据流（从当前位置读尽 length 字节）。</param>
    /// <param name="length">期望字节数（-1 = 未知长度——读尽流）。</param>
    /// <param name="metadata">用户元数据快照（null = 空）。</param>
    /// <param name="condition">写入条件（null = 无条件）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>落档完成后即完成。</returns>
    /// <exception cref="FileIOException">流提前结束。</exception>
    public ValueTask PutAsync(string key, Stream data, long length, ObjectMetadata? metadata = null,
                              PutCondition? condition = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        if (length == 0 && !data.CanSeek) length = -1;   // 不可寻零长按未知处理（与 S3 语义对齐）
        if (length < 0)
        {
            // 未知长度：读尽（替身零 spool——内存实现；同步体——CopyTo 同步形态）
            _unknownSink ??= new MemoryStream();
            var mem = _unknownSink;
            mem.SetLength(0);
            data.CopyTo(mem);
            var bytes = mem.ToArray();
#pragma warning disable TCSG137 // 设计必需：同步写 API 契约（同步体内部调异步实现）
            PutAsync(key, bytes, metadata, condition, ct).AsTask().GetAwaiter().GetResult();
#pragma warning restore TCSG137
            return default;
        }
        if (data.CanSeek && data.Length - data.Position < length)
            throw new ArgumentException($"流内可用字节不足（需 {length}，余 {data.Length - data.Position}）。", nameof(data));
        var buffer = new byte[length];
        var filled = 0;
        while (filled < buffer.Length)
        {
            var n = data.Read(buffer, filled, buffer.Length - filled);
            if (n <= 0) break;
            filled += n;
        }
        if (filled != buffer.Length)
            throw new FileIOException(IOError.IOFailure,
                $"流提前结束（期望 {length} 字节，实得 {filled}）——长度已知流契约。", key, nameof(PutAsync));
        return PutAsync(key, buffer, metadata, condition, ct);
    }

    /// <inheritdoc/>
    /// <summary>范围读取对象（RangeGet；EOF 归一返回 0 不抛——416 语义）。</summary>
    /// <param name="key">对象键。</param>
    /// <param name="offset">对象内起始偏移（字节，≥0）。</param>
    /// <param name="destination">接收缓冲（长度即单次最多读取字节数）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到实际读取的字节数（0 = offset 已到对象末尾）。</returns>
    /// <exception cref="FileIOException">对象不存在（NotFound）。</exception>
    public ValueTask<int> GetAsync(string key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Interlocked.Increment(ref Counters.Gets);
        lock (_lock)
        {
            if (!_objects.TryGetValue(key, out var state))
                throw new FileIOException(IOError.NotFound, $"对象不存在: {key}", key, nameof(GetAsync));
            var available = state.Data.LongLength - offset;
            if (available <= 0) return ValueTask.FromResult(0);   // EOF（416 归一 0，不抛）
            var n = (int)Math.Min(destination.Length, available);
            state.Data.AsSpan((int)offset, n).CopyTo(destination.Span);
            Interlocked.Add(ref Counters.GetBytes, n);
            return ValueTask.FromResult(n);
        }
    }

    /// <inheritdoc/>
    public ValueTask<ObjectInfo?> HeadAsync(string key, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        Interlocked.Increment(ref Counters.Heads);
        lock (_lock)
        {
            return _objects.TryGetValue(key, out var state)
                ? ValueTask.FromResult<ObjectInfo?>(new ObjectInfo(key, state.Data.LongLength, state.ETag, state.Metadata, state.LastModifiedUtc))
                : ValueTask.FromResult<ObjectInfo?>(null);
        }
    }

    /// <inheritdoc/>
    /// <summary>删除对象（幂等——不存在仍成功；条件删除失配抛）。</summary>
    /// <param name="key">对象键。</param>
    /// <param name="condition">删除条件（IfMatch——null = 无条件）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>删除完成后即完成。</returns>
    /// <exception cref="FileIOException">条件失配（PreconditionFailed/NotFound）。</exception>
    public ValueTask DeleteAsync(string key, DeleteCondition? condition = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        Interlocked.Increment(ref Counters.Deletes);
        lock (_lock)
        {
            if (condition is { IfMatch: { } match })
            {
                if (!_objects.TryGetValue(key, out var current))
                    throw new FileIOException(IOError.NotFound, $"对象不存在（条件删除无从匹配）: {key}", key, nameof(DeleteAsync));
                if (!ETagMatches(current.ETag, match))
                    throw new FileIOException(IOError.PreconditionFailed,
                        $"条件删除失配（If-Match 不等于当前 ETag——锁已被他人接管，拒绝误删）: {key}", key, nameof(DeleteAsync));
            }
            _objects.Remove(key);   // 幂等：不存在仍成功（POSIX unlink 对齐）
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>列举对象（强一致快照）。</summary>
    /// <param name="prefix">键前缀过滤（null/空 = 全量）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到匹配条目清单。</returns>
    public ValueTask<IReadOnlyList<ObjectEntry>> ListAsync(string? prefix = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        Interlocked.Increment(ref Counters.Lists);
        lock (_lock)
        {
            var result = new List<ObjectEntry>();
            foreach (var kv in _objects)
            {
                if (prefix is not null && !kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                result.Add(new ObjectEntry(kv.Key, kv.Value.Data.LongLength, kv.Value.LastModifiedUtc));
            }
            return ValueTask.FromResult<IReadOnlyList<ObjectEntry>>(result);
        }
    }

    /// <inheritdoc/>
    /// <summary>整对象拷贝（深拷贝——源后续修改不影响目标；目标是新建对象取新时间戳）。</summary>
    /// <param name="sourceKey">源对象键。</param>
    /// <param name="destKey">目标对象键。</param>
    /// <param name="metadata">目标元数据处理（null = 继承源）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>拷贝完成后即完成。</returns>
    /// <exception cref="FileIOException">源对象不存在（NotFound）。</exception>
    public ValueTask CopyAsync(string sourceKey, string destKey, CopyMetadata? metadata = null,
                               CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(sourceKey);
        ObjectKeyValidator.Validate(destKey);
        Interlocked.Increment(ref Counters.Copies);
        lock (_lock)
        {
            if (!_objects.TryGetValue(sourceKey, out var source))
                throw new FileIOException(IOError.NotFound, $"源对象不存在: {sourceKey}", sourceKey, nameof(CopyAsync));
            _objects[destKey] = new ObjectState
            {
                Data = source.Data.ToArray(),   // 深拷贝——Copy 独立性（源后续修改不影响目标）
                LastModifiedUtc = DateTimeOffset.UtcNow,   // 目标是新建对象——新时间戳
                Metadata = metadata?.Metadata ?? source.Metadata,
                ETag = source.ETag,
            };
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>仅复制/替换元数据（数据不动；元数据更新 = 修改——刷新时间戳）。</summary>
    /// <param name="sourceKey">对象键。</param>
    /// <param name="replace">替换用元数据（null = 仅复制现有元数据）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到写入目标后的元数据。</returns>
    /// <exception cref="FileIOException">对象不存在（NotFound）。</exception>
    public ValueTask<ObjectMetadata> CopyMetadataAsync(string sourceKey, ObjectMetadata? replace = null,
                                                       CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(sourceKey);
        Interlocked.Increment(ref Counters.CopyMetadatas);
        lock (_lock)
        {
            if (!_objects.TryGetValue(sourceKey, out var source))
                throw new FileIOException(IOError.NotFound, $"对象不存在: {sourceKey}", sourceKey, nameof(CopyMetadataAsync));
            var effective = replace ?? source.Metadata;
            _objects[sourceKey] = new ObjectState { Data = source.Data, Metadata = effective, ETag = source.ETag,
                LastModifiedUtc = DateTimeOffset.UtcNow };   // 元数据更新 = 修改
            return ValueTask.FromResult(effective);
        }
    }

    // ═════════════════════════════ multipart / 范围拷贝 ═════════════════════════════

    /// <inheritdoc/>
    /// <summary>创建 multipart 会话（Complete/Abort 前对象不可见）。</summary>
    /// <param name="key">目标对象键。</param>
    /// <param name="metadata">目标用户元数据（null = 空）。</param>
    /// <returns>会话句柄（Complete 后失效——后续操作 NotFound）。</returns>
    public IMultipartUpload CreateMultipartUpload(string key, ObjectMetadata? metadata = null)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(key);
        Interlocked.Increment(ref Counters.MultipartCreates);
        lock (_lock)
        {
            var session = new UploadSession
            {
                Owner = this,
                Key = key,
                UploadId = Guid.NewGuid().ToString("N"),
                InitiatedUtc = DateTimeOffset.UtcNow,
                Metadata = metadata,
            };
            _sessions.Add(session);
            return session;
        }
    }

    /// <inheritdoc/>
    /// <summary>范围拷贝——源对象切片为新对象（默认不带源元数据——目标语义独立）。</summary>
    /// <param name="sourceKey">源对象键。</param>
    /// <param name="destKey">目标对象键。</param>
    /// <param name="sourceOffset">源对象内起始偏移（字节，≥0）。</param>
    /// <param name="length">拷贝字节数（≥0；超出源剩余部分按实际可得截取）。</param>
    /// <param name="metadata">目标元数据处理（null = 空，不继承源）。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到服务端实际拷贝的字节数（字节）。</returns>
    /// <exception cref="FileIOException">源对象不存在（NotFound）。</exception>
    public ValueTask<long> CopyRangeAsync(string sourceKey, string destKey, long sourceOffset, long length,
                                          CopyMetadata? metadata = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ObjectKeyValidator.Validate(sourceKey);
        ObjectKeyValidator.Validate(destKey);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Interlocked.Increment(ref Counters.CopyRanges);
        lock (_lock)
        {
            if (!_objects.TryGetValue(sourceKey, out var source))
                throw new FileIOException(IOError.NotFound, $"源对象不存在: {sourceKey}", sourceKey, nameof(CopyRangeAsync));
            var available = Math.Max(0, source.Data.LongLength - sourceOffset);
            var n = (int)Math.Min(length, available);
            var slice = source.Data.AsSpan((int)sourceOffset, n).ToArray();
            _objects[destKey] = new ObjectState
            {
                Data = slice,
                Metadata = metadata?.Metadata ?? ObjectMetadata.Empty,   // 范围拷贝默认不带源元数据（目标语义独立）
                ETag = ETagOf(slice),
                LastModifiedUtc = DateTimeOffset.UtcNow,
            };
            return ValueTask.FromResult((long)n);
        }
    }

    /// <inheritdoc/>
    /// <summary>列举进行中 multipart 会话（孤儿清理扫描面）。</summary>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>完成后得到活跃会话清单（键/UploadId/发起时刻）。</returns>
    public ValueTask<IReadOnlyList<MultipartUploadSession>> ListMultipartUploadsAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            var result = _sessions.Select(x => new MultipartUploadSession(x.Key, x.UploadId, x.InitiatedUtc)).ToArray();
            return ValueTask.FromResult<IReadOnlyList<MultipartUploadSession>>(result);
        }
    }

    /// <inheritdoc/>
    /// <summary>中止 multipart 会话（幂等——不存在/已终结静默成功）。</summary>
    /// <param name="key">目标对象键。</param>
    /// <param name="uploadId">会话 UploadId。</param>
    /// <param name="ct">取消令牌（本实现同步完成，不响应取消）。</param>
    /// <returns>会话终止后即完成。</returns>
    public ValueTask AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(x => x.Key == key && x.UploadId == uploadId);
            if (session is not null)
                EndSession(session);   // 幂等：不存在 = 已终结，静默成功
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_lock)
        {
            _objects.Clear();
            foreach (var s in _sessions) s.Terminated = true;
            _sessions.Clear();
        }
    }

    // ═════════════════════════════ 内部 ═════════════════════════════

    /// <summary>条件写检查（持锁调用）——失配语义见类注释（IfMatch+缺失=NotFound；失配=PreconditionFailed）。</summary>
    private void CheckPutConditionNoLock(string key, PutCondition? condition)
    {
        if (condition is not { } c) return;
        _objects.TryGetValue(key, out var current);
        if (c.IfMatch is { } ifMatch)
        {
            if (current is null)
                throw new FileIOException(IOError.NotFound,
                    $"对象不存在（If-Match 无从匹配——并发删除或从未创建）: {key}", key, nameof(PutAsync));
            if (!ETagMatches(current.ETag, ifMatch))
                throw new FileIOException(IOError.PreconditionFailed,
                    $"条件写失配（If-Match 不等于当前 ETag——对象已被并发替换）: {key}", key, nameof(PutAsync));
        }
        if (c.IfNoneMatch is { } ifNoneMatch)
        {
            if (ifNoneMatch == "*")
            {
                if (current is not null)
                    throw new FileIOException(IOError.PreconditionFailed,
                        $"条件写失配（If-None-Match:* 撞已存在——抢占失败）: {key}", key, nameof(PutAsync));
            }
            else if (current is not null && ETagMatches(current.ETag, ifNoneMatch))
            {
                throw new FileIOException(IOError.PreconditionFailed,
                    $"条件写失配（If-None-Match 命中当前 ETag）: {key}", key, nameof(PutAsync));
            }
        }
    }

    /// <summary>ETag 比较——容忍 S3 风格引号（归一化口径：存储不带引号）。</summary>
    private static bool ETagMatches(string actual, string condition)
    {
        var expected = condition.Trim('"');
        return string.Equals(actual, expected, StringComparison.Ordinal);
    }

    private static string ETagOf(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[UnifiedXxHash.Hash128Len];
        UnifiedXxHash.Hash128(data, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private void SessionOp(UploadSession session, string op)
    {
        if (session.Terminated || !_sessions.Contains(session))
            throw new FileIOException(IOError.NotFound,
                $"multipart 会话已失效（NoSuchUpload 归一——已 Complete/Abort 或存储已 Dispose）: key={session.Key}, op={op}",
                session.Key, op);
    }

    private void EndSession(UploadSession session)
    {
        session.Terminated = true;
        _sessions.Remove(session);
    }
}
