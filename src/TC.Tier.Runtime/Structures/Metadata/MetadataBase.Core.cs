using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>核心机制 partial：版本链 IO + 内存多版本 + 截断 + 2PC + Dispose。</summary>
public abstract partial class MetadataBase
{
    // ════════════════════════════════════════════════════════════
    // === 三个核心方法：写 / 读（数据结构自身能力）===
    // 恢复（①）已上移为 LifecycleBase.Initialize（类面方法，见 MetadataBase.cs），不再在此暴露独立 Recover 入口。
    // ════════════════════════════════════════════════════════════

    // ★ EnsureReady / EnsureNotDisposed 由 LifecycleBase 提供（ThrowIfDisposed + IsReady 检查）。
    //   本类读写入口（Write/Read/Prepare 等）调用的 EnsureReady/EnsureNotDisposed 走基类实现。

    /// <summary>
    /// ② 写（同步）——把 data 写入内存镜像，推进版本号，返回版本号（long）。按落盘策略触发持久化。
    /// <para>Sync 策略立即落盘 / Async 策略后台批量。Prepare(seq) 无论何种策略都强制同步 flush。</para>
    /// </summary>
    public long Write(ReadOnlySpan<byte> data)
    {
        EnsureNotDisposed();
        EnsureReady();
        int n = Math.Min(data.Length, _payloadSize);
        _epoch.Resume();
        try
        {
            // ★ 覆盖 [0] 前先把当前 [0] 滑到 [1]（保留为 Abort 零 IO 回退源）。
            //   [0] 此刻 = 当前已提交/待覆盖版本，写入新数据后 [1] 持有上一版本。
            SlideMemoryWindow();

            _hotVersions[0].GetSpanUnsafe(0, _payloadSize).Clear(); // 清零（data < payloadSize 时尾部补零）
            data[..n].CopyTo(_hotVersions[0].GetSpanUnsafe(0, _payloadSize));
        }
        finally
        {
            _epoch.Suspend();
        }

        // 推进版本号 + 超越加载版本（当前内容切到热区）+ 开启编辑会话
        _currentVersion++;
        _sessionVersion = _currentVersion;
        _sessionActive = true;
        _serveLoaded = false;
        if (_hotVersionCount >= 2 && _loadedVersion is not null)
        {
            // 两次写之后 Abort 只做热-热回退（[1]），加载版本不可达——归还池
            _bufferPool.ReturnAligned(_loadedVersion);
            _loadedVersion = null;
        }

        // 按落盘策略触发持久化
        if (_persistencePolicy is { } policy && policy.ShouldPersist(_currentVersion))
            AppendVersionToDisk();

        return _currentVersion;
    }

    /// <summary>持久化——把内存镜像作为新版本追加到磁盘版本链（强制落盘）。
    /// ★ 内容未变（无新 Write）跳过追加：冷热分离后热区不再持有恢复载入的历史镜像，
    ///   直接追加会把零/旧内容当新版本写进链（缩容零覆写）。</summary>
    public void Persist()
    {
        EnsureNotDisposed();
        EnsureReady();
        if (_currentVersion > _persistedVersion)
            AppendVersionToDisk();
    }

