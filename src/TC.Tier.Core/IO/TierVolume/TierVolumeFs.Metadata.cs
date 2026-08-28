using System.Buffers.Binary;
using System.IO.Hashing;

namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 元数据 partial——条目模型（命名空间 + 区间三态 + FileExtra 内联）与镜像序列化。
/// <para>★ 内存权威 + 提交时全量镜像（§4.1 的全量检查点形态——原子翻转点不变，增量 CoW 是后续优化）。</para>
/// <para>★ FileExtra ≤1536B 内联在条目记录（≤1.5K 不值一块——布局细节偏离 §3 图，平面语义不变）。</para>
/// </summary>
public sealed partial class TierVolumeFs
{
    /// <summary>区间状态（§3.2 三态模型的在档两态；洞 = 无区间记录）。</summary>
    internal enum ExtentState : byte
    {
        /// <summary>预分配（fallocate 语义）：物理已留、读零、写时转 Written。</summary>
        Unwritten = 0,

        /// <summary>已写数据。</summary>
        Written = 1,
    }

    /// <summary>区间记录：逻辑字节区间 → 物理连续块run（块粒度对齐——首尾含部分块）。</summary>
    internal readonly record struct Extent(long LogicalStart, long Length, ulong PhysicalBlock, ExtentState State)
    {
        public long LogicalEnd => LogicalStart + Length;
    }

    /// <summary>文件/目录条目（内存权威）。</summary>
    internal sealed class Entry
    {
        public required string Path;
        public bool IsDirectory;
        public long LogicalLength;
        public byte[] Extra = [];
        public List<Extent> Extents = [];   // 按 LogicalStart 有序、互不相交（洞 = 间隙）
        public long CreatedTicks;
        public long ModifiedTicks;

        /// <summary>写计划闸（CORE-02 写路径锁外化）：同文件写串行——规划/数据/提交三段的互斥。
        /// SpinLock（无竞争 ~5ns vs Monitor ~20ns——单写者热路径；临界区纯内存微秒级，有界自旋安全）。
        /// 锁序：WriteGate → MetadataLock → 页 Gate（单向；低频元数据路径在 MetadataLock 内调
        /// v1 形态 WriteData 时经 Monitor 重入——不反向拿 WriteGate——无环）。</summary>
        internal SpinLock WriteGate;

        /// <summary>数据段在途写者计数（§2.1 Parallel 协议——由 0/1 单写者升级为计数：同文件不相交区间
        /// 写并发执行数据段；规划/提交仍经 WriteGate 串行）：删除/截断/打洞等"锁内释放块"路径的等待判据：
        /// 持 MetadataLock 自旋等归零（写者数据段锁外不碰锁——无死锁环；异常路径先减计数再补发布）。
        /// ★ 替代数据段 epoch 钉块（每写省 Resume/Suspend ~100ns）。</summary>
        internal int WritersInFlight;
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _sortedKeys = new(StringComparer.Ordinal);   // RM-11：有序键镜像（前缀查询 O(log n + subtree)——全表扫描淘汰）
    private readonly HashSet<string> _directories = new(StringComparer.Ordinal);   // 含祖先登记（mkdir -p 语义）

    // ═══════════════ 镜像序列化（提交物）═══════════════

