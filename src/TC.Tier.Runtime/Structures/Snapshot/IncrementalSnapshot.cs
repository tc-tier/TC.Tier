using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Shared;

namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>
/// IncrementalSnapshot——增量快照结构（镜像快照设计稿 §3.3——方案 A：段增量）。
/// <para>★ 语义：段（Segment）= 一次快照增量 = 一帧 [Header 14B][N₀ 8B 前缀 + 条目流][Footer 28B]；
///   新段 append 不重写旧段（raft 定期压缩——快照越频繁写省越多，基准实测 8 次快照 4.5× 字节差）；
///   读 = 段表驱动逐段顺序拼接 = 最新快照；段累积达阈值 → 合并为新基线段（低频全量重写，raft 只需最新快照）。</para>
/// <para>★ 段表（各段起点 + N₀——恢复 O(1) 定位）：内存列表 + opaque meta 原子落盘（搭水位线同一块
///   同一 CRC——AppendSegment 完成后 SetOpaqueMeta + WriteMeta）。</para>
/// <para>★ 容错：段级帧 CRC64——崩溃 = 旧段完好、半写段恢复期忽略（尾部检查 = 段表物理尾 vs 引擎
///   CommittedTail，不依赖 meta 水位）；段内 N₀ 前缀自描述（独立读一段可知覆盖点）。</para>
/// <para>★ 生命周期：继承 <see cref="SnapshotBase"/>（引擎/生命周期/事务参与基类能力）——恢复核心 =
///   <see cref="IncrementalRecovery"/>（RecoveryBase 模板派生：join 引擎 + meta 段表 + 尾部检查）。</para>
/// </summary>
public sealed partial class IncrementalSnapshot : SnapshotBase
{
    /// <summary>段内 [N₀ 8B] 前缀（快照覆盖点——段自描述）。</summary>
    internal const int SegmentPrefixSize = sizeof(long);

    /// <summary>合并截断对齐粒度（引擎 ReclaimHead 打洞契约：地址对齐 AllocationUnit 4096）。</summary>
    internal const long TruncateAlign = 4096;

    // === 段表（内存 + opaque meta 持久化；raft 快照低频单写者——写路径串行）===
    private readonly List<SegmentInfo> _segments = [];
    private readonly object _segmentLock = new();
    private readonly IncrementalSnapshotSettings _settings;
    private long _latestN0;
    private int _writing;      // AppendSegment/ImportSegment 单写者闸门（0/1——重入/并发抛）
    private long _nextSeq;     // 2PC 事务序号（恢复后 = LastCommittedSeq + 1——Confirm 须 > 已提交）

    /// <summary>段条目：逻辑起点（帧头位置）+ 物理起点（对齐——读会话物理锚点）+ 快照覆盖点 N₀。</summary>
    internal readonly record struct SegmentInfo(LogicalAddress LogicalStart, LogicalAddress PhysStart, long N0);

    /// <summary>构造（参数序同 SnapshotBase：codec 内建——fs → settings → 可选注入）。</summary>
    public IncrementalSnapshot(
        IFileSystem fileSystem,
        IncrementalSnapshotSettings settings,
        IRecovery<SnapshotRecoveryHints>? recovery = null,
        MetaPolicyFactory<SnapshotMetaHeader, SnapshotMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null,
        ILogger? logger = null)
        : base(new StreamFrameCodec(), fileSystem, settings, recovery, metaPolicyFactory, metaTransport, logger)
    {
        _settings = settings;
    }

    /// <summary>★ 恢复核心 = IncrementalRecovery（RecoveryBase 模板派生——meta 段表 + 尾部检查）。</summary>
    protected override IRecovery<SnapshotRecoveryHints> CreateRecovery()
        => new IncrementalRecovery(this);

    /// <summary>段数（内存段表——含最新段）。</summary>
    public int SegmentCount { get { lock (_segmentLock) return _segments.Count; } }

