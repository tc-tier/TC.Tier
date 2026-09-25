using System.Buffers;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Core.Resources;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Structures.SortedIndex.Contracts;

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierZSet——score 有序集（tc-tier-collections-spec：Ring×HashIndex×BTree 三件组合特化，零新结构）。
/// <para>★ 组合配方（spec §1/定案③）：RingOfZSetKey（数据真相源——envelope TZS1 自述
/// member 字节 + score）+ HashOfZSetKey（member→score 点查——判重/点查真相源）+
/// BTreeOfZScoreKey（score 有序视图——范围/排名/极值）+ VersionedMetadata（域账）。</para>
/// <para>★ 线程模型（spec §6）：写操作全程单闸串行（<see cref="_opGate"/>）；读无锁（索引 epoch
/// 读保护 + Ring 冷热透明回源）。</para>
/// <para>★ 双索引一致性（spec §4/§7）：ZAdd 覆盖 = 删旧 ZScoreKey + 插新（单闸窗口内顺序执行）；
/// 崩溃窗口（旧有序键残留/缺条）由恢复对账「live 判据 = member 点查 == 本地址」收口。</para>
/// <para>★ 反向迭代（定案③ 波次 0b 原语）：ZRevRange/ZPopMax = TryGetFloor 域上哨兵 +
/// TryGetPrev 严格前驱步进（O(k·log n)）。</para>
/// </summary>
public sealed class TierZSet : LifecycleBase<CollectionRecoveryHints>, ITierZSet
{
    private const uint DefaultDomainId = ITierZSet.DefaultDomainId;

    private readonly RingOfZSetKey _ring;
    private readonly HashOfZSetKey _memberIndex;
    private readonly BTreeOfZScoreKey _scoreIndex;
    private readonly VersionedMetadata _watermark;
    private readonly DomainRegistry _registry;
    private readonly ZSetOptions _options;
    private readonly TimeProvider _clock;
    private readonly IFileSystem _fs;   // 保留：介质面
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _opGate = new(1, 1);   // 写串行闸（spec §6 单闸）

    private BackgroundWorkerLoop? _retentionWorker;

    /// <summary>构造 internal——外部只能经 <see cref="TierZSetBuilder.StartAsync"/>。</summary>
    internal TierZSet(RingOfZSetKey ring, HashOfZSetKey memberIndex, BTreeOfZScoreKey scoreIndex,
        VersionedMetadata watermark, IFileSystem fs, ZSetOptions options, ILogger? logger)
        : base(recovery: null, logger)
    {
        _ring = ring;
        _memberIndex = memberIndex;
        _scoreIndex = scoreIndex;
        _watermark = watermark;
        _registry = new DomainRegistry(options.DomainCapacity);
        _fs = fs;
        _options = options;
        _clock = options.Clock;
        _logger = logger;
        Resources.Add(watermark, ownership: ResourceOwnership.Owned);
    }

    /// <summary>已注册域数。</summary>
    public int DomainCount => _registry.Count;

    /// <summary>已分配尾地址（含未落盘——诊断/装配日志用）。</summary>
    public LogicalAddress TailAddress => _ring.TailAddress;

