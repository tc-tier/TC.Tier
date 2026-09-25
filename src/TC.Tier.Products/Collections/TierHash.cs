using System.Buffers;
using System.Buffers.Binary;
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
/// TierHash——域级哈希表（tc-tier-collections-spec：Ring×HashIndex 组合特化，零新结构）。
/// <para>★ 组合配方（spec §1 四件积木）：RingOfHashKey（数据真相源——append-only，record key 只做分类）
///   + HashOfHashKey（点查索引——判重/覆盖/点查的真相源）+ VersionedMetadata（域账真相源——
///   last-write/TTL 锚/计数 keyed 块）+ Session 域（可选 2PC 经 <see cref="GetWatermarkParticipant"/>）。</para>
/// <para>★ 线程模型（spec §6）：写操作全程单闸串行（<see cref="_opGate"/>——HSet/HDEL/HIncrBy/
///   TruncateDomain/TTL 过期/备份导入互斥；第一版单闸够用——网关消费面远低于 1M op/s 级）；
///   读无锁（HashIndex epoch 读保护 + Ring 冷热透明回源）。</para>
/// <para>★ 回收（spec §5/§9）：域界回收 = 索引域内清理 → Ring 截断下限 = 全体域钉住地址 min →
///   域账注销持久。TTL = 域账 last-write 锚整域过期（定案②）；DomainMaxBytes = 写侧 fail-fast 守卫。</para>
/// <para>★ 字节占用口径：envelope 逻辑字节（固定头 + field + value）——非 Ring 物理开销
///   （record 头/对齐不计量）；DomainMaxBytes 守卫与统计快照同口径。</para>
/// </summary>
public sealed class TierHash : LifecycleBase<CollectionRecoveryHints>, ITierHash
{
    private const uint DefaultDomainId = ITierHash.DefaultDomainId;

    private readonly RingOfHashKey _ring;
    private readonly HashOfHashKey _index;
    private readonly VersionedMetadata _watermark;
    private readonly DomainRegistry _registry;
    private readonly HashOptions _options;
    private readonly TimeProvider _clock;
    private readonly IFileSystem _fs;   // 保留：介质面（降档/运维路径）
    private readonly ILogger? _logger;
    private readonly SemaphoreSlim _opGate = new(1, 1);   // 写串行闸（spec §6 单闸——写/回收/导入互斥）

    private BackgroundWorkerLoop? _retentionWorker;

    /// <summary>构造 internal——外部只能经 <see cref="TierHashBuilder.StartAsync"/>。</summary>
    /// <param name="ring">数据 Ring（真相源）。</param>
    /// <param name="index">点查索引（判重/覆盖/点查真相源）。</param>
    /// <param name="watermark">域账 meta（last-write/TTL 锚持久域）。</param>
    /// <param name="fs">文件系统抽象（介质面）。</param>
    /// <param name="options">Hash 选项（实例名/几何/TTL/容量护栏）。</param>
    /// <param name="logger">日志（缺省 null）。</param>
    internal TierHash(RingOfHashKey ring, HashOfHashKey index, VersionedMetadata watermark,
        IFileSystem fs, HashOptions options, ILogger? logger)
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

    /// <summary>已注册域标识快照（升序——备份导出/诊断遍历面）。</summary>
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

