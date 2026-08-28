using System.Buffers;
using System.Collections.Concurrent;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 统一页管理 partial（§3.4）——自管页缓存顶替 OS page cache 的位置。
/// <para>★ v1 形态：读缓存（命中 memcpy）+ 数据写直通载体（write-through）+ 元数据常驻内存权威。
///   写回（脏页 + 后台 flusher）为 §14.7 决策点的后续增强——数据面契约不依赖它
///   （探针基线：直通写 ≈ Disk 缓冲写持平，§12.4）。</para>
/// <para>★ 页 = 内部块；键 = 物理块号。预算封顶 LRU 近似（访问序队列 + 字节计数；0 = 禁用=直达档）。</para>
/// <para>★ 在途去重：同块并发 miss 只发一次载体读（per-page 门闩）。</para>
/// </summary>
public sealed partial class TierVolumeFs
{
    /// <summary>页缓冲租用（D4：ArrayPool——块大小恒 2 的幂故 Rent 精确；预算级常驻页的分配税 = 写速率，池化消除）。</summary>
    private byte[] RentPageBuffer() => ArrayPool<byte>.Shared.Rent(_pageSize);

    private static void ReturnPageBuffer(byte[] buffer) => ArrayPool<byte>.Shared.Return(buffer);

    private sealed class Page(byte[] bytes)
    {
        public readonly byte[] Bytes = bytes;   // 长度 = 块大小（Rent 精确——2 的幂）
        public bool Loaded;
        public bool Dirty;   // 写回页缓存（14.7）：脏页待刷——Flush/逐出/提交序排干
        public bool Tracked; // 已入 LRU/字节计数（D2 修复：重复 Store 免双计数——否则虚高触发逐出风暴）
        public readonly object Gate = new();
    }

    private readonly ConcurrentDictionary<ulong, Page> _pages = new();
    private readonly ConcurrentQueue<ulong> _lru = new();
    private long _pageBytes;                       // 当前缓存字节
    private long _dirtyBytes;                      // 脏字节（压力排干的批量化判据）
    private readonly ConcurrentDictionary<ulong, Page> _dirtyPages = new();   // 脏页索引（RM-12 锁外读者压力排干路径与写者并发——并发字典消除枚举竞态）
    /// <summary>载体在途写字节（RM-40）：<see cref="WriteCarrier"/> 直落但尚未被 <see cref="FlushCarrier"/>
    /// 屏障覆盖的字节数——写绕/直达/零基/脏页排干等一切不经自管脏页记账的载体写。
    /// Flush 判据 = 脏页 ∪ 本计数 &gt; 0（持久化语义缺陷修复：写绕+空转快道曾使 Flush 返回时数据仅在内核页缓存）。</summary>
    private long _carrierWritePendingBytes;
    private readonly long _pageBudget;             // 预算上限（构造注入；0 = 禁用=直达档）
    private const long FlushThresholdBytes = 1L << 20;   // 压力排干阈值（滞后——防 O(n²)）

    // ═══ 后台 flusher（RM-02——kernel writeback 模型：写路径只 copy+标脏+记账，回写归后台）═══
    private Thread? _flusher;
    private volatile bool _flusherStop;
    private readonly AutoResetEvent _flushWake = new(false);
    private int _flusherGate;                        // 创建竞态闸（Interlocked——EnsureFlusher 单射）
    private readonly long _backgroundDirtyThreshold; // 唤醒阈值（max(1MB, 预算/8)——kernel background_thresh 同构）

    /// <summary>懒启动后台 flusher（首个脏字节过阈值时）——周期 50ms 轮询 + 阈值唤醒。</summary>
    private void EnsureFlusher()
    {
        if (_flusherStop || _pageBudget <= 0 || _readOnly || _flusher is not null) return;
        if (Interlocked.CompareExchange(ref _flusherGate, 1, 0) != 0) return;
        _flusher = new Thread(FlusherLoop) { IsBackground = true, Name = "tier-raw-flusher" };
        _flusher.Start();
    }

    /// <summary>flusher 主体：排干不 fsync（持久化屏障由提交序/显式 Flush 持有——崩溃窗口语义不变）；
    /// 日志模式 = 周期 JournalCommit（记录屏障——崩溃窗口 = 阈值时间，raw-journal §4 提交点 3）+
    /// 检查点周期衰减（W3：距上次 > 30s 且结构脏 → CommitMetadata——防日志区长期高占用与重放尾变长）；
    /// 维护门闩期间跳过（静默协议——租约方对载体有独占编排）；载体异常吞掉（后台线程无消费者上下文，
    /// 错误由显式 Flush/提交路径承担）。</summary>
    private void FlusherLoop()
    {
        var lastCheckpoint = DateTime.UtcNow;
        while (!_flusherStop)
        {
            _flushWake.WaitOne(50);
            if (_flusherStop) return;
            if (_maintenance.IsUnderMaintenance) { Thread.Sleep(10); continue; }
            try
            {
                lock (MetadataLock)
                {
                    TryFreeRetiredLocked();   // D1b：周期推进安全批次回收（无新分配路径时不积压）
                    // W3 检查点衰减：30s 无检查点且结构/时间戳脏 → 周期检查点
                    //（镜像含最新态——重放尾有界；JournalCommit 内 75% 衰减仍为快速路径）
                    if (_journalOn && (DateTime.UtcNow - lastCheckpoint).TotalSeconds >= 30
                        && (MetadataDirty || _timestampsDirty))
                    {
                        CommitMetadata();
                        lastCheckpoint = DateTime.UtcNow;
                    }
                    else if ((DateTime.UtcNow - lastCheckpoint).TotalSeconds >= 30)
                        lastCheckpoint = DateTime.UtcNow;   // 干净窗口滑动（防连续补账）
                }
                // 排干/提交在锁外（O_DIRECT 写排干不可阻塞数据面——页门拴并发安全，W2 同款哲学）
                if (_journalOn && (Volatile.Read(ref _dirtyBytes) > 0 || _pendingRecords.Count > 0))
                    JournalCommit(holdLock: false);   // W2 两段式（记录 + 数据同屏障）
                else if (Volatile.Read(ref _dirtyBytes) > 0)
                    FlushDirtyPages(sync: false);
            }
            catch { /* 见注释——不上抛 */ }
        }
    }

    /// <summary>停止并回收 flusher（ReleaseResources 首步——先于载体句柄释放）。</summary>
    private void StopFlusher()
    {
        _flusherStop = true;
        _flushWake.Set();
        _flusher?.Join(3000);
        _flushWake.Dispose();
    }

    // ═══ 异步预读器（性能债 6——O_DIRECT 前置：自管预读顶替内核 readahead 的位置）═══
    // 形态：有界队列 + 专用线程（镜像 flusher 模式）。Advise(Sequential) 句柄读后由
    // PrefetchFollowing 把后续窗口块入队（per-handle 前沿游标去重——每块只入队一次），
    // 预读线程 GetOrLoadPage 装入（与读者共用页门拴——在途去重自动成立，预算/逐出自然遵守）。

    private readonly ConcurrentQueue<ulong> _prefetchQueue = new();
    private readonly AutoResetEvent _prefetchWake = new(false);
    private Thread? _prefetcher;
    private volatile bool _prefetcherStop;
    private int _prefetcherGate;                        // 创建竞态闸（Interlocked——EnsurePrefetcher 单射）
    private const int PrefetchQueueCap = 512;           // 背压上限（块）——尽力而为语义

