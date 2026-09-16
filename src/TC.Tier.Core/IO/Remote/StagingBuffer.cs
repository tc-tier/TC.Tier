using System.Collections.Concurrent;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Shared;

namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// staging 缓冲——页缓存的远端同构物（写句柄的写回层，B3.3）。
/// <para>★ 页粒度稀疏覆盖层：Dictionary&lt;页号, 页&gt;——<b>页存在 = 已物化</b>（写入或从旧对象按需加载）；
///   页缺失 = 未触（Flush 时走服务端回填，不落本层）。</para>
/// <para>★ 内存预算 + spill：内存页总量超 <c>memoryLimit</c> 时 LRU 淘汰到 spill 文件
///   （DiskFileSystem 自举——Core/IO 复用，零新持久化代码类）；<c>spillRoot</c> 为 null 时超限即
///   <see cref="IOError.DiskFull"/>（嵌入式无盘形态）。页字节确定性零填充（新页读零契约）。</para>
/// <para>★ 线程模型：内部锁保护（句柄层串行使用为主——锁仅防池共享下的意外并发）。</para>
/// </summary>
/// <param name="pageSize">页大小（字节，2 的幂，[4KiB, 1MiB]）——延迟加载/回填的最小粒度。</param>
/// <param name="memoryLimit">内存页总量预算（字节，超出即 spill 到 <c>spillRoot</c>；嵌入式无盘形态 <c>spillRoot</c> = null → 超限抛 <see cref="IOError.DiskFull"/>）。</param>
/// <param name="spillRoot">spill 根目录（staging 超内存预算时的本地落盘根）。null = 纯内存——超限抛 <see cref="IOError.DiskFull"/>（�嵌入式无盘部署形态）。</param>
/// <param name="spillOwner">spill 后端 fs 级共享（RemoteFileSystem 注入——磁盘=DiskFileSystem 自举 / 无盘=MemoryFileSystem）。</param>
internal sealed class StagingBuffer(int pageSize, long memoryLimit, string? spillRoot,IFileSystem? spillOwner=null) : IDisposable
{
    private sealed class Page
    {
        public byte[]? Memory;      // null = 已 spill 到盘
        public long Touch;          // LRU 时钟
        public bool Clean;          // 自最近一次成功 Flush 后未被写（增量 Flush 判定：clean 页内容 == 已持久对象）
    }

    private readonly string? _spillRoot = spillRoot;
    private readonly object _sync = new();
    private readonly Dictionary<long, Page> _pages = new();
    private readonly string _spillName = $".staging-{Guid.NewGuid():N}";
    private IFileHandle? _spillFile;
    private long _memoryBytes;
    private long _clock;
    private long _length;
    private int _disposed;

    /// <summary>spill 后端 fs 级共享（RemoteFileSystem 注入——磁盘=DiskFileSystem 自举 / 无盘=MemoryFileSystem）。</summary>
    internal IFileSystem? SpillOwner { get; } = spillOwner;

    /// <summary>当前逻辑长度。</summary>
    public long Length => Volatile.Read(ref _length);

    /// <summary>当前物化页数（测试/诊断）。</summary>
    internal int MaterializedPageCount
    {
        get { lock (_sync) return _pages.Count; }
    }

    /// <summary>内存驻留字节数（spill 压力观测）。</summary>
    internal long MemoryBytes => Volatile.Read(ref _memoryBytes);