    /// <summary>镜像序列化（RM-29 范围化收口）：条目经 _sortedKeys 有序直迭代（免每次 OrderBy——
    /// RM-11 不变量即镜像序）；PooledBufStream 增长写（免 MemoryStream.ToArray 尾拷贝）。
    /// 返回流形态——调用方按 <see cref="PooledBufStream.LengthBytes"/> 切片。</summary>
    private PooledBufStream SerializeMetadata()
    {
        var ms = new PooledBufStream(new byte[256]);
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write((uint)0x5241574D);   // 镜像 magic "RAWM"
        w.Write(TierVolumeLayoutVersion);
        w.Write((uint)_entries.Count);
        w.Write((uint)_directories.Count);
        w.Write((uint)_journalReserveBlocks.Count);   // 日志保留区（§3.9——恢复对账的可达集成员）
        foreach (var b in _journalReserveBlocks) w.Write(b);
        foreach (var d in _directories.OrderBy(x => x, StringComparer.Ordinal))
        {
            var b = System.Text.Encoding.UTF8.GetBytes(d);
            w.Write((ushort)b.Length);
            w.Write(b);
        }
        foreach (var k in _sortedKeys)   // RM-11 有序键 = 镜像序（免 OrderBy）
        {
            var e = _entries[k];
            var b = System.Text.Encoding.UTF8.GetBytes(e.Path);
            w.Write((ushort)b.Length);
            w.Write(b);
            w.Write(e.IsDirectory);
            w.Write(e.LogicalLength);
            w.Write((ushort)e.Extra.Length);
            if (e.Extra.Length > 0) w.Write(e.Extra);
            w.Write(e.CreatedTicks);
            w.Write(e.ModifiedTicks);
            w.Write((uint)e.Extents.Count);
            foreach (var x in e.Extents)
            {
                w.Write(x.LogicalStart);
                w.Write(x.Length);
                w.Write(x.PhysicalBlock);
                w.Write((byte)x.State);
            }
        }
        w.Flush();
        return ms;
    }

    private void LoadMetadata(byte[] image)
    {
        using var r = new BinaryReader(new MemoryStream(image, writable: false));
        if (r.ReadUInt32() != 0x5241574D)
            throw new FileIOException(IOError.IOFailure, "元数据镜像 magic 不符", null, "Open");
        if (r.ReadUInt16() != TierVolumeLayoutVersion)
            throw new FileIOException(IOError.Unsupported, "元数据镜像版本不支持", null, "Open");
        var entryCount = r.ReadUInt32();
        var dirCount = r.ReadUInt32();
        var reserveCount = r.ReadUInt32();
        for (var i = 0; i < reserveCount; i++) _journalReserveBlocks.Add(r.ReadUInt64());
        for (var i = 0; i < dirCount; i++)
        {
            var len = r.ReadUInt16();
            _directories.Add(System.Text.Encoding.UTF8.GetString(r.ReadBytes(len)));
        }
        for (var i = 0; i < entryCount; i++)
        {
            var len = r.ReadUInt16();
            var e = new Entry
            {
                Path = System.Text.Encoding.UTF8.GetString(r.ReadBytes(len)),
                IsDirectory = r.ReadBoolean(),
                LogicalLength = r.ReadInt64(),
            };
            var extraLen = r.ReadUInt16();
            e.Extra = extraLen > 0 ? r.ReadBytes(extraLen) : [];
            e.CreatedTicks = r.ReadInt64();
            e.ModifiedTicks = r.ReadInt64();
            var extentCount = r.ReadUInt32();
            for (var k = 0; k < extentCount; k++)
                e.Extents.Add(new Extent(r.ReadInt64(), r.ReadInt64(), r.ReadUInt64(),
                    (ExtentState)r.ReadByte()));
            _entries[e.Path] = e;
        }
        _sortedKeys.UnionWith(_entries.Keys);   // RM-11 索引重建
    }

    // ═══════════════ 命名空间操作（fs 锁内——MetadataGate/共享登记之外的元数据权威）═══════════════

    private Entry GetEntryOrThrow(string path, string op)
        => _entries.TryGetValue(path, out var e)
            ? e
            : throw new FileIOException(IOError.NotFound, $"条目不存在: {path}", path, op);

    /// <summary>触碰 mtime（lazytime——RM-17）：只缓存在内存条目 + 置时间戳脏标记，
    /// 不触发检查点（内核 lazytime 同构：随结构提交/clean 关闭顺带持久）。</summary>
    internal void TouchModified(Entry e)
    {
        e.ModifiedTicks = DateTimeOffset.UtcNow.UtcTicks;
        _timestampsDirty = true;
    }

    /// <summary>写路径区间分配：确保 [offset, offset+len) 有 Written 区间覆盖并推进逻辑长度。</summary>
    private void AllocateForWrite(Entry e, long offset, int len)
    {
        e.Extents = EnsureExtentCovering(e, e.Extents, offset, len);
        e.LogicalLength = Math.Max(e.LogicalLength, offset + len);
        MetadataDirty = true;
        TouchModified(e);
    }