    /// <summary>懒启动预读线程（首个入队时）。</summary>
    private void EnsurePrefetcher()
    {
        if (_prefetcherStop || _pageBudget <= 0 || _prefetcher is not null) return;
        if (Interlocked.CompareExchange(ref _prefetcherGate, 1, 0) != 0) return;
        _prefetcher = new Thread(PrefetcherLoop) { IsBackground = true, Name = "tier-raw-prefetcher" };
        _prefetcher.Start();
    }

    /// <summary>预读主体：排空队列装入页（维护门闩期间跳过——静默协议；载体异常吞掉——显式读路径承担错误）。</summary>
    private void PrefetcherLoop()
    {
        while (!_prefetcherStop)
        {
            _prefetchWake.WaitOne(20);
            if (_prefetcherStop) return;
            if (_maintenance.IsUnderMaintenance) { Thread.Sleep(5); continue; }
            var budget = 0;
            while (!_prefetcherStop && budget < 64 && _prefetchQueue.TryDequeue(out var block))
            {
                try
                {
                    if (_pageBudget <= 0) return;
                    // 连续 run 聚合批量装入（顺序预取 4KB/次 → run/次——与读者冷段路径同机制）
                    var runCount = 1u;
                    while (runCount < 64 && _prefetchQueue.TryPeek(out var next) && next == block + runCount)
                    {
                        if (_prefetchQueue.TryDequeue(out _)) runCount++;
                        else break;
                    }
                    LoadPageRun(block, runCount);
                    budget += (int)runCount;
                }
                catch { /* 尽力——载体错误由显式读路径承担 */ }
            }
        }
    }

    /// <summary>停止并回收预读线程（ReleaseResources——先于载体句柄释放）。</summary>
    private void StopPrefetcher()
    {
        _prefetcherStop = true;
        _prefetchWake.Set();
        _prefetcher?.Join(2000);
        _prefetchWake.Dispose();
    }

    /// <summary>
    /// 预取跟随（Advise(Sequential) 读后调用——TierVolumeFileHandle.Read）：逻辑终点 <paramref name="logicalPos"/>
    /// 所在（或之后）的 Written 区间内，把后续 <paramref name="windowBlocks"/> 个块入队。
    /// <paramref name="cursor"/> = per-handle 前沿（物理块号，ref——去重：每块只入队一次；
    /// 读落后前沿 = 顺序性破坏 → 本次不推进）。尽力而为：队列背压时停进不推进。
    /// </summary>
    internal void PrefetchFollowing(ref ulong cursor, DataSnapshot snap, long logicalPos, int windowBlocks)
    {
        if (_pageBudget <= 0 || windowBlocks <= 0 || logicalPos >= snap.LogicalLength) return;
        var x = FindExtent(snap.Extents, logicalPos);
        if (x is null)
        {
            var next = NextExtentStart(snap.Extents, logicalPos);
            if (next is null) return;
            x = FindExtent(snap.Extents, next.Value);
            if (x is null) return;
        }
        var ext = x.Value;
        if (ext.State != ExtentState.Written) return;   // unwritten 读零——无载体内容可预取
        var bs = (long)_pageSize;
        var block = ext.PhysicalBlock + (ulong)((Math.Max(logicalPos, ext.LogicalStart) - ext.LogicalStart) / bs);
        if (block < cursor) return;                     // 读落后前沿——本次不推进（随机访问形态）
        var endBlock = ext.PhysicalBlock + (ulong)(ext.Length / bs);
        var to = Math.Min(endBlock, Math.Max(block, cursor) + (ulong)windowBlocks);
        for (var b = Math.Max(block, cursor); b < to; b++)
        {
            if (_prefetchQueue.Count > PrefetchQueueCap) return;   // 背压——尽力而为（前沿不推进，下读重试）
            if (_pages.ContainsKey(b)) { cursor = b + 1; continue; }
            _prefetchQueue.Enqueue(b);
            cursor = b + 1;
        }
        EnsurePrefetcher();
        _prefetchWake.Set();
    }

    /// <summary>
    /// 冷段批量装入（性能债 6 收尾——顺序读 4KB/次载体读 → 1MB/次）：一次载体读装入连续 run，
    /// 逐页入缓存（页门拴在途去重 + LRU 预算逐出与按需装入共用机制）。
    /// </summary>
    private void LoadPageRun(ulong firstBlock, uint count)
    {
        if (_pageBudget <= 0 || count == 0) return;
        var bs = (long)_pageSize;
        var spanLen = (int)count * _pageSize;
        var buf = ArrayPool<byte>.Shared.Rent(spanLen);
        try
        {
            ReadCarrierExactly((long)(firstBlock * (ulong)bs), buf.AsSpan(0, spanLen));
            for (var i = 0u; i < count; i++)
            {
                var block = firstBlock + i;
                var page = _pages.GetOrAdd(block, _ => new Page(RentPageBuffer()));
                lock (page.Gate)
                {
                    if (page.Loaded) continue;
                    buf.AsSpan((int)i * _pageSize, _pageSize).CopyTo(page.Bytes);
                    page.Loaded = true;
                    TrackPage(block, page);
                }
            }
            MaybePressureFlush();   // 锁外（CORE-01：持页 Gate 排干 = ABBA）
        }
        catch
        {
            throw;   // 载体错误向上——显式读路径承担（与 GetOrLoadPage 同族）
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>取页（命中返回；未命中装入——在途去重 + 预算逐出）。
    /// ★ RM-12 读者重检：持门拴后验证仍是字典在册实例（逐出-归还池-重租竞态封闭——
    /// 否则读者可能拷到已归还并被他人复用的缓冲）。★ CORE-01：返回在锁外——
    /// 调用方持页门拴拷贝前必须再次验证在册（返回后到调用方拿锁之间的逐出窗口）。</summary>
    private Page GetOrLoadPage(ulong block)
    {
        if (_pageBudget == 0)
            throw new InvalidOperationException("页缓存禁用（直达档）——不应抵达此路径");
        while (true)
        {
            var page = _pages.GetOrAdd(block, _ => new Page(RentPageBuffer()));
            var inRegistry = false;
            lock (page.Gate)
            {
                if (_pages.TryGetValue(block, out var current) && ReferenceEquals(current, page))
                {
                    inRegistry = true;
                    if (!page.Loaded)
                    {
                        ReadCarrierExactly((long)(block * (ulong)_pageSize), page.Bytes.AsSpan(0, _pageSize));
                        page.Loaded = true;
                        TrackPage(block, page);
                    }
                }
            }
            if (!inRegistry)
                continue;   // 装入前被逐出（或已换新实例）——锁外重走
            MaybePressureFlush();   // 锁外（CORE-01：持页 Gate 排干 = ABBA）
            return page;
        }
    }

    /// <summary>入 LRU + 字节计数（幂等——Tracked 防双计数：重复 Store/Load 不重复计）。调用方持页门拴。
    /// ★ CORE-01：本方法<b>不</b>排干（旧实现持拴调 FlushDirtyPages = 与并发排干者 ABBA 死锁）——
    /// 压力排干由调用方出锁后经 <see cref="MaybePressureFlush"/> 触发（best-effort）。</summary>
    private void TrackPage(ulong block, Page page)
    {
        if (page.Tracked) return;
        page.Tracked = true;
        _lru.Enqueue(block);
        Interlocked.Add(ref _pageBytes, _pageSize);
        // 压力策略（滞后阈值防 O(n²)）：脏字节 ≥ 阈值才整体排干（追加流物理连续——大 run 写）；
        // 阈值下脏页免逐出（有界超额 ≤ 阈值）——排干后页干净，逐出零成本
        while (Volatile.Read(ref _pageBytes) > _pageBudget && _lru.TryDequeue(out var victim))
        {
            if (!_pages.TryGetValue(victim, out var check)) continue;
            lock (check.Gate)
            {
                if (check.Dirty && Volatile.Read(ref _dirtyBytes) < FlushThresholdBytes)
                    return;   // 阈值下的脏页保留（等待批量排干）——LRU 队列消费尽，超额有界
            }
            if (_pages.TryRemove(victim, out var removed))
            {
                lock (removed.Gate)
                {
                    if (removed.Tracked)
                    {
                        Interlocked.Add(ref _pageBytes, -_pageSize);
                        removed.Tracked = false;
                    }
                    if (removed.Dirty)
                    {
                        WriteCarrier((long)(victim * (ulong)_pageSize), removed.Bytes.AsSpan(0, _pageSize));   // 排干后仍脏——单页兜底
                        Interlocked.Add(ref _dirtyBytes, -_pageSize);
                        removed.Dirty = false;
                        _dirtyPages.TryRemove(victim, out _);
                    }
                    ReturnPageBuffer(removed.Bytes);   // D4：缓冲归还池（逐出后页无引用者——数据面经 fs 锁串行）
                }
            }
        }
    }

    /// <summary>锁外压力排干（best-effort）：预算超限且脏字节 ≥ 阈值 → 批量合并排干。
    /// ★ CORE-01 契约：<b>禁止</b>在持有任何页 Gate 时调用——<see cref="WritePagesCoalesced"/>
    /// 会按序锁各脏页 Gate，持拴调用与另一并发持拴排干者构成 ABBA 死锁。
    /// 失败吞掉（排干是优化——错误由显式 Flush/提交路径承担；后台 flusher 兜底重试）。</summary>
    private void MaybePressureFlush()
    {
        if (Volatile.Read(ref _pageBytes) <= _pageBudget || Volatile.Read(ref _dirtyBytes) < FlushThresholdBytes)
            return;
        try { FlushDirtyPages(sync: false); }
        catch { /* best-effort——见注释 */ }
    }

    /// <summary>驻页并标脏（写回路径——数据入缓存不触载体，Flush/逐出/提交序排干）。</summary>
    private void StorePage(ulong block, ReadOnlySpan<byte> blockData)
    {
        while (true)
        {
            var page = _pages.GetOrAdd(block, _ => new Page(RentPageBuffer()));
            lock (page.Gate)
            {
                // ★ RM-12 重检：GetOrAdd 后锁前可能已被逐出（逐出者锁内还缓冲）——拷贝前必须验证在册
                if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, page))
                    continue;   // 逐出竞态——重走
                blockData.CopyTo(page.Bytes);
                page.Loaded = true;
                if (!page.Dirty)
                {
                    Interlocked.Add(ref _dirtyBytes, _pageSize);
                    _dirtyPages[block] = page;
                }
                page.Dirty = true;
                TrackPage(block, page);
                break;
            }
        }
        MaybePressureFlush();   // 锁外（CORE-01）
        if (Volatile.Read(ref _dirtyBytes) >= _backgroundDirtyThreshold)
        {
            EnsureFlusher();   // RM-02：过阈值唤醒后台排干——写路径免内联回写
            _flushWake.Set();
        }
    }