    /// <summary>设置逻辑长度（收缩即裁掉区间外页——不可达数据不占预算）。
    /// ★ IO-01：保留非对齐尾页并清尾——原实现 `lastKeep = length/pageSize - 1` 把部分尾页整页丢弃，
    ///   页内 [pageStart, length) 的未 flush 用户写一并丢失（Flush 分类"无脏页→服务端自拷贝"或零填
    ///   上传 = 静默数据损坏）；清尾对齐 Mem TruncateNoLock 语义（truncate-extend 读零、不复活旧数据）。</summary>
    /// <param name="length">目标逻辑长度（字节，≥0；收缩即裁掉区间外页 + 非对齐尾页清尾）。</param>
    public void SetLength(long length)
    {
        lock (_sync)
        {
            if (length < _length)
            {
                var keepPages = length / pageSize;   // 完整保留页数；非对齐时 key == keepPages 的部分页保留
                var aligned = length % pageSize == 0;
                var toDrop = _pages.Where(kv => kv.Key > keepPages || (kv.Key == keepPages && aligned)).ToArray();
                foreach (var kv in toDrop)
                    DropPageNoLock(kv.Key, kv.Value);
                // 部分保留页清尾（[length, pageEnd) 不可达——防截断后扩展复活旧数据；对齐 Mem 语义）
                if (!aligned && _pages.TryGetValue(keepPages, out var partial))
                {
                    var memory = ReadPageNoLock(keepPages, partial, pageSize);   // spilled 页先回读
                    Array.Clear(memory, (int)(length % pageSize), pageSize - (int)(length % pageSize));
                    partial.Clean = false;   // 内容 ≠ 已持久对象（增量 Flush 判定基准）
                }
            }
            _length = length;
        }
    }

    /// <summary>页是否已物化。</summary>
    /// <param name="pageIndex">页号（0 基，页大小由构造参数 pageSize 决定）。</param>
    /// <returns>true = 页已物化（写入或加载过）；false = 未触（读零/Flush 服务端回填）。</returns>
    public bool IsPageMaterialized(long pageIndex)
    {
        lock (_sync) return _pages.ContainsKey(pageIndex);
    }

    /// <summary>物化一页（未物化则分配零页；已物化 no-op）——PunchHole 全覆页场景。</summary>
    /// <param name="pageIndex">页号（0 基；未物化则分配零页，已物化 no-op）。</param>
    public void EnsurePage(long pageIndex)
    {
        lock (_sync)
        {
            if (_pages.ContainsKey(pageIndex)) return;
            var page = AllocPageNoLock();
            _pages[pageIndex] = page;
            EvictOverBudgetNoLock();
        }
    }

    /// <summary>
    /// 写入（覆写语义）——未物化页按需分配（确定性零填充）。★ 调用方须先完成"补集加载"
    /// （RemoteFileHandle.MaterializeComplement）——本层不触达对象存储。
    /// </summary>
    /// <param name="offset">写入起始偏移（字节，≥0；超出当前长度即扩长）。</param>
    /// <param name="source">源数据（空则 no-op）。</param>
    public void Write(long offset, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;
        lock (_sync)
        {
            if (offset + source.Length > _length) _length = offset + source.Length;
            var pos = 0L;
            while (pos < source.Length)
            {
                var pageIdx = (offset + pos) / pageSize;
                var inPage = (int)((offset + pos) % pageSize);
                var chunk = (int)Math.Min(pageSize - inPage, source.Length - pos);
                var page = GetOrLoadPageNoLock(pageIdx);
                // ★ CORE-07：spill 页（Memory==null）必须先读盘回——旧实现直接解引用 = NRE
                //   （超预算驱逐后对同页再写确定性崩溃；Read 经 ReadPageNoLock 已处理，Write 漏了）
                var memory = ReadPageNoLock(pageIdx, page, chunk);
                source.Slice((int)pos, chunk).CopyTo(memory.AsSpan(inPage, chunk));
                page.Touch = ++_clock;
                page.Clean = false;   // 用户写路径置脏（增量 Flush 判定基准）
                pos += chunk;
            }
            EvictOverBudgetNoLock();
        }
    }

