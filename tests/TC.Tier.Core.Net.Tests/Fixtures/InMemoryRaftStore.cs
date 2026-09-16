using System.Buffers;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// 内存 raft 存储（spec-12 §13.1 夹具模式——缺省夹具：快，测试主场）：
/// <see cref="IRaftStore"/> 全语义内存实现——双水位模型（Allocated 追加即推、
/// Persisted 经 <see cref="WaitForPersistedAsync"/> 推进——与 Fs 夹具同构可测协议时序）。
/// </summary>
public sealed class InMemoryRaftStore : IRaftStore
{
    private readonly object _lock = new();
    private readonly Dictionary<long, (long Term, byte Kind, byte[] Content)> _entries = new();
    private long _term;
    private NodeId _votedFor;
    private long _appliedIndex;
    private long _lastLogIndex;
    private long _persistedIndex;
    private long _snapshotIndex;

    /// <inheritdoc/>
    public long Term { get { lock (_lock) return _term; } }

    /// <inheritdoc/>
    public NodeId VotedFor { get { lock (_lock) return _votedFor; } }

    /// <inheritdoc/>
    public long AppliedIndex { get { lock (_lock) return _appliedIndex; } }

    /// <inheritdoc/>
    public long LastLogIndex { get { lock (_lock) return _lastLogIndex; } }

    /// <inheritdoc/>
    public long LastLogTerm
    {
        get
        {
            lock (_lock) return _lastLogIndex > 0 && _entries.TryGetValue(_lastLogIndex, out var e) ? e.Term : 0;
        }
    }

    /// <inheritdoc/>
    public long AllocatedIndex => LastLogIndex;

    /// <inheritdoc/>
    public long PersistedIndex { get { lock (_lock) return _persistedIndex; } }

    /// <inheritdoc/>
    public long SnapshotIndex { get { lock (_lock) return _snapshotIndex; } }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;   // 内存版零恢复

