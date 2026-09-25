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

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierSet——去重集合（tc-tier-collections-spec：Ring×HashIndex 组合特化，零新结构）。
/// <para>★ 组合配方（spec §1）：RingOfSetKey（数据真相源——envelope TSE1 自述完整 member 字节）+
/// HashOfSetKey（判重/点查真相源）+ VersionedMetadata（域账真相源）+ Session 域（可选 2PC）。</para>
/// <para>★ 线程模型（spec §6）：写操作全程单闸串行；读无锁（索引 epoch 读保护 + Ring 冷热透明回源）。</para>
/// <para>★ 集合代数（spec §3）：<see cref="TierSetAlgebra"/> 静态算子（Intersect/Union/Diff）——
/// 跨域扫描合并，零新结构；结果集落目标域由调用方 SAdd 承接。</para>
/// </summary>
public sealed class TierSet : LifecycleBase<CollectionRecoveryHints>, ITierSet
{
    private const uint DefaultDomainId = ITierSet.DefaultDomainId;

    private readonly RingOfSetKey _ring;
    private readonly HashOfSetKey _index;
    private readonly VersionedMetadata _watermark;
    private readonly DomainRegistry _registry;
    private readonly SetOptions _options;
    private readonly TimeProvider _clock;
    private readonly IFileSystem _fs;   // 保留：介质面
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _opGate = new(1, 1);   // 写串行闸（spec §6 单闸）

    private BackgroundWorkerLoop? _retentionWorker;

