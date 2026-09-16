using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 批量追加 partial——★ 地址空间窗口 + 页攒批模型。
/// <para>inline 写页缓冲（本地跟踪 _pageUsed），页满走<b>让渡轨</b>（<see cref="FlushPageYield"/>：
/// 当前页让渡给异步刷盘管线、立即切另一页继续写——页满不阻塞写线程，唯一等待点 = 双页都在途的
/// 背压），Dispose 时回写宿主状态。</para>
/// <para>★ 双形态：<see cref="AppendBatch.Append"/>（同步——页满让渡零阻塞，背压同步等=诚实语义）+
/// <see cref="AppendBatch.AppendAsync"/>（ValueTask——快路/让渡路同步完成，仅背压真异步）。</para>
/// <para>★ 消除 per-entry：EnsureNotDisposed / ComputePaddingLength / OnAppended / EnsureSpace*。</para>
/// <para>★ 单写者纪律（顺序读依赖批内物理连续性）：<see cref="AppendBatch.AppendAsync"/> 背压慢路的
/// Monitor.Exit 窗口内禁止其他写者插队 Begin——批协议的消费形态 = 单写者
/// （raft 适配器 = wal 唯一追加方）。</para>
/// </summary>
public abstract partial class LogBase
{
    /// <summary>★ 开启批量追加（ref struct，using 包裹；多 entry 共用页缓冲 + 地址空间窗口）。</summary>
    /// <returns>批句柄（ref struct；Dispose 时回写宿主状态并释放写锁）。</returns>
    public AppendBatch BeginAppendBatch()
    {
        EnsureNotDisposed();
        Monitor.Enter(_writeLock);   // ★ 批持写锁（批内本地游标零锁；Dispose 释放）——并发批串行安全
        try
        {
            // ★ 初始化在锁内（并发首次 Begin 双窗口竞态——_spaceStart 覆盖→数据错乱 0 条）
            EnsureWriteInitialized();
            EnsureSpaceAllocated();
            _batchAsyncCount = 0;
            _batchAsyncFirst = LogicalAddress.Empty;
            _batchAsyncVersion++;
            _batchLockOrphaned = false;
            return new AppendBatch(this);
        }
        catch
        {
            Monitor.Exit(_writeLock);
            throw;
        }
    }