    /// <summary>已注册域标识快照（升序）。</summary>
    public IEnumerable<uint> DomainIds
    {
        get
        {
            var ids = _registry.DomainIds.ToArray();
            Array.Sort(ids);
            return ids;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 生命周期 / 恢复编排
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>★ Initialize 第一阶段钩子：并行启动 Ring + 域账 meta（双索引延后到恢复核心）。</summary>
    protected override void OnInitializeBegin()
    {
        _ring.Initialize();
        _watermark.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点。</summary>
    protected override IRecovery<CollectionRecoveryHints> CreateRecovery() => new TierZSetRecovery(this);

    /// <summary>恢复完成后启动 TTL 后台循环（DomainTtl 配置才装配）。</summary>
    protected override void OnInitializeComplete()
    {
        if (_options.DomainTtl is null) return;
        _retentionWorker = new RetentionWorker(this, _options.RetentionScanInterval);
        ConfigureBackgroundWorker(_retentionWorker);
    }

    /// <inheritdoc/>
    protected override void DisposeOverride(bool disposing)
    {
        base.DisposeOverride(disposing);
        _scoreIndex.Dispose();
        _memberIndex.Dispose();
        _ring.Dispose();
        _opGate.Dispose();
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await base.DisposeOverrideAsync(disposing).ConfigureAwait(false);
        _scoreIndex.Dispose();
        _memberIndex.Dispose();
        _ring.Dispose();
        _opGate.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 写入（Redis ZADD/ZINCRBY/ZREM 对齐——spec §4）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<long> ZAddAsync(double score, ReadOnlyMemory<byte> member, CancellationToken ct)
        => ZAddAsync(DefaultDomainId, score, member, ct);

    /// <inheritdoc/>
    public async ValueTask<long> ZAddAsync(uint domain, double score, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = _clock.GetUtcNow().Ticks;
            var entry = EnsureDomainSlot(domain, now);
            ulong encoded = ScoreCodec.Encode(score);
            int recordSize = checked(ZSetEnvelope.HeaderSize + member.Length);
            GuardDomainBytes(entry, recordSize);

            var zkey = ToMemberKey(domain, member.Span);
            var existingAddr = _memberIndex.Find(zkey);
            bool isNew = existingAddr == LogicalAddress.Empty;
            if (!isNew)
            {
                // 覆盖：校验 member 字节（碰撞 fail-fast）+ 删旧 score 有序键（一致性窗口——单闸内）
                var oldEnvelope = await ReadRecordEnvelopeAsync(existingAddr, ct).ConfigureAwait(false);
                if (oldEnvelope is not null)
                {
                    VerifyEnvelope(oldEnvelope, domain, member.Span, nameof(ZAddAsync));
                    double oldScore = ReadScore(oldEnvelope);
                    _scoreIndex.Delete(new ZScoreKey(domain, ScoreCodec.Encode(oldScore), zkey.HashLo, zkey.HashHi));
                }
                else
                {
                    // 幽灵条目（record 已失持久）——有序键残留形态无法定位 score；
                    // 留待恢复对账 live 判据清扫，此处仅按"新成员"续写
                    isNew = true;
                }
            }

            var addr = await WriteZSetRecordAsync(zkey, domain, encoded, member, ct).ConfigureAwait(false);
            _memberIndex.Insert(zkey, addr, _memberIndex.BeginAddress);
            _scoreIndex.Insert(new ZScoreKey(domain, encoded, zkey.HashLo, zkey.HashHi), addr, _scoreIndex.BeginAddress);

            CommitWrite(entry, addr, now, deltaCount: isNew ? 1 : 0, deltaBytes: isNew ? recordSize : 0);
            return isNew ? 1 : 0;
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<double> ZIncrByAsync(double delta, ReadOnlyMemory<byte> member, CancellationToken ct)
        => ZIncrByAsync(DefaultDomainId, delta, member, ct);

    /// <inheritdoc/>
    public async ValueTask<double> ZIncrByAsync(uint domain, double delta, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = _clock.GetUtcNow().Ticks;
            var entry = EnsureDomainSlot(domain, now);
            var zkey = ToMemberKey(domain, member.Span);
            int recordSize = checked(ZSetEnvelope.HeaderSize + member.Length);
            GuardDomainBytes(entry, recordSize);

            double current = 0;
            bool exists = false;
            var existingAddr = _memberIndex.Find(zkey);
            if (existingAddr != LogicalAddress.Empty)
            {
                var envelope = await ReadRecordEnvelopeAsync(existingAddr, ct).ConfigureAwait(false);
                if (envelope is not null)
                {
                    VerifyEnvelope(envelope, domain, member.Span, nameof(ZIncrByAsync));
                    current = ReadScore(envelope);
                    exists = true;
                }
            }
            double updated = current + delta;
            if (double.IsNaN(updated))
                throw new InvalidOperationException(
                    $"ZIncrBy 结果为 NaN（{current} + {delta}）——fail-fast（Redis 同构）");
            ulong encoded = ScoreCodec.Encode(updated);

            if (exists)
                _scoreIndex.Delete(new ZScoreKey(domain, ScoreCodec.Encode(current), zkey.HashLo, zkey.HashHi));

            var addr = await WriteZSetRecordAsync(zkey, domain, encoded, member, ct).ConfigureAwait(false);
            _memberIndex.Insert(zkey, addr, _memberIndex.BeginAddress);
            _scoreIndex.Insert(new ZScoreKey(domain, encoded, zkey.HashLo, zkey.HashHi), addr, _scoreIndex.BeginAddress);

            CommitWrite(entry, addr, now, deltaCount: exists ? 0 : 1, deltaBytes: exists ? 0 : recordSize);
            return updated;
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<long> ZRemAsync(IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
        => ZRemAsync(DefaultDomainId, members, ct);

    /// <inheritdoc/>
    public async ValueTask<long> ZRemAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_registry.TryGet(domain, out var entry)) return 0;
            long removed = 0;
            long now = _clock.GetUtcNow().Ticks;
            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();
                var zkey = ToMemberKey(domain, member.Span);
                var addr = _memberIndex.Find(zkey);
                if (addr == LogicalAddress.Empty) continue;

                var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
                int recordSize;
                if (envelope is null)
                {
                    recordSize = 0;   // 幽灵条目——字节口径不可考，恢复校正
                }
                else
                {
                    VerifyEnvelope(envelope, domain, member.Span, nameof(ZRemAsync));
                    recordSize = envelope.Length;
                    double score = ReadScore(envelope);
                    _scoreIndex.Delete(new ZScoreKey(domain, ScoreCodec.Encode(score), zkey.HashLo, zkey.HashHi));
                }

                // 墓碑先行、索引随后（崩溃窗口由恢复重放收口）
                await _ring.WriteTombstoneAsync(zkey, ct).ConfigureAwait(false);
                _memberIndex.Delete(zkey);

                removed++;
                Interlocked.Decrement(ref entry.MemberCount);
                Interlocked.Add(ref entry.BytesOccupied, -recordSize);
                Volatile.Write(ref entry.LastWriteTicks, now);
                entry.Dirty = true;
            }
            return removed;
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>原子弹出核心（_opGate 内——ZPopMin/ZPopMax 共用；取有序极值 → 整条移除）。</summary>
    private async ValueTask<(byte[] Member, double Score)?> PopExtremeAsync(
        uint domain, bool max, CancellationToken ct)
    {
        if (!_registry.TryGet(domain, out var entry)) return null;

        (ZScoreKey Key, LogicalAddress Value) found;
        if (max)
        {
            // 域上哨兵 floor——出域 = 空域（TryGetPrev 家族形态，波次 0b）
            if (!_scoreIndex.TryGetFloor(new ZScoreKey(domain, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue),
                    out var floorKey, out var floorAddr))
                return null;
            if (floorKey.DomainId != domain) return null;
            found = (floorKey, floorAddr);
        }
        else
        {
            using var cursor = _scoreIndex.CreateScanCursor(ReadDirection.Forward);
            if (!cursor.SeekLowerBound(new ZScoreKey(domain, 0UL, 0, 0))) return null;
            if (cursor.CurrentKey.DomainId != domain) return null;
            found = (cursor.CurrentKey, cursor.CurrentValue);
        }

        var (skey, addr) = found;
        var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
        if (envelope is null)
        {
            // 幽灵有序条目——移除有序键 + member 点查键（member 字节不可考——按键哈希自愈）
            _scoreIndex.Delete(skey);
            var mk = new ZSetKey(skey.DomainId, skey.HashLo, skey.HashHi);
            if (_memberIndex.Find(mk) == addr)
            {
                await _ring.WriteTombstoneAsync(mk, ct).ConfigureAwait(false);
                _memberIndex.Delete(mk);
                Interlocked.Decrement(ref entry.MemberCount);
            }
            entry.Dirty = true;
            return null;   // 空读语义——调用方重试即得下一极值
        }

        if (!ZSetEnvelope.TryUnwrap(envelope, out var envDomain, out var score, out var member)
            || envDomain != domain)
            throw new InvalidDataException($"envelope 域标识不符或损坏——{nameof(PopExtremeAsync)} fail-fast");
        // 键/envelope 哈希自洽校验（损坏 fail-fast）
        var (lo, hi) = MemberHash.Compute(member);
        if (lo != skey.HashLo || hi != skey.HashHi)
            throw new InvalidDataException("member 哈希与有序键不符（损坏/碰撞）——fail-fast");

        // 整条移除（墓碑先行 + 双索引删除 + 侧账）
        var zkey = new ZSetKey(domain, lo, hi);
        await _ring.WriteTombstoneAsync(zkey, ct).ConfigureAwait(false);
        _memberIndex.Delete(zkey);
        _scoreIndex.Delete(skey);
        Interlocked.Decrement(ref entry.MemberCount);
        Interlocked.Add(ref entry.BytesOccupied, -envelope.Length);
        Volatile.Write(ref entry.LastWriteTicks, _clock.GetUtcNow().Ticks);
        entry.Dirty = true;
        return (member, score);
    }

    /// <inheritdoc/>
    public ValueTask<(byte[] Member, double Score)?> ZPopMinAsync(CancellationToken ct)
        => ZPopMinAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public async ValueTask<(byte[] Member, double Score)?> ZPopMinAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PopExtremeAsync(domain, max: false, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<(byte[] Member, double Score)?> ZPopMaxAsync(CancellationToken ct)
        => ZPopMaxAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public async ValueTask<(byte[] Member, double Score)?> ZPopMaxAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PopExtremeAsync(domain, max: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 查询（双索引驱动）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<double?> ZScoreAsync(ReadOnlyMemory<byte> member, CancellationToken ct)
        => ZScoreAsync(DefaultDomainId, member, ct);

    /// <inheritdoc/>
    public async ValueTask<double?> ZScoreAsync(uint domain, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        EnsureReady();
        var zkey = ToMemberKey(domain, member.Span);
        var addr = _memberIndex.Find(zkey);
        if (addr == LogicalAddress.Empty) return null;
        var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
        if (envelope is null) return null;   // 幽灵条目——读路自愈为 miss
        VerifyEnvelope(envelope, domain, member.Span, nameof(ZScoreAsync));
        return ReadScore(envelope);
    }

    /// <inheritdoc/>
    public ValueTask<long> ZCountAsync(double min, double max, CancellationToken ct)
        => ZCountAsync(DefaultDomainId, min, max, ct);

    /// <inheritdoc/>
    public ValueTask<long> ZCountAsync(uint domain, double min, double max, CancellationToken ct)
    {
        EnsureReady();
        long count = 0;
        ulong lo = ScoreCodec.Encode(min), hi = ScoreCodec.Encode(max);
        if (lo > hi) return ValueTask.FromResult(0L);
        using var cursor = _scoreIndex.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new ZScoreKey(domain, lo, 0, 0))) return ValueTask.FromResult(0L);
        while (cursor.CurrentKey.DomainId == domain && cursor.CurrentKey.Score <= hi)
        {
            count++;
            if (!cursor.MoveNext()) break;
        }
        return ValueTask.FromResult(count);
    }

    /// <inheritdoc/>
    public ValueTask<long> ZCardAsync(CancellationToken ct) => ZCardAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public ValueTask<long> ZCardAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        return ValueTask.FromResult(_registry.TryGet(domain, out var entry)
            ? Interlocked.Read(ref entry.MemberCount)
            : 0L);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(byte[] Member, double Score)> ZRangeAsync(long start, long stop, CancellationToken ct)
        => ZRangeAsync(DefaultDomainId, start, stop, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<(byte[] Member, double Score)> ZRangeAsync(
        uint domain, long start, long stop,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReady();
        long total = await ZCardAsync(domain, ct).ConfigureAwait(false);
        (start, stop) = NormalizeRankRange(start, stop, total);
        if (start > stop) yield break;

        using var cursor = _scoreIndex.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new ZScoreKey(domain, 0UL, 0, 0))) yield break;
        long rank = 0;
        while (cursor.CurrentKey.DomainId == domain)
        {
            ct.ThrowIfCancellationRequested();
            if (rank > stop) yield break;
            if (rank >= start)
            {
                var found = await ReadMemberScoreAsync(cursor.CurrentValue, domain, ct).ConfigureAwait(false);
                if (found is { } hit)
                    yield return hit;
            }
            rank++;
            if (!cursor.MoveNext()) yield break;
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(byte[] Member, double Score)> ZRevRangeAsync(long start, long stop, CancellationToken ct)
        => ZRevRangeAsync(DefaultDomainId, start, stop, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<(byte[] Member, double Score)> ZRevRangeAsync(
        uint domain, long start, long stop,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReady();
        long total = await ZCardAsync(domain, ct).ConfigureAwait(false);
        (start, stop) = NormalizeRankRange(start, stop, total);
        if (start > stop) yield break;

        // 降序第 start 位 = 从最大键 TryGetPrev 步进 start 次（O(start·log n)，波次 0b 原语）
        if (!_scoreIndex.TryGetFloor(new ZScoreKey(domain, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue),
                out var key, out var addr) || key.DomainId != domain)
            yield break;
        for (long i = 0; i < start; i++)
        {
            if (!_scoreIndex.TryGetPrev(key, out key, out addr)) yield break;
            if (key.DomainId != domain) yield break;
        }

        for (long rank = start; rank <= stop; rank++)
        {
            ct.ThrowIfCancellationRequested();
            var found = await ReadMemberScoreAsync(addr, domain, ct).ConfigureAwait(false);
            if (found is { } hit)
                yield return hit;
            if (!_scoreIndex.TryGetPrev(key, out key, out addr)) yield break;
            if (key.DomainId != domain) yield break;
        }
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(byte[] Member, double Score)> ZRangeByScoreAsync(double min, double max, CancellationToken ct)
        => ZRangeByScoreAsync(DefaultDomainId, min, max, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<(byte[] Member, double Score)> ZRangeByScoreAsync(
        uint domain, double min, double max,
        [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReady();
        ulong lo = ScoreCodec.Encode(min), hi = ScoreCodec.Encode(max);
        if (lo > hi) yield break;
        using var cursor = _scoreIndex.CreateScanCursor(ReadDirection.Forward);
        if (!cursor.SeekLowerBound(new ZScoreKey(domain, lo, 0, 0))) yield break;
        while (cursor.CurrentKey.DomainId == domain && cursor.CurrentKey.Score <= hi)
        {
            ct.ThrowIfCancellationRequested();
            var found = await ReadMemberScoreAsync(cursor.CurrentValue, domain, ct).ConfigureAwait(false);
            if (found is { } hit)
                yield return hit;
            if (!cursor.MoveNext()) yield break;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 家族公共面（spec §3）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<CollectionStats> GetStatsAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        if (!_registry.TryGet(domain, out var entry))
            return ValueTask.FromResult(new CollectionStats(domain, 0, 0, null, null));
        long lastWrite = Volatile.Read(ref entry.LastWriteTicks);
        long? ttlBoundary = _options.DomainTtl is { } ttl && lastWrite != 0
            ? lastWrite + ttl.Ticks
            : null;
        return ValueTask.FromResult(new CollectionStats(
            domain,
            Interlocked.Read(ref entry.MemberCount),
            Interlocked.Read(ref entry.BytesOccupied),
            lastWrite == 0 ? null : lastWrite,
            ttlBoundary));
    }

    /// <summary>域整域回收：双索引域内清理 → 域账注销 → Ring 截断下限收口 → 域账持久。</summary>
    public async ValueTask<long> TruncateDomainAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await TruncateDomainCoreAsync(domain, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    private async ValueTask<long> TruncateDomainCoreAsync(uint domain, CancellationToken ct)
    {
        if (!_registry.TryGet(domain, out var entry)) return 0;

        await _ring.FlushUntilAsync(_ring.TailAddress, ct).ConfigureAwait(false);

        long deleted = 0;
        await foreach (var (key, addr, tomb) in _ring.ScanAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (tomb || key.DomainId != domain) continue;
            if (_memberIndex.Find(key) != addr) continue;   // 非 live
            var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
            if (envelope is not null && ZSetEnvelope.TryPeekHeader(envelope, out _, out var scoreEnc, out _))
                _scoreIndex.Delete(new ZScoreKey(domain, scoreEnc, key.HashLo, key.HashHi));
            _memberIndex.Delete(key);
            deleted++;
        }

        _registry.Remove(domain);
        LogicalAddress target = _registry.MinPinnedAddress(_ring.FlushedUntilAddress);
        if (target.IsValid && target <= _ring.FlushedUntilAddress && target > _ring.BeginAddress)
            _ring.TruncatePrefix(target);

        WriteAccountsLocked();
        _logger?.LogInformation("TierZSet 域 {Domain} 整域回收：{Deleted} 成员，Ring 截断至 {Target}",
            domain, deleted, target);
        return deleted;
    }

    /// <summary>显式落盘 + 脏域账持久。</summary>
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        EnsureReady();
        await _ring.FlushUntilAsync(_ring.TailAddress, ct).ConfigureAwait(false);
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasDirtyAccounts()) WriteAccountsLocked();
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <inheritdoc/>
    public ITransactionParticipant GetWatermarkParticipant() => _watermark;

    // ═══════════════════════════════════════════════════════════════════
    // 内部写/读路径
    // ═══════════════════════════════════════════════════════════════════

    private static ZSetKey ToMemberKey(uint domain, ReadOnlySpan<byte> member)
    {
        var (lo, hi) = MemberHash.Compute(member);
        return new ZSetKey(domain, lo, hi);
    }

    private DomainEntry EnsureDomainSlot(uint domain, long nowTicks)
        => _registry.Register(domain, LogicalAddress.Invalid, nowTicks);

    private void GuardDomainBytes(DomainEntry entry, int incomingRecordSize)
    {
        if (_options.DomainMaxBytes is not { } max) return;
        long projected = Interlocked.Read(ref entry.BytesOccupied) + incomingRecordSize;
        if (projected > max)
            throw new InvalidOperationException(
                $"域字节占用将超 DomainMaxBytes（{projected} > {max}）——fail-fast（写侧守卫，不静默丢）");
    }

    private void CommitWrite(DomainEntry entry, LogicalAddress addr, long nowTicks, long deltaCount, long deltaBytes)
    {
        entry.InitPinnedAddress(addr);
        if (deltaCount != 0) Interlocked.Add(ref entry.MemberCount, deltaCount);
        if (deltaBytes != 0) Interlocked.Add(ref entry.BytesOccupied, deltaBytes);
        Volatile.Write(ref entry.LastWriteTicks, nowTicks);
        entry.Dirty = true;
    }

    /// <summary>写 record（溢出分流——TierHash 同构；TZS1 头 + member 单块）。</summary>
    private async ValueTask<LogicalAddress> WriteZSetRecordAsync(
        ZSetKey key, uint domain, ulong scoreEncoded, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        int total = checked(ZSetEnvelope.HeaderSize + member.Length);
        bool oversized = _options.OverflowPolicy == OverflowPolicy.Enabled
            && total > _options.MinOverflowSize;
        byte[] rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            ZSetEnvelope.WriteHeader(rented.AsSpan(), domain, scoreEncoded, member.Length);
            member.Span.CopyTo(rented.AsSpan(ZSetEnvelope.HeaderSize));
            return oversized
                ? await _ring.WriteAsync(key, rented.AsMemory(0, total), ct).ConfigureAwait(false)
                : _ring.Write(key, rented.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>读完整 record envelope 字节（冷热透明回源；null = 幽灵条目）。</summary>
    private async ValueTask<byte[]?> ReadRecordEnvelopeAsync(LogicalAddress addr, CancellationToken ct)
    {
        var recordKey = await _ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        if (recordKey.ValueLength <= 0) return null;
        var buf = new byte[recordKey.ValueLength];
        int read = await _ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        if (read < ZSetEnvelope.HeaderSize) return null;
        if (!ZSetEnvelope.TryPeekHeader(buf.AsSpan(0, read), out _, out _, out _)) return null;
        return buf;
    }

    /// <summary>envelope 校验（域标识 + member 字节比对——碰撞/损坏 fail-fast）。</summary>
    /// <returns>member 起始偏移。</returns>
    private static int VerifyEnvelope(byte[] envelope, uint domain, ReadOnlySpan<byte> member, string operation)
    {
        if (!ZSetEnvelope.TryPeekHeader(envelope, out var envDomain, out _, out var memberLength)
            || envDomain != domain)
            throw new InvalidDataException(
                $"envelope 域标识不符（{envDomain} ≠ {domain}）或损坏——{operation} fail-fast");
        if (!member.SequenceEqual(envelope.AsSpan(ZSetEnvelope.HeaderSize, memberLength)))
            throw new InvalidDataException(
                $"member 哈希命中但字节不匹配（16B 碰撞）——{operation} fail-fast（定案④）");
        return ZSetEnvelope.HeaderSize + memberLength;
    }

    private static double ReadScore(byte[] envelope)
    {
        var layout = ZSetEnvelopeHeaderLayoutCodec.Read(envelope);
        return ScoreCodec.Decode(layout.ScoreEncoded);
    }

    /// <summary>读 (member, score)（有序迭代交付路；幽灵/损坏 = null 跳过）。</summary>
    private async ValueTask<(byte[] Member, double Score)?> ReadMemberScoreAsync(
        LogicalAddress addr, uint domain, CancellationToken ct)
    {
        var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
        if (envelope is null) return null;
        if (!ZSetEnvelope.TryUnwrap(envelope, out var envDomain, out var score, out var member)
            || envDomain != domain)
            return null;
        return (member, score);
    }

    /// <summary>排名区间归一（Redis ZRANGE 语义——负数自尾部数；出界钳制）。</summary>
    private static (long Start, long Stop) NormalizeRankRange(long start, long stop, long total)
    {
        if (start < 0) start += total;
        if (stop < 0) stop += total;
        if (start < 0) start = 0;
        if (stop >= total) stop = total - 1;
        return (start, stop);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 域账持久（TierHash 同构）
    // ═══════════════════════════════════════════════════════════════════

    private bool HasDirtyAccounts()
    {
        foreach (var (_, entry) in _registry.Enumerate())
            if (entry.Dirty) return true;
        return false;
    }

    private void WriteAccountsLocked()
    {
        var blocks = new List<DomainAccountBlock>();
        foreach (var (domainId, entry) in _registry.Enumerate())
        {
            blocks.Add(new DomainAccountBlock(
                domainId,
                Volatile.Read(ref entry.LastWriteTicks),
                Interlocked.Read(ref entry.MemberCount),
                Interlocked.Read(ref entry.BytesOccupied)));
            entry.Dirty = false;
        }
        var payload = CollectionsWatermarkDoc.Encode(blocks);
        _watermark.Write(payload);
        _watermark.Persist();
        _watermark.ReclaimOldVersions();
    }

    // ═══════════════════════════════════════════════════════════════════
    // TTL 后台循环（TierHash 同构）
    // ═══════════════════════════════════════════════════════════════════

    internal async ValueTask RunRetentionAsync(CancellationToken ct)
    {
        EnsureReady();
        if (_options.DomainTtl is { } ttl)
        {
            long now = _clock.GetUtcNow().Ticks;
            foreach (var domainId in _registry.DomainIds.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                if (!_registry.TryGet(domainId, out var entry)) continue;
                long lastWrite = Volatile.Read(ref entry.LastWriteTicks);
                if (lastWrite == 0 || now - lastWrite <= ttl.Ticks) continue;
                await TruncateDomainAsync(domainId, ct).ConfigureAwait(false);
            }
        }

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasDirtyAccounts()) WriteAccountsLocked();
        }
        finally
        {
            _opGate.Release();
        }
    }

    private sealed class RetentionWorker(TierZSet owner, TimeSpan interval)
        : BackgroundWorkerLoop(null, 1, "TierZSetRetentionWorker")
    {
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            await owner._clock.Delay(interval, ct).ConfigureAwait(false);
            await owner.RunRetentionAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复核心（spec §7——域账载入 → 双索引启动 → live 判据对账）
    // ═══════════════════════════════════════════════════════════════════

    private sealed class TierZSetRecovery(TierZSet owner) : RecoveryBase<CollectionRecoveryHints>
    {
        /// <summary>层间 join——Ring + 域账 meta。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._ring.WaitForReadyAsync(ct).ConfigureAwait(false);
            await owner._watermark.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心（spec §7 三步 + 双索引各自重建、交集 = 真相）：
        /// 1) 域账 keyed 块载入种子化；2) member 点查索引重放（墓碑感知/最新写胜出——精确）+
        ///    score 有序索引重放（墓碑跳过——可能含旧 score 残留）；3) 对账：有序树全扫——
        ///    「member 点查 == 本地址」判据清扫陈旧有序键（旧 score/悬空），Ring 重放事实权威化
        ///    域账 + 有序键缺条即补；last-write 缺块以恢复时刻收口；4) 域账校正持久。
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(CollectionRecoveryHints hints, CancellationToken ct)
        {
            // ── 1. 域账载入 ──
            var wbuf = new byte[Math.Max(owner._watermark.CurrentPayloadLength, CollectionsWatermarkDoc.HeaderSize)];
            int wread = owner._watermark.Read(wbuf);
            if (wread > 0 && CollectionsWatermarkDoc.TryDecode(wbuf.AsSpan(0, wread), out var blocks))
            {
                foreach (var block in blocks)
                {
                    var entry = owner._registry.Seed(block.DomainId);
                    Volatile.Write(ref entry.LastWriteTicks, block.LastWriteTicks);
                    Interlocked.Exchange(ref entry.MemberCount, block.MemberCount);
                    Interlocked.Exchange(ref entry.BytesOccupied, block.BytesOccupied);
                }
            }

            // ── 2. 双索引启动（member 点查精确 / score 有序近似——陈旧条目第 3 步清扫）──
            owner._memberIndex.Initialize(new ProbingIndexRecoveryHints(
                owner._ring.BeginAddress, owner._ring.TailAddress));
            await owner._memberIndex.WaitForReadyAsync(ct).ConfigureAwait(false);
            owner._scoreIndex.Initialize(new SortedIndexRecoveryHints(
                owner._ring.BeginAddress, owner._ring.TailAddress));
            await owner._scoreIndex.WaitForReadyAsync(ct).ConfigureAwait(false);

            // ── 3a. 有序树陈旧条目清扫（live 判据 = member 点查 == 本地址——旧 score/悬空全收口）──
            long cleaned = 0;
            var staleKeys = new List<ZScoreKey>();
            using (var cursor = owner._scoreIndex.CreateScanCursor(ReadDirection.Forward))
            {
                while (cursor.MoveNext())
                {
                    ct.ThrowIfCancellationRequested();
                    var k = cursor.CurrentKey;
                    var mk = new ZSetKey(k.DomainId, k.HashLo, k.HashHi);
                    if (owner._memberIndex.Find(mk) != cursor.CurrentValue)
                        staleKeys.Add(k);
                }
            }
            foreach (var k in staleKeys)
            {
                if (owner._scoreIndex.Delete(k)) cleaned++;
            }

            // ── 3b. Ring 重放事实权威化域账 + 有序键缺条即补 ──
            long recoveryNow = owner._clock.GetUtcNow().Ticks;
            long reinserted = 0;
            var liveFacts = new Dictionary<uint, (long Count, long Bytes)>();
            await foreach (var (key, addr, tomb) in owner._ring.ScanAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (tomb) continue;
                if (owner._memberIndex.Find(key) != addr) continue;
                var entry = owner._registry.Seed(key.DomainId);
                entry.InitPinnedAddress(addr);
                var recordKey = owner._ring.GetKey(addr);
                var envelope = await owner.ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
                if (envelope is not null && ZSetEnvelope.TryPeekHeader(envelope, out _, out var scoreEnc, out _))
                {
                    var expected = new ZScoreKey(key.DomainId, scoreEnc, key.HashLo, key.HashHi);
                    if (owner._scoreIndex.Find(expected) != addr)
                    {
                        owner._scoreIndex.Insert(expected, addr, owner._scoreIndex.BeginAddress);
                        reinserted++;
                    }
                }
                var (count, bytes) = liveFacts.TryGetValue(key.DomainId, out var cur) ? cur : (0L, 0L);
                liveFacts[key.DomainId] = (count + 1, bytes + recordKey.ValueLength);
            }

            foreach (var domainId in owner._registry.DomainIds.ToArray())
            {
                if (!owner._registry.TryGet(domainId, out var entry)) continue;
                if (liveFacts.TryGetValue(domainId, out var live))
                {
                    Interlocked.Exchange(ref entry.MemberCount, live.Count);
                    Interlocked.Exchange(ref entry.BytesOccupied, live.Bytes);
                    if (Volatile.Read(ref entry.LastWriteTicks) == 0)
                        Volatile.Write(ref entry.LastWriteTicks, recoveryNow);
                    entry.Dirty = true;
                }
                else
                {
                    owner._registry.Remove(domainId);
                }
            }

            // ── 4. 域账校正持久 ──
            owner.WriteAccountsLocked();
            RaiseProgress(90,
                $"cleaned={cleaned} reinserted={reinserted} domains={owner._registry.Count} members={owner._memberIndex.EntryCount}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 冷备份导入（TierZSetBackup 编排——同域连续批次）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导入批次（同域记录——TierZSetBackup.ImportAsync 逐批调）：写路径与 ZAdd 同构，幂等重放；
    /// last-write = 导入时刻（备份流无写入时刻事实）。_opGate 内整批执行。
    /// </summary>
    internal async ValueTask<long> ImportBatchAsync(
        uint domain, IReadOnlyList<(double Score, ReadOnlyMemory<byte> Member)> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return 0;
        EnsureReady();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long written = 0;
            long now = _clock.GetUtcNow().Ticks;
            var entry = EnsureDomainSlot(domain, now);
            foreach (var (score, member) in records)
            {
                ct.ThrowIfCancellationRequested();
                ulong encoded = ScoreCodec.Encode(score);
                int recordSize = checked(ZSetEnvelope.HeaderSize + member.Length);
                GuardDomainBytes(entry, recordSize);

                var zkey = ToMemberKey(domain, member.Span);
                var existingAddr = _memberIndex.Find(zkey);
                bool isNew = existingAddr == LogicalAddress.Empty;
                if (!isNew)
                {
                    var oldEnvelope = await ReadRecordEnvelopeAsync(existingAddr, ct).ConfigureAwait(false);
                    if (oldEnvelope is not null)
                    {
                        VerifyEnvelope(oldEnvelope, domain, member.Span, nameof(ImportBatchAsync));
                        double oldScore = ReadScore(oldEnvelope);
                        _scoreIndex.Delete(new ZScoreKey(domain, ScoreCodec.Encode(oldScore), zkey.HashLo, zkey.HashHi));
                    }
                    else
                    {
                        isNew = true;
                    }
                }

                var addr = await WriteZSetRecordAsync(zkey, domain, encoded, member, ct).ConfigureAwait(false);
                _memberIndex.Insert(zkey, addr, _memberIndex.BeginAddress);
                _scoreIndex.Insert(new ZScoreKey(domain, encoded, zkey.HashLo, zkey.HashHi), addr, _scoreIndex.BeginAddress);
                CommitWrite(entry, addr, now, deltaCount: isNew ? 1 : 0, deltaBytes: isNew ? recordSize : 0);
                written++;
            }
            return written;
        }
        finally
        {
            _opGate.Release();
        }
    }
}
