using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Tests.Fixtures;

/// <summary>
/// 文件 raft 存储（spec-12 §13.1 夹具——Core.IO/TierFs 承载，真持久化语义场景：
/// 重启恢复/掉电半写截断。公共组件夹具——不等产品适配器）。
/// <para>★ 布局（v2——kind 结构字段化随 wire v3 信封之死）：meta = <c>[ver 1B][term 8B]
///   [votedFor 16B][applied 8B][snapshotIndex 8B]</c>（41B，temp + <c>Move(overwrite)</c>
///   原子替换——DurableRename）；log = 条目帧 <c>[index 8B][term 8B][kind 1B][len 4B][content]</c>
///   （21B 头，定位式追加写）。</para>
/// <para>★ 恢复：扫 log 重建——index 连续性校验 + 坏尾（半帧/错序）截断（掉电半写语义）；
///   双水位：Allocated 追加即推、Persisted 经 <see cref="WaitForPersistedAsync"/>
///   FlushData 推进（fsync 时机由协议层控制）。</para>
/// </summary>
public sealed class FsRaftStore : IRaftStore, IDisposable
{
    private const byte MetaVersion = 1;
    private const int MetaBytes = 41;
    private const int EntryHeaderSize = 21;   // v2：[index 8B][term 8B][kind 1B][len 4B]

    private readonly IFileSystem _fs;
    private readonly string _directory;
    private readonly object _lock = new();
    private IFileHandle? _log;
    private long _term;
    private NodeId _votedFor;
    private long _appliedIndex;
    private long _snapshotIndex;
    private long _lastLogIndex;
    private long _lastLogTerm;
    private long _persistedIndex;

