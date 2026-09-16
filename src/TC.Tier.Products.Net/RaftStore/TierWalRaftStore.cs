using System.Buffers;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Products.Wal;

namespace TC.Tier.Products.Net.RaftStore;

/// <summary>
/// IRaftStore 的 TierWAL 适配器（产品组合层——Core.Net 引擎 × TierWAL 存储接线）：
/// 日志帧格式知识归本适配器（引擎与存储互不泄露布局——IRaftStore 契约）。
/// <para>★ 载荷帧格式（WAL entry payload = 引擎信封之外的本适配器帧）：
///   <c>[Term 8B LE][Kind 1B][Content]</c>——index 由 WAL"起点 + 顺序计数"推导（TierWAL §8.7，帧内零 index）。
///   布局声明式单点：<see cref="RaftWalFrameHeader"/>（生成 codec 出偏移/读写/尺寸）。</para>
/// <para>★ 元数据 opaque 槽（原子替换，TierWAL 内容零知识）：本适配器定义
///   <c>[ver 1B][term 8B][votedFor 16B][applied 8B][snapshotIndex 8B]</c>——布局声明式单点：
///   <see cref="RaftStoreMeta"/>。</para>
/// <para>★ 语义映射：AppendAsync 的 prevIndex 前置断言（冲突 = 截断后重写一体）翻译为
///   <see cref="ITierWal.TruncateSuffixAsync"/>（保留 ≤ prevIndex）+ <see cref="ITierWal.AppendBatchAsync"/>；
///   fsync 同步点 = <see cref="ITierWal.CommitAsync"/> + <see cref="ITierWal.WaitForPersistedAsync"/>（组提交
///   三维度经 TierWalOptions 配置——W4 批尾链调优旋钮）；TruncatePrefixTo = 仅推快照水位
///   （snapshotIndex 进 opaque，物理截头延迟至 TierWal 压缩调度——快照区数据保留供 spec-03 快照流）。</para>
/// <para>★ 串行化：append/truncate/import 序列经 <see cref="_gate"/> 排他（prevIndex 断言到落位非原子，
///   跨 await 需可等待门）；读面走 WAL 租约（线程安全）+ 门内状态守卫。</para>
/// <param name="wal">TierWAL 存储实例（日志/meta/快照面——Core.Net 引擎经本适配器与之接线）。</param>
/// </summary>
public sealed class TierWalRaftStore(ITierWal wal) : IRaftStore, IDisposable
{
    private const byte MetaVersion = 1;
    /// <summary>meta blob 宽（<see cref="RaftStoreMetaCodec"/> 生成物派生——布局变更零漂移）。</summary>
    private static int MetaBytes => RaftStoreMetaCodec.StructSize;
    /// <summary>帧头宽（<see cref="RaftWalFrameHeaderCodec"/> 生成物派生——布局变更零漂移）。</summary>
    private static int FrameHeaderBytes => RaftWalFrameHeaderCodec.StructSize;

    private readonly ITierWal _wal = wal ?? throw new ArgumentNullException(nameof(wal));
    private readonly SemaphoreSlim _gate = new(1, 1);

    private long _term;
    private NodeId _votedFor;
    private long _appliedIndex;
    private long _snapshotIndex;
    private long _lastLogIndex;
    private long _lastLogTerm;
    /// <summary>快照边界 term（entry N₀ 的 term——压缩时=尾 term，导入时=流末 term；重启从日志/镜像重建）。
    /// ★ 快照边界 prev（prevLogIndex == N₀）的 AppendEntries 必须携带真 term——发 0 会被无快照
    /// follower 的 term 比对拒绝（hint 走退→安装→换届风暴，HDD DIO+WD MultiNode 实测）。</summary>
    private long _snapshotBoundaryTerm;

    /// <inheritdoc/>
    public long Term { get { _gate.Wait(); try { return _term; } finally { _gate.Release(); } } }

    /// <inheritdoc/>
    public NodeId VotedFor { get { _gate.Wait(); try { return _votedFor; } finally { _gate.Release(); } } }

    /// <inheritdoc/>
    public long AppliedIndex { get { _gate.Wait(); try { return _appliedIndex; } finally { _gate.Release(); } } }

