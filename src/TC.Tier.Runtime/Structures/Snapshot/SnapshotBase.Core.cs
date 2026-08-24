using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>核心机制 partial：append/Overwrite/读 + 截断 + 2PC + 会话工厂。</summary>
public abstract partial class SnapshotBase
{
    // ════════════════════════════════════════════════════════════
    // === 写原语（LogicalAddress 寻址；引擎管跨段/DIO 对齐/并发）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 写前确保写窗口够用（引擎 Write 要求目标在已分配区内）。
    /// 窗口模型：剩余计数扣减；不足时 Allocate 补一波（sessionBufferSize 量级，减少 Allocate 次数）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsureAllocated(int length)
    {
        if (length <= _writeWindow) { _writeWindow -= length; return; }
        int sectorSize = _sectorSize > 0 ? _sectorSize : 1;
        long refill = (length + _sessionBufferSize).AlignUp(sectorSize);
        _engine.Allocate(refill);
        _writeWindow += refill - length;
    }

    /// <summary>异步写（会话/帧写入器内部用）。</summary>
    private protected async ValueTask WriteAtAsync(LogicalAddress addr, ReadOnlyMemory<byte> source, CancellationToken ct = default)
    {
        EnsureReady();
        EnsureAllocated(source.Length);
        await _engine.WriteAsync(addr, source, ct).ConfigureAwait(false);
    }

    /// <summary>异步读（会话内部用）。</summary>
    private protected ValueTask<int> ReadAtAsync(LogicalAddress addr, Memory<byte> dest, CancellationToken ct = default)
    {
        EnsureReady();
        return _engine.ReadAsync(addr, dest, ct);
    }

    /// <summary>同步写（冷路径/微写入）。</summary>
    private protected void WriteAt(LogicalAddress addr, ReadOnlySpan<byte> source)
    {
        EnsureReady();
        EnsureAllocated(source.Length);
        _engine.Write(addr, source);
    }

    /// <summary>同步读（冷路径）。</summary>
    private protected int ReadAt(LogicalAddress addr, Span<byte> dest)
    {
        EnsureReady();
        return _engine.Read(addr, dest);
    }