    /// <summary>
    /// 批量 ref struct——inline 写页缓冲，本地跟踪 _pageUsed，Dispose 时回写宿主状态。
    /// <para>★ 消费纪律：批（ref struct）绝不跨 await 持有（持写锁）；<see cref="AppendBatch.AppendAsync"/>
    /// 背压慢路的 await 发生在宿主侧（<see cref="AppendBatchSlowAsync"/>）——返回后批把写锁交还
    /// （孤儿认领协议），消费方的下一个批操作在当前线程重新认领，任意续体线程安全。</para>
    /// </summary>
    public ref struct AppendBatch
    {
        private readonly LogBase _owner;
        private readonly int _headerSize;
        private readonly int _alignment;
        private readonly int _pageSize;
        private bool _open;
        private int _count;
        private LogicalAddress _firstOffset;
        private int _localPageUsed;   // 本地跟踪当前页 entry 累计偏移（同步宿主 _pageUsed）
        private int _syncedPage;      // 重同步 token：宿主活跃页号（背压慢路切页后批本地游标失效）
        private int _syncedVersion;   // 重同步 token：宿主批镜像版本（背压慢路写入后批簿记失效）

        internal AppendBatch(LogBase owner)
        {
            _owner = owner;
            _headerSize = owner.LogCodec.HeaderSize;
            _alignment = owner.LogCodec.Alignment;
            _pageSize = owner.PageSize;
            _localPageUsed = owner.ActivePageUsed;
            _syncedPage = owner._activePage;
            _syncedVersion = owner._batchAsyncVersion;
            _open = true;
            _count = 0;
            _firstOffset = LogicalAddress.Empty;
        }

        /// <summary>批量追加单条 entry（inline 写页缓冲，本地跟踪偏移，返回 entry 起始 LogicalAddress）。
        /// ★ 页满 = 让渡轨（异步刷盘、不阻塞写线程）；双页都在途 = 背压同步等（诚实阻塞语义）。</summary>
        /// <param name="entry">entry payload 字节（不含记录头；含 header+padding 须能放进单个页）。</param>
        /// <returns>本条 entry 的起始 LogicalAddress（记录头起始处）。</returns>
        public LogicalAddress Append(ReadOnlySpan<byte> entry)
        {
            if (!_open) throw new ObjectDisposedException(nameof(AppendBatch));
            ReclaimBatchLock();
            ResyncFromHost();

            int contentLen = _headerSize + entry.Length;
            int paddingLen = contentLen.AlignUp(_alignment) - contentLen;
            int totalSize = contentLen + paddingLen;

            if (_localPageUsed + totalSize > _pageSize)
            {
                _owner.ActivePageUsed = _localPageUsed;
                _owner.AppendPageYieldSync();     // ★ 让渡（背压同步等）——页 IO 不再阻塞写线程
                _owner.EnsureSpaceForNextPage();
                _localPageUsed = 0;
                _syncedPage = _owner._activePage;
            }

            return AppendInline(entry, totalSize, paddingLen);
        }

        /// <summary>
        /// ★ 批内异步追加（ValueTask 三档）：
        /// <para>页有空间 = 纯 memcpy 同步快路（ValueTask 同步完成，零分配零跳线程）；</para>
        /// <para>页满无背压 = 让渡（同步完成——纯数据写任务已启动，写线程不阻塞）；</para>
        /// <para>页满背压（双页都在途）= 唯一真异步点：宿主侧 Monitor.Exit → await 在途刷 →
        /// Re-Enter → 让渡 → 写入 → 写锁交还（孤儿认领协议——消费方下一个批操作在当前线程重新认领）。</para>
        /// </summary>
        /// <param name="entry">entry payload 字节（不含记录头；单条含 header+padding 须 ≤ 页大小，
        /// 否则抛 <see cref="InvalidOperationException"/>——大对象须由调用方拆成多条 entry）。</param>
        /// <param name="ct">取消令牌（进入即检查；取消抛 OperationCanceledException）。</param>
        /// <returns>ValueTask 三档：页有空间 / 页满无背压 = 同步完成；页满背压 = 真异步（宿主侧等在途刷盘后写入）。
        /// 完成后结果 = 本条 entry 的起始 LogicalAddress（记录头起始处）。</returns>
        public ValueTask<LogicalAddress> AppendAsync(ReadOnlySpan<byte> entry, CancellationToken ct = default)
        {
            if (!_open) throw new ObjectDisposedException(nameof(AppendBatch));
            ct.ThrowIfCancellationRequested();
            ReclaimBatchLock();
            ResyncFromHost();

            int contentLen = _headerSize + entry.Length;
            int paddingLen = contentLen.AlignUp(_alignment) - contentLen;
            int totalSize = contentLen + paddingLen;
            if (totalSize > _pageSize)
                throw new InvalidOperationException(
                    $"Entry size {totalSize} exceeds page size {_pageSize} (Header={_headerSize} Payload={entry.Length}). " +
                    "Single entry MUST fit within one page — split large objects into multiple entries at the caller.");

            if (_localPageUsed + totalSize > _pageSize)
            {
                _owner.ActivePageUsed = _localPageUsed;
                if (_owner._inFlightFlush is null)
                {
                    // 让渡（零等待）——同步完成
                    _owner.FlushPageYield();
                    _owner.EnsureSpaceForNextPage();
                    _localPageUsed = 0;
                    _syncedPage = _owner._activePage;
                }
                else
                {
                    // 背压——唯一真异步点（payload 拷贝过 await；背压罕见 = 页边界 × 双页在途）
                    return _owner.AppendBatchSlowAsync(entry.ToArray(), entry.Length, totalSize, paddingLen,
                        _count, _firstOffset, ct);
                }
            }

            return new ValueTask<LogicalAddress>(AppendInline(entry, totalSize, paddingLen));
        }

        /// <summary>本批已追加的 entry 条数。</summary>
        public int Count => _count;
        /// <summary>本批第一条 entry 的起始 LogicalAddress（空批时为 Empty）。</summary>
        public LogicalAddress FirstOffset => _firstOffset;

        /// <summary>Dispose：回写宿主 _pageUsed（地址空间窗口状态由 FlushPage/让渡轨维护，无需回写）+ 释放写锁。</summary>
        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            ReclaimBatchLock();
            ResyncFromHost();
            _owner.ActivePageUsed = _localPageUsed;
            Monitor.Exit(_owner._writeLock);
        }

