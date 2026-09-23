using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Transactions;
using TC.Tier.Core.Lifecycle;
using TC.Tier.Core.Resources;
using TC.Tier.Runtime.Structures.Snapshot;
using TC.Tier.Runtime.Structures.Snapshot.Contracts;

namespace TC.Tier.Products.Blob;

/// <summary>
/// TierBlob——本地 Blob 空间（tierblob-spec：SnapshotBase 的产品封面，零新结构）。
/// <para>★ 组合配方（spec §1 六大裁定）：StreamSnapshot 单形态数据引擎（<c>{name}.blob.data</c>——对象 =
///   一段连续地址区间的 CRC64 帧流，句柄 = 起始 LogicalAddress）+ 对象表（<c>{name}.blob.meta</c>——
///   版本链记录帧流，meta 真相源）。</para>
/// <para>★ 帧几何不变式：每帧扇区对齐（数据区补零）——物理布局 = 逻辑布局，重启后恒等映射读回成立；
///   定长读免帧解析（数据区物理连续直达）。</para>
/// <para>★ 线程模型：读并发无锁（表镜像点查 + 引擎读）；写单通道（产品写闸串行——结构层写尾单会话
///   契约，多生产者并行 = Runtime 迭代候选，spec §4 诚实降级）。</para>
/// <para>★ 恢复（spec §5）：数据引擎 join（Backward 找帧尾 O(1)——无 meta，帧自描述）+ 表重放
///   （meta O(1) 水位 + 定步长）→ 对账（Active 逐个验帧 → 损坏标墓碑；孤儿帧不可见空间保留）。</para>
/// </summary>
public sealed class TierBlob : LifecycleBase<BlobRecoveryHints>, ITierBlob
{
    private readonly StreamSnapshot _data;          // 主数据引擎（对象帧流）
    private readonly BlobObjectTable _table;        // 对象表（meta 真相源）
    private readonly TierBlobOptions _options;
    private readonly ILogger? _logger;

    /// <summary>产品写闸——写通道串行（结构层单会话契约；会话存活期独占，Put 单发全程持闸）。</summary>
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>数据引擎 fsync 屏障的内部序号（FlushAsync/ReclaimDeleted 共用 seq 域）。</summary>
    private long _dataFlushSeq;

    /// <summary>对象表参与者（单例——会话域挂在表上，重复取同一参与者）。</summary>
    private BlobTableParticipant? _participant;

    /// <summary>构造 internal——外部只能经 <see cref="TierBlobBuilder.StartAsync"/>。</summary>
    internal TierBlob(StreamSnapshot data, BlobObjectTable table, TierBlobOptions options, ILogger? logger)
        : base(recovery: null, logger)
    {
        _data = data;
        _table = table;
        _options = options;
        _logger = logger;
        Resources.Add(_data, ownership: ResourceOwnership.Owned);
        Resources.Add(_table.TableSnapshot, ownership: ResourceOwnership.Owned);
    }

    internal TierBlobOptions Options => _options;

    /// <summary>内部诊断（测试/白盒）——数据引擎。</summary>
    internal StreamSnapshot DiagnosticDataSnapshot => _data;

    /// <summary>内部诊断（测试/白盒）——对象表。</summary>
    internal BlobObjectTable DiagnosticTable => _table;

    /// <summary>内部诊断（测试/白盒）——恢复重放记录数。</summary>
    internal long DiagnosticReplayedRecords { get; private set; }

    /// <summary>内部诊断（测试/白盒）——恢复对账标损（损坏标墓碑）对象数。</summary>
    internal long DiagnosticMarkedMissing { get; private set; }

    /// <inheritdoc/>
    public long Bytes => _data.Distance(_data.TruncatedAddress, _data.PhysicalWriteAddress);