    /// <summary>
    /// 排干全部脏页（物理连续 run 合并写）。<paramref name="sync"/>=true 追加载体 fsync——
    /// 提交序首步与显式 Flush 使用（数据先于元数据）；压力排干用 false（仅入 OS 缓存——
    /// fsync 由提交序/显式 Flush 兜底，防每 MB 一次 fsync 吃光吞吐）。
    /// </summary>
    internal void FlushDirtyPages(bool sync = true)
    {
        if (_dirtyPages.IsEmpty)
        {
            if (sync) JournalBarrier();   // 无脏页仍需屏障（fsync 语义——写绕数据在内核缓存；写穿档 = 写穿完成即达盘）
            return;
        }
        var dirty = _dirtyPages.Select(kv => (kv.Key, kv.Value)).ToList();
        _dirtyPages.Clear();
        WritePagesCoalesced(dirty);
        if (sync) JournalBarrier();
    }

    /// <summary>按物理连续 run 合并写页组（写后清脏标）。分块（RM-02a）：单块 ≤256KB——
    /// 池内租用（预算级整段 run 的 Rent 会超出 ArrayPool 1MB 桶 = 池外新分配 + 零初始化 + 双倍拷贝）。</summary>
    private void WritePagesCoalesced(List<(ulong Block, Page Page)> pages)
    {
        pages.Sort((a, b) => a.Block.CompareTo(b.Block));
        var chunkPages = Math.Max(1, (64 * 1024) / _pageSize);   // 64KB 分块——O_DIRECT 写甜点（256KB/1MB 段灾难性慢，实测）
        var chunkBuf = ArrayPool<byte>.Shared.Rent(chunkPages * _pageSize);
        try
        {
            var chunkSpan = chunkBuf.AsSpan(0, chunkPages * _pageSize);
            var i = 0;
            while (i < pages.Count)
            {
                var runStart = i;
                while (i + 1 < pages.Count && pages[i + 1].Block == pages[i].Block + 1) i++;
                var runLen = i - runStart + 1;
                for (var k = 0; k < runLen; k += chunkPages)
                {
                    var take = Math.Min(chunkPages, runLen - k);
                    long gapLo = 0, gapHi = 0;   // 逐出缺口位图（chunk ≤ 128 页全覆盖）
                    for (var j = 0; j < take; j++)
                    {
                        var pg = pages[runStart + k + j].Page;
                        var block = pages[runStart + k + j].Block;
                        lock (pg.Gate)
                        {
                            // ★ RM-12 竞态封闭：采集后页可能已被并发逐出者 TryRemove + 锁内还缓冲——
                            // 拷贝前验证仍在册；不在册 = 逐出者已写盘并清计数，记缺口跳过（不写垃圾）
                            if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, pg))
                            {
                                if (j < 64) gapLo |= 1L << j;
                                else gapHi |= 1L << (j - 64);
                                continue;
                            }
                            pg.Bytes.AsSpan(0, _pageSize).CopyTo(chunkSpan.Slice(j * _pageSize, _pageSize));
                            if (pg.Dirty) Interlocked.Add(ref _dirtyBytes, -_pageSize);
                            pg.Dirty = false;
                        }
                    }
                    var startBlock = pages[runStart + k].Block;
                    if (gapLo == 0 && gapHi == 0)
                    {
                        WriteCarrier((long)(startBlock * (ulong)_pageSize), chunkSpan.Slice(0, take * _pageSize));
                    }
                    else
                    {
                        // 稀有路径（逐出与排干并发）：按连续无缺口段写（缺口页由逐出者写过）
                        var segStart = 0;
                        for (var j = 0; j <= take; j++)
                        {
                            var gap = j < take && ((j < 64 ? gapLo & (1L << j) : gapHi & (1L << (j - 64))) != 0);
                            if (j < take && !gap) continue;
                            if (j > segStart)
                                WriteCarrier((long)((startBlock + (ulong)segStart) * (ulong)_pageSize),
                                    chunkSpan.Slice(segStart * _pageSize, (j - segStart) * _pageSize));
                            segStart = j + 1;
                        }
                    }
                }
                i++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunkBuf);
        }
    }

    /// <summary>写后同步驻留页（写直通——缓存与载体一致）。</summary>
    private void UpdatePage(ulong block, ReadOnlySpan<byte> blockData)
    {
        if (_pageBudget == 0) return;
        while (true)
        {
            if (!_pages.TryGetValue(block, out var page)) return;
            lock (page.Gate)
            {
                // ★ RM-12 重检：TryGetValue 后锁前可能已被逐出（逐出者锁内还缓冲）——拷贝前必须验证在册
                if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, page))
                    continue;   // 逐出竞态——重走
                blockData.CopyTo(page.Bytes);
                if (!page.Loaded)
                {
                    page.Loaded = true;
                    TrackPage(block, page);
                }
                break;
            }
        }
        MaybePressureFlush();   // 锁外（CORE-01）
    }

    // ═══════════════ 文件数据面（区间三态路由）═══════════════

    /// <summary>读快照（RM-12）：锁内捕获的不可变视图（Extents 列表 CoW + 逻辑长度）——锁外读安全。</summary>
    internal readonly record struct DataSnapshot(List<Extent> Extents, long LogicalLength);

    /// <summary>捕获读快照（fs 锁内调用；引用捕获 + CoW 保证快照不可变）。</summary>
    internal DataSnapshot CaptureSnapshot(Entry e) => new(e.Extents, e.LogicalLength);

    /// <summary>文件数据读（Entry 形态——fs 锁内内部调用方）：快照化后走锁外路径。</summary>
    internal int ReadData(Entry e, long offset, Span<byte> destination, bool direct = false, bool streaming = false)
        => ReadData(new DataSnapshot(e.Extents, e.LogicalLength), offset, destination, direct, streaming);

    /// <summary>文件数据读（快照形态——RM-12 锁外）：Written→块读；Unwritten/洞→零（§3.2 三态语义）。</summary>
    internal int ReadData(DataSnapshot snap, long offset, Span<byte> destination, bool direct = false, bool streaming = false)
    {
        var remaining = (int)Math.Min(destination.Length, Math.Max(0, snap.LogicalLength - offset));
        var done = 0;
        while (done < remaining)
        {
            var pos = offset + done;
            if (FindExtent(snap.Extents, pos) is not { } hit)
            {
                var next = NextExtentStart(snap.Extents, pos) ?? snap.LogicalLength;
                var zeroLen = (int)Math.Min(remaining - done, next - pos);
                destination.Slice(done, zeroLen).Clear();
                done += zeroLen;
            }
            else
            {
                var len = (int)Math.Min(remaining - done, hit.LogicalEnd - pos);
                ReadPhysical(hit, pos, destination.Slice(done, len), direct, streaming);
                done += len;
            }
        }
        return done;
    }


    /// <summary>物理读：区间内逻辑偏移 → 物理块序列（Unwritten 读零——fallocate 语义）。
    /// <paramref name="streaming"/> = 纯流式读（Advise(Sequential)）：载体直读不驻留自管缓存、
    /// 无页机制开销——排干前置由调用方保证（TierVolumeFileHandle.Read 读前一次排干）。</summary>
    private void ReadPhysical(Extent x, long logicalPos, Span<byte> dest, bool direct, bool streaming = false)
    {
        var bs = (long)_pageSize;
        var block = x.PhysicalBlock + (ulong)((logicalPos - x.LogicalStart) / bs);
        var inBlock = (int)((logicalPos - x.LogicalStart) % bs);
        if (direct && !_dirtyPages.IsEmpty)
            FlushDirtyPages(sync: false);   // B2（O_DIRECT 纪律）：直达读前排干脏页——否则读到载体滞后数据；粗粒度全排干（混合访问罕见，排干恒安全）
        var done = 0;
        while (done < dest.Length)
        {
            var take = Math.Min(dest.Length - done, _pageSize - inBlock);
            if (x.State == ExtentState.Unwritten)
            {
                dest.Slice(done, take).Clear();
            }
            else if (_pageBudget > 0 && !direct && !streaming)
            {
                if (_pages.ContainsKey(block))
                {
                    var page = GetOrLoadPage(block);   // RM-12 重检路径——命中页拷贝
                    lock (page.Gate)
                    {
                        // ★ RM-12 竞态封闭：返回后到本锁之间的逐出窗口——拷前验证仍在册，否则重走
                        if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, page))
                            continue;
                        page.Bytes.AsSpan(inBlock, take).CopyTo(dest.Slice(done, take));
                    }
                }
                else if (dest.Length - done >= 16 * _pageSize)
                {
                    // 读绕（流式冷读）：大段直读不驻留——页机制开销（64 页 × 门锁/拷贝 ≈ 200μs/256KB）
                    // 会吃掉顺序读吞吐；缓存填充归预读线程（重复访问走命中路径）
                    var extentEndBlock = x.PhysicalBlock + (ulong)(x.Length / (long)_pageSize);
                    var runBytes = take;
                    var runBlocks = (ulong)1;
                    while (done + runBytes < dest.Length && block + runBlocks < extentEndBlock)
                    {
                        runBytes += _pageSize;
                        runBlocks++;
                    }
                    ReadCarrierExactly((long)(block * (ulong)_pageSize) + inBlock,
                        dest.Slice(done, Math.Min(runBytes, dest.Length - done)));
                    done += Math.Min(runBytes, dest.Length - done);
                    block += runBlocks;
                    inBlock = 0;
                    continue;
                }
                else
                {
                    // 小段冷读：批量装入 + 页拷贝（随机/小读路径）
                    var extentEndBlock = x.PhysicalBlock + (ulong)(x.Length / (long)_pageSize);
                    var loadBlocks = (uint)Math.Min(64,
                        Math.Min(extentEndBlock - block, (ulong)((dest.Length - done + _pageSize - 1) / _pageSize)));
                    LoadPageRun(block, loadBlocks);
                    var page = GetOrLoadPage(block);
                    lock (page.Gate)
                    {
                        // ★ RM-12 竞态封闭：返回后到本锁之间的逐出窗口——拷前验证仍在册，否则重走
                        if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, page))
                            continue;
                        page.Bytes.AsSpan(inBlock, take).CopyTo(dest.Slice(done, take));
                    }
                }
            }
            else
            {
                // 直达/流式档：连续块合并读（RM-16 复测修复——4MB 顺序读此前 = 1024 次 4KB 载体读，
                // 冷顺序读落后 Disk 25%；整段一次载体读后与 Disk 同 syscall 形态）。
                // 流式（Advise(Sequential)）不驻留自管缓存（OS 缓存服务重复扫描）；
                // 直达档优先 O_DIRECT 读通道（RM-28——真 DIO 对齐 Disk NoBuffering 形态），
                // DIO 不可用回退缓冲读 + DONTNEED 扫描纪律（直达读干净页弃之无写回代价）。
                var extentEndBlock = x.PhysicalBlock + (ulong)(x.Length / (long)_pageSize);
                var runBytes = take;
                var runBlocks = (ulong)1;
                while (done + runBytes < dest.Length && block + runBlocks < extentEndBlock)
                {
                    runBytes += _pageSize;
                    runBlocks++;
                }
                var readOffset = (long)(block * (ulong)_pageSize) + inBlock;
                var readLen = (int)Math.Min(runBytes, dest.Length - done);
                if (direct && TryReadCarrierDio(readOffset, dest.Slice(done, readLen)))
                {
                    done += readLen;
                    block += runBlocks;
                    inBlock = 0;
                    continue;
                }
                ReadCarrierExactly(readOffset, dest.Slice(done, readLen));
                if (direct)
                    DropCarrierCache(readOffset, readLen);   // DONTNEED 扫描纪律（DIO 回退路径）
                done += readLen;
                block += runBlocks;
                inBlock = 0;
                continue;
            }
            done += take;
            inBlock = 0;
            block++;
        }
    }

    /// <summary>
    /// 文件数据写（v1 形态——低频路径用：ShiftData/Collapse/Insert 等元数据操作锁内调用，
    /// 直接改在档列表；不适用写计划协议——调用方须持 MetadataLock，同文件串行由该锁承担）。
    /// 主路径（TierVolumeFileHandle.Write/Append）走 <see cref="WriteDataPlanned"/>（三段式锁外化）。
    /// </summary>
    internal void WriteData(Entry e, long offset, ReadOnlySpan<byte> source, bool direct = false)
    {
        var done = 0;
        var writeEnd = offset + source.Length;   // 日志记录 newLen 穿线（extent 记录携带）
        var metaChanged = false;   // 区间结构变更（MetadataDirty 判据——命中写不置）
        while (done < source.Length)
        {
            var pos = offset + done;
            Extent hit;
            var found = FindExtent(e.Extents, pos);
            var segTake = (int)Math.Min(source.Length - done,
                found is { } f ? f.LogicalEnd - pos : long.MaxValue);
            // ★ V2 §1.1：命中冻结块 → CoW（原地覆写毁快照——冻结块是快照数据物源；同 WriteDataPlanned 纪律）
            if (found is { } fx && !ExtentHitFrozen(fx, pos, segTake))
            {
                hit = fx;
            }
            else
            {
                var bs = (long)_pageSize;
                var coverEnd = RoundUp(Math.Min(offset + source.Length, NextExtentStart(e.Extents, pos) ?? long.MaxValue), bs);
                metaChanged = true;
                e.Extents = EnsureExtentCovering(e, e.Extents, RoundDown(pos, bs), coverEnd - RoundDown(pos, bs), writeEnd, direct, pos);
                hit = FindExtent(e.Extents, pos)!.Value;
            }
            var take = (int)Math.Min(source.Length - done, hit.LogicalEnd - pos);
            WritePhysical(e, hit, pos, source.Slice(done, take), direct, out var conv);
            if (conv is { } c)
            {
                var bs = (long)_pageSize;
                var cStart = c.X.LogicalStart + (long)(c.FirstTouched - c.X.PhysicalBlock) * bs;
                var cEnd = c.X.LogicalStart + (long)(c.LastTouched + 1 - c.X.PhysicalBlock) * bs;
                e.Extents = ConvertExtentRange(e, e.Extents, c.X, cStart, cEnd);
            }
            done += take;
        }
        var grew = offset + source.Length > e.LogicalLength;
        e.LogicalLength = Math.Max(e.LogicalLength, offset + source.Length);
        if (grew)
        {
            // D10：位置写增长 EOF——追加预留权威单调跟进（否则后续 Append 从陈旧游标预留 → 覆写既有数据）。
            // CAS 单调推进：在途 Append 预留已在更高值时不动（并发混合写属调用方纪律，不覆盖其预留）。
            if (_appendCursors.TryGetValue(e.Path, out var cursor))
            {
                var newLen = e.LogicalLength;
                while (true)
                {
                    var cur = Volatile.Read(ref cursor.Value);
                    if (cur >= newLen) break;
                    if (Interlocked.CompareExchange(ref cursor.Value, newLen, cur) == cur) break;
                }
            }
        }
        if (metaChanged || grew)
            MetadataDirty = true;   // 区间变更/长度增长才置（恢复可见性判据——命中写不触发检查点）
        TouchModified(e);
    }

    /// <summary>
    /// ★ 文件数据写（写计划协议——CORE-02 主路径 + §2.1 写并发档）：三阶段锁外化。
    /// ① 规划段（WriteGate + MetadataLock）：快照 → 区间决策/分配/日志发射 → 构造工作列表（不发布）+ 零基需求 + 变更窗口；
    /// ② 数据段（Serial 档 = WriteGate 内全串行；Parallel 档 = 无 WriteGate、锁外 + 写者计数钉块——同文件不相交区间并发）；
    /// ③ 提交段（WriteGate + MetadataLock）：合并发布（替换窗口——并发计划互不覆盖）+ unwritten 转换 + 推长度 + 游标 + 脏标。
    /// ★ 不变量：发布在数据段之后（读者见新区间 ⟹ 数据已在载体/脏页）；崩溃窗口与 v1 同构
    /// （分配/记录在规划段、数据写后屏障——记录未过屏障 = 位图恢复未分配，无泄漏）。
    /// ★ 并发契约（§2.1 Parallel 档）：不相交区间写完全并行；重叠写 = 数据内容未指定、结构不损坏
    /// （合并提交锁内裁切 + epoch 回收——无双重释放无泄漏）。Serial 档 = 同文件强序全串行（现状行为，缺省）。
    /// 不同文件完全并行；锁序 WriteGate → MetadataLock 单向；数据段不持锁（页门拴并发安全）。
    /// </summary>
    internal void WriteDataPlanned(Entry e, long offset, ReadOnlySpan<byte> source, bool direct = false)
    {
        if (source.IsEmpty) return;
        var parallel = _parallelWrites;   // §2.1 卷级档（实测判定门两极——快速载体 Serial 占优、慢载体 Parallel 占优）
        var bs = (long)_pageSize;
        var writeEnd = offset + source.Length;
        // ★ 惰性分配（热路径零 List 分配）：zeroOps 仅"分配路径"（洞写/覆盖重分配）产生；
        // converts 仅 unwritten 写产生；plan 单段特判（首段局部变量，第二段起才建 List）——
        // 命中覆写热路径 = 单段 + 无 zeroOps/converts = 规划/数据段全程零分配
        List<(ulong FirstBlock, ulong LastBlock, long SpanStart, long SpanEnd)>? zeroOps = null;
        List<(Extent X, ulong FirstTouched, ulong LastTouched)>? converts = null;
        (Extent Hit, long Pos, int Take) plan0 = default;
        List<(Extent Hit, long Pos, int Take)>? plan = null;
        List<Extent> working = null!;
        List<Extent> baseRef = null!;   // §2.1：规划起点的在档列表（并发重叠裁切的释放判据——防双重释放）
        var metaChanged = false;   // 区间结构变更（MetadataDirty 判据——命中写不置）
        var anyUnwritten = false;  // 数据段后需 unwritten 转换
        var grew = false;
        long mutStart = long.MaxValue, mutEnd = long.MinValue;   // §2.1：替换窗口（半开——合并发布判据）

        // ── ① 规划段（WriteGate + MetadataLock 内——微秒级：纯内存 + 分配 + 记录发射）──
        using (var gate = SpinLockScope.Enter(ref e.WriteGate))
        {
            lock (MetadataLock)
            {
                // working 直接引用在档列表（CoW 由 EnsureExtentCovering 按需做——追加主路径零复制）；
                // 提交段才合并发布（I4/I1——读者见完整态）
                working = e.Extents;
                baseRef = working;
                grew = offset + source.Length > e.LogicalLength;
                var done = 0;
                while (done < source.Length)
                {
                    var pos = offset + done;
                    Extent hit;
                    var found = FindExtent(working, pos);
                    var segTake = (int)Math.Min(source.Length - done,
                        found is { } f ? f.LogicalEnd - pos : long.MaxValue);
                    // ★ V2 §1.1：命中冻结块 → CoW 路径（原地覆写会毁快照读面——冻结块是快照数据的物源）。
                    // 覆写快照引用块 = 分配新块 + 旧块钉住（与 qcow2 refcount 触发 COW 同族；
                    // 放大 = 每冻结块每快照生命周期一次重分配，判定门 1 实测）。
                    if (found is { } fx && !ExtentHitFrozen(fx, pos, segTake))
                    {
                        hit = fx;
                    }
                    else
                    {
                        zeroOps ??= [];
                        metaChanged = true;
                        var coverEnd = RoundUp(Math.Min(offset + source.Length, NextExtentStart(working, pos) ?? long.MaxValue), bs);
                        working = EnsureExtentCovering(e, working, RoundDown(pos, bs), coverEnd - RoundDown(pos, bs),
                            writeEnd, direct, pos, zeroOps, out var ms, out var me);
                        ExtendMut(ref mutStart, ref mutEnd, ms, me);
                        hit = FindExtent(working, pos)!.Value;
                    }
                    if (hit.State == ExtentState.Unwritten) anyUnwritten = true;
                    var take = (int)Math.Min(source.Length - done, hit.LogicalEnd - pos);
                    if (plan is null && done == 0)
                        plan0 = (hit, pos, take);   // 单段快路径——零分配
                    else
                        (plan ??= [plan0]).Add((hit, pos, take));
                    done += take;
                }
            }
            if (!parallel)
            {
                // Serial 档（缺省——现状行为）：数据段在 gate 内——同文件全串行（强序、零争用）
                ExecuteWriteDataSection(e, offset, source, direct, zeroOps, plan0, plan, ref converts,
                    baseRef, working, mutStart, mutEnd, ref metaChanged);
                if (metaChanged || grew || anyUnwritten)
                    lock (MetadataLock)
                        PublishPlanMergeLocked(e, baseRef, working, mutStart, mutEnd, grew, writeEnd, converts);
                TouchModified(e);   // mtime 锁外更新（8B 原子写 + bool 原子——v1 每写语义）
                return;
            }
        }

        // ── ②/③ Parallel 档（V2 §2.1）：数据段无 WriteGate、锁外——同文件不相交区间并发──
        ExecuteWriteDataSection(e, offset, source, direct, zeroOps, plan0, plan, ref converts,
            baseRef, working, mutStart, mutEnd, ref metaChanged);

        // ── 提交段（WriteGate + MetadataLock 内——微秒级）──
        // ★ 纯命中写（无区间变更/无长度增长/无 unwritten 转换）跳过提交段——省 1 次锁 + 置位
        //（命中覆写热路径；mtime 仍锁外更新——原子字段，v1 每写语义保持）
        if (metaChanged || grew || anyUnwritten)
        {
            using (SpinLockScope.Enter(ref e.WriteGate))
            {
                lock (MetadataLock)
                    PublishPlanMergeLocked(e, baseRef, working, mutStart, mutEnd, grew, writeEnd, converts);
            }
        }
        TouchModified(e);
    }

    /// <summary>数据段执行（Serial/Parallel 两档共用）——零基先行、数据后写（v1 顺序）；
    /// per-Entry 在途写者计数钉块（删除/截断/打洞锁内自旋等归零；写者数据段不碰锁，无死锁环）。
    /// 失败语义：先减计数再补发布（防互等）——区间补发布（含已写部分数据，块随区间有主），
    /// 长度不推（v1 同）；异常照抛。</summary>
    private void ExecuteWriteDataSection(Entry e, long offset, ReadOnlySpan<byte> source, bool direct,
        List<(ulong FirstBlock, ulong LastBlock, long SpanStart, long SpanEnd)>? zeroOps,
        (Extent Hit, long Pos, int Take) plan0, List<(Extent Hit, long Pos, int Take)>? plan,
        ref List<(Extent X, ulong FirstTouched, ulong LastTouched)>? converts,
        List<Extent> baseRef, List<Extent> working, long mutStart, long mutEnd, ref bool metaChanged)
    {
        Interlocked.Increment(ref e.WritersInFlight);
        try
        {
            if (zeroOps is not null)
                foreach (var (first, last, sStart, sEnd) in zeroOps)
                    ZeroPartialWriteBlocks(first, last, sStart, sEnd, direct);
            if (plan is null)
            {
                WritePhysical(e, plan0.Hit, plan0.Pos, source.Slice((int)(plan0.Pos - offset), plan0.Take), direct, out var conv);
                if (conv is { } c)
                    converts = [(c.X, c.FirstTouched, c.LastTouched)];
            }
            else
            {
                foreach (var (hit, pos, take) in plan)
                {
                    WritePhysical(e, hit, pos, source.Slice((int)(pos - offset), take), direct, out var conv);
                    if (conv is { } c)
                        (converts ??= []).Add((c.X, c.FirstTouched, c.LastTouched));
                }
            }
        }
        catch
        {
            // ★ 先清写者计数（删除的等待判据）再补发布——防"删除持锁自旋 vs 本线程补发布等锁"互等
            Interlocked.Decrement(ref e.WritersInFlight);
            if (metaChanged)
                lock (MetadataLock)
                    PublishPlanMergeLocked(e, baseRef, working, mutStart, mutEnd, grew: false, targetLength: 0, converts: null);
            throw;
        }
        Interlocked.Decrement(ref e.WritersInFlight);
    }

    /// <summary>§2.1 合并发布（MetadataLock 内调用）：本计划的区间变更 delta 与当前在档列表合并——
    /// 替换窗口 [mutStart, mutEnd)：窗口内当前区间移除、工作区间并入（并发重叠裁切——非规划起点的
    /// 被替区间块经 epoch 回收，规划起点区间块已在规划段释放——防双重释放）；窗口外（不相交计划的
    /// 已提交变更）原样保留。整体交换发布（I1——读者完整旧态或完整新态）。纯 unwritten 转换与
    /// 结构变更同走窗口合并（并发提交互不覆盖）。无窗口（纯 grew——理论不可达，防御）= 整表交换。</summary>
    private void PublishPlanMergeLocked(Entry e, List<Extent> baseRef, List<Extent> working,
        long mutStart, long mutEnd, bool grew, long targetLength,
        List<(Extent X, ulong FirstTouched, ulong LastTouched)>? converts)
    {
        var bs = (long)_pageSize;
        if (converts is not null)
            foreach (var (x, firstTouched, lastTouched) in converts)
            {
                var cStart = x.LogicalStart + (long)(firstTouched - x.PhysicalBlock) * bs;
                var cEnd = x.LogicalStart + (long)(lastTouched + 1 - x.PhysicalBlock) * bs;
                working = ConvertExtentRange(e, working, x, cStart, cEnd);
                ExtendMut(ref mutStart, ref mutEnd, cStart, cEnd);
            }
        if (mutStart < mutEnd)
        {
            // ★ 快道：窗口内无并发计划的已提交变更（Serial 档恒成立）→ 整表交换（O(1)——旧行为一致）
            var concurrent = false;
            foreach (var x in e.Extents)
            {
                if (x.LogicalEnd <= mutStart || x.LogicalStart >= mutEnd) continue;
                if (!baseRef.Contains(x)) { concurrent = true; break; }
            }
            if (!concurrent)
            {
                e.Extents = working;
            }
            else
            {
                // ★ 添加窗口 = 替换窗口 ∪ 被移除区间的范围（替换区间在 working 中被切分成外侧余段 +
                // 新段——外侧余段也在被移除区间范围内，须一并并入，否则预分配/覆盖的未触及部分丢失）
                var addStart = mutStart;
                var addEnd = mutEnd;
                var merged = new List<Extent>(e.Extents.Count + 4);
                foreach (var x in e.Extents)
                {
                    if (x.LogicalEnd <= mutStart || x.LogicalStart >= mutEnd)
                    {
                        merged.Add(x);
                        continue;
                    }
                    // 窗口内移除：并发重叠裁切——非规划起点的区间（并发计划已提交的）块释放
                    //（规划起点区间块已在规划段释放——防双重释放；disjoint 契约路径零移除零成本）
                    if (!baseRef.Contains(x))
                        FreePhysical(e, x.LogicalStart, x.LogicalEnd, x, bs);
                    addStart = Math.Min(addStart, x.LogicalStart);
                    addEnd = Math.Max(addEnd, x.LogicalEnd);
                }
                foreach (var x in working)
                    if (x.LogicalEnd > addStart && x.LogicalStart < addEnd)
                        merged.Add(x);
                merged.Sort((a, b) => a.LogicalStart.CompareTo(b.LogicalStart));
                e.Extents = merged;   // 整体交换发布（I1：锁外读者持旧列表——完整旧态或完整新态）
            }
        }
        else
        {
            e.Extents = working;   // 无窗口变更（纯 grew——防御分支）——工作列表即完整新表
        }
        if (grew)
        {
            e.LogicalLength = Math.Max(e.LogicalLength, targetLength);
            // D10：位置写增长 EOF——追加预留权威单调跟进（并发追加预留已在更高值时不动）
            if (_appendCursors.TryGetValue(e.Path, out var cursor))
            {
                var newLen = e.LogicalLength;
                while (true)
                {
                    var cur = Volatile.Read(ref cursor.Value);
                    if (cur >= newLen) break;
                    if (Interlocked.CompareExchange(ref cursor.Value, newLen, cur) == cur) break;
                }
            }
        }
        MetadataDirty = true;
    }

    /// <summary>
    /// 物理写（数据面——写计划协议的数据段锁外执行；v1 低频路径锁内调用）。
    /// <paramref name="convert"/> = 输出：unwritten 转换需求（触及块范围——由调用方在提交段
    /// 经 <see cref="ConvertExtentRange"/> 执行；null = 无转换需求）。
    /// </summary>
    private void WritePhysical(Entry e, Extent x, long logicalPos, ReadOnlySpan<byte> src, bool direct,
        out (Extent X, ulong FirstTouched, ulong LastTouched)? convert)
    {
        var bs = (long)_pageSize;
        var block = x.PhysicalBlock + (ulong)((logicalPos - x.LogicalStart) / bs);
        var inBlock = (int)((logicalPos - x.LogicalStart) % bs);
        var done = 0;
        var touched = false;                 // B1 族修复（unwritten 转换）：跟踪本写实际触及的块——
        ulong firstTouched = 0, lastTouched = 0;   // 转换只覆盖触及块，未触及块保持 Unwritten（读零不依赖载体内容）
        while (done < src.Length)
        {
            if (!touched) { firstTouched = block; touched = true; }
            lastTouched = block;
            if (inBlock == 0 && src.Length - done >= _pageSize)
            {
                // 连续整块合并写（物理连续的整块 run 一次载体写——追加 64K = 1 syscall 而非 16）
                var runBlocks = 1;
                while (done + (long)runBlocks * _pageSize < src.Length
                       && block + (ulong)runBlocks < x.PhysicalBlock + (ulong)(x.Length / (long)_pageSize))
                    runBlocks++;
                var runBytes = (int)Math.Min(src.Length - done, (long)runBlocks * _pageSize);
                // B1 族：整块 run 单迭代覆盖多块——lastTouched 推进到 run 末块（尾块不足整块时
                // 由后续部分路径的逐块跟踪覆盖）
                lastTouched = block + (ulong)(runBytes / _pageSize) - 1;
                if (_pageBudget > 0 && !direct)
                {
                    // 写回（14.7）：整块入脏页——命中写 ~100ns；尾段不足块经零填充 scratch 入页。
                    // 写绕（write-around，RM-02——kernel 流式写策略）：整块 run 全部未驻留 → 直落载体，
                    // 不入缓存不标脏——免三重内存拷贝（页 memcpy + 排干聚合 memcpy + 内核 copy_from_user；
                    // 追加重负载下缓冲档 = 3x 内存流量 vs 直达档 1x，是吞吐缺口的全部）。
                    // 流式写不污染缓存（POSIX_FADV_DONTNEED 同款语义）；读一致性经载体（数据即刻在盘）。
                    // ★ O_DIRECT 载体例外（性能轮）：写绕直落 = 每次小 O_DIRECT 写一次设备
                    // flush（实测 860μs/64KB）——必须入自管缓存由 flusher 批量排干摊销（写回吸收）。
                    var fullBytes = runBytes - runBytes % _pageSize;
                    if (_carrierDio)
                    {
                        for (var i = 0; i < fullBytes / _pageSize; i++)
                            StorePage(block + (ulong)i, src.Slice(done + i * _pageSize, _pageSize));
                    }
                    else
                    {
                        var anyResident = false;
                        for (var i = 0; i < fullBytes / _pageSize && !anyResident; i++)
                            if (_pages.ContainsKey(block + (ulong)i)) anyResident = true;
                        if (!anyResident && fullBytes > 0)
                            WriteCarrier((long)(block * (ulong)_pageSize), src.Slice(done, fullBytes));
                        else
                            for (var i = 0; i < fullBytes / _pageSize; i++)
                                StorePage(block + (ulong)i, src.Slice(done + i * _pageSize, _pageSize));
                    }
                    if (runBytes > fullBytes)
                    {
                        var tailBuf = RentPageBuffer();
                        try
                        {
                            var tail = tailBuf.AsSpan(0, _pageSize);
                            tail.Clear();
                            src.Slice(done + fullBytes, runBytes - fullBytes).CopyTo(tail);
                            StorePage(block + (ulong)(fullBytes / _pageSize), tail);
                        }
                        finally
                        {
                            ReturnPageBuffer(tailBuf);
                        }
                    }
                    // ★ RM-02 修复（写放大根因）：整块 run 已一次落位（写绕直落或逐块入页）——
                    // 必须推进 done/block 并 continue，否则落入下方部分块路径逐块 RMW：
                    // 每块 = 一次载体回读 + 冗余 memcpy，且下一轮快道对剩余 run 整体重写一遍
                    // （64KB 追加实测 520KB 载体写 + 64KB 回读——"3x 内存流量缺口"的全部）。
                    done += runBytes;
                    block += (ulong)((runBytes + _pageSize - 1) / _pageSize);   // ceil：含尾块
                    continue;
                }
                else
                {
                    // 直达档：只写整块（页粒度——O_DIRECT 载体对齐纪律）；不足块尾段落入下方
                    // 部分块路径（scratch RMW 全页写——对齐 ✓，弹跳兜底不进热路径）
                    var directBytes = runBytes - runBytes % _pageSize;
                    if (directBytes > 0)
                    {
                        WriteCarrier((long)(block * (ulong)_pageSize), src.Slice(done, directBytes));
                        if (_pageBudget > 0)
                            InvalidateCacheBlocks(block, (uint)(directBytes / _pageSize));   // B1（O_DIRECT 纪律）：直达整块写失效驻留页——脏内容作废（直达数据为新）
                    }
                    done += directBytes;
                    block += (ulong)(directBytes / _pageSize);
                    if (directBytes == runBytes)
                        continue;
                    // 尾段不足块：落入下方部分块路径（scratch RMW 全页写——对齐，弹跳不进热路径）
                }
            }
            var take = Math.Min(src.Length - done, _pageSize - inBlock);
            var slice = src.Slice(done, take);
            if (_pageBudget > 0 && !direct && x.State == ExtentState.Written)
            {
                var page = GetOrLoadPage(block);   // RMW（部分块经页补齐——写回：标脏不触载体）
                lock (page.Gate)
                {
                    // ★ RM-12 竞态封闭：返回后到本锁之间的逐出窗口——写前验证仍在册，否则重走（RMW 幂等）
                    if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, page))
                        continue;
                    slice.CopyTo(page.Bytes.AsSpan(inBlock, take));
                    if (!page.Dirty)
                    {
                        Interlocked.Add(ref _dirtyBytes, _pageSize);
                        _dirtyPages[block] = page;
                    }
                    page.Dirty = true;
                }
            }
            else
            {
                // 部分块且（直达档或 unwritten）：unwritten 物理块从未写过——零填充基底（读载体=垃圾）
                // scratch 池化（D4：块大小可达 1MiB——免栈溢出免堆零初始化）
                var scratchBuf = RentPageBuffer();
                try
                {
                    var scratch = scratchBuf.AsSpan(0, _pageSize);
                    scratch.Clear();
                    var residentBase = false;
                    if (_pageBudget > 0)
                    {
                        // ★ RM-12 竞态封闭：TryGetValue 后锁前可能已被逐出（逐出者锁内还缓冲）——
                        // 验证在册才拷贝；不在册 = 逐出者已把脏数据写盘 → 下方 written 分支读载体兜底
                        while (true)
                        {
                            if (!_pages.TryGetValue(block, out var resident)) break;
                            lock (resident.Gate)
                            {
                                if (!_pages.TryGetValue(block, out var current) || !ReferenceEquals(current, resident))
                                    continue;
                                resident.Bytes.AsSpan(0, _pageSize).CopyTo(scratch);   // B1b：驻留页为基底（含未排干脏数据——载体可能滞后）
                                residentBase = true;
                                break;
                            }
                        }
                    }
                    if (!residentBase && x.State == ExtentState.Written)
                        ReadCarrierExactly((long)(block * (ulong)_pageSize), scratch);
                    slice.CopyTo(scratch.Slice(inBlock, take));
                    if (_pageBudget > 0 && !direct)
                        StorePage(block, scratch);   // 写回：unwritten 零填充基底入脏页
                    else
                    {
                        WriteCarrier((long)(block * (ulong)_pageSize), scratch);
                        UpdatePage(block, scratch);
                    }
                }
                finally
                {
                    ReturnPageBuffer(scratchBuf);
                }
            }
            done += take;
            inBlock = 0;
            block++;
        }
        if (touched)
            TrackDeltaDirtyBlocks(firstTouched, lastTouched - firstTouched + 1);   // ★ V2 §1.2：增量窗口脏块（数据面随流携带）
        if (x.State == ExtentState.Unwritten && touched)
        {
            // B1 族修复：整区间转换会暴露未写块陈旧字节——只转换本写触及的块范围，
            // 未触及块保持 Unwritten（状态读零，不依赖载体内容）
            var convertStart = x.LogicalStart + (long)(firstTouched - x.PhysicalBlock) * bs;
            var convertEnd = x.LogicalStart + (long)(lastTouched + 1 - x.PhysicalBlock) * bs;
            convert = (x, firstTouched, lastTouched);
        }
        else
            convert = null;
    }

    /// <summary>unwritten → written 块粒度转换（在线/重放共用——B1 族修复）：
    /// 仅 [convertStart, convertEnd) 转 Written，两侧未触及部分保持 Unwritten（读零）。
    /// 触及块的未写残段由零基 scratch 保证为零——转换后读语义不变。
    /// ★ CoW：返回新列表（<paramref name="source"/> 不被修改——锁外读者持旧引用安全），由调用方发布。
    /// 调用方负责 MetadataDirty（写计划 = 提交段统一置位；重放路径不置）。</summary>
    private List<Extent> ConvertExtentRange(Entry e, List<Extent> source, Extent x, long convertStart, long convertEnd)
    {
        var bs = (long)_pageSize;
        var cutStart = Math.Max(x.LogicalStart, RoundDown(convertStart, bs));
        var cutEnd = Math.Min(x.LogicalEnd, RoundUp(convertEnd, bs));
        if (cutEnd <= cutStart) return source;
        var list = new List<Extent>(source.Count + 2);
        list.AddRange(source);
        list.RemoveAll(t => t.LogicalStart == x.LogicalStart && t.PhysicalBlock == x.PhysicalBlock);
        if (x.LogicalStart < cutStart)
            list.Add(x with { Length = cutStart - x.LogicalStart });
        list.Add(new Extent(cutStart, cutEnd - cutStart,
            x.PhysicalBlock + (ulong)((cutStart - x.LogicalStart) / bs), ExtentState.Written));
        if (cutEnd < x.LogicalEnd)
            list.Add(new Extent(cutEnd, x.LogicalEnd - cutEnd,
                x.PhysicalBlock + (ulong)((cutEnd - x.LogicalStart) / bs), ExtentState.Unwritten));
        list.Sort((a, b) => a.LogicalStart.CompareTo(b.LogicalStart));
        JnlExtentWritten(e.Path, x.LogicalStart, convertStart, convertEnd);
        return list;
    }

    /// <summary>块范围退出页缓存（B1 直达写失效 / Map 一致性共用）——脏页丢弃不回写
    /// （调用语义保证载体数据更新：直达写直落、Map 路径先排干）。</summary>
    private void InvalidateCacheBlocks(ulong start, uint count)
    {
        for (var i = 0UL; i < count; i++)
        {
            var block = start + i;
            if (!_pages.TryRemove(block, out var removed)) continue;
            lock (removed.Gate)
            {
                if (removed.Tracked)
                {
                    Interlocked.Add(ref _pageBytes, -_pageSize);
                    removed.Tracked = false;
                }
                if (removed.Dirty)
                {
                    Interlocked.Add(ref _dirtyBytes, -_pageSize);
                    removed.Dirty = false;
                    _dirtyPages.TryRemove(block, out _);
                }
                ReturnPageBuffer(removed.Bytes);   // D4：缓冲归还池
            }
        }
    }

    /// <summary>区间查找（二分——按 LogicalStart 有序；列表参数 = 快照/在线两用，RM-12）。</summary>
    internal static Extent? FindExtent(List<Extent> list, long logicalPos)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var x = list[mid];
            if (logicalPos < x.LogicalStart) hi = mid - 1;
            else if (logicalPos >= x.LogicalEnd) lo = mid + 1;
            else return x;
        }
        return null;
    }

    /// <summary>pos 之后的下一个区间起点（二分——同 FindExtent 序；列表参数 = 快照/在线两用）。</summary>
    private static long? NextExtentStart(List<Extent> list, long logicalPos)
    {
        int lo = 0, hi = list.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (list[mid].LogicalStart > logicalPos) hi = mid - 1;
            else lo = mid + 1;
        }
        return lo < list.Count ? list[lo].LogicalStart : null;
    }
}