    /// <summary>
    /// 读取——已物化页取实值，未物化页取零。★ 调用方须先完成"补集加载"（对旧对象区间）——
    /// 本层对未物化区间按零返回（越 effectiveBase 的语义即零）。
    /// </summary>
    /// <param name="offset">读取起始偏移（字节，≥0）。</param>
    /// <param name="destination">接收缓冲（长度即单次最多读取字节数）。</param>
    /// <returns>实际读取的字节数（0 = offset 已达逻辑长度末尾；未物化区间读零）。</returns>
    public int Read(long offset, Span<byte> destination)
    {
        lock (_sync)
        {
            var available = _length - offset;
            if (available <= 0) return 0;
            var n = (int)Math.Min(destination.Length, available);
            var pos = 0L;
            while (pos < n)
            {
                var pageIdx = (offset + pos) / pageSize;
                var inPage = (int)((offset + pos) % pageSize);
                var chunk = (int)Math.Min(pageSize - inPage, n - pos);
                if (_pages.TryGetValue(pageIdx, out var page))
                {
                    var src = ReadPageNoLock(pageIdx, page, chunk);
                    src.AsSpan(inPage, chunk).CopyTo(destination.Slice((int)pos, chunk));
                }
                else
                {
                    destination.Slice((int)pos, chunk).Clear();
                }
                pos += chunk;
            }
            return n;
        }
    }

    /// <summary>
    /// 干净物化（补集加载路径专用）：页<b>缺失</b>时分配+填充并标记 clean（内容镜像自当前对象——
    /// 增量 Flush 不得因加载而重传）；已存在页不动（补集加载从不触达已物化页）。
    /// </summary>
    /// <param name="offset">写入起始偏移（字节，≥0）。</param>
    /// <param name="source">镜像数据（仅填充页缺失部分——已物化页不动）。</param>
    public void WriteClean(long offset, ReadOnlySpan<byte> source)
    {
        if (source.IsEmpty) return;
        lock (_sync)
        {
            if (offset + source.Length > _length) _length = offset + source.Length;
            var pos = 0L;
            while (pos < source.Length)
            {
                var pageIdx = (offset + pos) / pageSize;
                var inPage = (int)((offset + pos) % pageSize);
                var chunk = (int)Math.Min(pageSize - inPage, source.Length - pos);
                if (!_pages.ContainsKey(pageIdx))
                {
                    var page = AllocPageNoLock();
                    source.Slice((int)pos, chunk).CopyTo(page.Memory.AsSpan(inPage, chunk));
                    page.Touch = ++_clock;
                    page.Clean = true;
                    _pages[pageIdx] = page;
                }
                pos += chunk;
            }
            EvictOverBudgetNoLock();
        }
    }

    /// <summary>Flush 成功后：全部驻留页标记 clean（内容 == 已持久对象）。</summary>
    public void MarkAllClean()
    {
        lock (_sync)
        {
            foreach (var page in _pages.Values)
                page.Clean = true;
        }
    }

    /// <summary>页区间内是否存在脏页（增量 Flush 分类：无脏且在基线内 → 服务端拷贝）。</summary>
    /// <param name="firstPage">起始页号（含）。</param>
    /// <param name="lastPage">结束页号（含）。</param>
    /// <returns>true = 区间内至少一页已物化且非 clean；false = 全 clean 或未物化。</returns>
    public bool HasDirtyPage(long firstPage, long lastPage)
    {
        lock (_sync)
        {
            for (var p = firstPage; p <= lastPage; p++)
            {
                if (_pages.TryGetValue(p, out var page) && !page.Clean)
                    return true;
            }
            return false;
        }
    }

    /// <summary>读取一页到新数组（Flush 路径——物化页取实值/未物化零）。</summary>
    /// <summary>读取一段到新数组（Flush 路径——物化页取实值/未物化零）。</summary>
    /// <param name="offset">读取起始偏移（字节，≥0）。</param>
    /// <param name="length">读取字节数（输出数组长度）。</param>
    /// <returns>新分配的字节数组（长度 = length；未物化区间为零填充）。</returns>
    public byte[] ReadToArray(long offset, int length)
    {
        var buf = new byte[length];
        Read(offset, buf);
        return buf;
    }