    /// <summary>最新段覆盖点 N₀（无段 = 0——无快照）。</summary>
    public long LatestN0 => Volatile.Read(ref _latestN0);

    /// <summary>段 i 的覆盖点（越界抛）。</summary>
    public long GetSegmentN0(int index)
    {
        lock (_segmentLock)
        {
            if (index < 0 || index >= _segments.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _segments[index].N0;
        }
    }

    /// <summary>
    /// 追加增量段：[N₀ 8B] + 条目流写入新帧（append 不重写旧段）→ 2PC 提交 → 段表注册 + meta 原子落盘 →
    /// 段数达阈值自触发合并。返回 N₀。
    /// <para>★ 单写者契约（raft 快照低频串行）；并发/重入抛 InvalidOperationException。</para>
    /// <para>★ 事务语义（会话模式——底层 2PC）：写帧 → Prepare(seq) → ConfirmCommitted(seq) → 段表注册；
    ///   chunk 流异常 = Abort(seq)——尾截断回滚到上次提交点（新段物理清除，旧段完好）。</para>
    /// </summary>
    public async ValueTask<long> AppendSegmentAsync(long n0,
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken ct = default)
    {
        EnsureReady();
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw new InvalidOperationException("AppendSegmentAsync 单写者——并发/重入被拒（raft 快照低频串行）。");
        try
        {
            return await WriteSegmentCoreAsync(n0, chunks, replace: false, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    /// <summary>
    /// ★ 导入段（替换语义——raft 快照安装）：流式写完整镜像段 → 2PC 提交 → 段表替换为单段
    /// （旧段内容被新镜像包含）+ 旧段物理回收。chunk 流异常（如外部 Footer 校验失败）= Abort 回滚——
    /// 新段物理清除、旧快照完好（失败即清理，会话模式）。
    /// </summary>
    public async ValueTask<long> ImportSegmentAsync(long n0,
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, CancellationToken ct = default)
    {
        EnsureReady();
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw new InvalidOperationException("ImportSegmentAsync 单写者——并发/重入被拒。");
        try
        {
            return await WriteSegmentCoreAsync(n0, chunks, replace: true, ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    /// <summary>
    /// ★ 段写核心（2PC 事务——会话模式）：帧写 → Prepare → Confirm → 段表注册（追加或替换）+ meta 原子落盘。
    /// chunk 流异常（消费方校验失败/传输中断）= Abort——回滚到上次提交点（新段清除，旧段完好）。
    /// </summary>
    private async ValueTask<long> WriteSegmentCoreAsync(long n0,
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks, bool replace, CancellationToken ct)
    {
        // ★ 段起点 = 写前水位（逻辑 + 物理锚点——会话开始地址，Abort 回滚基准）
        var logicalStart = _writeAddress;
        var physStart = _physicalWriteAddress;
        long seq = Interlocked.Increment(ref _nextSeq);

        try
        {
            await using (var session = OpenWriteSession(physStart))
            {
                // ★ flush 完成回调：双水位推进（逻辑按 logicalBytes 非对齐、物理按 alignedBytes）
                session.OnFlushed += (_, logicalBytes, alignedBytes) =>
                {
                    _writeAddress = _engine.CalculationAddress(_writeAddress, logicalBytes);
                    _physicalWriteAddress = _engine.CalculationAddress(_physicalWriteAddress, alignedBytes);
                };

                // 帧头（14B）——flags 默认（CRC64|PAYLOAD_4B|CRC_IN_FOOTER|FOOTER_MAGIC）
                var header = new byte[StreamFrameHeaderCodec.StructSize];
                var h = StreamFrameHeaderCodec.Create();
                StreamFrameHeaderCodec.Write(header, in h);

                var hash = new Crc64();
                hash.Append(header);
                session.WriteSmall(header);

                // [N₀ 8B] 前缀（段自描述覆盖点）
                var prefix = new byte[SegmentPrefixSize];
                BinaryPrimitives.WriteInt64LittleEndian(prefix, n0);
                hash.Append(prefix);
                session.Write(prefix);

                // 条目流（GB/TB 级不驻内存——边写边 CRC 增量累积）
                long total = SegmentPrefixSize;
                long entries = 1;
                await foreach (var chunk in chunks.WithCancellation(ct).ConfigureAwait(false))
                {
                    hash.Append(chunk.Span);
                    await session.WriteAsync(chunk, ct).ConfigureAwait(false);
                    total += chunk.Length;
                    entries++;
                }

                // 帧尾（28B）：前 20B（Magic+TotalLength+EntryCount）累积 CRC，末 8B 是结果
                await session.FlushIfFullAsync(StreamFrameFooterCodec.StructSize, ct).ConfigureAwait(false);
                var footer = new byte[StreamFrameFooterCodec.StructSize];
                var f = StreamFrameFooterCodec.Create();
                f.TotalLength = (ulong)total;
                f.EntryCount = (ulong)entries;
                f.Crc = 0;   // 占位，回填
                StreamFrameFooterCodec.Write(footer, in f);
                hash.Append(footer.AsSpan(0, 20));
                BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(20), UnifiedCrc.FinalizeCrc64(hash));
                session.WriteSmall(footer);

                await session.FlushAsync(ct).ConfigureAwait(false);
            }

            // ★ 2PC 提交（会话模式）：Prepare（meta 记 prepared + 水位）→ Confirm（推进提交点——
            //   Abort 回退基准 = 本段尾；崩溃在此之间 = 悬干裁决恢复回滚）
            await PrepareAsync(seq, ct);
            ConfirmCommitted(seq);

            // ★ 段表注册（提交点后——悬干裁决只回滚未提交段，已注册段不受影响）+ meta 原子落盘
            lock (_segmentLock)
            {
                if (replace) _segments.Clear();
                _segments.Add(new SegmentInfo(logicalStart, physStart, n0));
                _latestN0 = n0;
                SetOpaqueMeta(SerializeSegments());
                WriteMeta();
            }

            switch (replace)
            {
                // ★ 替换：注册（meta 落盘）后截旧段（物理回收——崩溃安全：段表已 [新]，旧段残留不可见）
                case true:
                    TruncatePrefix(new LogicalAddress(logicalStart.SegId, AlignDownTruncate(logicalStart.Offset)));
                    break;
                // ★ 合并阈值自触发（仅追加形态——段数 ≥ 阈值；低频全量重写；raft 只需最新快照）
                case false when _segments.Count >= _settings.CompactSegmentThreshold:
                    await CompactSegmentsCoreAsync(ct).ConfigureAwait(false);
                    break;
            }

            return n0;
        }
        catch
        {
            // ★ 失败即清理（会话模式核心）：Abort = 尾截断回滚到上次提交点——新段物理清除（含半写残留），旧段完好
            await AbortAsync(seq, ct);
            throw;
        }
    }

    /// <summary>
    /// 顺序流式读回全部段（跳过段内 N₀ 前缀）= 最新快照的条目流——GB/TB 级不驻内存，
    /// 段级帧 CRC64 增量校验（读多少验多少——校验失败抛 InvalidDataException）。
    /// </summary>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllChunksAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        EnsureReady();
        SegmentInfo[] segs;
        lock (_segmentLock) segs = _segments.ToArray();
        foreach (var seg in segs)
        {
            await foreach (var chunk in ReadSegmentDataAsync(seg, ct).ConfigureAwait(false))
                yield return chunk;
        }
    }

    /// <summary>段 i 的数据流（测试/诊断——同上校验语义；越界抛）。</summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadSegmentDataAsync(int index, CancellationToken ct = default)
    {
        SegmentInfo seg;
        lock (_segmentLock)
        {
            if (index < 0 || index >= _segments.Count)
                throw new ArgumentOutOfRangeException(nameof(index));
            seg = _segments[index];
        }
        return ReadSegmentDataAsync(seg, ct);
    }

    /// <summary>
    /// 合并全部段为新基线段（读全段拼接 → 写新帧 → 截旧段 → 段表 = 单段 + meta 落盘）。
    /// 低频（阈值触发/显式调）——成本 = 一次全量写（基准对照 B 单次快照）。
    /// </summary>
    public async ValueTask CompactSegmentsAsync(CancellationToken ct = default)
    {
        EnsureReady();
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw new InvalidOperationException("CompactSegmentsAsync 与 AppendSegmentAsync 互斥（单写者）。");
        try
        {
            await CompactSegmentsCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    /// <summary>
    /// 清空全部段（快照替换语义用——导入新快照前清旧；截断全量 + 段表空 + meta 落盘）。
    /// </summary>
    public ValueTask ClearAsync(CancellationToken ct = default)
    {
        EnsureReady();
        if (Interlocked.CompareExchange(ref _writing, 1, 0) != 0)
            throw new InvalidOperationException("ClearAsync 与 AppendSegmentAsync 互斥（单写者）。");
        try
        {
            LogicalAddress tail;
            lock (_segmentLock)
            {
                if (_segments.Count == 0) return ValueTask.CompletedTask;   // 已空
                tail = _writeAddress;
            }
            _engine.Flush();
            TruncatePrefix(new LogicalAddress(tail.SegId, AlignDownTruncate(tail.Offset)));
            lock (_segmentLock)
            {
                _segments.Clear();
                _latestN0 = 0;
                SetOpaqueMeta(SerializeSegments());
                WriteMeta();
            }
            return ValueTask.CompletedTask;
        }
        finally
        {
            Volatile.Write(ref _writing, 0);
        }
    }

    /// <summary>合并核心（调用方须已持 _writing 闸门——AppendSegment 阈值自触发与显式调共用）。</summary>
    private async ValueTask CompactSegmentsCoreAsync(CancellationToken ct)
    {
        SegmentInfo[] segs;
        long n0;
        lock (_segmentLock)
        {
            segs = _segments.ToArray();
            n0 = _latestN0;
            if (segs.Length <= 1) return;   // 单段无可合并
        }

            // ★ 读全段 → 写新基线帧（[N₀ 8B] + 全部条目——内容 = 最新快照）
            var logicalStart = _writeAddress;
            var physStart = _physicalWriteAddress;
            await using (var session = OpenWriteSession(physStart))
            {
                session.OnFlushed += (_, logicalBytes, alignedBytes) =>
                {
                    _writeAddress = _engine.CalculationAddress(_writeAddress, logicalBytes);
                    _physicalWriteAddress = _engine.CalculationAddress(_physicalWriteAddress, alignedBytes);
                };

                // 帧头（14B）——flags 默认
                var header = new byte[StreamFrameHeaderCodec.StructSize];
                var h = StreamFrameHeaderCodec.Create();
                StreamFrameHeaderCodec.Write(header, in h);
                var hash = new Crc64();
                hash.Append(header);
                session.WriteSmall(header);

                // [N₀ 8B] 前缀（段自描述覆盖点）
                var prefix = new byte[SegmentPrefixSize];
                BinaryPrimitives.WriteInt64LittleEndian(prefix, n0);
                hash.Append(prefix);
                session.Write(prefix);
                long total = SegmentPrefixSize;
                long entries = 1;

                foreach (var seg in segs)
                {
                    await foreach (var chunk in ReadSegmentDataAsync(seg, ct).ConfigureAwait(false))
                    {
                        hash.Append(chunk.Span);
                        await session.WriteAsync(chunk, ct).ConfigureAwait(false);
                        total += chunk.Length;
                        entries++;
                    }
                }

                await session.FlushIfFullAsync(StreamFrameFooterCodec.StructSize, ct).ConfigureAwait(false);
                var footer = new byte[StreamFrameFooterCodec.StructSize];
                var f = StreamFrameFooterCodec.Create();
                f.TotalLength = (ulong)total;
                f.EntryCount = (ulong)entries;
                f.Crc = 0;
                StreamFrameFooterCodec.Write(footer, in f);
                hash.Append(footer.AsSpan(0, 20));
                BinaryPrimitives.WriteUInt64LittleEndian(footer.AsSpan(20), UnifiedCrc.FinalizeCrc64(hash));
                session.WriteSmall(footer);

                await session.FlushAsync(ct).ConfigureAwait(false);
            }

            // ★ 截旧段（新帧完整落盘后）：截到新基线帧起点向下对齐（引擎 PunchHole 契约——
            //   残留旧段尾 ≤4KB 碎片不可见；读从精确帧起点起）
            TruncatePrefix(new LogicalAddress(logicalStart.SegId, AlignDownTruncate(logicalStart.Offset)));
            lock (_segmentLock)
            {
                _segments.Clear();
                _segments.Add(new SegmentInfo(logicalStart, physStart, n0));
                SetOpaqueMeta(SerializeSegments());
                WriteMeta();
            }
    }

    // ════════════════════════════════════════════════════════════
    // 段读（帧解析 + 流式交付 + CRC64 校验）
    // ════════════════════════════════════════════════════════════

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadSegmentDataAsync(SegmentInfo seg,
        [EnumeratorCancellation] CancellationToken ct)
    {
        LogicalAddress end;
        LogicalAddress physEnd;
        lock (_segmentLock)
        {
            int idx = _segments.IndexOf(seg);
            if (idx < 0) yield break;   // 段已被合并/截断（读游标竞态）——安静停止
            end = idx + 1 < _segments.Count ? _segments[idx + 1].LogicalStart : _writeAddress;
            physEnd = idx + 1 < _segments.Count ? _segments[idx + 1].PhysStart
                : AlignUpToSector(_physicalWriteAddress);
        }

        var hash = new Crc64();
        var session = OpenReadSession(seg.LogicalStart, end, seg.PhysStart, physEnd);
        await using var _ = session.ConfigureAwait(false);

        // 帧头（14B）——magic 校验
        byte[] hdr = new byte[StreamFrameHeaderCodec.StructSize];
        int got = await session.ReadAsync(hdr, ct).ConfigureAwait(false);
        if (got < StreamFrameHeaderCodec.StructSize)
            throw new InvalidDataException($"段帧头不完整（{got}/{StreamFrameHeaderCodec.StructSize}B）——快照段损坏。");
        if (StreamFrameHeaderCodec.Read(hdr.AsSpan()).MagicValue != StreamSnapshot.StreamFrameHeader.Magic)
            throw new InvalidDataException("段帧头 magic 非法——快照段损坏。");
        hash.Append(hdr);

        // data 区 = [N₀ 8B 前缀] + 条目流——前缀跳过（N₀ 由段表/Header 提供），交付条目流；
        // ★ CRC 覆盖全部 data（含前缀——与写侧累积一致；前缀字节算 CRC 不交付）
        long dataAvailable = _engine.GetDistance(seg.LogicalStart, end) - StreamFrameHeaderCodec.StructSize
            - StreamFrameFooterCodec.StructSize;
        long dataRead = 0;
        long prefixRemaining = SegmentPrefixSize;
        byte[] buf = new byte[64 * 1024];
        while (dataRead < dataAvailable)
        {
            int want = (int)Math.Min(buf.Length, dataAvailable - dataRead);
            int n = await session.ReadAsync(buf.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n == 0) break;
            dataRead += n;

            int delivered = 0;
            if (prefixRemaining > 0)
            {
                // 前缀部分：算 CRC 不交付
                int skip = (int)Math.Min(prefixRemaining, n);
                hash.Append(buf.AsSpan(0, skip));
                prefixRemaining -= skip;
                delivered = skip;
            }
            if (delivered < n)
            {
                hash.Append(buf.AsSpan(delivered, n - delivered));
                yield return buf.AsMemory(delivered, n - delivered);
            }
        }
        if (prefixRemaining > 0 && dataAvailable > 0)
            throw new InvalidDataException("段 data 区截断——快照段损坏。");

        // 帧尾（28B）——magic + CRC64 校验（前 20B 累积）
        byte[] footer = new byte[StreamFrameFooterCodec.StructSize];
        int gotFooter = await session.ReadAsync(footer, ct).ConfigureAwait(false);
        if (gotFooter < StreamFrameFooterCodec.StructSize)
            throw new InvalidDataException("段帧尾不完整——快照段损坏。");
        var f = StreamFrameFooterCodec.Read(footer.AsSpan());
        if (f.Magic != StreamSnapshot.StreamFrameFooter.FooterMagic)
            throw new InvalidDataException("段帧尾 magic 非法——快照段损坏。");
        hash.Append(footer.AsSpan(0, 20));
        if (UnifiedCrc.FinalizeCrc64(hash) != f.Crc)
            throw new InvalidDataException("段 CRC64 校验失败——快照段损坏。");
        if ((long)f.TotalLength != dataAvailable)
            throw new InvalidDataException($"段 TotalLength 与 data 区不符（{f.TotalLength} != {dataAvailable}）。");
    }

    // ════════════════════════════════════════════════════════════
    // 段表序列化（opaque meta 载体；[count 4B][pad 4B][条目 40B × N]）
    // ════════════════════════════════════════════════════════════

    /// <summary>段表条目序列化大小 = LogicalStart 16B + PhysStart 16B + N₀ 8B（PhysStart 必须持久化——
    /// 逻辑↔物理有 flush padding 偏差，无法从逻辑推算）。</summary>
    internal const int SegmentEntrySize = 16 + 16 + 8;

    internal static byte[] SerializeSegments(IReadOnlyList<SegmentInfo> segs)
    {
        var buf = new byte[8 + segs.Count * SegmentEntrySize];
        BinaryPrimitives.WriteInt32LittleEndian(buf, segs.Count);
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            int off = 8 + i * SegmentEntrySize;
            WriteAddress(buf.AsSpan(off), s.LogicalStart);
            WriteAddress(buf.AsSpan(off + 16), s.PhysStart);
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(off + 32), s.N0);
        }
        return buf;
    }

    private byte[] SerializeSegments()
    {
        lock (_segmentLock) return SerializeSegments(_segments);
    }

    /// <summary>解析 opaque 段表（内部用）；非法格式返回空（回退无段表——恢复兜底走尾部检查）。</summary>
    internal static List<SegmentInfo> DeserializeSegments(ReadOnlySpan<byte> buf)
    {
        var list = new List<SegmentInfo>();
        if (buf.Length < 8) return list;
        int count = BinaryPrimitives.ReadInt32LittleEndian(buf);
        if (count < 0 || 8 + count * SegmentEntrySize > buf.Length) return list;
        for (int i = 0; i < count; i++)
        {
            int off = 8 + i * SegmentEntrySize;
            var logical = ReadAddress(buf.Slice(off));
            var phys = ReadAddress(buf.Slice(off + 16));
            long n0 = BinaryPrimitives.ReadInt64LittleEndian(buf.Slice(off + 32));
            list.Add(new SegmentInfo(logical, phys, n0));
        }
        return list;
    }

    private static void WriteAddress(Span<byte> dst, LogicalAddress addr)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dst, addr.SegId);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(4), addr.Extension);
        BinaryPrimitives.WriteInt64LittleEndian(dst.Slice(8), addr.Offset);
    }

    private static LogicalAddress ReadAddress(ReadOnlySpan<byte> src) => new(
        BinaryPrimitives.ReadInt32LittleEndian(src),
        BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4)),
        BinaryPrimitives.ReadInt64LittleEndian(src.Slice(8)));

    /// <summary>截断边界向下对齐（引擎 ReclaimHead 打洞契约）。</summary>
    internal static long AlignDownTruncate(long offset) => offset & ~(TruncateAlign - 1);
}