    /// <summary>③ 读——读当前内容（零 IO）。epoch 保护。
    /// <para>★ 加载版本优先：首次 Write 前当前内容 = 恢复载入的历史版本（按其<b>真实大小</b>交付——
    ///   不补零不截断，历史大小 ≠ 当前 PayloadSize 也完整读回）；Write 后 = 热区（当前配置大小）。</para></summary>
    public int Read(Span<byte> dst)
    {
        EnsureNotDisposed();
        EnsureReady();
        _epoch.Resume();
        try
        {
            return ReadCore(dst);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>★ 热路径变体：不含 epoch 进出的读（供已持 epoch 的 scope/batch 内调）。裸调危险。</summary>
    public int ReadNoEpoch(Span<byte> dst)
    {
        EnsureNotDisposed();
        EnsureReady();
        return ReadCore(dst);
    }

    private int ReadCore(Span<byte> dst)
    {
        if (_serveLoaded && _loadedVersion is { } lv)
        {
            int n = Math.Min(_loadedVersionLength, dst.Length);
            lv.GetSpanUnsafe(0, n).CopyTo(dst);
            return n;
        }
        int m = Math.Min(_payloadSize, dst.Length);
        _hotVersions[0].GetSpanUnsafe(0, m).CopyTo(dst);
        return m;
    }

    /// <summary>当前内容 Span（加载版本 slice 或热区，热路径 GetSpanUnsafe 零校验）。</summary>
    private protected Span<byte> CurrentMemorySpan
        => _serveLoaded && _loadedVersion is { } lv
            ? lv.GetSpanUnsafe(0, _loadedVersionLength)
            : _hotVersions[0].GetSpanUnsafe(0, _payloadSize);

    /// <summary>
    /// 读路径：返回当前版本 payload 的 0-copy Span 视图给调用方读。
    /// 调用方持有 Span 读数据，销毁 Span 不影响内部数据。写数据用 Write(data)。
    /// </summary>
    public Span<byte> AsSpan() => CurrentMemorySpan;

    public ref T GetRef<T>() where T : unmanaged
        => ref _serveLoaded && _loadedVersion is { } lv
            ? ref lv.GetRefUnsafe<T>(0)
            : ref _hotVersions[0].GetRefUnsafe<T>(0);

    /// <summary>强类型 Span 视图。★ 要求当前内容大小与 sizeof(T) 对齐——恢复载入的历史大小
    /// ≠ 当前 PayloadSize 时可能不满足（MemoryMarshal.Cast 将抛），大小无关读取用 Read/AsSpan。</summary>
    public Span<T> GetSpan<T>() where T : unmanaged => MemoryMarshal.Cast<byte, T>(CurrentMemorySpan);

    // ════════════════════════════════════════════════════════════
    // === 版本链追加（核心 IO）===
    // ════════════════════════════════════════════════════════════

    /// <summary>把当前内存镜像作为新版本追加到磁盘版本链 + flush + 写 meta。</summary>
    private protected void AppendVersionToDisk()
    {
        _epoch.Resume();
        try
        {
            // ★ 不推进版本号——Write() 已推进。这里只落盘当前版本
            long newVersion = _currentVersion;

            // 写 record（Header + Payload + Padding）——★ Allocate + Write（引擎统一模型，§3.6）
            // Allocate 预留空间（零 IO CAS 推 AllocatedTail），Write 覆写已分配区
            using var buf = new AlignedMemoryManager(_recordSize, (int)_engine.SectorSize);
            var span = buf.GetSpan();
            span.Clear();
            // Header
            _codec.WriteHeader(span, new MetadataRecordFields(
                Flags: MetadataHeader.DefaultFlags,
                PayloadLength: (uint)_payloadSize,
                PaddingLength: (ushort)_paddingLength,
                PreviousVersion: _highestVersionAddress,
                MetadataVersion: newVersion));
            // Payload（从热区当前镜像拷到 record buffer）
            _hotVersions[0].GetSpanUnsafe(0, _payloadSize)
                .CopyTo(buf.GetSpan(_codec.HeaderSize, _payloadSize + _paddingLength));
            // CRC
            _codec.FillCrc(span, _codec.HeaderSize, _payloadSize, _paddingLength);
            // Allocate + Write（统一模型）
            var addr = _engine.Allocate(_recordSize).Start;
            _engine.Write(addr, span);
            _engine.Flush();

            // 更新水位——用 _currentVersion == 0 判断首版本（不用地址值，Empty 是合法地址）
            if (_lowestVersionAddress == default && _highestVersionAddress == default)
                _lowestVersionAddress = addr; // 第一个版本
            _highestVersionAddress = addr;
            _persistedVersion = _currentVersion;   // 链头已落此版本——未变再 Prepare 跳过追加
            // ★ 不推进 _currentVersion——Write() 已推进
            // ★ 不在此 SlideMemoryWindow——Write() 在覆盖 [0] 前已滑动（[1] 持有上一版本，Abort 回退源）
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    /// <summary>
    /// 内存窗口滑动：把当前镜像 [0] 拷贝到历史位 [1]（保留为 Abort 零 IO 回退源）。
    /// <para>★ 调用时机：Write() 在覆盖 [0] 写新数据**之前**调用本方法——
    ///   [0] 此刻 = 当前（上一）版本，拷到 [1] 作为回退源；随后 [0] 被新数据覆盖。</para>
    /// <para>窗口 N≥2：[0]=当前版本，[1]=上一版本（Abort 回退源），[2..]=更老（MVCC 可选）。</para>
    /// <para>★ 拷贝顺序从高索引往低索引，避免 [0]→[1]、[1]→[2] 时 [1] 被覆盖前未读到。</para>
    /// </summary>
    private void SlideMemoryWindow()
    {
        for (int i = _maxMemoryVersions - 1; i > 0; i--)
            _hotVersions[i - 1].GetSpanUnsafe(0, _payloadSize).CopyTo(_hotVersions[i].GetSpanUnsafe(0, _payloadSize));
        if (_hotVersionCount < _maxMemoryVersions)
            _hotVersionCount++;
    }

    // ════════════════════════════════════════════════════════════
    // === 截断（头/尾，epoch 保护，不能截到当前版本）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 头截断（业务/后台调）——回收旧版本磁盘空间。
    /// <para>按保留窗口（MaxMemoryVersions）保留最近 N 个版本，更老的从链尾方向回收。</para>
    /// <para>★ epoch 保护（BumpCurrentEpoch 等 readers 退出后回收）。</para>
    /// <para>★ 硬约束：keepAddr 不能截到当前活跃版本。</para>
    /// </summary>
    public void ReclaimOldVersions()
    {
        EnsureNotDisposed();
        // 版本数 ≤ 保留窗口，无需回收
        if (_currentVersion < _maxMemoryVersions) return;
        if (_lowestVersionAddress == _highestVersionAddress) return; // 只有一个版本，不回收

        // ★ 保留窗口：[keepAddr, _highestVersionAddress] = 最近 N 个版本；回收 [MinAddress, keepAddr)
        //   ★★ 逐 record 自身几何推进（用户裁定：配置不参与物理事实）——链上 record 尺寸可能不同
        //   （PayloadSize 跨重启变更后新旧混尺寸），统一 _recordSize 步进会落在旧 record 中段 →
        //   ReclaimHead 掐半活 record → 下次扫盘断链静默丢数据。每条 record 的 header 自述自己的
        //   PayloadLength/PaddingLength（窗口 N 小，读 N 个 header 可忽略）。
        var keepAddr = _lowestVersionAddress;
        var sectorSize = (int)_engine.SectorSize;
        for (int i = 1; i < _maxMemoryVersions && keepAddr.CompareTo(_highestVersionAddress) < 0; i++)
        {
            using var hdrBuf = new AlignedMemoryManager(_codec.HeaderSize, sectorSize);
            if (_engine.Read(keepAddr, hdrBuf.GetSpan()) < _codec.HeaderSize) return;
            if (!_codec.TryReadHeader(hdrBuf.GetSpanUnsafe(0, _codec.HeaderSize), out var fields)) return;
            // 按该 record 自身几何推进（CalculationAddress 推进，不手动算 Offset）
            keepAddr = _engine.CalculationAddress(keepAddr, _codec.HeaderSize + (long)fields.PayloadLength + fields.PaddingLength);
        }

        // 硬约束：keepAddr 不能超过当前版本地址
        if (keepAddr.CompareTo(_highestVersionAddress) >= 0) return;

        // ★ epoch 保护：等 readers 退出后 ReclaimHead
        _epoch.Resume();
        try
        {
            _epoch.BumpCurrentEpoch(() =>
            {
                _engine.ReclaimHead(keepAddr);
                _lowestVersionAddress = keepAddr;
            });
        }
        finally
        {
            _epoch.Suspend();
        }
    }

    // ════════════════════════════════════════════════════════════
    // === 2PC（ITransactionParticipant，独立协议，与数据写正交）===
    // ════════════════════════════════════════════════════════════

    long ITransactionParticipant.LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);
    long ITransactionParticipant.LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    /// <summary>Prepare：记录回退快照 → 追加新版本到磁盘链头 → flush + 写 meta。
    /// <para>★ 内容未变跳过追加（防重复/防缩容零覆写）：本会话无新 Write（_currentVersion ==
    ///   _persistedVersion）时链头已是最新镜像，再追加只会复制旧内容或把零内容当新版本写进链。</para></summary>
    public void Prepare(long seq)
    {
        EnsureNotDisposed();
        EnsureReady();
        // ★ 记录 Prepare 前的链头地址——Abort 据此 ReclaimTail 回退悬干新版本（防空间泄漏）
        //   ★ 用 _hasPrepareSnapshot 标志表示"记录过"——不能用 addr==Empty 判断（Empty 是合法地址，首版本链头就是 Empty）
        _prepareSnapshotAddress = _highestVersionAddress;
        _hasPrepareSnapshot = true;
        _preparePersistedVersion = _persistedVersion;
        _prepareAppended = _currentVersion > _persistedVersion;
        if (_prepareAppended)
            AppendVersionToDisk();
        Volatile.Write(ref _lastPreparedSeq, seq);
        WriteMeta();
    }

    public async ValueTask PrepareAsync(long seq, CancellationToken ct)
    {
        EnsureNotDisposed();
        EnsureReady();
        _prepareSnapshotAddress = _highestVersionAddress;
        _hasPrepareSnapshot = true;
        _preparePersistedVersion = _persistedVersion;
        _prepareAppended = _currentVersion > _persistedVersion;
        if (_prepareAppended)
            AppendVersionToDisk(); // 同步 flush（原生 flush 不支持异步）
        Volatile.Write(ref _lastPreparedSeq, seq);
        await MetaPolicy.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>ConfirmCommitted：CAS 推进 LastCommittedSeq + 持久化 meta（刷新水位）+ 清空 Abort 回退快照 + 触发回调。</summary>
    public void ConfirmCommitted(long seq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return;
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);

        // ★ 提交成功：新版本正式成为当前版本，刷新 meta 持久化水位（LastCommittedSeq 落盘，恢复时正确还原）
        //   ★ 仅在 MetaPolicy ≠ Disabled 时落盘——Disabled 走扫盘，无 meta 文件
        if (_settings.MetaPolicyKind != MetaPolicyKind.Disabled)
            WriteMeta();
        // ★ 会话守卫：无 Write（如恢复后直接 Prepare+Confirm 固化 seq）不得改版本号；
        //   有会话则正式采纳 _sessionVersion 为当前版本。新基准 = 当前版本（后续 Abort 的回退界）。
        if (_sessionActive)
        {
            _currentVersion = _sessionVersion;
            _sessionActive = false;
        }
        _baseVersion = _currentVersion;
        // 加载版本已被提交写超越——归还池
        if (!_serveLoaded && _loadedVersion is not null)
        {
            _bufferPool.ReturnAligned(_loadedVersion);
            _loadedVersion = null;
        }
        _hasPrepareSnapshot = false;
        _prepareSnapshotAddress = LogicalAddress.Empty;
        _prepareAppended = false;   // 追加的 record 已被采纳为当前——不再有悬干可回退
        FireTransactionCallbacks(seq);
    }

    /// <summary>★ Abort：内存窗口回退到上一版本（零 IO）+ 尾截断 ReclaimTail 回退悬干新版本 + flush + 写 meta。</summary>
    public void Abort(long seq)
    {
        EnsureNotDisposed();
        // 幂等
        if (seq <= Volatile.Read(ref _lastAbortedSeq)) return;

        _epoch.Resume();
        try
        {
            // ★ 内存回退（零 IO）——仅当本会话有未确认的 Write（_currentVersion > _baseVersion）：
            //   两次以上写 → 热-热回退（[1]→[0]）；单次写且前置=加载版本 → 当前内容指回加载版本
            //   （窗口清零——后续写重新滑动；旧版 >=2 守卫漏掉"恢复后单次写即 Abort"的回退）。
            if (_currentVersion > _baseVersion)
            {
                if (_hotVersionCount >= 2 || _loadedVersion is null)
                {
                    if (_hotVersionCount >= 1)
                        _hotVersions[1].GetSpanUnsafe(0, _payloadSize).CopyTo(_hotVersions[0].GetSpanUnsafe(0, _payloadSize));
                    _serveLoaded = false;
                }
                else
                {
                    _serveLoaded = true;   // 回退到恢复载入的历史版本（完整大小，未截断）
                    _hotVersionCount = 0;
                }
            }

            // ★ 尾截断回退磁盘链头——用 _hasPrepareSnapshot 标志判断有无 Prepare 快照
            //   （不能用 addr==Empty 判断——Empty 是合法地址，首版本链头就是 Empty）
            if (_hasPrepareSnapshot && _lastPreparedSeq > _lastCommittedSeq)
            {
                var snapshot = _prepareSnapshotAddress;
                // ★ 逻辑回退立即生效（_highestVersionAddress / _currentVersion）——
                //   内存/水位层面 Abort 已完成，调用方后续读写即见回退后状态。
                _highestVersionAddress = snapshot;
                if (_currentVersion > _baseVersion)
                    _currentVersion--; // 回退版本号（仅当本会话 Write 真推进过——纯 seq 悬干不回退号）
                // ★ 仅当本 Prepare 真追加过 record 才物理回收（内容未变跳过追加 → 链尾无悬干，
                //   ReclaimTail 目标越过尾会误动段几何）。盘上链头版本记账一并回退到追加前——
                //   否则下一次 Prepare 误判"已落盘"跳过追加，新 Write 内容永不上盘。
                if (_prepareAppended)
                {
                    _persistedVersion = _preparePersistedVersion;
                    // 物理回收（ReclaimTail）走 BumpCurrentEpoch 等 readers 退出后执行（防 use-after-free）
                    _epoch.BumpCurrentEpoch(() =>
                    {
                        // 回退 AllocatedTail 到 Prepare 前链头之后：丢弃悬干新版本 [snapshot, snapshot+_recordSize)
                        // ★ 用 CalculationAddress 推算回退点（不能手动算 Offset）
                        var reclaimFrom = _engine.CalculationAddress(snapshot, _recordSize);
                        _engine.ReclaimTail(reclaimFrom);
                    });
                }
            }

            _hasPrepareSnapshot = false;
            _prepareSnapshotAddress = LogicalAddress.Empty;
            _prepareAppended = false;
            _sessionActive = false;

            Volatile.Write(ref _lastPreparedSeq, _lastCommittedSeq);
            Volatile.Write(ref _lastAbortedSeq, seq);
        }
        finally
        {
            _epoch.Suspend();
        }
    }

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

            // 移除已触发
            while (_txCallbacks.Count > 0 && _txCallbacks.Keys[0] <= committedSeq)
                _txCallbacks.RemoveAt(0);
        }

        if (toFire is not null)
            foreach (var cb in toFire)
                cb(); // 锁外触发避免死锁
    }

    // ════════════════════════════════════════════════════════════
    // === Dispose ===
    // ════════════════════════════════════════════════════════════

    /// <summary>已释放则抛——委托 LifecycleBase.ThrowIfDisposed（_disposed 在基类）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private protected void EnsureNotDisposed() => ThrowIfDisposed();

    /// <summary>★ 子类额外清理钩子（基类核心清理不可绕过：Resources.Dispose 释放 owned 资源 + 取消后台 task）。
    /// <para>释放 MetadataBase 私有非托管内存（冷区/热区）+ MetaPolicy + epoch（自管的）。
    /// 先归还加载版本到池——随后 Resources 释放池（归还的 buffer 随池一并释放）。</para></summary>
    protected override void DisposeOverride(bool disposing)
    {
        _bufferPool.ReturnAligned(_loadedVersion);
        _loadedVersion = null;
        // 热区（N 个对齐内存对象）
        foreach (var hot in _hotVersions) hot?.Dispose();
    }

    /// <summary>异步额外清理（同 <see cref="DisposeOverride"/>，MetaPolicy 走异步轨）。</summary>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        _bufferPool.ReturnAligned(_loadedVersion);
        _loadedVersion = null;
        foreach (var hot in _hotVersions) hot.Dispose();
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }
}