    /// <summary>★ Initialize 第一阶段钩子：并行启动 Ring + 域账 meta（索引延后到恢复核心——依赖 Ring Ready）。</summary>
    protected override void OnInitializeBegin()
    {
        _ring.Initialize();
        _watermark.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点——恢复核心 = 域账载入 + 索引启动 + 对账（spec §7）。</summary>
    /// <returns>恢复算法实例（由基类持有并在 Initialize 中执行）。</returns>
    protected override IRecovery<CollectionRecoveryHints> CreateRecovery() => new TierHashRecovery(this);

    /// <summary>恢复完成 + 装配就绪后启动 TTL 后台循环（spec §5——低频扫域账；DomainTtl 配置才装配）。</summary>
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
    // 写入（Redis HSET/HDEL/HINCRBY 对齐——spec §4）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<bool> HSetAsync(ReadOnlyMemory<byte> field, ReadOnlyMemory<byte> value, CancellationToken ct)
        => HSetAsync(DefaultDomainId, field, value, ct);

    /// <inheritdoc/>
    public async ValueTask<bool> HSetAsync(uint domain, ReadOnlyMemory<byte> field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await HSetCoreAsync(domain, field, value, ct).ConfigureAwait(false);
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <summary>HSet 核心（_opGate 内调——HSet/HIncrBy/备份导入共用写路径）。</summary>
    private async ValueTask<bool> HSetCoreAsync(uint domain, ReadOnlyMemory<byte> field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        long now = _clock.GetUtcNow().Ticks;
        var entry = EnsureDomainSlot(domain, now);
        var key = ToKey(domain, field.Span);
        int recordSize = checked(HashEnvelope.HeaderSize + field.Length + value.Length);
        GuardDomainBytes(entry, recordSize);

        bool isNew = _index.Find(key) == LogicalAddress.Empty;
        LogicalAddress addr = await WriteHashRecordAsync(key, domain, field, value, ct).ConfigureAwait(false);
        _index.Insert(key, addr, _index.BeginAddress);

        // 侧账（写后提交——容量/字节护栏已前置）
        CommitWrite(entry, addr, now, deltaCount: isNew ? 1 : 0, deltaBytes: recordSize);
        return isNew;
    }

    /// <inheritdoc/>
    public ValueTask<long> HIncrByAsync(ReadOnlyMemory<byte> field, long delta, CancellationToken ct)
        => HIncrByAsync(DefaultDomainId, field, delta, ct);

    /// <inheritdoc/>
    public async ValueTask<long> HIncrByAsync(uint domain, ReadOnlyMemory<byte> field, long delta, CancellationToken ct)
    {
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long now = _clock.GetUtcNow().Ticks;
            var entry = EnsureDomainSlot(domain, now);
            var key = ToKey(domain, field.Span);

            long current = 0;
            bool exists = false;
            var addr = _index.Find(key);
            if (addr != LogicalAddress.Empty)
            {
                var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
                if (envelope is null)
                {
                    // 崩溃窗口幽灵条目——算术路自愈为"不存在"（从 0 起算，覆写幽灵）
                }
                else
                {
                    int valueOffset = VerifyEnvelope(envelope, domain, field.Span, nameof(HIncrByAsync));
                    current = ParseInt64Value(envelope.AsSpan(valueOffset));
                    exists = true;
                }
            }
            long updated = checked(current + delta);   // int64 溢出 fail-fast

            int recordSize = checked(HashEnvelope.HeaderSize + field.Length + 8);
            GuardDomainBytes(entry, recordSize);

            var valueBuf = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(valueBuf, updated);
            var writeAddr = await WriteHashRecordAsync(key, domain, field, valueBuf, ct).ConfigureAwait(false);
            _index.Insert(key, writeAddr, _index.BeginAddress);

            CommitWrite(entry, writeAddr, now, deltaCount: exists ? 0 : 1, deltaBytes: recordSize);
            return updated;
        }
        finally
        {
            _opGate.Release();
        }
    }

    /// <inheritdoc/>
    public ValueTask<long> HDelAsync(IReadOnlyList<ReadOnlyMemory<byte>> fields, CancellationToken ct)
        => HDelAsync(DefaultDomainId, fields, ct);

    /// <inheritdoc/>
    public async ValueTask<long> HDelAsync(uint domain, IReadOnlyList<ReadOnlyMemory<byte>> fields, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fields);
        EnsureReady();
        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_registry.TryGet(domain, out var entry)) return 0;
            long deleted = 0;
            long now = _clock.GetUtcNow().Ticks;
            foreach (var field in fields)
            {
                ct.ThrowIfCancellationRequested();
                var key = ToKey(domain, field.Span);
                var addr = _index.Find(key);
                if (addr == LogicalAddress.Empty) continue;

                var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
                if (envelope is null)
                {
                    // 崩溃窗口幽灵条目——删路自愈：墓碑 + 索引清理（字节口径不可考，恢复校正）
                    await _ring.WriteTombstoneAsync(key, ct).ConfigureAwait(false);
                    _index.Delete(key);
                    Interlocked.Decrement(ref entry.MemberCount);
                    Volatile.Write(ref entry.LastWriteTicks, now);
                    entry.Dirty = true;
                    deleted++;
                    continue;
                }

                // 碰撞校验（envelope field 字节比对——fail-fast）
                VerifyEnvelope(envelope, domain, field.Span, nameof(HDelAsync));

                // 墓碑先行、索引随后（崩溃窗口由恢复重放收口——墓碑必触发索引删除）
                await _ring.WriteTombstoneAsync(key, ct).ConfigureAwait(false);
                _index.Delete(key);

                deleted++;
                Interlocked.Decrement(ref entry.MemberCount);
                Interlocked.Add(ref entry.BytesOccupied, -envelope.Length);
                Volatile.Write(ref entry.LastWriteTicks, now);
                entry.Dirty = true;
            }
            return deleted;
        }
        finally
        {
            _opGate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 查询（索引驱动——点查 + 全域枚举）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ValueTask<ReadOnlyMemory<byte>?> HGetAsync(ReadOnlyMemory<byte> field, CancellationToken ct)
        => HGetAsync(DefaultDomainId, field, ct);

    /// <inheritdoc/>
    public async ValueTask<ReadOnlyMemory<byte>?> HGetAsync(uint domain, ReadOnlyMemory<byte> field, CancellationToken ct)
    {
        EnsureReady();
        var key = ToKey(domain, field.Span);
        var addr = _index.Find(key);
        if (addr == LogicalAddress.Empty) return null;
        var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
        if (envelope is null) return null;   // 崩溃窗口幽灵条目——读路自愈为 miss
        int valueOffset = VerifyEnvelope(envelope, domain, field.Span, nameof(HGetAsync));
        return envelope.AsMemory(valueOffset);
    }

    /// <inheritdoc/>
    public ValueTask<bool> HExistsAsync(ReadOnlyMemory<byte> field, CancellationToken ct)
        => HExistsAsync(DefaultDomainId, field, ct);

    /// <inheritdoc/>
    public async ValueTask<bool> HExistsAsync(uint domain, ReadOnlyMemory<byte> field, CancellationToken ct)
    {
        EnsureReady();
        var key = ToKey(domain, field.Span);
        var addr = _index.Find(key);
        if (addr == LogicalAddress.Empty) return false;
        var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
        if (envelope is null) return false;   // 幽灵条目——读路自愈为 miss
        VerifyEnvelope(envelope, domain, field.Span, nameof(HExistsAsync));
        return true;
    }

    /// <inheritdoc/>
    public ValueTask<long> HLenAsync(CancellationToken ct) => HLenAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public ValueTask<long> HLenAsync(uint domain, CancellationToken ct)
    {
        EnsureReady();
        return ValueTask.FromResult(_registry.TryGet(domain, out var entry)
            ? Interlocked.Read(ref entry.MemberCount)
            : 0L);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<(byte[] Field, byte[] Value)> HGetAllAsync(CancellationToken ct)
        => HGetAllAsync(DefaultDomainId, ct);

    /// <inheritdoc/>
    public async IAsyncEnumerable<(byte[] Field, byte[] Value)> HGetAllAsync(
        uint domain, [EnumeratorCancellation] CancellationToken ct)
    {
        EnsureReady();
        // Ring 流扫描 + 索引 live 判据（点查 == 本地址 = 存活版本；被覆盖/已删的旧 record 跳过）
        await foreach (var (key, addr, tomb) in _ring.ScanAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (tomb || key.DomainId != domain) continue;
            if (_index.Find(key) != addr) continue;
            var envelope = await ReadRecordEnvelopeAsync(addr, ct).ConfigureAwait(false);
            if (envelope is null) continue;   // 幽灵条目（索引未及清理的崩溃窗口残留）——跳过
            if (!HashEnvelope.TryUnwrap(envelope, out var envDomain, out var envField, out var value))
                continue;
            if (envDomain != domain) continue;
            yield return (envField, value);
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

    /// <summary>域整域回收（手动档 + TTL 过期共用——spec §5/§9）：索引域内清理 → 域账注销 →
    /// Ring 截断下限 = 剩余域钉住 min → 域账持久。</summary>
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

    /// <summary>整域回收核心（_opGate 内调）。</summary>
    private async ValueTask<long> TruncateDomainCoreAsync(uint domain, CancellationToken ct)
    {
        if (!_registry.TryGet(domain, out var entry)) return 0;

        // 前置 flush（截断守卫：Ring TruncatePrefix 须 ≤ FlushedUntilAddress）
        await _ring.FlushUntilAsync(_ring.TailAddress, ct).ConfigureAwait(false);

        long deleted = 0;
        await foreach (var (key, addr, tomb) in _ring.ScanAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (tomb || key.DomainId != domain) continue;
            if (_index.Find(key) != addr) continue;   // 非 live（被覆盖/已删）
            _index.Delete(key);
            deleted++;
        }

        // 域账注销（钉住随之解除）+ Ring 截断下限 = 剩余域钉住 min
        _registry.Remove(domain);
        LogicalAddress target = _registry.MinPinnedAddress(_ring.FlushedUntilAddress);
        if (target.IsValid && target <= _ring.FlushedUntilAddress && target > _ring.BeginAddress)
            _ring.TruncatePrefix(target);

        WriteAccountsLocked();
        _logger?.LogInformation("TierHash 域 {Domain} 整域回收：{Deleted} 成员，Ring 截断至 {Target}",
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

    /// <summary>field 字节 → 点查键（16B 强哈希双 64B 字拼装）。</summary>
    private static HashKey ToKey(uint domain, ReadOnlySpan<byte> field)
    {
        var (lo, hi) = MemberHash.Compute(field);
        return new HashKey(domain, lo, hi);
    }

    /// <summary>写前域条目就位（惰性注册 + 容量护栏 fail-fast——数据未写即拒绝）。</summary>
    private DomainEntry EnsureDomainSlot(uint domain, long nowTicks)
        => _registry.Register(domain, LogicalAddress.Invalid, nowTicks);

    /// <summary>域字节上限守卫（写侧 fail-fast——不静默丢；spec §5 DomainMaxBytes）。</summary>
    private void GuardDomainBytes(DomainEntry entry, int incomingRecordSize)
    {
        if (_options.DomainMaxBytes is not { } max) return;
        long projected = Interlocked.Read(ref entry.BytesOccupied) + incomingRecordSize;
        if (projected > max)
            throw new InvalidOperationException(
                $"域字节占用将超 DomainMaxBytes（{projected} > {max}）——fail-fast（写侧守卫，不静默丢）");
    }

    /// <summary>写后侧账提交（钉住 + 计数/字节/last-write 增量——写前护栏已过）。</summary>
    private void CommitWrite(DomainEntry entry, LogicalAddress addr, long nowTicks, long deltaCount, long deltaBytes)
    {
        entry.InitPinnedAddress(addr);
        if (deltaCount != 0) Interlocked.Add(ref entry.MemberCount, deltaCount);
        Interlocked.Add(ref entry.BytesOccupied, deltaBytes);
        Volatile.Write(ref entry.LastWriteTicks, nowTicks);
        entry.Dirty = true;
    }

    /// <summary>写 record（溢出分流：超大值租赁单块异步协调——引擎承担；常规值租赁拼接同步内联）。</summary>
    private async ValueTask<LogicalAddress> WriteHashRecordAsync(
        HashKey key, uint domain, ReadOnlyMemory<byte> field, ReadOnlyMemory<byte> value, CancellationToken ct)
    {
        int total = checked(HashEnvelope.HeaderSize + field.Length + value.Length);
        bool oversized = _options.OverflowPolicy == OverflowPolicy.Enabled
            && total > _options.MinOverflowSize;
        byte[] rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            HashEnvelope.WriteHeader(rented.AsSpan(), domain, field.Length);
            field.Span.CopyTo(rented.AsSpan(HashEnvelope.HeaderSize));
            value.Span.CopyTo(rented.AsSpan(HashEnvelope.HeaderSize + field.Length));
            return oversized
                ? await _ring.WriteAsync(key, rented.AsMemory(0, total), ct).ConfigureAwait(false)
                : _ring.Write(key, rented.AsSpan(0, total));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>读完整 record envelope 字节（冷热透明异步回源；null = record 无效——崩溃窗口幽灵条目）。</summary>
    private async ValueTask<byte[]?> ReadRecordEnvelopeAsync(LogicalAddress addr, CancellationToken ct)
    {
        var recordKey = await _ring.GetKeyAsync(addr, ct).ConfigureAwait(false);
        if (recordKey.ValueLength <= 0) return null;
        var buf = new byte[recordKey.ValueLength];
        int read = await _ring.GetValueAsync(addr, buf, ct).ConfigureAwait(false);
        if (read < HashEnvelope.HeaderSize) return null;
        if (!HashEnvelope.TryPeekHeader(buf.AsSpan(0, read), out _, out _)) return null;
        return buf;
    }

    /// <summary>envelope 校验（域标识 + field 字节比对——16B 哈希碰撞/损坏 fail-fast，定案④）。</summary>
    /// <returns>value 起始偏移（= HeaderSize + fieldLength）。</returns>
    private static int VerifyEnvelope(byte[] envelope, uint domain, ReadOnlySpan<byte> field, string operation)
    {
        if (!HashEnvelope.TryPeekHeader(envelope, out var envDomain, out var fieldLength)
            || envDomain != domain)
            throw new InvalidDataException(
                $"envelope 域标识不符（{envDomain} ≠ {domain}）或损坏——{operation} fail-fast");
        if (!field.SequenceEqual(envelope.AsSpan(HashEnvelope.HeaderSize, fieldLength)))
            throw new InvalidDataException(
                $"field 哈希命中但字节不匹配（16B 碰撞）——{operation} fail-fast（定案④）");
        return HashEnvelope.HeaderSize + fieldLength;
    }

    /// <summary>解析 8B 小端 int64 值档（HIncrBy 专用——非 8B 既有值 fail-fast）。</summary>
    private static long ParseInt64Value(ReadOnlySpan<byte> value)
    {
        if (value.Length != 8)
            throw new InvalidDataException(
                $"既有值非 8B 整数档（{value.Length}B）——HIncrBy 仅支持 8B 小端 int64 值（HSet 写入后不可 HIncrBy）");
        return BinaryPrimitives.ReadInt64LittleEndian(value);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 域账持久（spec §1 keyed 块——_opGate 内或恢复单线程调）
    // ═══════════════════════════════════════════════════════════════════

    private bool HasDirtyAccounts()
    {
        foreach (var (domainId, entry) in _registry.Enumerate())
            if (entry.Dirty) return true;
        return false;
    }

    /// <summary>域账持久（全体已注册域当前侧账快照——原子提交；空注册表 = 仅头部）。</summary>
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
    // TTL 后台循环（spec §5——整域过期 + 脏域账定期持久）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>TTL 单轮（worker 周期调）：逐域判过期（now - lastWrite > DomainTtl → 整域消失）+
    /// 脏域账持久（TTL 锚的持久化时机——崩溃窗口 ≤ 扫描间隔，恢复按数据事实收口）。</summary>
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

        // 脏域账持久（独立短闸——禁门内走带门属性：TruncateDomainAsync 自取 _opGate，不在此门内叠调）
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

    /// <summary>TTL 后台循环（低频——默认 1 分钟扫一次域账，spec §5）。</summary>
    private sealed class RetentionWorker(TierHash owner, TimeSpan interval)
        : BackgroundWorkerLoop(null, 1, "TierHashRetentionWorker")
    {
        /// <summary>单周期：到点跑一轮 TTL 过期 + 脏域账持久。</summary>
        /// <param name="ct">取消令牌（Delay 响应取消——取消即终止循环）。</param>
        /// <returns>true = 继续下一周期。</returns>
        protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
        {
            await owner._clock.Delay(interval, ct).ConfigureAwait(false);
            await owner.RunRetentionAsync(ct).ConfigureAwait(false);
            return true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复核心（RecoveryBase 模板派生——join Ring/域账 → 域账载入 → 索引启动 → 对账）
    // ═══════════════════════════════════════════════════════════════════

    private sealed class TierHashRecovery(TierHash owner) : RecoveryBase<CollectionRecoveryHints>
    {
        /// <summary>层间 join——Ring + 域账 meta（OnInitializeBegin 已并行启动）。</summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>任务在 Ring 与域账 meta 均 Ready 后完成。</returns>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._ring.WaitForReadyAsync(ct).ConfigureAwait(false);
            await owner._watermark.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心（spec §7 三步）：域账 keyed 块载入（注册表种子化）→ 点查索引启动（Ring Ready 后
        /// ——resolver 数据面可用；重放含墓碑感知，流序折叠 = 最新写胜出）→ 对账（Ring 重放按
        /// envelope 路由，live 判据 = 索引点查 == 本地址：悬空/被覆盖/已删 record 不计；域账计数/
        /// 字节以重放事实权威化；last-write 缺块以恢复时刻收口防误过期）→ 域账校正持久。
        /// </summary>
        /// <param name="hints">恢复 hints（无注入项——预留）。</param>
        /// <param name="ct">取消令牌。</param>
        protected override async ValueTask OnRecoveryCoreAsync(CollectionRecoveryHints hints, CancellationToken ct)
        {
            // ── 1. 域账载入（首次启动 payload 缺失 → Initial；keyed 块种子化注册表）──
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

            // ── 2. 索引启动（Queue 幂等索引同款时序：Ring Ready 之后，重放窗口 [Begin, Tail)；
            //        悬空清理（地址出窗口）+ 缺条重建已由重放折叠收口——墓碑触发删除/同键最新胜出）──
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
                entry.InitPinnedAddress(addr);   // 重放地址序——首见 = 最早存活 record
                var recordKey = owner._ring.GetKey(addr);
                var (count, bytes) = liveFacts.TryGetValue(key.DomainId, out var cur)
                    ? cur
                    : (0L, 0L);
                liveFacts[key.DomainId] = (count + 1, bytes + recordKey.ValueLength);
            }

            // 计数/字节 = 重放事实（权威——持久块是上次持久时刻快照，可能落后）；
            // 零存活域注销（整域已回收/过期的崩溃窗口残留——空域不持久）；last-write 缺块收口。
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

            // ── 4. 域账校正持久（种子化/事实校正/注销一次性落盘）──
            owner.WriteAccountsLocked();
            RaiseProgress(90, $"domains={owner._registry.Count} indexEntries={owner._index.EntryCount}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 冷备份导入（TierHashBackup 编排——同域连续批次；_opGate 内整批）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 导入批次（同域记录——TierHashBackup.ImportAsync 逐批调）：写路径与 HSet 同构
    /// （envelope/索引/侧账/容量护栏/溢出分流），判重/覆盖语义原样生效（幂等重放）；
    /// last-write = 导入时刻（备份流无写入时刻事实——TimeSeries 样本自带 ts 不同源，
    /// 域账回拉语义在此 = 计数/字节按写路径维护）。
    /// <para>★ 串行面：_opGate 内执行——与写/TTL 过期/其他导入批次互斥。</para>
    /// </summary>
    /// <param name="domain">域标识。</param>
    /// <param name="records">记录批次（流序保持）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入数。</returns>
    internal async ValueTask<long> ImportBatchAsync(
        uint domain, IReadOnlyList<(ReadOnlyMemory<byte> Field, ReadOnlyMemory<byte> Value)> records, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0) return 0;
        EnsureReady();

        await _opGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long written = 0;
            foreach (var (field, value) in records)
            {
                ct.ThrowIfCancellationRequested();
                await HSetCoreAsync(domain, field, value, ct).ConfigureAwait(false);
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