    /// <inheritdoc/>
    public long LastLogIndex { get { _gate.Wait(); try { return _lastLogIndex; } finally { _gate.Release(); } } }

    /// <inheritdoc/>
    public long LastLogTerm { get { _gate.Wait(); try { return _lastLogTerm; } finally { _gate.Release(); } } }

    /// <summary>已分配尾 = max(WAL 真相, 适配器记账)。★ 下限钳制：快照安装/导入把记账视图
    /// （_lastLogIndex）推到 N₀ 后，raft 可见分配尾不得低于视图——否则 lane nextIndex 被
    /// Allocated+1 钳回快照边界之下 → 安装触发取代心跳发送 → follower 选举窗必到期 =
    /// 换届风暴（HDD DIO+WD MultiNode 实测 t2→134/35s）。</summary>
    public long AllocatedIndex => Math.Max(_wal.AllocatedIndex, Volatile.Read(ref _lastLogIndex));

    /// <summary>持久化水位 = max(WAL 真相, 快照视图)。★ 语义：快照前缀 [1..N₀] 按定义即已
    /// 持久化（提交数据的镜像）——水位不得低于快照视图；否则应答 matchIndex 回落到视图之下，
    /// leader lane next 被拖回边界 → 安装触发与心跳互斥活锁（同上风暴机制）。</summary>
    public long PersistedIndex => Math.Max(_wal.PersistedIndex, Volatile.Read(ref _snapshotIndex));

    /// <inheritdoc/>
    public long SnapshotIndex { get { _gate.Wait(); try { return _snapshotIndex; } finally { _gate.Release(); } } }