    // ════════════════════════════════════════════════════════════
    // === 核心读写 API（non-virtual 热路径）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// append（可回滚）——从物理写尾追加，双水位推进（逻辑 += len；物理 += 对齐 len）。
    /// </summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (data.IsEmpty) return;
        int aligned = data.Length.AlignUp(_sectorSize);
        EnsureAllocated(aligned); // 对齐量整体入窗口账（padding 也占空间）
        _engine.Write(_physicalWriteAddress, data);
        _writeAddress = _engine.CalculationAddress(_writeAddress, data.Length);
        _physicalWriteAddress = _engine.CalculationAddress(_physicalWriteAddress, aligned);
    }

    /// <summary>append 异步轨。</summary>
    public async ValueTask AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        EnsureNotDisposed();
        EnsureReady();
        if (data.IsEmpty) return;
        int aligned = data.Length.AlignUp(_sectorSize);
        EnsureAllocated(aligned);
        await _engine.WriteAsync(_physicalWriteAddress, data, ct).ConfigureAwait(false);
        _writeAddress = _engine.CalculationAddress(_writeAddress, data.Length);
        _physicalWriteAddress = _engine.CalculationAddress(_physicalWriteAddress, aligned);
    }

    /// <summary>
    /// Overwrite（<b>不可回滚</b>——覆写已破坏旧数据，独立方法名显形，2PC 原子性不覆盖本操作）。
    /// </summary>
    public void Overwrite(LogicalAddress addr, ReadOnlySpan<byte> data)
    {
        EnsureNotDisposed();
        EnsureReady();
        _engine.Write(addr, data);
    }

    /// <summary>Overwrite 异步轨（同样不可回滚）。</summary>
    public async ValueTask OverwriteAsync(LogicalAddress addr, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        EnsureNotDisposed();
        EnsureReady();
        await _engine.WriteAsync(addr, data, ct).ConfigureAwait(false);
    }

    /// <summary>读（从 addr 起读到 dst，返回实际读取数）。</summary>
    public int Read(LogicalAddress addr, Span<byte> dst)
    {
        EnsureNotDisposed();
        EnsureReady();
        return _engine.Read(addr, dst);
    }

    /// <summary>读异步轨。</summary>
    public ValueTask<int> ReadAsync(LogicalAddress addr, Memory<byte> dst, CancellationToken ct)
    {
        EnsureNotDisposed();
        EnsureReady();
        return _engine.ReadAsync(addr, dst, ct);
    }

    // ════════════════════════════════════════════════════════════
    // === 截断（物理回收；回收是线性的——只有已读头部与已回滚尾部可回收）===
    // ════════════════════════════════════════════════════════════

    /// <summary>头截断（业务调，回收已读头部）：引擎 ReclaimHead + 推进截断水位。</summary>
    public void TruncatePrefix(LogicalAddress address)
    {
        EnsureNotDisposed();
        _engine.ReclaimHead(address);
        _truncatedAddress = address;
    }

    /// <summary>尾截断（2PC Abort 内部调，回滚 append 部分；Overwrite 不可回滚）。</summary>
    public void TruncateSuffix(LogicalAddress address)
    {
        EnsureNotDisposed();
        _engine.ReclaimTail(address);
        _writeAddress = address;
        _physicalWriteAddress = address;
        _writeWindow = 0; // 窗口已作废——下次写重新 Allocate
    }

    /// <summary>区间回收（任意区间 PunchHole）。</summary>
    public void ReclaimRange(LogicalAddress? from, LogicalAddress? to)
    {
        EnsureNotDisposed();
        _engine.Reclaim(from, to);
    }

    // ════════════════════════════════════════════════════════════
    // === 2PC（ITransactionParticipant；只对 append 提供原子性）===
    // ════════════════════════════════════════════════════════════

    long ITransactionParticipant.LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);
    long ITransactionParticipant.LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    /// <summary>Prepare：flush 追加数据 + meta.Commit（记录 LastPreparedSeq + 当前水位）。</summary>
    public void Prepare(long seq)
    {
        EnsureNotDisposed();
        EnsureReady();
        _engine.Flush();
        Volatile.Write(ref _lastPreparedSeq, seq);
        WriteMeta();
    }

    /// <summary>Prepare 异步轨（flush 原生仅同步，实质等价）。</summary>
    public async ValueTask PrepareAsync(long seq, CancellationToken ct)
    {
        Prepare(seq);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>ConfirmCommitted：CAS 推进 LastCommittedSeq + 推进 CommittedWriteAddress（Abort 回退点）+ meta + 回调。</summary>
    public void ConfirmCommitted(long seq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return;
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);

        _committedWriteAddress = _writeAddress;
        if (_settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            WriteMeta();
        FireTransactionCallbacks(seq);
    }

    /// <summary>Abort：TruncateSuffix(CommittedWriteAddress) 回滚 append 部分（Overwrite 不可回滚）+ meta。</summary>
    public void Abort(long seq)
    {
        EnsureNotDisposed();
        if (seq <= Volatile.Read(ref _lastAbortedSeq)) return; // 幂等

        if (_writeAddress.CompareTo(_committedWriteAddress) > 0)
        {
            TruncateSuffix(_committedWriteAddress);
        }
        Volatile.Write(ref _lastPreparedSeq, _lastCommittedSeq);
        Volatile.Write(ref _lastAbortedSeq, seq);
        if (_settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            WriteMeta();
    }

    /// <summary>Abort 异步轨（引擎截断原生同步，实质等价）。</summary>
    public async ValueTask AbortAsync(long seq, CancellationToken ct)
    {
        Abort(seq);
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>注册提交回调（链式触发）。</summary>
    public void OnCommitted(long seq, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_txCallbackLock)
        {
            if (seq <= Volatile.Read(ref _lastCommittedSeq))
            {
                callback();
                return;
            }

            if (!_txCallbacks.TryGetValue(seq, out var list))
            {
                list = new();
                _txCallbacks[seq] = list;
            }

            list.Add(callback);
        }
    }

    private void FireTransactionCallbacks(long committedSeq)
    {
        List<Action>? toFire = null;
        lock (_txCallbackLock)
        {
            foreach (var kvp in _txCallbacks)
            {
                if (kvp.Key <= committedSeq)
                {
                    (toFire ??= new()).AddRange(kvp.Value);
                }
            }

            while (_txCallbacks.Count > 0 && _txCallbacks.Keys[0] <= committedSeq)
                _txCallbacks.RemoveAt(0);
        }

        if (toFire is not null)
            foreach (var cb in toFire)
                cb(); // 锁外触发避免死锁
    }

    // ════════════════════════════════════════════════════════════
    // === 会话工厂（子类/调用方开双缓冲流式会话）===
    // ════════════════════════════════════════════════════════════

    /// <summary>打开写会话（双 buffer flush 流水线）。</summary>
    private protected ISnapshotWriteSession OpenWriteSession(LogicalAddress startAddress)
        => new WriteSession(this, startAddress);

    /// <summary>打开读会话（双 buffer 异步预读 + 物理/逻辑偏移分离 + padding 剔除）。</summary>
    private protected ISnapshotReadSession OpenReadSession(LogicalAddress logicalStart, LogicalAddress logicalEnd,
        LogicalAddress physicalStart, LogicalAddress physicalEnd)
        => new ReadSession(this, logicalStart, logicalEnd, physicalStart, physicalEnd);

    /// <summary>已释放则抛——委托 LifecycleBase.ThrowIfDisposed。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsureNotDisposed() => ThrowIfDisposed();
}