    /// <inheritdoc/>
    public ValueTask WriteTermAndVoteAsync(long term, NodeId votedFor, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(term);
        lock (_lock)
        {
            if (term < _term) throw new InvalidOperationException($"任期回退违规：{term} < 当前 {_term}（term 单调不降）。");
            _term = term;
            _votedFor = votedFor;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask UpdateAppliedIndexAsync(long index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        lock (_lock) _appliedIndex = index;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<long> AppendAsync(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(prevIndex);
        lock (_lock)
        {
            if (prevIndex < _snapshotIndex)
                throw new InvalidOperationException($"prevIndex {prevIndex} 落入快照区（≤ SnapshotIndex {_snapshotIndex}）——快照区不可回写。");
            if (prevIndex > _lastLogIndex)
                throw new InvalidOperationException($"prevIndex {prevIndex} 超当前尾 {_lastLogIndex}（空洞追加违规）。");

            for (long i = prevIndex + 1; i <= _lastLogIndex; i++) _entries.Remove(i);   // 冲突回退一体
            var index = prevIndex;
            foreach (var (term, kind, content) in entries)
            {
                index++;
                _entries[index] = (term, kind, content.ToArray());
            }
            _lastLogIndex = index;
            // ★ 截断回退后水位钳制（持久化水位不可超尾——乱序旧批重写回退时 persisted 不得悬空）
            _persistedIndex = Math.Min(_persistedIndex, _lastLogIndex);
            return ValueTask.FromResult(_lastLogIndex);
        }
    }

    /// <inheritdoc/>
    public bool TryGetEntry(long index, out long term, out byte kind, out ReadOnlyMemory<byte> content)
    {
        lock (_lock)
        {
            if (index <= _snapshotIndex)   // 快照区经快照读面（ReadSnapshotEntriesAsync）——主数据区读面拒绝
            {
                term = 0;
                kind = 0;
                content = default;
                return false;
            }
            if (_entries.TryGetValue(index, out var e))
            {
                term = e.Term;
                kind = e.Kind;
                content = e.Content;
                return true;
            }
        }
        term = 0;
        kind = 0;
        content = default;
        return false;
    }

    /// <inheritdoc/>
    public IEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> Scan(long fromIndex, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        List<(long, long, byte, ReadOnlyMemory<byte>)> result = [];
        lock (_lock)
        {
            var start = Math.Max(fromIndex, _snapshotIndex + 1);
            for (var i = start; i <= _lastLogIndex && result.Count < maxCount; i++)
            {
                if (_entries.TryGetValue(i, out var e)) result.Add((i, e.Term, e.Kind, e.Content));
            }
        }
        return result;
    }

    /// <inheritdoc/>
    public int CountEntries(long fromIndex, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        lock (_lock)
        {
            if (fromIndex <= _snapshotIndex || fromIndex > _persistedIndex) return 0;
            return (int)Math.Min(maxCount, _persistedIndex - fromIndex + 1);
        }
    }

    /// <inheritdoc/>
    public ValueTask<int> WriteEntriesToAsync(long fromIndex, int count, IBufferWriter<byte> destination, long[] termsOut, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        lock (_lock)
        {
            if (fromIndex <= _snapshotIndex || fromIndex > _persistedIndex) return ValueTask.FromResult(0);   // 防御性零写入（越界自愈——接口契约）
            var n = (int)Math.Min(count, _persistedIndex - fromIndex + 1);
            if (n > termsOut.Length) n = termsOut.Length;
            if (n <= 0) return ValueTask.FromResult(0);
            RaftEntriesRegion.WriteCount(destination, n);
            for (var i = 0; i < n; i++)
            {
                if (!_entries.TryGetValue(fromIndex + i, out var e))
                    throw new InvalidOperationException($"条目 {fromIndex + i} 缺失（持久化区空洞——日志损坏形态）。");
                RaftEntriesRegion.WriteEntry(destination, e.Term, e.Kind, e.Content);
                termsOut[i] = e.Term;
            }
            return ValueTask.FromResult(n);
        }
    }

    /// <inheritdoc/>
    public ValueTask TruncateSuffixFromAsync(long indexInclusive, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indexInclusive);
        lock (_lock)
        {
            if (indexInclusive <= _snapshotIndex)
                throw new InvalidOperationException($"截尾起点 {indexInclusive} 落入快照区（≤ SnapshotIndex {_snapshotIndex}）。");
            for (var i = indexInclusive; i <= _lastLogIndex; i++) _entries.Remove(i);
            _lastLogIndex = Math.Min(_lastLogIndex, indexInclusive - 1);
            _persistedIndex = Math.Min(_persistedIndex, _lastLogIndex);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <summary>全量存储不支持 witness 断言（witness 用 WitnessHighWaterStore——DDR-F2）。</summary>
    public ValueTask<bool> AssertHighWatermarkAsync(long index, long term, CancellationToken ct = default)
        => throw new NotSupportedException("全量存储无 witness 断言语义（二期-F2）。");

    public ValueTask TruncatePrefixToAsync(long indexInclusive, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (indexInclusive <= _snapshotIndex) return ValueTask.CompletedTask;   // 幂等
            // ★ 只推水位（快照区条目保留——快照=可导出的日志前缀镜像，spec-03；
            //   主数据区读面经 TryGetEntry/Scan 的快照区拒绝隔离）
            _snapshotIndex = indexInclusive;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WaitForPersistedAsync(long index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (index > _lastLogIndex)
                throw new InvalidOperationException($"等待持久化 {index} 超已分配尾 {_lastLogIndex}。");
            _persistedIndex = Math.Max(_persistedIndex, index);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadEntriesAsync(
        long fromIndex, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in Scan(fromIndex, int.MaxValue))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public ValueTask<bool> PrevLogMatchesAsync(long prevLogIndex, long prevLogTerm, CancellationToken ct = default)
    {
        bool matched;
        lock (_lock)
        {
            matched = prevLogIndex == 0
                || prevLogIndex <= _snapshotIndex
                || prevLogIndex <= _persistedIndex && _entries.TryGetValue(prevLogIndex, out var e) && e.Term == prevLogTerm;
        }
        return ValueTask.FromResult(matched);
    }

    /// <inheritdoc/>
    public ValueTask<long> ReadLogTermAsync(long index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (index == _snapshotIndex && _snapshotIndex > 0)
                return ValueTask.FromResult(_entries[index].Term);   // 快照边界 term（复制边界 prev 用）
            if (index > _lastLogIndex || index < _snapshotIndex)
                throw new InvalidOperationException($"日志 {index} 处无条目（越界读：tail={_lastLogIndex} snapshot={_snapshotIndex}）。");
            return ValueTask.FromResult(_entries[index].Term);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadSnapshotEntriesAsync(
        long snapshotIndex, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // ★ 先锁内收集再产出（yield iterator 方法体内不可 lock——延迟执行使锁跨 MoveNext 失配，
        //   finally 的 Monitor.Exit 在无锁上下文抛 SynchronizationLockException）
        List<(long, long, byte, ReadOnlyMemory<byte>)> snapshot;
        lock (_lock)
        {
            snapshot = [];
            for (var i = 1; i <= Math.Min(snapshotIndex, _lastLogIndex); i++)
                if (_entries.TryGetValue(i, out var e)) snapshot.Add((i, e.Term, e.Kind, e.Content));
        }
        foreach (var entry in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return entry;
        }
    }

    /// <inheritdoc/>
    public async ValueTask ImportSnapshotAsync(long snapshotIndex,
        IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotIndex);
        var imported = new Dictionary<long, (long Term, byte Kind, byte[] Content)>();
        await foreach (var (index, term, kind, content) in entries.WithCancellation(ct).ConfigureAwait(false))
        {
            if (index < 1 || index > snapshotIndex)
                throw new InvalidOperationException($"快照条目 index {index} 越界（[1..{snapshotIndex}]）。");
            imported[index] = (term, kind, content.ToArray());
        }
        lock (_lock)
        {
            _entries.Clear();
            foreach (var (index, entry) in imported) _entries[index] = entry;
            _snapshotIndex = snapshotIndex;
            _lastLogIndex = snapshotIndex;
            _persistedIndex = snapshotIndex;
        }
    }

    /// <inheritdoc/>
    public ValueTask RefreshTailAfterImportAsync(CancellationToken ct = default) => ValueTask.CompletedTask;   // 内存版导入内建重锚
}