    /// <summary>构造（零 IO——打开在 <see cref="InitializeAsync"/>）。</summary>
    /// <param name="fs">文件系统（TierFs 任意介质——memory 卷快路径 / local 真磁盘）。</param>
    /// <param name="directory">存储子目录（相对 fs 根）。</param>
    public FsRaftStore(IFileSystem fs, string directory)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(directory);
        _fs = fs;
        _directory = directory;
    }

    private string MetaPath => $"{_directory}/meta";
    private string MetaTempPath => $"{_directory}/meta.tmp";
    private string LogPath => $"{_directory}/log";
    private string LogTempPath => $"{_directory}/log.tmp";

    /// <inheritdoc/>
    public long Term { get { lock (_lock) return _term; } }

    /// <inheritdoc/>
    public NodeId VotedFor { get { lock (_lock) return _votedFor; } }

    /// <inheritdoc/>
    public long AppliedIndex { get { lock (_lock) return _appliedIndex; } }

    /// <inheritdoc/>
    public long LastLogIndex { get { lock (_lock) return _lastLogIndex; } }

    /// <inheritdoc/>
    public long LastLogTerm { get { lock (_lock) return _lastLogTerm; } }

    /// <inheritdoc/>
    public long AllocatedIndex => LastLogIndex;

    /// <inheritdoc/>
    public long PersistedIndex { get { lock (_lock) return _persistedIndex; } }

    /// <inheritdoc/>
    public long SnapshotIndex { get { lock (_lock) return _snapshotIndex; } }

    /// <inheritdoc/>
    public ValueTask InitializeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _fs.CreateDirectory(_directory);
        lock (_lock)
        {
            _log?.Dispose();
            _log = null;
        }

        if (_fs.Exists(MetaPath)) ReadMeta();
        var entries = new List<(long Index, long Term, byte Kind, byte[] Content)>();
        var offsets = new List<long>();
        long offset = 0;
        if (_fs.Exists(LogPath))
        {
            long length = 0;
            var header = new byte[EntryHeaderSize];
            using (var handle = _fs.Open(LogPath, new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
            {
                length = handle.Length;
                while (offset + EntryHeaderSize <= length)
                {
                    handle.Read(offset, header);
                    var index = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(0, 8));
                    var term = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8, 8));
                    var kind = header[16];
                    var contentLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(17, 4));
                    if (contentLength < 0 || offset + EntryHeaderSize + contentLength > length) break;   // 坏尾——截断
                    var content = new byte[contentLength];
                    handle.Read(offset + EntryHeaderSize, content);
                    if (entries.Count > 0 && index != entries[^1].Index + 1) break;                      // 错序——坏尾截断
                    entries.Add((index, term, kind, content));
                    offset += EntryHeaderSize + contentLength;
                    offsets.Add(offset);
                }
            }
            // 半写/坏尾回退：物理截到好尾（掉电半写语义——未持久化条目丢弃；读句柄已释放）
            if (offset < length) TruncateLogTo(offset);
        }

        lock (_lock)
        {
            _lastLogIndex = entries.Count > 0 ? entries[^1].Index : 0;
            _lastLogTerm = entries.Count > 0 ? entries[^1].Term : 0;
            _persistedIndex = _lastLogIndex;   // ★ 磁盘即持久化——重启后已持久化水位=尾（追加已全量刷盘）
            _logOffset = offset;   // 好尾字节位置——定位写追加锚点（坏尾已物理截到 offset）
        }
        _entries = entries;
        _offsets = offsets;
        return ValueTask.CompletedTask;
    }

    private List<(long Index, long Term, byte Kind, byte[] Content)> _entries = [];

    /// <summary>条目帧字节偏移前缀表（_offsets[i] = 条目 i+1 帧的末字节位置——截尾/冲突回退
    /// O(1) 定位，取代整日志重写（判例： follower 冲突路径 RewriteLog O(日志全长) 是
    /// TCP 长窗停顿轮主源，与生产 TierWAL 定位截断同构化））。</summary>
    private List<long> _offsets = [];

    /// <summary>日志文件字节尾（定位写的追加锚点——纯追加累加；回退/截尾/导入/坏尾整写时重锚）。</summary>
    private long _logOffset;

    private void ReadMeta()
    {
        using var handle = _fs.Open(MetaPath, new FileOpenOptions { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        Span<byte> meta = stackalloc byte[MetaBytes];
        handle.Read(0, meta);
        if (meta[0] != MetaVersion) throw new FormatException($"meta 版本不支持：{meta[0]}（期望 {MetaVersion}）。");
        lock (_lock)
        {
            _term = BinaryPrimitives.ReadInt64LittleEndian(meta[1..9]);
            _votedFor = new NodeId(meta[9..25]);
            _appliedIndex = BinaryPrimitives.ReadInt64LittleEndian(meta[25..33]);
            _snapshotIndex = BinaryPrimitives.ReadInt64LittleEndian(meta[33..41]);
        }
    }

    private void WriteMeta()
    {
        Span<byte> meta = stackalloc byte[MetaBytes];
        long term; NodeId votedFor; long applied, snapshot;
        lock (_lock) { term = _term; votedFor = _votedFor; applied = _appliedIndex; snapshot = _snapshotIndex; }
        meta[0] = MetaVersion;
        BinaryPrimitives.WriteInt64LittleEndian(meta[1..9], term);
        votedFor.CopyTo(meta[9..25]);
        BinaryPrimitives.WriteInt64LittleEndian(meta[25..33], applied);
        BinaryPrimitives.WriteInt64LittleEndian(meta[33..41], snapshot);

        if (_fs.Exists(MetaTempPath)) _fs.Delete(MetaTempPath);   // 残留 tmp 清理（disk 介质 Truncate 不建文件）
        using (var temp = _fs.Open(MetaTempPath, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew, Sharing = FileSharing.ReadWrite }))
        {
            temp.Write(0, meta);
            temp.Flush();   // temp 全量刷——替换前内容完整
        }
        _fs.Move(MetaTempPath, MetaPath, overwrite: true);   // DurableRename 原子替换
    }

    private IFileHandle LogHandle()
    {
        lock (_lock) _log ??= _fs.Open(LogPath, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite });
        return _log;
    }

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
        WriteMeta();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask UpdateAppliedIndexAsync(long index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        lock (_lock) _appliedIndex = index;
        WriteMeta();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask<long> AppendAsync(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(prevIndex);
        lock (_lock)
        {
            if (prevIndex < _snapshotIndex) throw new InvalidOperationException($"prevIndex {prevIndex} 落入快照区（≤ {_snapshotIndex}）。");
            if (prevIndex > _lastLogIndex) throw new InvalidOperationException($"prevIndex {prevIndex} 超当前尾 {_lastLogIndex}（空洞追加违规）。");

            if (prevIndex == _lastLogIndex)
            {
                // ★ 纯尾部追加——定位写到日志字节尾（零全量重写；O(1) 追加。整批拼一个连续缓冲
                //   单次写——每条 2 次小写会让 Mem 介质按写粒度租物理块；单次写 = 单区间连续分配）。
                //   缓存句柄（LogHandle）——回退/导入/坏尾路径替换文件前自释放。
                var offset = _logOffset;
                var index = prevIndex;
                var total = 0;
                for (var i = 0; i < entries.Count; i++)
                    total += EntryHeaderSize + entries[i].Content.Length;
                var batch = GC.AllocateUninitializedArray<byte>(total);
                var cursor = 0;
                var handle = LogHandle();
                for (var i = 0; i < entries.Count; i++)
                {
                    index++;
                    var (term, kind, content) = entries[i];
                    var data = content.ToArray();
                    BinaryPrimitives.WriteInt64LittleEndian(batch.AsSpan(cursor, 8), index);
                    BinaryPrimitives.WriteInt64LittleEndian(batch.AsSpan(cursor + 8, 8), term);
                    batch[cursor + 16] = kind;
                    BinaryPrimitives.WriteInt32LittleEndian(batch.AsSpan(cursor + 17, 4), data.Length);
                    data.AsSpan().CopyTo(batch.AsSpan(cursor + EntryHeaderSize));
                    _entries.Add((index, term, kind, data));
                    cursor += EntryHeaderSize + data.Length;
                    _offsets.Add(offset + cursor);
                }
                if (total > 0)
                    handle.Write(offset, batch);
                _logOffset = offset + total;
                _lastLogIndex = index;
                if (entries.Count > 0) _lastLogTerm = entries[^1].Term;
                return ValueTask.FromResult(_lastLogIndex);
            }

            // ★ 幂等重发快路径（判例 2026-09-03——TCP 长窗崩塌根因）：raft 在途重试窗下 leader
            //   会对未确认区间反复重发；follower 收到内容一致的重复批（prevIndex < tail）时，
            //   旧形态走下方全日志重建（TakeWhile + 逐条 byte[] 复制 = O(日志规模)/次——日志
            //   70k+ 后单次重发 ~10MB+，GC→更慢→更多重发 = 死亡螺旋，探针拐点实锤）。逐条比对
            //   内容一致 → 无操作幂等确认（真冲突仍走重建）。
            var idempotent = entries.Count > 0 && prevIndex + entries.Count == _lastLogIndex;   // ★ 恰重发当前尾窗才幂等免重建；prev+count < 尾 = leader 截短日志的乱序旧批——必须真截断重写
        //   （≤ 误判会漏删 leader 已替换的尾部条目 + matchIndex 虚高 → 复制楔死——ClampsPersistedWatermark 契约回归实锤）
            if (idempotent)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    var existing = _entries[(int)prevIndex + i];   // 直接寻址（index 连续 1..N）
                    var (term, kind, content) = entries[i];
                    if (existing.Term != term || existing.Kind != kind
                        || !existing.Content.AsSpan().SequenceEqual(content.Span))
                    {
                        idempotent = false;
                        break;
                    }
                }
            }
            if (idempotent)
            {
                var tail = prevIndex + entries.Count;
                _persistedIndex = Math.Max(_persistedIndex, tail);   // 幂等确认推进持久化水位
                return ValueTask.FromResult(_lastLogIndex);
            }

            var keep = _entries.TakeWhile(e => e.Index <= prevIndex).ToList();   // 冲突回退一体
            var keepOffsets = _offsets.Take(keep.Count).ToList();
            var index2 = prevIndex;
            foreach (var (term, kind, content) in entries)
            {
                index2++;
                keep.Add((index2, term, kind, content.ToArray()));
            }
            // ★ O(1) 截尾（原 RewriteLog 整写判例：O(日志全长) 的 temp+Move 是停顿轮主源）：
            //   物理截到保留尾字节位置 + 走纯追加定位写拼新批——帧字节布局不变
            TruncateLogTo(keepOffsets.Count > 0 ? keepOffsets[^1] : 0);
            AppendBatchInPlace(keep, keepOffsets);
            _entries = keep;
            _offsets = keepOffsets;
            _lastLogIndex = index2;
            _lastLogTerm = keep.Count > 0 ? keep[^1].Term : 0;
            // ★ 截断回退后水位钳制（持久化水位不可超尾——乱序旧批重写回退时 persisted 不得悬空）
            _persistedIndex = Math.Min(_persistedIndex, _lastLogIndex);
            return ValueTask.FromResult(_lastLogIndex);
        }
    }

    /// <summary>纯追加落位（冲突回退重写共用——在 _logOffset 起定位写整批，补 _offsets 前缀表）。
    /// ★ 调用方持 _lock；entries 尾段（index &gt; 旧尾）为新增段。</summary>
    private void AppendBatchInPlace(List<(long Index, long Term, byte Kind, byte[] Content)> entries, List<long> offsets)
    {
        var offset = _logOffset;
        var total = 0;
        for (var i = offsets.Count; i < entries.Count; i++)
            total += EntryHeaderSize + entries[i].Content.Length;
        if (total == 0) return;
        var batch = GC.AllocateUninitializedArray<byte>(total);
        var cursor = 0;
        var handle = LogHandle();
        for (var i = offsets.Count; i < entries.Count; i++)
        {
            var (index, term, kind, content) = entries[i];
            BinaryPrimitives.WriteInt64LittleEndian(batch.AsSpan(cursor, 8), index);
            BinaryPrimitives.WriteInt64LittleEndian(batch.AsSpan(cursor + 8, 8), term);
            batch[cursor + 16] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(batch.AsSpan(cursor + 17, 4), content.Length);
            content.AsSpan().CopyTo(batch.AsSpan(cursor + EntryHeaderSize));
            cursor += EntryHeaderSize + content.Length;
            offsets.Add(offset + cursor);
        }
        handle.Write(offset, batch);
        _logOffset = offset + total;
    }

    /// <summary>物理截断到好尾（掉电半写语义）：SetLength 原位截断——无 temp/Move
    /// （跨实例句柄共存安全：MoveFileEx 撞已打开目标句柄 error 5——同目录新旧实例并存是
    /// 掉电重启模拟的固有形态），未持久化条目丢弃。</summary>
    private void TruncateLogTo(long offset)
    {
        lock (_lock)
        {
            LogHandle().SetLength(offset);
            _logOffset = offset;
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
            // 直接寻址（index 连续 1..N、截前缀全量保留——_entries[i].Index == i+1）
            var i = (int)(index - 1);
            if (i < 0 || i >= _entries.Count || _entries[i].Index != index)
            {
                term = 0;
                kind = 0;
                content = default;
                return false;
            }
            term = _entries[i].Term;
            kind = _entries[i].Kind;
            content = _entries[i].Content;
            return true;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> Scan(long fromIndex, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        lock (_lock)
        {
            var from = Math.Max(fromIndex, _snapshotIndex + 1);
            var start = (int)Math.Clamp(from - 1, 0, _entries.Count);   // 直接寻址起点（连续 index）
            return _entries
                .Skip(start)
                .Take(maxCount)
                .Select(e => (e.Index, e.Term, e.Kind, (ReadOnlyMemory<byte>)e.Content))
                .ToList();
        }
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
                var e = _entries[(int)(fromIndex + i - 1)];
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
            if (indexInclusive <= _snapshotIndex) throw new InvalidOperationException($"截尾起点 {indexInclusive} 落入快照区（≤ {_snapshotIndex}）。");
            var keepCount = 0;
            while (keepCount < _entries.Count && _entries[keepCount].Index < indexInclusive) keepCount++;
            // ★ O(1) 物理截尾（SetLength 到保留尾——帧布局前缀不变，无整写）
            TruncateLogTo(keepCount > 0 ? _offsets[keepCount - 1] : 0);
            _entries.RemoveRange(keepCount, _entries.Count - keepCount);
            _offsets.RemoveRange(keepCount, _offsets.Count - keepCount);
            _lastLogIndex = keepCount > 0 ? _entries[^1].Index : 0;
            _lastLogTerm = keepCount > 0 ? _entries[^1].Term : 0;
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
            // ★ 只推水位（log 文件与内存全量保留——快照=可导出的日志前缀镜像，spec-03；
            //   主数据区读面经 TryGetEntry/Scan 的快照区拒绝隔离）
            _snapshotIndex = indexInclusive;
        }
        WriteMeta();   // snapshotIndex 变更持久化
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WaitForPersistedAsync(long index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (index > _lastLogIndex) throw new InvalidOperationException($"等待持久化 {index} 超已分配尾 {_lastLogIndex}。");
            LogHandle().Flush();   // fsync 同步点（协议层应答前）
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
            if (prevLogIndex == 0 || prevLogIndex <= _snapshotIndex)
            {
                matched = true;
            }
            else
            {
                // 直接寻址（index 连续——见 TryGetEntry）+ 持久化门槛
                var i = (int)(prevLogIndex - 1);
                matched = prevLogIndex <= _persistedIndex
                    && i < _entries.Count && _entries[i].Index == prevLogIndex
                    && _entries[i].Term == prevLogTerm;
            }
        }
        return ValueTask.FromResult(matched);
    }

    /// <inheritdoc/>
    public ValueTask<long> ReadLogTermAsync(long index, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (index == _snapshotIndex && _snapshotIndex > 0)
                return ValueTask.FromResult(_entries[(int)(index - 1)].Term);   // 快照边界 term（复制边界 prev 用）
            var i = (int)(index - 1);
            if (index < _snapshotIndex || index > _lastLogIndex
                || i < 0 || i >= _entries.Count || _entries[i].Index != index)
                throw new InvalidOperationException($"日志 {index} 处无条目（越界读：tail={_lastLogIndex} snapshot={_snapshotIndex}）。");
            return ValueTask.FromResult(_entries[i].Term);
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadSnapshotEntriesAsync(
        long snapshotIndex, [EnumeratorCancellation] CancellationToken ct = default)
    {
        List<(long Index, long Term, byte Kind, byte[] Content)> snapshot;
        lock (_lock)
        {
            snapshot = _entries.TakeWhile(e => e.Index <= Math.Min(snapshotIndex, _lastLogIndex))
                .Select(e => (e.Index, e.Term, e.Kind, e.Content)).ToList();
        }
        foreach (var (index, term, kind, content) in snapshot)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return (index, term, kind, content);
        }
    }

    /// <inheritdoc/>
    public async ValueTask ImportSnapshotAsync(long snapshotIndex,
        IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotIndex);
        // ★ 流式落盘（读一帧写一帧——传输面 O(单帧) 驻留）+ 内存工作集重建（本夹具全内存形态与
        //   AppendAsync 同构——文件是持久化镜像，内存是工作集）；完成后 temp+Move 原子替换
        long offset = 0;
        long tailTerm = 0;
        var imported = new List<(long Index, long Term, byte Kind, byte[] Content)>();
        var importOffsets = new List<long>();
        using (var handle = _fs.Open(LogTempPath, new FileOpenOptions { Access = AccessMode.ReadWrite, Mode = FileOpenMode.CreateNew, Sharing = FileSharing.ReadWrite }))
        {
            await foreach (var (index, term, kind, content) in entries.WithCancellation(ct).ConfigureAwait(false))
            {
                if (index < 1 || index > snapshotIndex)
                    throw new InvalidOperationException($"快照条目 index {index} 越界（[1..{snapshotIndex}]）。");
                var data = content.ToArray();
                var header = new byte[EntryHeaderSize];
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(0, 8), index);
                BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8, 8), term);
                header[16] = kind;
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(17, 4), data.Length);
                handle.Write(offset, header);
                handle.Write(offset + EntryHeaderSize, data);
                offset += EntryHeaderSize + data.Length;
                tailTerm = term;
                imported.Add((index, term, kind, data));
                importOffsets.Add(offset);
            }
            handle.Flush();
        }
        // ★ 句柄先释放再原子替换（Windows MoveFileEx 撞已打开的目标句柄——error 5；meta 同构路径）
        lock (_lock)
        {
            _log?.Dispose();
            _log = null;
        }
        _fs.Move(LogTempPath, LogPath, overwrite: true);   // DurableRename 原子替换
        lock (_lock)
        {
            _entries = imported;
            _offsets = importOffsets;
            _lastLogIndex = snapshotIndex;
            _lastLogTerm = tailTerm;
            _logOffset = offset;   // 导入文件尾——后续增量定位写锚点
            _persistedIndex = snapshotIndex;
            _snapshotIndex = snapshotIndex;
        }
        WriteMeta();   // snapshotIndex 持久化（meta temp+Move 原子）
    }

    /// <inheritdoc/>
    public ValueTask RefreshTailAfterImportAsync(CancellationToken ct = default)
    {
        // 导入已内建重锚（尾缓存随导入更新）——本方法幂等占位（重启恢复路径的尾缓存已在 InitializeAsync 重建）
        return ValueTask.CompletedTask;
    }

    /// <summary>释放 log 句柄。</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _log?.Dispose();
            _log = null;
        }
    }
}