    /// <inheritdoc/>
    public async ValueTask InitializeAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var meta = _wal.ReadMeta();
            if (meta.Length >= MetaBytes && meta.Span[0] == MetaVersion)
            {
                var m = RaftStoreMetaCodec.Read(meta.Span);
                _term = m.Term;
                _votedFor = m.VotedFor;
                _appliedIndex = m.AppliedIndex;
                _snapshotIndex = m.SnapshotIndex;
            }
            _lastLogIndex = _wal.AllocatedIndex;
            // ★ term 重建（导入态：日志区含目标 index → 直读；压缩态：目标已截出日志区（startIndex <
            //   HeadIndex）→ 快照镜像末帧——镜像覆盖 [Head..N₀]，末帧即 N₀ 的 term（快照边界与
            //   压缩态日志尾共用该值）。
            long boundary = 0;
            long imageLastTerm = 0;
            var needImage = false;
            try
            {
                _lastLogTerm = await ReadTermCoreAsync(_lastLogIndex).ConfigureAwait(false);
            }
            catch (ArgumentOutOfRangeException)
            {
                needImage = true;   // 日志尾已截出主区
            }
            if (_snapshotIndex > 0)
            {
                try
                {
                    boundary = await ReadTermCoreAsync(_snapshotIndex).ConfigureAwait(false);
                }
                catch (ArgumentOutOfRangeException)
                {
                    needImage = true;   // N₀ 已随压缩截出日志区
                }
                if (needImage && _wal.SnapshotIndex > 0)
                {
                    await foreach (var frame in _wal.ReadSnapshotEntriesAsync(ct).ConfigureAwait(false))
                    {
                        var term = RaftWalFrameHeaderCodec.Read(frame.Span).Term;
                        imageLastTerm = term;
                        if (boundary == 0) boundary = term;
                    }
                    if (_lastLogTerm == 0) _lastLogTerm = imageLastTerm;
                    if (boundary == 0) boundary = imageLastTerm;
                }
                _snapshotBoundaryTerm = boundary;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask WriteTermAndVoteAsync(long term, NodeId votedFor, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(term);
        byte[] opaque;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (term < _term) throw new InvalidOperationException($"任期回退违规：{term} < 当前 {_term}（term 单调不降）。");
            _term = term;
            _votedFor = votedFor;
            opaque = BuildMeta();
            await _wal.WriteMetaAsync(opaque, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask UpdateAppliedIndexAsync(long index, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        byte[] opaque;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _appliedIndex = index;
            opaque = BuildMeta();
            await _wal.WriteMetaAsync(opaque, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask<long> AppendAsync(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(prevIndex);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (prevIndex < _snapshotIndex) throw new InvalidOperationException($"prevIndex {prevIndex} 落入快照区（≤ {_snapshotIndex}）。");
            if (prevIndex > _lastLogIndex) throw new InvalidOperationException($"prevIndex {prevIndex} 超当前尾 {_lastLogIndex}（空洞追加违规）。");

            // 冲突回退一体：prevIndex < 当前尾 = 截断 (prevIndex, 尾] 后追加
            if (prevIndex < _lastLogIndex)
                await _wal.TruncateSuffixAsync(prevIndex, ct).ConfigureAwait(false);

            // ★ async 外壳 + sync 核心（批租约存活期零 await——C#12 ref struct 约束；
            //   页满 = 底层让渡零阻塞，sync 核心无挂起点由结构保证）
            var (lastIndex, lastTerm) = AppendBatchCore(prevIndex, entries);
            _lastLogIndex = lastIndex;
            if (entries.Count > 0) _lastLogTerm = lastTerm;
            return _lastLogIndex;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 追加 sync 核心：帧化进复用 scratch + 批租约直写（~1 次池租借/批——原形态 N 帧小数组 +
    /// views 数组 = N+1 次分配）。帧格式知识归本适配器——lease 只收一帧 payload；页满 = 底层让渡
    /// （异步刷盘零阻塞），scratch 在 Append 返回后立即可复用——数据已拷入页缓冲，零生命周期风险。
    /// </summary>
    /// <param name="prevIndex">前一条 index（断言 start == prevIndex+1；冲突时已先截断 (prevIndex, 尾]）。</param>
    /// <param name="entries">待追加条目表（Term/Kind/Content 三元组——帧化进复用 scratch）。</param>
    /// <returns>(本批末 index, 末条 term)。</returns>
    private (long LastIndex, long LastTerm) AppendBatchCore(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries)
    {
        long lastTerm = 0;
        var totalBytes = 0;
        for (var i = 0; i < entries.Count; i++)
            totalBytes += FrameHeaderBytes + entries[i].Content.Length;
        var scratch = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            using var lease = _wal.BeginAppendBatch();
            // StartIndex 在写锁内取（AllocatedIndex 无漂移窗口）——漂移 = 有第三方写者越权，fail-fast 零追加
            var start = lease.StartIndex;
            if (start != prevIndex + 1)
                throw new InvalidOperationException($"WAL 分配 index 漂移：start={start}（期望 {prevIndex + 1}）。");

            Span<byte> scratchSpan = scratch;
            var off = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var (term, kind, content) = entries[i];
                var frameLen = FrameHeaderBytes + content.Length;
                var frame = scratchSpan.Slice(off, frameLen);
                RaftWalFrameHeaderCodec.Write(frame, new RaftWalFrameHeader(term, kind));
                content.Span.CopyTo(frame[FrameHeaderBytes..]);
                _ = lease.Append(frame);
                off += frameLen;
                lastTerm = term;
            }

            return (start + entries.Count - 1, lastTerm);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <inheritdoc/>
    public bool TryGetEntry(long index, out long term, out byte kind, out ReadOnlyMemory<byte> content)
    {
        term = 0; kind = 0; content = default;
        foreach (var e in _wal.ReadFromSync(index))   // 冷 API——sync 迭代器单条即断
        {
            (term, kind, content) = Unframe(e.Data);
            content = content.ToArray();   // 租约视图租期止于 Dispose——跨出须独立拷贝
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public IEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> Scan(long fromIndex, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        var start = Math.Max(fromIndex, SnapshotIndex + 1);
        var taken = 0;
        foreach (var e in _wal.ReadFromSync(start))   // 冷 API——sync 迭代器
        {
            if (e.Index > _lastLogIndex || taken >= maxCount) yield break;
            var (term, kind, content) = Unframe(e.Data);
            yield return (e.Index, term, kind, content.ToArray());   // 租约视图跨出拷贝
            taken++;
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadEntriesAsync(
        long fromIndex, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var start = Math.Max(fromIndex, SnapshotIndex + 1);
        await foreach (var e in _wal.ReadFromAsync(start, ct).ConfigureAwait(false))
        {
            var (term, kind, content) = Unframe(e.Data);
            yield return (e.Index, term, kind, content);
        }
    }

    /// <inheritdoc/>
    public int CountEntries(long fromIndex, int maxCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxCount);
        if (fromIndex <= SnapshotIndex || fromIndex > _wal.PersistedIndex) return 0;
        return (int)Math.Min(maxCount, _wal.PersistedIndex - fromIndex + 1);
    }

    /// <inheritdoc/>
    public async ValueTask<int> WriteEntriesToAsync(long fromIndex, int count, IBufferWriter<byte> destination, long[] termsOut, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (fromIndex <= SnapshotIndex || fromIndex > _wal.PersistedIndex) return 0;   // 防御性零写入（接口契约）
        var n = (int)Math.Min(count, _wal.PersistedIndex - fromIndex + 1);
        if (n > termsOut.Length) n = termsOut.Length;
        if (n <= 0) return 0;
        RaftEntriesRegion.WriteCount(destination, n);
        using var lease = _wal.ReadBatchLeaseSync(fromIndex, n, ct);   // 页切片视图——单拷贝 WAL 页→帧
        var i = 0;
        await foreach (var entry in lease.Entries.WithCancellation(ct))
        {
            var (term, kind, content) = Unframe(entry.Data);
            RaftEntriesRegion.WriteEntry(destination, term, kind, content);
            termsOut[i] = term;
            i++;
        }
        return i;
    }

    /// <inheritdoc/>
    public async ValueTask<bool> PrevLogMatchesAsync(long prevLogIndex, long prevLogTerm, CancellationToken ct = default)
    {
        if (prevLogIndex == 0 || prevLogIndex <= SnapshotIndex) return true;
        if (prevLogIndex > _wal.PersistedIndex) return false;   // 未持久化 = 重启即丢
        return await ReadTermAtAsync(prevLogIndex, ct).ConfigureAwait(false) == prevLogTerm;
    }

    /// <inheritdoc/>
    public async ValueTask<long> ReadLogTermAsync(long index, CancellationToken ct = default)
    {
        // ★ 上适配器门：压缩窗内（wal 物理截头已推进 head、适配器视图未同步的间隙）无门读会撞
        //   "startIndex < HeadIndex" 抛——门序列化后视图/_head 一致，边界 term 由压缩面供给。
        //   边界读每 lane 每边界一次+缓存，锁开销可忽略。
        //   ★ 门内禁走带门属性（LastLogIndex/SnapshotIndex=同步 _gate.Wait——SemaphoreSlim 不可
        //   重入=自锁死）——直读字段。
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var snapshot = Volatile.Read(ref _snapshotIndex);
            if (index == snapshot && snapshot > 0)
                return Volatile.Read(ref _snapshotBoundaryTerm);   // 快照边界——term 从压缩/导入面取（日志区已截/无该槽语义）
            if (index < snapshot || index > Volatile.Read(ref _lastLogIndex))
                throw new InvalidOperationException($"日志 {index} 处无条目（越界读：tail={_lastLogIndex} snapshot={snapshot}）。");
            return await ReadTermAtAsync(index, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// witness 高水位断言——全量存储不支持（诚实失败优于静默，同 <see cref="WitnessHighWaterStore.PrevLogMatchesAsync"/> 口径）。
    /// <para>★ 断言流是 witness 专用复制形态（二期-F2/DDR-F2）——AE 接收侧 <c>IsWitness</c> 分支才调用，
    /// 全量成员走 <see cref="PrevLogMatchesAsync"/> 条目持久化路径。本方法被触达 = 装配/角色配置错误。</para>
    /// </summary>
    /// <param name="index">Leader 日志前沿 index。</param>
    /// <param name="term">Leader 任期（高水位记录的任期口径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>不返回——恒抛 <see cref="NotSupportedException"/>。</returns>
    /// <exception cref="NotSupportedException">恒抛（断言流不达全量存储——witness 分支专用）。</exception>
    public ValueTask<bool> AssertHighWatermarkAsync(long index, long term, CancellationToken ct = default)
        => throw new NotSupportedException(
            "TierWalRaftStore 为全量存储（存日志体）——witness 高水位断言流不适用（二期-F2：断言流仅达 WitnessHighWaterStore）；" +
            "本调用被触达说明角色与存储装配不一致（全量成员 AE 走 PrevLogMatchesAsync 条目持久化路径）。");

    /// <inheritdoc/>
    public async ValueTask TruncateSuffixFromAsync(long indexInclusive, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(indexInclusive);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (indexInclusive <= _snapshotIndex)
                throw new InvalidOperationException($"截尾起点 {indexInclusive} 落入快照区（≤ {_snapshotIndex}）。");
            // 映射：IRaftStore 删 [index, 尾] ⇔ TierWal 保留 ≤ newTail（newTail = index-1）
            await _wal.TruncateSuffixAsync(indexInclusive - 1, ct).ConfigureAwait(false);
            _lastLogIndex = indexInclusive - 1;
            _lastLogTerm = _lastLogIndex > 0 ? await ReadTermCoreAsync(_lastLogIndex).ConfigureAwait(false) : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 截头（快照/压缩）：★ v1 = 仅推快照水位（snapshotIndex 进 opaque）——数据保留供
    /// spec-03 快照流（<see cref="ReadSnapshotEntriesAsync"/> 读 [1..snapshotIndex] 全量前缀）；
    /// 物理截头延迟至 TierWal 压缩调度（<see cref="ITierWal.SnapshotAsync"/>——宿主定期驱动）。
    /// </summary>
    /// <param name="indexInclusive">快照覆盖点 N₀（≤ 当前 SnapshotIndex 即幂等返回）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照水位已推进到 <paramref name="indexInclusive"/>（物理截头延迟至压缩调度）后完成的 <see cref="ValueTask"/>。</returns>
    public async ValueTask TruncatePrefixToAsync(long indexInclusive, CancellationToken ct = default)
    {
        if (indexInclusive <= SnapshotIndex) return;   // 幂等
        byte[] opaque;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _snapshotIndex = indexInclusive;
            opaque = BuildMeta();
            await _wal.WriteMetaAsync(opaque, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask WaitForPersistedAsync(long index, CancellationToken ct = default)
    {
        // raft 同步点（应答前）：显式提交（一次 fsync = 一批持久化）+ 等水位
        await _wal.CommitAsync(ct).ConfigureAwait(false);
        await _wal.WaitForPersistedAsync(index, ct).ConfigureAwait(false);
    }

    /// <summary>宿主快照压缩（一体快照+截头，N₀=PersistedIndex——TierWal 压缩调度的 store 面）。
    /// ★ 必经本面而非直调 <see cref="ITierWal.SnapshotAsync"/>：①经 <see cref="_gate"/> 排他——压缩
    /// 中途的并发 append/读撞截断头 = store 异常源（MultiNode@20 风暴实测）；②门内同步
    /// <see cref="_snapshotIndex"/> 适配器视图——直调 WAL 会绕过视图同步，快照覆盖钳位守卫
    /// （AppendAsync prevIndex 守卫 / AE 覆盖区跳过）失准。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>压缩后的快照覆盖点 N₀（= PersistedIndex）。</returns>
    public async ValueTask<long> SnapshotAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var n0 = await _wal.SnapshotAsync(ct).ConfigureAwait(false);
            if (n0 > _snapshotIndex)
            {
                _snapshotIndex = n0;
                _snapshotBoundaryTerm = _lastLogTerm;   // N₀=压缩时持久化尾——边界 term=尾条 term
            }
            return n0;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadSnapshotEntriesAsync(
        long snapshotIndex, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (snapshotIndex <= 0) yield break;
        // ★ 读源拼接（防洞——适配器视图与 WAL 镜像水位可短暂分叉）：宿主压缩（SnapshotAsync
        //   物理截头——TruncatePrefixAsync(walN₀+1)）后 [Head..walN₀] 归镜像帧流（走 ReadFromAsync(1)
        //   会撞 HeadIndex 抛"区间已截断"），(walN₀, snapshotIndex] 仍在日志区（物理截头只到 walN₀+1）
        //   ——两段拼接才覆盖完整 [1..snapshotIndex]，缺一段=重建读面成洞（drain 未追满→热循环实测）；
        //   协议 TruncatePrefixToAsync（仅推水位）数据全在日志区——walN₀=0 走全量重放读。
        if (_wal.SnapshotIndex > 0)
        {
            var walN0 = _wal.SnapshotIndex;
            var index = _wal.SnapshotHeadIndex;
            await foreach (var frame in _wal.ReadSnapshotEntriesAsync(ct).ConfigureAwait(false))
            {
                if (index > snapshotIndex) yield break;
                var (term, kind, content) = Unframe(frame);
                yield return (index, term, kind, content);
                index++;
            }
            if (snapshotIndex > walN0)
            {
                await foreach (var e in _wal.ReadFromAsync(walN0 + 1, ct).ConfigureAwait(false))
                {
                    if (e.Index > snapshotIndex) yield break;
                    var (term, kind, content) = Unframe(e.Data);
                    yield return (e.Index, term, kind, content);
                }
            }
            yield break;
        }
        await foreach (var e in _wal.ReadFromAsync(1, ct).ConfigureAwait(false))   // 异步游标——帧间页 IO 不阻塞线程
        {
            if (e.Index > snapshotIndex) yield break;
            var (term, kind, content) = Unframe(e.Data);
            yield return (e.Index, term, kind, content);
        }
    }

    /// <inheritdoc/>
    public async ValueTask ImportSnapshotAsync(long snapshotIndex,
        IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotIndex);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 清全部（TruncateSuffix(0) = 保留 ≤ 0 = 空）→ 逐批重灌导入流（读一帧写一帧——O(单帧) 驻留）
            await _wal.TruncateSuffixAsync(0, ct).ConfigureAwait(false);
            long lastTerm = 0;
            var buffered = new List<ReadOnlyMemory<byte>>(256);
            long expected = 1;
            await foreach (var (index, term, kind, content) in entries.WithCancellation(ct).ConfigureAwait(false))
            {
                if (index < 1 || index > snapshotIndex)
                    throw new InvalidOperationException($"快照条目 index {index} 越界（[1..{snapshotIndex}]）。");
                buffered.Add(Frame(term, kind, content));
                lastTerm = term;
                if (buffered.Count < 256) continue;
                var r = await _wal.AppendBatchAsync(buffered, ct).ConfigureAwait(false);
                if (r.StartIndex != expected) throw new InvalidOperationException($"快照重灌 index 断言失败：{r.StartIndex} ≠ {expected}。");
                if (r.Count != buffered.Count) throw new InvalidOperationException($"快照重灌条数断言失败：{r.Count} ≠ {buffered.Count}（短落=数据缺口）。");
                expected += r.Count;
                buffered.Clear();
            }
            if (buffered.Count > 0)
            {
                var r = await _wal.AppendBatchAsync(buffered, ct).ConfigureAwait(false);
                if (r.StartIndex != expected) throw new InvalidOperationException($"快照重灌 index 断言失败：{r.StartIndex} ≠ {expected}。");
                if (r.Count != buffered.Count) throw new InvalidOperationException($"快照重灌条数断言失败：{r.Count} ≠ {buffered.Count}（尾批短落=视图谎报源）。");
            }
            await _wal.CommitAsync(ct).ConfigureAwait(false);   // 导入数据持久化（快照区随日志保留）
            _lastLogIndex = snapshotIndex;
            _lastLogTerm = lastTerm;
            _snapshotIndex = snapshotIndex;
            _snapshotBoundaryTerm = lastTerm;   // 边界 term = 导入流末条 term
            await _wal.WriteMetaAsync(BuildMeta(), ct).ConfigureAwait(false);   // snapshotIndex 持久化（term/vote/applied 不动）
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask RefreshTailAfterImportAsync(CancellationToken ct = default)
    {
        // 导入已内建重锚（尾缓存随导入更新）——本方法幂等占位
        return ValueTask.CompletedTask;
    }

    // ═══ 内部 ═══

    /// <summary>读 index 处条目 term（租约流式取首条——term 是值拷贝；异步游标 MoveNextAsync 页 IO 不阻塞）。</summary>
    /// <param name="index">待读条目 index（须 ∈ (snapshotIndex, lastLogIndex]——由调用方门内守卫）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>该 index 处条目的 term。</returns>
    /// <exception cref="InvalidOperationException">index 越界（日志区无该条目）。</exception>
    private async ValueTask<long> ReadTermAtAsync(long index, CancellationToken ct)
    {
        using var lease = _wal.ReadBatchLeaseSync(index, 1, ct);
        await foreach (var e in lease.Entries.WithCancellation(ct).ConfigureAwait(false))
            return RaftWalFrameHeaderCodec.Read_Term(e.Data.Span);
        // ★ 异常消息禁走带门属性（LastLogIndex/SnapshotIndex=同步 _gate.Wait）——本方法经
        //   ReadLogTermAsync 门内调用（SemaphoreSlim 不可重入），构造消息即等自己的门：
        //   异常永远抛不出 + 门永久泄漏，全部同步属性等待者楔死（MultiNode 快照调度实测）。
        //   直读字段（与 ReadLogTermAsync 门内同规）。
        throw new InvalidOperationException(
            $"日志 {index} 处无条目（越界读：tail={Volatile.Read(ref _lastLogIndex)} snapshot={Volatile.Read(ref _snapshotIndex)}）。");
    }

    /// <summary>门内读尾 term（状态维护路径，异步游标）。</summary>
    /// <param name="index">待读条目 index（≤ 0 返回 0）。</param>
    /// <returns>该 index 处条目的 term（越界/不存在返回 0）。</returns>
    private async ValueTask<long> ReadTermCoreAsync(long index)
    {
        if (index <= 0) return 0;
        using var lease = _wal.ReadBatchLeaseSync(index, 1);
        await foreach (var e in lease.Entries.ConfigureAwait(false))
            return RaftWalFrameHeaderCodec.Read_Term(e.Data.Span);
        return 0;
    }

    /// <summary>帧化：[Term 8B LE][Kind 1B][Content]（布局见 <see cref="RaftWalFrameHeader"/>）。</summary>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类（<see cref="RaftEntryKind"/>）。</param>
    /// <param name="content">条目载荷。</param>
    /// <returns>帧化后的字节缓冲（帧头 + 载荷紧随）。</returns>
    private static byte[] Frame(long term, byte kind, ReadOnlyMemory<byte> content)
    {
        var buf = GC.AllocateUninitializedArray<byte>(FrameHeaderBytes + content.Length);
        RaftWalFrameHeaderCodec.Write(buf, new RaftWalFrameHeader(term, kind));
        content.Span.CopyTo(buf.AsSpan(FrameHeaderBytes));
        return buf;
    }

    /// <summary>解帧（页切片视图——调用方决定拷贝或原地消费）。</summary>
    /// <param name="data">原始帧数据（帧头 + 载荷紧随）。</param>
    /// <returns>(term, kind, content)——content 为页切片视图（租期内有效，跨出须独立拷贝）。</returns>
    private static (long term, byte kind, ReadOnlyMemory<byte> content) Unframe(ReadOnlyMemory<byte> data)
    {
        var header = RaftWalFrameHeaderCodec.Read(data.Span);
        return (header.Term, header.Kind, data[FrameHeaderBytes..]);
    }

    /// <summary>opaque 元数据 blob（布局见 <see cref="RaftStoreMeta"/>——门内调用）。</summary>
    /// <returns>序列化后的元数据字节缓冲（term/votedFor/appliedIndex/snapshotIndex）。</returns>
    private byte[] BuildMeta()
    {
        var meta = GC.AllocateUninitializedArray<byte>(MetaBytes);
        RaftStoreMetaCodec.Write(meta, new RaftStoreMeta(MetaVersion, _term, _votedFor, _appliedIndex, _snapshotIndex));
        return meta;
    }
    /// <summary>释放排他门（组合层负责调用——IRaftStore 无生命周期面，门资源随适配器收尾）。</summary>
    public void Dispose() => _gate.Dispose();
}