        /// <summary>inline 写 entry 进当前页（header+payload+padding）+ 批簿记；页写满即同步让渡。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private LogicalAddress AppendInline(ReadOnlySpan<byte> entry, int totalSize, int paddingLen)
        {
            var pageFrameStart = _owner._engine.CalculationAddress(_owner._spaceStart, _owner._spaceWriteOffset);
            var entryStart = _owner._engine.CalculationAddress(pageFrameStart, LogPageFrameHeaderCodec.StructSize + _localPageUsed);

            var page = _owner.ActivePage.GetSpan(_localPageUsed, totalSize);
            entry.CopyTo(page[_headerSize..]);
            page.Slice(_headerSize + entry.Length, paddingLen).Clear();
            _owner.LogCodec.WriteHeader(page, entry.Length, paddingLen, false);
            _localPageUsed += totalSize;

            if (_localPageUsed >= _pageSize)
            {
                _owner.ActivePageUsed = _localPageUsed;
                _owner.AppendPageYieldSync();     // ★ exact-fill 同步让渡（背压同步等——诚实语义）
                _owner.EnsureSpaceForNextPage();
                _localPageUsed = 0;
                _syncedPage = _owner._activePage;
            }

            if (_count == 0) _firstOffset = entryStart;
            _count++;
            return entryStart;
        }

        /// <summary>★ 背压慢路把写锁留在续体线程（孤儿）——批的下一个操作在当前线程重新认领。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReclaimBatchLock()
        {
            if (!_owner._batchLockOrphaned) return;
            Monitor.Enter(_owner._writeLock);
            _owner._batchLockOrphaned = false;
        }

        /// <summary>★ 宿主状态重同步：背压慢路在宿主侧切页/写入（批 ref struct 不跨 await）——
        /// 批本地游标/簿记按宿主镜像取回。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ResyncFromHost()
        {
            if (_syncedPage == _owner._activePage && _syncedVersion == _owner._batchAsyncVersion) return;
            _syncedPage = _owner._activePage;
            _syncedVersion = _owner._batchAsyncVersion;
            _localPageUsed = _owner.ActivePageUsed;
            _count = _owner._batchAsyncCount;
            _firstOffset = _owner._batchAsyncFirst;
        }
    }

    /// <summary>
    /// ★ 批协议背压慢路（唯一真异步点）：批持写锁（当前线程 = 持有者）→ Monitor.Exit → await 在途刷 +
    /// 补延迟提交链（锁外——提交链经 AppendCoreAsync 自取锁）→ Monitor.Re-Enter → 让渡当前页 →
    /// 写入 entry → 更新批镜像簿记 → <b>写锁交还（孤儿标记）</b>——消费方的下一个批操作在当前线程
    /// 重新认领（ref struct 消费方的续体线程不可预期，每跳一致不炸 SynchronizationLockException）。
    /// <para>★ 单写者纪律：Exit 窗口内其他写者插队 Begin 会破坏批物理连续性（顺序读依赖）。</para>
    /// </summary>
    private async ValueTask<LogicalAddress> AppendBatchSlowAsync(
        byte[] payload, int payloadLen, int totalSize, int paddingLen,
        int batchCount, LogicalAddress batchFirst, CancellationToken ct)
    {
        Monitor.Exit(_writeLock);
        try
        {
            await DrainInFlightAsync().ConfigureAwait(false);   // 等在途刷 + 补延迟提交链（锁外——提交链自取锁）
        }
        catch
        {
            // 失败路径：锁交还批（孤儿标记）——消费方 Dispose 在任意线程重新认领后干净释放
            Monitor.Enter(_writeLock);
            Monitor.Exit(_writeLock);
            _batchLockOrphaned = true;
            throw;
        }

        LogicalAddress entryStart;
        Monitor.Enter(_writeLock);
        try
        {
            FlushPageYield();
            EnsureSpaceForNextPage();

            var pageFrameStart = _engine.CalculationAddress(_spaceStart, _spaceWriteOffset);
            entryStart = _engine.CalculationAddress(pageFrameStart, LogPageFrameHeaderCodec.StructSize + ActivePageUsed);
            WriteEntryToPageBuffer(ActivePageUsed, payload, payloadLen, paddingLen, isMeta: false);
            ActivePageUsed += totalSize;

            _batchAsyncCount = batchCount + 1;
            _batchAsyncFirst = batchCount == 0 ? entryStart : batchFirst;
            _batchAsyncVersion++;
        }
        finally
        {
            Monitor.Exit(_writeLock);      // ★ 锁交还批（孤儿）——消费方下一个操作重新认领
            _batchLockOrphaned = true;
        }

        return entryStart;
    }
}