    /// <summary>释放全部资源（spill 文件关闭 + 删除）。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IFileHandle? spillFile;
        IFileSystem? spillFs;
        string spillName;
        lock (_sync)
        {
            spillFile = _spillFile;
            spillFs = SpillOwner;
            spillName = _spillName;
            _spillFile = null;
            _pages.Clear();
            _memoryBytes = 0;
        }
        // 池外关闭语义：未 Flush 的 staging 丢弃（"未 fsync 即丢"）——spill 临时文件显式回收
        try
        {
            spillFile?.Dispose();
            if (spillFs is not null)
            {
                try { spillFs.Delete(spillName); } catch { /* 残留由 fs 级清理兜底 */ }
            }
        }
        catch
        {
            // 清理失败不掩盖主流程
        }
    }

    // ═════════════════════════════ 内部 ═════════════════════════════

    private Page AllocPageNoLock()
    {
        var page = new Page { Memory = new byte[pageSize] };   // 确定性零填充
        _memoryBytes += pageSize;
        return page;
    }

    private Page GetOrLoadPageNoLock(long pageIndex)
    {
        if (_pages.TryGetValue(pageIndex, out var page)) return page;
        page = AllocPageNoLock();
        _pages[pageIndex] = page;
        return page;
    }

    /// <summary>取一页可读内存（spilled 则从盘读回并物化为内存页）。
    /// ★ CORE-22：回读刷新时钟（旧实现保留最旧 Touch → 下一轮驱逐立即再 spill = 每次访问盘读+盘写抖动）。</summary>
    private byte[] ReadPageNoLock(long pageIndex, Page page, int hintLength)
    {
        if (page.Memory is not null) return page.Memory;
        var file = EnsureSpillFileNoLock();
        var buf = new byte[pageSize];
        var n = file.Read(pageIndex * pageSize, buf);
        if (n < pageSize) buf.AsSpan(n).Clear();   // 部分读（异常路径）补零
        page.Memory = buf;
        page.Touch = ++_clock;   // ★ CORE-22：回读即近期访问——驱逐时钟刷新
        _memoryBytes += pageSize;
        return buf;
    }

    private IFileHandle EnsureSpillFileNoLock()
    {
        if (_spillFile is not null) return _spillFile;
        if (SpillOwner is null)
            throw new FileIOException(IOError.DiskFull,
                $"staging 内存预算耗尽且未配置 spill 后端（Spill 未配置——超限即 DiskFull；budget={memoryLimit}）。",
                null, "staging-spill");
        _spillFile = SpillOwner.Open(_spillName, new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.None,
        });
        return _spillFile;
    }

    private void EvictOverBudgetNoLock()
    {
        if (_memoryBytes <= memoryLimit) return;
        // ★ CORE-22：批量驱逐——一次扫描 + 排序取最旧（旧实现每页一次全表扫描 = O(P²)；
        //   大批量写入超预算时 1GB/64KiB 页 ≈ 33M+ 次比较量级）
        // ★ IO-P4：驱逐到半水位（原回补到 memoryLimit——随后每写一页又越限再全表扫描，
        //   O(P log P)/写）。一次多驱逐一半预算，摊薄扫描频率 ~limit/2 字节的写量。
        var needBytes = _memoryBytes - memoryLimit / 2;
        var candidates = new List<(long Touch, long PageIdx, Page Page)>();
        foreach (var kv in _pages)
            if (kv.Value.Memory is not null)
                candidates.Add((kv.Value.Touch, kv.Key, kv.Value));
        if (candidates.Count <= 1) return;   // 保底一页驻留（原语义）
        candidates.Sort((a, b) => a.Touch.CompareTo(b.Touch));   // 最旧在前
        var file = EnsureSpillFileNoLock();
        var freed = 0L;
        foreach (var (_, pageIdx, page) in candidates)
        {
            if (freed >= needBytes || _pages.Count <= 1) break;
            file.Write(pageIdx * pageSize, page.Memory);
            page.Memory = null;
            _memoryBytes -= pageSize;
            freed += pageSize;
        }
    }

    private void DropPageNoLock(long pageIndex, Page page)
    {
        if (_pages.Remove(pageIndex) && page.Memory is not null)
            _memoryBytes -= pageSize;
        // spilled 页直接弃（spill 文件整体生命周期归 Dispose）
    }
}
