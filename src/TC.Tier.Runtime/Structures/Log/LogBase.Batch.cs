namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// LogBase 批量追加 partial——★ 地址空间窗口 + 页攒批模型。
/// <para>inline 写页缓冲（本地跟踪 _pageUsed），换页时同步宿主 _pageUsed 并提交，Dispose 时回写宿主状态。</para>
/// <para>★ 消除 per-entry：EnsureNotDisposed / ComputePaddingLength / OnAppended / EnsureSpace*。</para>
/// </summary>
public abstract partial class LogBase
{
    /// <summary>★ 开启批量追加（ref struct，using 包裹；多 entry 共用页缓冲 + 地址空间窗口）。</summary>
    public AppendBatch BeginAppendBatch()
    {
        EnsureNotDisposed();
        Monitor.Enter(_writeLock);   // ★ 批持写锁（批内本地游标零锁；Dispose 释放）——并发批串行安全
        try
        {
            // ★ 初始化在锁内（并发首次 Begin 双窗口竞态——_spaceStart 覆盖→数据错乱 0 条）
            EnsureWriteInitialized();
            EnsureSpaceAllocated();
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

        internal AppendBatch(LogBase owner)
        {
            _owner = owner;
            _headerSize = owner.LogCodec.HeaderSize;
            _alignment = owner.LogCodec.Alignment;
            _pageSize = owner.PageSize;
            _localPageUsed = owner.ActivePageUsed;
            _open = true;
            _count = 0;
            _firstOffset = LogicalAddress.Empty;
        }

        /// <summary>批量追加单条 entry（inline 写页缓冲，本地跟踪偏移，返回 entry 起始 LogicalAddress）。</summary>
        public LogicalAddress Append(ReadOnlySpan<byte> entry)
        {
            if (!_open) throw new ObjectDisposedException(nameof(AppendBatch));

            int contentLen = _headerSize + entry.Length;
            int paddingLen = contentLen.AlignUp(_alignment) - contentLen;
            int totalSize = contentLen + paddingLen;

            if (_localPageUsed + totalSize > _pageSize)
            {
                _owner.ActivePageUsed = _localPageUsed;
                _owner.FlushPage();
                _owner.EnsureSpaceForNextPage();
                _localPageUsed = 0;
            }

            var pageFrameStart = _owner._engine.CalculationAddress(_owner._spaceStart, _owner._spaceWriteOffset);
            LogicalAddress entryStart = _owner._engine.CalculationAddress(pageFrameStart, LogPageFrameHeaderCodec.StructSize + _localPageUsed);

            Span<byte> page = _owner.ActivePage.GetSpan(_localPageUsed, totalSize);
            entry.CopyTo(page.Slice(_headerSize));
            page.Slice(_headerSize + entry.Length, paddingLen).Clear();
            _owner.LogCodec.WriteHeader(page, entry.Length, paddingLen, false);
            _localPageUsed += totalSize;

            if (_localPageUsed >= _pageSize)
            {
                _owner.ActivePageUsed = _localPageUsed;
                _owner.FlushPage();
                _owner.EnsureSpaceForNextPage();
                _localPageUsed = 0;
            }

            if (_count == 0) _firstOffset = entryStart;
            _count++;
            return entryStart;
        }

        public int Count => _count;
        public LogicalAddress FirstOffset => _firstOffset;

        /// <summary>Dispose：回写宿主 _pageUsed（地址空间窗口状态由 FlushPage 维护，无需回写）+ 释放写锁。</summary>
        public void Dispose()
        {
            if (!_open) return;
            _open = false;
            _owner.ActivePageUsed = _localPageUsed;
            Monitor.Exit(_owner._writeLock);
        }
    }
}