    /// <summary>确保 [logicalStart, logicalStart+length) 有 Written 区间覆盖（相交区间块粒度切分 + 整段连续重分配）。
    /// ★ CoW/就地双模：<paramref name="source"/> == e.Extents（已发布——锁外读者持引用）→ CoW 返回新列表；
    /// 写计划路径（局部未发布工作列表）→ 就地改（免每写二次 O(n) 复制——CORE-02 热路径）。调用方决定发布时机。
    /// <paramref name="writeEnd"/> = 本次写的逻辑终点（记录 newLen 用——发射侧穿线）。
    /// <paramref name="direct"/> = 直达档（部分块零填充落载体 vs 落页缓存——B1 修复）。
    /// <paramref name="writeSpanStart"/> = 本次写实际起点（B1：零基判据用实际写范围，非覆盖范围）。
    /// <paramref name="zeroOps"/> = 零基需求收集（非 null：记录不执行——数据段锁外执行；null：立即执行 v1 语义）。</summary>
    private List<Extent> EnsureExtentCovering(Entry e, List<Extent> source, long logicalStart, long length, long writeEnd = 0,
        bool direct = false, long writeSpanStart = 0,
        List<(ulong FirstBlock, ulong LastBlock, long SpanStart, long SpanEnd)>? zeroOps = null)
        => EnsureExtentCovering(e, source, logicalStart, length, writeEnd, direct, writeSpanStart, zeroOps, out _, out _);

    /// <summary>§2.1 写计划重载：额外上报区间变更范围 <paramref name="mutStart"/>/<paramref name="mutEnd"/>
    /// （块对齐、半开 [start, end)——合并提交的"替换窗口"判据；无变更 = MaxValue/MinValue 哨兵）。</summary>
    private List<Extent> EnsureExtentCovering(Entry e, List<Extent> source, long logicalStart, long length, long writeEnd,
        bool direct, long writeSpanStart,
        List<(ulong FirstBlock, ulong LastBlock, long SpanStart, long SpanEnd)>? zeroOps,
        out long mutStart, out long mutEnd)
    {
        mutStart = long.MaxValue;
        mutEnd = long.MinValue;
        if (length <= 0) return source;
        var bs = (long)_sb.BlockSize;
        var logicalEnd = logicalStart + length;
        var newLen = writeEnd > 0 ? writeEnd : e.LogicalLength;
        var spanStart = writeSpanStart > 0 ? writeSpanStart : logicalStart;              // 实际写起点
        var spanEnd = writeEnd > 0 ? Math.Min(writeEnd, logicalEnd) : logicalEnd;       // 实际写终点
        var inPlace = !ReferenceEquals(source, e.Extents);   // 局部未发布列表可就地改（写计划热路径）

        // ═══ 追加快道（RM-01/D1）：目标在现有覆盖之外追加——免整表重建/排序 ═══
        // 逐次追加的旧路径 = O(n) 重建 + O(n log n) 排序 + 区间数线性膨胀 → O(n²) 累计税（探针已证）。
        var tail = source.Count > 0 ? source[^1] : (Extent?)null;
        if (tail is { } t && logicalStart >= t.LogicalEnd || tail is null)
        {
            if (tail is { } tt && logicalStart == tt.LogicalEnd && tt.State == ExtentState.Written)
            {
                // ① 尾区间物理邻接延伸：tail 末尾后续块恰好空闲 → 原地扩 Length（区间数不膨胀——O(1)）
                var extBlocks = (uint)((RoundUp(logicalEnd, bs) - tt.LogicalEnd) / bs);
                var after = tt.PhysicalBlock + (ulong)(tt.Length / bs);
                if (extBlocks > 0 && TryMarkContiguous(after, extBlocks))
                {
                    var grown = inPlace
                        ? source
                        : new List<Extent>(source);   // CoW（RM-12：锁外读者持旧列表——原位改写会撕裂）
                    grown[^1] = tt with { Length = tt.Length + (long)extBlocks * bs };
                    RecordOrRunZeroBlocks(after, after + extBlocks - 1, spanStart, spanEnd, direct, zeroOps);   // B1：未覆盖残段零基
                    JnlExtentTailExtend(e.Path, tt.LogicalEnd + (long)extBlocks * bs, newLen);
                    ExtendMut(ref mutStart, ref mutEnd, tt.LogicalStart, tt.LogicalEnd + (long)extBlocks * bs);   // 替换窗口含旧尾（合并提交替换整段）
                    return grown;
                }
            }
            // ② 非邻接/延伸失败：尾部追加新区间（表已有序——免重建免排序）
            var fastStart = RoundDown(logicalStart, bs);
            var fastEnd = RoundUp(logicalEnd, bs);
            var fastBlocks = (uint)((fastEnd - fastStart) / bs);
            var fastPhys = AllocateBlocks(fastBlocks, "Write");
            var appended = inPlace
                ? source
                : new List<Extent>(source) { new Extent(fastStart, fastEnd - fastStart, fastPhys, ExtentState.Written) };
            if (inPlace)
                appended.Add(new Extent(fastStart, fastEnd - fastStart, fastPhys, ExtentState.Written));
            RecordOrRunZeroBlocks(fastPhys, fastPhys + fastBlocks - 1, spanStart, spanEnd, direct, zeroOps);   // B1
            JnlExtentAppend(e.Path, fastStart, fastEnd - fastStart, fastPhys, ExtentState.Written, newLen);
            ExtendMut(ref mutStart, ref mutEnd, fastStart, fastEnd);
            return appended;
        }

        var phys = AllocateBlocks((uint)((RoundUp(logicalEnd, bs) - RoundDown(logicalStart, bs)) / bs), "Write");
        var covered = ApplyExtentCover(e, source, logicalStart, length, phys);
        RecordOrRunZeroBlocks(phys, phys + (uint)((RoundUp(logicalEnd, bs) - RoundDown(logicalStart, bs)) / bs) - 1,
            spanStart, spanEnd, direct, zeroOps);   // B1
        JnlExtentCover(e.Path, RoundDown(logicalStart, bs),
            RoundUp(logicalEnd, bs) - RoundDown(logicalStart, bs), phys, newLen);
        ExtendMut(ref mutStart, ref mutEnd, RoundDown(logicalStart, bs), RoundUp(logicalEnd, bs));
        return covered;
    }