    // ═══════════════════════════════════════════════════════════════════
    // 生命周期 / 恢复编排
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>★ Initialize 第一阶段钩子：并行启动数据引擎 + 表引擎（join 在恢复核心——
    /// 数据尾由表推导后经 TruncateSuffix 纠偏）。</summary>
    protected override void OnInitializeBegin()
    {
        _data.Initialize();
        _table.TableSnapshot.Initialize();
    }

    /// <summary>★ 默认 Recovery 单一创建点——恢复核心 = 引擎 join + 表重放 + 对账（spec §5）。</summary>
    /// <returns>恢复算法实例（由基类持有并在 Initialize 中执行）。</returns>
    protected override IRecovery<BlobRecoveryHints> CreateRecovery() => new BlobRecovery(this);

    /// <inheritdoc/>
    protected override void DisposeOverride(bool disposing)
    {
        base.DisposeOverride(disposing);
        _writeGate.Dispose();
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        await base.DisposeOverrideAsync(disposing).ConfigureAwait(false);
        _writeGate.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 写入（写闸串行——门的所有权：Open 接管，会话 Complete/Abort 归还）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<BlobPutResult> PutAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        EnsureReady();
        ThrowIfDisposed();
        CheckCapacity(data.Length);
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        BlobWriteSession session;
        try
        {
            session = OpenWriteCore(data.Length);
        }
        catch
        {
            _writeGate.Release();
            throw;
        }
        try
        {
            await session.WriteAsync(data, ct).ConfigureAwait(false);
            return await session.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);   // Abort 回滚 + 归还门
            throw;
        }
    }

    /// <inheritdoc/>
    public BlobWriteSession OpenWrite(long expectedLength = -1)
    {
        EnsureReady();
        ThrowIfDisposed();
        CheckCapacity(expectedLength);
        _writeGate.Wait();   // 会话存活期独占写通道（无半开会话形态——恒立即可入或排队）
        BlobWriteSession session;
        try
        {
            session = OpenWriteCore(expectedLength);
        }
        catch
        {
            _writeGate.Release();
            throw;
        }
        return session;
    }

    private BlobWriteSession OpenWriteCore(long expectedLength)
    {
        var start = _data.WriteAddress;   // 句柄 = 帧流起始地址（Open 即定——同址即同对象）
        var writer = _data.OpenWrite();
        return new BlobWriteSession(this, writer, start, expectedLength, gateHeld: true, _data.SectorSize);
    }

    /// <summary>登记（Complete 收口点——会话转调；写闸内）。</summary>
    internal async ValueTask RegisterObjectAsync(LogicalAddress objectId, long length, long frameLength, CancellationToken ct)
    {
        var entry = new BlobObjectTable.Entry(objectId, length, frameLength, DateTime.UtcNow.Ticks, BlobState.Active);
        if (_table.SessionOpen)
        {
            // participant 会话域内——延迟提交挂 undo（GetParticipant "业务写+对象登记"原子，裁定⑤）
            _table.RegisterDeferred(entry);
            return;
        }
        await _table.RegisterAsync(entry, ct).ConfigureAwait(false);
        _logger?.LogDebug("Blob registered: {ObjectId} length={Length}", objectId, length);
    }

    /// <summary>Abort 尾截断回滚（会话转调——写闸内）。</summary>
    internal void RollbackTail(LogicalAddress sessionStart) => _data.TruncateSuffix(sessionStart);

    /// <summary>容量守卫（Put/OpenWrite 开门 fail-fast——spec §6）。</summary>
    internal void CheckCapacity(long incomingBytes)
    {
        if (_options.MaxBytes is { } max && Bytes + FrameBudget(incomingBytes) > max)
            throw new InvalidOperationException(
                $"Blob 空间容量超限：已用 {Bytes} + 待写 {incomingBytes} > MaxBytes {max}（fail-fast）");
    }

    /// <summary>写入后容量终判（不定长流完成时点——false = 超限须回滚）。</summary>
    internal bool CheckCapacityAfterWrite()
        => _options.MaxBytes is not { } max || Bytes <= max;

    private long FrameBudget(long userLength)
        => userLength < 0 ? 0 : BlobObjectTable.FrameLengthOf(userLength, _data.SectorSize);

    internal void ReleaseWriteGate() => _writeGate.Release();

    // ═══════════════════════════════════════════════════════════════════
    // 读取（地址直达——表镜像点查 + 引擎读，与写路径零共享）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public async ValueTask<int> GetAsync(LogicalAddress objectId, Memory<byte> dst, CancellationToken ct = default)
    {
        EnsureReady();
        if (!_table.TryGet(objectId, out var entry))
            throw new KeyNotFoundException($"对象 {objectId} 不存在（句柄查表未命中）");
        if (entry.State == BlobState.Tombstone)
            throw new KeyNotFoundException($"对象 {objectId} 已删除（墓碑）");
        if (dst.Length < entry.Length)
            throw new ArgumentException($"dst 长度 {dst.Length} < 对象长度 {entry.Length}（定长读契约）", nameof(dst));

        // 免帧解析快速路径：数据区物理连续（帧扇区对齐不变式），句柄 +14 直达
        var dataStart = _data.AdvanceAddress(objectId, BlobObjectTable.FrameHeaderSize);
        var read = await _data.ReadAsync(dataStart, dst[..(int)entry.Length], ct).ConfigureAwait(false);
        if (read < entry.Length)
            throw new System.IO.IOException($"对象 {objectId} 短读 {read}/{entry.Length}——数据区截断");
        return (int)entry.Length;
    }

    /// <inheritdoc/>
    public BlobReadSession OpenRead(LogicalAddress objectId)
    {
        EnsureReady();
        if (!_table.TryGet(objectId, out var entry))
            throw new KeyNotFoundException($"对象 {objectId} 不存在（句柄查表未命中）");
        if (entry.State == BlobState.Tombstone)
            throw new KeyNotFoundException($"对象 {objectId} 已删除（墓碑）");

        var frameLength = entry.FrameLength;
        var reader = _data.OpenReadRange(objectId, _data.AdvanceAddress(objectId, frameLength));
        var pad = (int)(frameLength - BlobObjectTable.FrameHeaderSize
                        - BlobObjectTable.FrameFooterSize - entry.Length);
        return new BlobReadSession(reader, objectId, entry.Length, pad);
    }

    /// <inheritdoc/>
    public ValueTask<BlobInfo> GetInfoAsync(LogicalAddress objectId, CancellationToken ct = default)
    {
        EnsureReady();
        if (!_table.TryGet(objectId, out var entry))
            throw new KeyNotFoundException($"对象 {objectId} 不存在（句柄查表未命中）");
        return ValueTask.FromResult(
            new BlobInfo(entry.ObjectId, entry.Length, entry.CreatedTicks, entry.State));
    }