    /// <summary>构造 internal——外部只能经 <see cref="TierSetBuilder.StartAsync"/>。</summary>
    internal TierSet(RingOfSetKey ring, HashOfSetKey index, VersionedMetadata watermark,
        IFileSystem fs, SetOptions options, ILogger? logger)
        : base(recovery: null, logger)
    {
        _ring = ring;
        _index = index;
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

    /// <summary>★ Initialize 第一阶段钩子：并行启动 Ring + 域账 meta（索引延后到恢复核心）。</summary>
    protected override void OnInitializeBegin()
    {
        _ring.Initialize();
        _watermark.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点。</summary>
    protected override IRecovery<CollectionRecoveryHints> CreateRecovery() => new TierSetRecovery(this);

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
        _index.Dispose();
        _ring.Dispose();
        _opGate.Dispose();
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await base.DisposeOverrideAsync(disposing).ConfigureAwait(false);
        _index.Dispose();
        _ring.Dispose();
        _opGate.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 写入（Redis SADD/SREM 对齐——spec §4）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<long> SAddAsync(IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
        => SAddAsync(DefaultDomainId, members, ct);

    /// <inheritdoc/>
    public async ValueTask<long> SAddAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = _clock.GetUtcNow().Ticks;
            var entry = EnsureDomainSlot(domain, now);
            long added = 0;
            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();
                added += await SAddCoreAsync(entry, domain, member, now, ct).ConfigureAwait(false) ? 1 : 0;
            }
            return added;
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>SAdd 单成员核心（_opGate 内调——SAdd/备份导入共用写路径）。返回是否新增。</summary>
    private async ValueTask<bool> SAddCoreAsync(DomainEntry entry, uint domain, ReadOnlyMemory<byte> member, long now, CancellationToken ct)
    {
        var key = ToKey(domain, member.Span);
        int recordSize = checked(SetEnvelope.HeaderSize + member.Length);
        GuardDomainBytes(entry, recordSize);

        bool isNew = _index.Find(key) == LogicalAddress.Empty;
        if (!isNew) return false;   // 幂等——已存在成员 no-op

        var addr = await WriteSetRecordAsync(key, domain, member, ct).ConfigureAwait(false);
        _index.Insert(key, addr, _index.BeginAddress);
        CommitWrite(entry, addr, now, deltaCount: 1, deltaBytes: recordSize);
        return true;
    }

    /// <inheritdoc/>
    public ValueTask<long> SRemAsync(IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
        => SRemAsync(DefaultDomainId, members, ct);

    /// <inheritdoc/>
    public async ValueTask<long> SRemAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
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
                var key = ToKey(domain, member.Span);
                var addr = _index.Find(key);
                if (addr == LogicalAddress.Empty) continue;

                var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
                int recordSize;
                if (envelope is null)
                {
                    recordSize = 0;   // 崩溃窗口幽灵条目——删路自愈（字节口径不可考，恢复校正）
                }
                else
                {
                    VerifyEnvelope(envelope, domain, member.Span, nameof(SRemAsync));
                    recordSize = envelope.Length;
                }

                // 墓碑先行、索引随后（崩溃窗口由恢复重放收口）
                await _ring.WriteTombstoneAsync(key, ct).ConfigureAwait(false);
                _index.Delete(key);

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

    // ═══════════════════════════════════════════════════════════════════
    // 查询
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<bool> SIsMemberAsync(ReadOnlyMemory<byte> member, CancellationToken ct)
        => SIsMemberAsync(DefaultDomainId, member, ct);

    /// <inheritdoc/>
    public async ValueTask<bool> SIsMemberAsync(uint domain, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        EnsureReady();
        var key = ToKey(domain, member.Span);
        var addr = _index.Find(key);
        if (addr == LogicalAddress.Empty) return false;
        var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
        if (envelope is null) return false;   // 幽灵条目——读路自愈为 miss
        VerifyEnvelope(envelope, domain, member.Span, nameof(SIsMemberAsync));
        return true;
    }

    /// <inheritdoc/>
    public ValueTask<long> SCardAsync(CancellationToken ct) => SCardAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public ValueTask<long> SCardAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        return ValueTask.FromResult(_registry.TryGet(domain, out var entry)
            ? Interlocked.Read(ref entry.MemberCount)
            : 0L);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<byte[]> SMembersAsync(CancellationToken ct)
        => SMembersAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<byte[]> SMembersAsync(
        uint domain, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReady();
        // Ring 流扫描 + 索引 live 判据（点查 == 本地址 = 存活版本）
        await foreach (var (key, addr, tomb) in _ring.ScanAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (tomb || key.DomainId != domain) continue;
            if (_index.Find(key) != addr) continue;
            var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
            if (envelope is null) continue;   // 幽灵条目——跳过
            if (!SetEnvelope.TryUnwrap(envelope, out var envDomain, out var member) || envDomain != domain)
                continue;
            yield return member;
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

    /// <summary>域整域回收：索引域内清理 → 域账注销 → Ring 截断下限收口 → 域账持久。</summary>
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
            if (_index.Find(key) != addr) continue;   // 非 live
            _index.Delete(key);
            deleted++;
        }

        _registry.Remove(domain);
        LogicalAddress target = _registry.MinPinnedAddress(_ring.FlushedUntilAddress);
        if (target.IsValid && target <= _ring.FlushedUntilAddress && target > _ring.BeginAddress)
            _ring.TruncatePrefix(target);

        WriteAccountsLocked();
        _logger?.LogInformation("TierSet 域 {Domain} 整域回收：{Deleted} 成员，Ring 截断至 {Target}",
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

    private static SetKey ToKey(uint domain, ReadOnlySpan<byte> member)
    {
        var (lo, hi) = MemberHash.Compute(member);
        return new SetKey(domain, lo, hi);
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

    /// <summary>写 record（溢出分流——TierHash 同构；TSE1 头 + member 单块）。</summary>
    private async ValueTask<LogicalAddress> WriteSetRecordAsync(
        SetKey key, uint domain, ReadOnlyMemory<byte> member, CancellationToken ct)
    {
        int total = checked(SetEnvelope.HeaderSize + member.Length);
        bool oversized = _options.OverflowPolicy == OverflowPolicy.Enabled
            && total > _options.MinOverflowSize;
        byte[] rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            SetEnvelope.WriteHeader(rented.AsSpan(), domain, member.Length);
            member.Span.CopyTo(rented.AsSpan(SetEnvelope.HeaderSize));
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
        if (read < SetEnvelope.HeaderSize) return null;
        if (!SetEnvelope.TryPeekHeader(buf.AsSpan(0, read), out _, out _)) return null;
        return buf;
    }

    /// <summary>envelope 校验（域标识 + member 字节比对——碰撞/损坏 fail-fast，定案④）。</summary>
    private static void VerifyEnvelope(byte[] envelope, uint domain, ReadOnlySpan<byte> member, string operation)
    {
        if (!SetEnvelope.TryPeekHeader(envelope, out var envDomain, out var memberLength)
            || envDomain != domain)
            throw new InvalidDataException(
                $"envelope 域标识不符（{envDomain} ≠ {domain}）或损坏——{operation} fail-fast");
        if (!member.SequenceEqual(envelope.AsSpan(SetEnvelope.HeaderSize, memberLength)))
            throw new InvalidDataException(
                $"member 哈希命中但字节不匹配（16B 碰撞）——{operation} fail-fast（定案④）");
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

    private sealed class RetentionWorker(TierSet owner, TimeSpan interval)
        : BackgroundWorkerLoop(null, 1, "TierSetRetentionWorker")
    {
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            await owner._clock.Delay(interval, ct).ConfigureAwait(false);
            await owner.RunRetentionAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复核心（spec §7——TierHash 同构：域账载入 → 索引重放 → 重放事实权威化）
    // ═══════════════════════════════════════════════════════════════════

    private sealed class TierSetRecovery(TierSet owner) : RecoveryBase<CollectionRecoveryHints>
    {
        /// <summary>层间 join——Ring + 域账 meta。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._ring.WaitForReadyAsync(ct).ConfigureAwait(false);
            await owner._watermark.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心（spec §7 三步）：域账 keyed 块载入（注册表种子化）→ 点查索引启动（Ring Ready 后
        /// ——重放含墓碑感知，流序折叠 = 最新写胜出）→ 对账（Ring 重放事实权威化域账计数/字节/钉住；
        /// 零存活域注销；last-write 缺块以恢复时刻收口防误过期）→ 域账校正持久。
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

            // ── 2. 索引启动（重放窗口 [Begin, Tail)——悬空清理 + 缺条重建由重放折叠收口）──
            owner._index.Initialize(new ProbingIndexRecoveryHints(
                owner._ring.BeginAddress, owner._ring.TailAddress));
            await owner._index.WaitForReadyAsync(ct).ConfigureAwait(false);

            // ── 3. 对账：Ring 重放事实权威化域账（live 判据 = 索引点查 == 本地址）──
            long recoveryNow = owner._clock.GetUtcNow().Ticks;
            var liveFacts = new Dictionary<uint, (long Count, long Bytes)>();
            await foreach (var (key, addr, tomb) in owner._ring.ScanAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (tomb) continue;
                if (owner._index.Find(key) != addr) continue;
                var entry = owner._registry.Seed(key.DomainId);
                entry.InitPinnedAddress(addr);
                var recordKey = owner._ring.GetKey(addr);
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
            RaiseProgress(90, $"domains={owner._registry.Count} members={owner._index.EntryCount}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 冷备份导入（TierSetBackup 编排——同域连续批次）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导入批次（同域成员——TierSetBackup.ImportAsync 逐批调）：写路径与 SAdd 同构，幂等重放；
    /// last-write = 导入时刻。_opGate 内整批执行。
    /// </summary>
    internal async ValueTask<long> ImportBatchAsync(
        uint domain, IReadOnlyList<ReadOnlyMemory<byte>> members, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0) return 0;
        EnsureReady();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long written = 0;
            long now = _clock.GetUtcNow().Ticks;
            var entry = EnsureDomainSlot(domain, now);
            foreach (var member in members)
            {
                ct.ThrowIfCancellationRequested();
                await SAddCoreAsync(entry, domain, member, now, ct).ConfigureAwait(false);
                written++;   // 执行数口径（与 THB1/TZB1 一致——幂等 no-op 也计入）
            }
            return written;
        }
        finally
        {
            _opGate.Release();
        }
    }
}