    /// <summary>变更范围并集（块对齐；哨兵初值 = long.MaxValue/MinValue——首值即取）。</summary>
    private static void ExtendMut(ref long mutStart, ref long mutEnd, long start, long end)
    {
        mutStart = Math.Min(mutStart, start);
        mutEnd = Math.Max(mutEnd, end);
    }

    /// <summary>零基需求分派：<paramref name="zeroOps"/> 非 null = 记录（数据段锁外执行——写计划协议）；
    /// null = 立即执行（v1/重放语义——调用方持锁）。</summary>
    private void RecordOrRunZeroBlocks(ulong firstBlock, ulong lastBlock, long writeStart, long writeEnd, bool direct,
        List<(ulong FirstBlock, ulong LastBlock, long SpanStart, long SpanEnd)>? zeroOps)
    {
        if (zeroOps is not null)
        {
            zeroOps.Add((firstBlock, lastBlock, writeStart, writeEnd));
            return;
        }
        ZeroPartialWriteBlocks(firstBlock, lastBlock, writeStart, writeEnd, direct);
    }

    /// <summary>
    /// B1 修复（数据泄漏）——新分配块从未清零，载体上是已释放文件的陈旧字节：
    /// 部分块写的 RMW 以载体为基底会把陈旧字节复活为"洞内数据"。本方法在新分配 run 的
    /// [firstBlock, lastBlock] 内，对写区间 [writeStart, writeEnd) 未完全覆盖的首/尾块做零基初始化：
    /// 缓冲档 = 零页入缓存（RMW 基底即零）；直达档 = 载体清零 + 失效驻留页（对齐纪律满足）。
    /// 整块全覆盖的中间块走写绕/整块写自然覆盖，无需双写。
    /// </summary>
    private void ZeroPartialWriteBlocks(ulong firstBlock, ulong lastBlock, long writeStart, long writeEnd, bool direct)
    {
        var bs = (long)_pageSize;
        var headPartial = writeStart % bs != 0;
        var tailPartial = writeEnd % bs != 0;
        if (!headPartial && !tailPartial) return;
        if (headPartial) ZeroBlock(firstBlock, direct);
        // 尾部分块：写终点未对齐。首尾同块时仅当头不部分（头零化未覆盖本块）才补零；
        // 头部分时首块已零化，无需重复。
        if (tailPartial && (lastBlock != firstBlock || !headPartial)) ZeroBlock(lastBlock, direct);
    }