#pragma warning disable CS1998 // 表快照已在锁内收集——纯内存同步交付，无 await 点（异步迭代器形制要求）
    /// <inheritdoc/>
    public async IAsyncEnumerable<BlobInfo> ListAsync(
        BlobListFilter filter = default,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        foreach (var entry in _table.Snapshot(filter))
        {
            ct.ThrowIfCancellationRequested();
            yield return new BlobInfo(entry.ObjectId, entry.Length, entry.CreatedTicks, entry.State);
        }
    }
#pragma warning restore CS1998

    /// <inheritdoc/>
    public async ValueTask DeleteAsync(LogicalAddress objectId, CancellationToken ct = default)
    {
        EnsureReady();
        ThrowIfDisposed();
        if (!_table.TryGet(objectId, out var entry))
            throw new KeyNotFoundException($"对象 {objectId} 不存在（句柄查表未命中）");
        if (entry.State == BlobState.Tombstone) return;   // 幂等

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_table.SessionOpen)
                _table.RegisterDeferredTombstone(entry);
            else
                await _table.RegisterTombstoneAsync(entry, ct).ConfigureAwait(false);
            _logger?.LogDebug("Blob deleted (tombstone): {ObjectId}", objectId);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 治理（B1——spec §6）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>观测快照（ObjectCount/TombstoneCount/Bytes——spec §6 统计面）。</summary>
    public ValueTask<BlobStats> GetStatsAsync(CancellationToken ct = default)
    {
        EnsureReady();
        var (active, tombs, tombBytes) = _table.CountStates();
        return ValueTask.FromResult(new BlobStats(
            active, tombs, Bytes, tombBytes,
            _data.TruncatedAddress, _data.WriteAddress));
    }

    /// <summary>
    /// 物理回收手动档（裁定⑥）：回收墓碑对象空间——引擎内逐墓碑 extent 打洞 + 连续死亡前缀头截断
    /// （TruncatePrefix，Bytes 实际下降）。Active 对象区间零触碰。
    /// 删除后不调本方法 = 空间保留（诚实定案）；重启后回收效果由表状态重推导（自愈）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>估算回收字节数（打洞 extent + 死亡前缀帧长合计）。</returns>
    public async ValueTask<long> ReclaimDeletedAsync(CancellationToken ct = default)
    {
        EnsureReady();
        ThrowIfDisposed();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushDataEngineBarrierAsync().ConfigureAwait(false);

            var truncated = _data.TruncatedAddress;
            long reclaimed = 0;
            LogicalAddress? livePrefixBound = null;   // 首个存活对象帧起点（其前 = 死亡前缀）
            foreach (var entry in _table.SnapshotAll())
            {
                if (entry.State == BlobState.Tombstone)
                {
                    // 前缀已回收段（帧尾 ≤ 截断头）不计——物理空间上轮已释放
                    var frameEnd = _data.AdvanceAddress(entry.ObjectId, entry.FrameLength);
                    if (frameEnd.CompareTo(truncated) > 0)
                    {
                        _data.ReclaimRange(entry.ObjectId, frameEnd);
                        reclaimed += entry.FrameLength;
                    }
                }
                else if (livePrefixBound is null || entry.ObjectId.CompareTo(livePrefixBound.Value) < 0)
                {
                    livePrefixBound = entry.ObjectId;
                }
            }

            // 死亡前缀头截断（Bytes 下降的来源——TruncatedAddress 推进到首个存活对象帧起点）
            long prefixReclaimed = 0;
            if (livePrefixBound is { } bound && bound.CompareTo(truncated) > 0)
            {
                prefixReclaimed = _data.Distance(truncated, bound);
                _data.TruncatePrefix(bound);
            }

            _logger?.LogInformation(
                "Blob reclaim: holes={HoleBytes}B prefix={PrefixBytes}B bytes={Bytes}",
                reclaimed, prefixReclaimed, Bytes);
            return reclaimed + prefixReclaimed;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc/>
    public async ValueTask FlushAsync(CancellationToken ct)
    {
        EnsureReady();
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 数据引擎 fsync 屏障（表登记在 Complete 时已经表引擎 Prepare/Confirm 持久收口）
            await FlushDataEngineBarrierAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>数据引擎 fsync 屏障（Prepare = 引擎 Flush；meta Disabled——水位持久 no-op，恢复走 Backward 找帧尾）。</summary>
    private ValueTask FlushDataEngineBarrierAsync()
    {
        var seq = ++_dataFlushSeq;
        _data.Prepare(seq);
        _data.ConfirmCommitted(seq);
        return ValueTask.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Session 接线（spec §7——裁定⑤）
    // ═══════════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public ITransactionParticipant GetParticipant()
        => _participant ??= new BlobTableParticipant(_table);

    /// <summary>
    /// 对象表事务参与者——表引擎 2PC + 产品侧 undo 链（"业务写 + 对象登记"原子，裁定⑤）。
    /// <para>Prepare → 会话域开启（此后对象登记延迟提交挂 undo）；Confirm → 统一 fsync + 提交水位；
    /// Abort → 表帧尾截断（ReclaimTail）+ 内存镜像逆序回退。</para>
    /// </summary>
    private sealed class BlobTableParticipant(BlobObjectTable table) : ITransactionParticipant
    {
        private readonly StreamSnapshot _tableSnapshot = table.TableSnapshot;

        public long LastCommittedSeq => ((ITransactionParticipant)_tableSnapshot).LastCommittedSeq;
        public long LastPreparedSeq => ((ITransactionParticipant)_tableSnapshot).LastPreparedSeq;

        public void Prepare(long seq) => table.PrepareSession(seq);

        public ValueTask PrepareAsync(long seq, CancellationToken ct)
        {
            Prepare(seq);
            return ValueTask.CompletedTask;
        }

        public void ConfirmCommitted(long seq) => table.ConfirmSession(seq);

        public void Abort(long seq) => table.AbortSession(seq);

        public ValueTask AbortAsync(long seq, CancellationToken ct)
        {
            Abort(seq);
            return ValueTask.CompletedTask;
        }

        public void OnCommitted(long seq, Action callback) => ((ITransactionParticipant)_tableSnapshot).OnCommitted(seq, callback);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 恢复核心（RecoveryBase 模板派生——spec §5 四步）
    // ═══════════════════════════════════════════════════════════════════

    private sealed class BlobRecovery(TierBlob owner) : RecoveryBase<BlobRecoveryHints>
    {
        /// <summary>层间 join——数据引擎 + 表引擎（OnInitializeBegin 已并行启动）。</summary>
        protected override async ValueTask WaitForDependenciesAsync(CancellationToken ct)
        {
            await owner._data.WaitForReadyAsync(ct).ConfigureAwait(false);
            await owner._table.TableSnapshot.WaitForReadyAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// 恢复核心（spec §5）：表重放（meta O(1) 水位）→ 数据尾由表推导 + TruncateSuffix 纠偏 →
        /// 对账验帧（损坏标墓碑显式可见）→ 放行。
        /// <para>★ 数据尾不走 Backward 扫描结果（spec §5 定案修正）：对象表 = meta 真相源（spec §2）——
        ///   扫描命中的"帧尾"可能落在未写窗口的伪 footer 上（稀疏/池化介质未写区非零），登记帧几何
        ///   才是权威：尾 = max(帧尾)，TruncateSuffix 一致化（写窗口归零 + 扫尾垃圾区间物理回收）。
        ///   孤儿帧（有数据无登记）落在推导尾之上，被后续写入自然覆盖（at-least-once：崩溃 Put 由
        ///   调用方重试产生新对象，空间即收复）。</para>
        /// </summary>
        protected override async ValueTask OnRecoveryCoreAsync(BlobRecoveryHints hints, CancellationToken ct)
        {
            // ── 2. 对象表重放（版本链记录帧——后记录覆盖同句柄，墓碑即删除）──
            var replayed = await owner._table.ReplayAsync(ct).ConfigureAwait(false);
            owner.DiagnosticReplayedRecords = replayed;
            RaiseProgress(50, $"table replayed records={replayed}");

            // ── 3. 数据尾推导（表真相源）+ TruncateSuffix 纠偏（仅在扫尾偏离时——水位/写窗口一致化）──
            var entries = owner._table.SnapshotAll();
            var floor = owner._data.TruncatedAddress;   // 恢复期 = 引擎 MinAddress（前缀已回收段基底）
            var tail = floor;
            foreach (var entry in entries)
            {
                var frameEnd = owner._data.AdvanceAddress(entry.ObjectId, entry.FrameLength);
                if (entry.ObjectId.CompareTo(floor) >= 0 && frameEnd.CompareTo(tail) > 0)
                    tail = frameEnd;
            }
            if (owner._data.WriteAddress.CompareTo(tail) > 0)
            {
                // 扫尾命中未写窗口伪 footer(高于登记尾)——水位 ref 拉回登记尾。
                // ★ 不走 TruncateSuffix:新尾可等于引擎 AllocatedTail(ReclaimTail 拒绝 ≥);
                //   写窗口按旧(更高)物理尾计——对新尾只大不小,EnsureAllocated 两分支皆安全。
                owner._data.WriteAddress = tail;
                owner._data.PhysicalWriteAddress = tail;   // 帧扇区对齐不变式——登记尾恒扇区整倍数
            }

            // ── 4. 对账：Active 对象逐个验帧（损坏标墓碑——诚实可见）──
            long marked = 0;
            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.State != BlobState.Active) continue;
                if (entry.ObjectId.CompareTo(floor) < 0) continue;   // 前缀已回收段——上轮已释放

                var (isValid, reason) = await VerifyFrameAsync(owner, entry, ct).ConfigureAwait(false);
                if (!isValid)
                {
                    await owner._table.RegisterTombstoneAsync(entry, ct).ConfigureAwait(false);
                    marked++;
                    owner._logger?.LogWarning("恢复对账：对象 {ObjectId} 帧校验失败（{Reason}）——已标墓碑",
                        entry.ObjectId, reason);
                }
            }

            owner.DiagnosticMarkedMissing = marked;
            RaiseProgress(90, $"reconciled entries={entries.Count} marked={marked} tail={tail}");
        }

        /// <summary>验帧：尾级 O(1)（footer magic + TotalLength 对账）或深检（整帧 CRC64）。</summary>
        private static async ValueTask<(bool isValid, string reason)> VerifyFrameAsync(
            TierBlob owner, BlobObjectTable.Entry entry, CancellationToken ct)
        {
            // 帧越写尾 = 尾部丢失（登记后崩溃回退形态）
            var frameEnd = owner._data.AdvanceAddress(entry.ObjectId, entry.FrameLength);
            if (frameEnd.CompareTo(owner._data.WriteAddress) > 0)
                return (false, "帧区间越写尾（尾部丢失）");

            var expectedDataLength = entry.FrameLength - BlobObjectTable.FrameHeaderSize
                                     - BlobObjectTable.FrameFooterSize;
            if (!owner._options.DeepVerifyOnRecovery)
            {
                // 尾级：读帧尾 28B——magic + TotalLength 对账（捕获截断/撕裂/帧区损坏）
                var footerAddr = owner._data.AdvanceAddress(
                    entry.ObjectId, entry.FrameLength - BlobObjectTable.FrameFooterSize);
                var footer = new byte[BlobObjectTable.FrameFooterSize];
                var got = await owner._data.ReadAsync(footerAddr, footer, ct).ConfigureAwait(false);
                if (got < BlobObjectTable.FrameFooterSize)
                    return (false, $"帧尾短读 {got}/{BlobObjectTable.FrameFooterSize}");
                var magic = StreamFrameFooterCodec.Read_Magic(footer);
                if (magic != RecordMagic.SnapshotFrameFooter)
                    return (false, $"帧尾 magic 不符（0x{magic:X}）");
                var totalLength = StreamFrameFooterCodec.Read_TotalLength(footer);
                if ((long)totalLength != expectedDataLength)
                    return (false, $"帧尾 TotalLength {totalLength} ≠ 登记对账值 {expectedDataLength}");
                return (true, string.Empty);
            }

            // 深检：整帧读回 + CRC64 全量校验（运维档——TB 级耗时线性）
            await using var reader = owner._data.OpenReadRange(entry.ObjectId, frameEnd);
            var buf = new byte[64 * 1024];
            long total = 0;
            int n;
            while ((n = await reader.ReadDataAsync(buf, ct).ConfigureAwait(false)) > 0)
                total += n;
            if (total != expectedDataLength)
                return (false, $"深检帧长 {total} ≠ 登记对账值 {expectedDataLength}");
            if (!reader.IsFooterValid)
                return (false, "深检 CRC64 校验失败");
            return (true, string.Empty);
        }
    }
}