    /// <summary>单块零基（B1）——缓冲档零页入缓存（StorePage 标脏）；直达档载体清零 + 驻留页失效。</summary>
    private void ZeroBlock(ulong block, bool direct)
    {
        var zero = RentPageBuffer();
        try
        {
            zero.AsSpan(0, _pageSize).Clear();
            if (_pageBudget > 0 && !direct)
            {
                StorePage(block, zero);   // 零页驻留标脏——部分块写 RMW 基底即零
            }
            else
            {
                WriteCarrier((long)(block * (ulong)_pageSize), zero);   // 载体零基（块对齐——DIO 纪律满足）
                if (_pageBudget > 0)
                    InvalidateCacheBlocks(block, 1);   // B1 纪律：直达写失效驻留页（陈旧页不得成为 RMW 基底）
            }
        }
        finally
        {
            ReturnPageBuffer(zero);
        }
    }

    /// <summary>覆盖手术（在线/重放共用——raw-journal §8）：相交区间块粒度切分回收 +
    /// 整段单连续 run 重分配至 <paramref name="newPhys"/>（在线 = 分配器决策；重放 = 记录物理事实）。
    /// ★ CoW：返回新列表（<paramref name="source"/> 不被修改）。调用方负责发布与 MetadataDirty。</summary>
    private List<Extent> ApplyExtentCover(Entry e, List<Extent> source, long logicalStart, long length, ulong newPhys)
    {
        var bs = (long)_sb.BlockSize;
        var logicalEnd = logicalStart + length;
        var newExtents = new List<Extent>();

        foreach (var x in source)
        {
            if (x.LogicalEnd <= logicalStart || x.LogicalStart >= logicalEnd)
            {
                newExtents.Add(x);   // 区间外不动
                continue;
            }
            // 相交区间：块粒度保留外侧、回收相交物理块（写将重分配重写）
            var cutStart = Math.Max(x.LogicalStart, RoundDown(logicalStart, bs));
            var cutEnd = Math.Min(x.LogicalEnd, RoundUp(logicalEnd, bs));
            if (x.LogicalStart < cutStart)
                newExtents.Add(x with { Length = cutStart - x.LogicalStart });
            if (cutEnd < x.LogicalEnd)
                newExtents.Add(new Extent(cutEnd, x.LogicalEnd - cutEnd,
                    x.PhysicalBlock + (ulong)((cutEnd - x.LogicalStart) / bs), x.State));
            FreePhysical(e, cutStart, cutEnd, x, bs);
        }

        // 整段重分配为单连续 run（块对齐）
        var allocStart = RoundDown(logicalStart, bs);
        var allocEnd = RoundUp(logicalEnd, bs);
        var blocks = (uint)((allocEnd - allocStart) / bs);
        MarkBlocks(newPhys, blocks, used: true);
        newExtents.Add(new Extent(allocStart, allocEnd - allocStart, newPhys, ExtentState.Written));

        newExtents.Sort((a, b) => a.LogicalStart.CompareTo(b.LogicalStart));
        return newExtents;
    }

    private static long RoundDown(long v, long bs) => v / bs * bs;
    private static long RoundUp(long v, long bs) => (v + bs - 1) / bs * bs;

    /// <summary>等本文件在途写者出数据段（MetadataLock 内调用——CORE-02 写者计数钉块：
    /// 释放块的路径必须等写者数据段结束——写者锁外不碰锁，本自旋有界（数据段毫秒级）无死锁环）。</summary>
    private static void WaitWritersIdle(Entry e)
    {
        while (Volatile.Read(ref e.WritersInFlight) != 0) Thread.Yield();   // 写者数据段锁外不碰锁——自旋有界（毫秒级）无死锁环
    }

    private void FreePhysical(Entry e, long logicalStart, long logicalEnd, Extent x, long bs)
    {
        var firstBlock = x.PhysicalBlock + (ulong)((logicalStart - x.LogicalStart) / bs);
        var count = (uint)((logicalEnd - logicalStart) / bs);
        if (count > 0)
        {
            // ★ CORE-02：释放前等本文件在途写者出数据段（数据段写的是既有块——不等待会与
            //   删除/截断/打洞的块释放并发错位写；写者计数替代 epoch——每写免 Resume/Suspend）
            WaitWritersIdle(e);
            // ★ D1b（read-after-free 修复）：释放不即时还位图——epoch 延迟回收（RM-12 锁外快照读者
            //   可能仍持有旧区间引用；块保持 used 标记直到全部在途读者退出，杜绝读到重分配数据）
            RetireBlocks(firstBlock, count);
            InvalidateCacheBlocks(firstBlock, count);   // RM-12：释放块退出缓存（锁外读者不得再经旧页读到已释放数据——重分配后为新属主）
        }
    }

    // ═══════════════ epoch 延迟回收（D1b——RM-12 锁外快照读者 vs 块即时回收）═══════════════
    // 协议：回收 = 排队（批次 seq）→ bump（回调推进 _safeBatch 至本批上界）→ 锁内 TryFreeRetiredLocked
    // 只还 _safeBatch 之内的批次。读者（TierVolumeFileHandle.Read）Resume/Suspend 包夹快照捕获与读取；
    // bump 回调等待所有 bump 前受保护的读者退出——块在读者退出前保持 used，分配器不可复用。

    private readonly List<(ulong Block, uint Count, ulong Batch)> _retiredBlocks = new();
    private ulong _retireSeq;
    private ulong _safeBatch;      // 回调推进：≤ 此批次的回收已安全（Volatile——回调运行于任意线程）
    private bool _bumpPending;

    /// <summary>回收排队（fs 锁内调用）——块暂不还位图，随后 bump 覆盖本批。</summary>
    private void RetireBlocks(ulong firstBlock, uint count)
    {
        if (count == 0) return;
        _retiredBlocks.Add((firstBlock, count, ++_retireSeq));
        EnsureRetireBumpLocked();
    }

    /// <summary>触发 epoch bump（锁内；无在途 bump 且有未安全批次时）——回调纯内存（写 volatiles），
    /// 不碰锁、不嵌套 bump（LightEpoch 协议）。</summary>
    private void EnsureRetireBumpLocked()
    {
        if (_bumpPending) return;
        if (_retiredBlocks.Count == 0) return;
        if (Volatile.Read(ref _safeBatch) >= _retireSeq) return;
        var batch = _retireSeq;   // 本批上界——其后新回收需下一轮 bump（读者可能在本批之后才受保护）
        _bumpPending = true;
        _readEpoch.Resume();
        try
        {
            _readEpoch.BumpCurrentEpoch(() =>
            {
                Volatile.Write(ref _safeBatch, batch);
                Volatile.Write(ref _bumpPending, false);
            });
        }
        finally
        {
            _readEpoch.Suspend();
        }
    }

    /// <summary>锁内推进（分配/提交/flusher/关卷路径调用）：安全批次内的回收块正式还位图；
    /// 剩余批次补 bump（进度保证——无新回收也不泄漏）。
    /// ★ 回收清缓存（CORE-02 完整性）：块还位图即可能被复用——清除其驻留页（含删除后写计划
    /// 数据段新标脏的页——逐出者写盘先于回收，清缓存安全）；epoch 已保证无在途读者/写者。</summary>
    private void TryFreeRetiredLocked()
    {
        if (_retiredBlocks.Count == 0) return;
        var safe = Volatile.Read(ref _safeBatch);
        _retiredBlocks.RemoveAll(t =>
        {
            if (t.Batch > safe) return false;
            MarkBlocks(t.Block, t.Count, used: false);
            InvalidateCacheBlocks(t.Block, t.Count);   // ★ 回收清缓存（幂等——原显式清除路径保留）
            TrimCarrierBlocks(t.Block, t.Count);   // RM-05 + V2 §1.3：真正回收点空间归还（设备 TRIM / 文件打洞——epoch 读者已退出）
            return true;
        });
        EnsureRetireBumpLocked();
    }
}
