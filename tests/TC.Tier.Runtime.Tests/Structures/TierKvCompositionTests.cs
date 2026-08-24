using System.Runtime.InteropServices;
using TC.Tier.Runtime.Tests.Structures.ProbingIndex;
using TC.Tier.Runtime.Tests.Structures.Ring;
using TC.Tier.Runtime.Tests.Structures.SortedIndex;
using TC.Tier.Contracts.Structures;

namespace TC.Tier.Runtime.Tests.Structures;

/// <summary>
/// KV 组合正确性测试（设计稿 §5——改版的直接目的）：测试组合根直接组 KV——
/// RingOfLong（真相源 record 流）+ 两族索引（派生数据），按 §4 两段式协议编排恢复。
/// <para>★ 组合原语（Put 两步正序/点查两段合口径/锚点解析）= Products 层将来同形薄封装的内核。</para>
/// <para>★ §4 协议：①Ring.Initialize+恢复（水位+opaque）→ 锚点 W（无/损坏→Begin）→ ②index 拉流重放自建。</para>
/// </summary>
public class TierKvCompositionTests
{
    // ═══ 组合原语（测试组合根）═══

    /// <summary>写：先 Ring.Write 得地址（真相源）、再 index.Insert（派生）——写编排归组合层正序。</summary>
    static LogicalAddress KvPut(RingOfLong ring, IIndex<long> index, long key, ReadOnlySpan<byte> value)
    {
        var addr = ring.Write(key, value);
        index.Insert(key, addr, LogicalAddress.Empty);
        return addr;
    }

    /// <summary>点查两段合口径：index.Find 命中 → Ring.GetValue 取值；false = 不存在。</summary>
    static bool KvTryGet(RingOfLong ring, IIndex<long> index, long key, Span<byte> buf, out int len)
    {
        len = 0;
        var addr = index.Find(key);
        if (addr == LogicalAddress.Empty) return false;
        len = ring.GetValue(addr, buf);
        return true;
    }

    /// <summary>§4 锚点解析：opaque 读 W——无锚点/损坏（W 越过当前尾=不可能的未来锚点，宁可旧多重放的守卫）→ Begin（全量重建同一条路）。</summary>
    static LogicalAddress ResolveAnchor(RingOfLong ring)
    {
        var opaque = ring.ReadOpaqueMeta();
        if (opaque.Length >= WireSize)
        {
            var w = MemoryMarshal.Read<LogicalAddressWire>(opaque).ToAddress();
            if (w > LogicalAddress.Empty && w <= ring.TailAddress) return w;
        }
        return ring.BeginAddress;
    }

    private const int WireSize = sizeof(int) * 2 + sizeof(long);   // LogicalAddressWire：SegId+Extension+Offset

    /// <summary>锚点线上格式（LogicalAddress blittable 投影——SegId/Extension/Offset 16B）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct LogicalAddressWire(int SegId, int Extension, long Offset)
    {
        public readonly LogicalAddress ToAddress() => new(SegId, Extension, Offset);
        public static LogicalAddressWire From(LogicalAddress a) => new(a.SegId, a.Extension, a.Offset);
    }

    // ═══ 索引装配（同卷异引擎名与 Ring 共栖）═══

    static BlittableRingSettings RingSettings(TestVolume vol, bool deleteOnClose = false)
        => TestRingSettingsFactory.On(vol, "kv-ring", deleteOnClose: deleteOnClose,
            metaKind: MetaPolicyKind.Managed);

    static HashIndex<long> NewHash(TestVolume vol, RingOfLong ring,
        ProbingIndexRecoveryHints hints = default)
        => TestProbingIndexSettingsFactory.NewHash<long>(vol,
            TestProbingIndexSettingsFactory.On(vol, "kv-hash"), ring, hints: hints);

    static BTreeIndex<long> NewBTree(TestVolume vol, RingOfLong ring,
        SortedIndexRecoveryHints hints = default)
        => TestSortedIndexSettingsFactory.NewBTree<long>(vol,
            TestSortedIndexSettingsFactory.BTreeOn(vol, "kv-bt"), keyResolver: ring, hints: hints);

    static SkipListIndex<long> NewSkipList(TestVolume vol, RingOfLong ring,
        SortedIndexRecoveryHints hints = default)
        => TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
            TestSortedIndexSettingsFactory.SkipListOn(vol, "kv-sl"), keyResolver: ring, hints: hints);

    static byte[] ValueOf(long key) => BitConverter.GetBytes(key * 7 + 1);

    // ═══ KV 语义矩阵（三族同测：写/点查/覆盖/删除/未命中）═══

    [Fact]
    public void Kv_Semantics_HashIndex()
    {
        using var vol = new TestVolume();
        using var ring = RingOfLong.Create(RingSettings(vol), vol.Fs);
        using var index = NewHash(vol, ring);
        AssertKvSemantics(ring, index);
    }

    [Fact]
    public void Kv_Semantics_BTreeIndex()
    {
        using var vol = new TestVolume();
        using var ring = RingOfLong.Create(RingSettings(vol), vol.Fs);
        using var index = NewBTree(vol, ring);
        AssertKvSemantics(ring, index);
    }

    [Fact]
    public void Kv_Semantics_SkipListIndex()
    {
        using var vol = new TestVolume();
        using var ring = RingOfLong.Create(RingSettings(vol), vol.Fs);
        using var index = NewSkipList(vol, ring);
        AssertKvSemantics(ring, index);
    }

    static void AssertKvSemantics(RingOfLong ring, IIndex<long> index)
    {
        const int count = 100;
        var buf = new byte[16];

        // 写 + 点查往返
        for (long k = 1; k <= count; k++)
            KvPut(ring, index, k, ValueOf(k));
        index.EntryCount.Should().Be(count);
        for (long k = 1; k <= count; k++)
        {
            KvTryGet(ring, index, k, buf, out var len).Should().BeTrue($"key {k} 应命中");
            len.Should().Be(8);
            buf.AsSpan(0, len).ToArray().Should().BeEquivalentTo(ValueOf(k));
        }

        // 未命中
        KvTryGet(ring, index, -1, buf, out _).Should().BeFalse("未写入的 key 不应命中");

        // 覆盖：最新写胜出（index 指向新 record，旧 record 仍在 log——真相源不可变）
        KvPut(ring, index, 42, BitConverter.GetBytes(0xC0FFEE));
        KvTryGet(ring, index, 42, buf, out var len2).Should().BeTrue();
        len2.Should().Be(4);
        buf.AsSpan(0, len2).ToArray().Should().BeEquivalentTo(BitConverter.GetBytes(0xC0FFEE));

        // 删除：index 层墓碑（log record 不动——append-only 真相源）；再写同 key 复活
        var lastAddr = index.Find(7);
        index.Delete(7).Should().BeTrue();
        KvTryGet(ring, index, 7, buf, out _).Should().BeFalse("删除后应未命中");
        ring.TryGetKey(lastAddr, out var keyAfterDelete).Should().BeTrue("Ring record 是 append-only 真相源，index 删除不动它");
        keyAfterDelete.Should().Be(7);
        KvPut(ring, index, 7, ValueOf(7));
        KvTryGet(ring, index, 7, buf, out _).Should().BeTrue("同 key 重写应复活");
    }

    // ═══ §4 跨实例恢复：ring meta 恢复 → 锚点解析 → index 拉流重放自建 ═══

    [Fact]
    public void Kv_Reopen_RebuildsFromBegin_HashIndex()
    {
        using var vol = new TestVolume();
        const int count = 50;

        // 实例 1：写 KV + Prepare（FlushUntil + WriteMeta——数据+水位+（无）opaque 落盘）
        using (var ring1 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs))
        {
            using var idx1 = NewHash(vol, ring1);
            for (long k = 1; k <= count; k++)
                KvPut(ring1, idx1, k, ValueOf(k));
            ring1.Prepare(seq: 1);
        }

        // 实例 2：Ring 恢复（Managed meta）→ 锚点解析（未登记 → Begin）→ index 全量重放
        using var ring2 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs);
        ResolveAnchor(ring2).Should().Be(ring2.BeginAddress, "未登记锚点 → W=Begin 全量重建同一条路");
        using var idx2 = NewHash(vol, ring2,
            hints: new ProbingIndexRecoveryHints(ResolveAnchor(ring2), ring2.TailAddress));

        idx2.EntryCount.Should().Be(count, "全量重放应重建全部条目");
        var buf = new byte[16];
        for (long k = 1; k <= count; k++)
        {
            KvTryGet(ring2, idx2, k, buf, out var len).Should().BeTrue($"key {k} 跨实例重建后应命中");
            buf.AsSpan(0, len).ToArray().Should().BeEquivalentTo(ValueOf(k));
        }
    }

    [Fact]
    public void Kv_Reopen_RebuildsFromBegin_BTreeIndex_AndOverwriteReplaysLatest()
    {
        using var vol = new TestVolume();
        const int count = 60;

        using (var ring1 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs))
        {
            using var idx1 = NewBTree(vol, ring1);
            for (long k = 1; k <= count; k++)
                KvPut(ring1, idx1, k, ValueOf(k));
            // 覆盖一条：真实 Ring 扫描流按 log 序吐两条 record——重放折叠后最新写胜出
            KvPut(ring1, idx1, 33, BitConverter.GetBytes(0xBEEF));
            ring1.Prepare(seq: 1);
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs);
        using var idx2 = NewBTree(vol, ring2,
            hints: new SortedIndexRecoveryHints(ResolveAnchor(ring2), ring2.TailAddress));

        idx2.EntryCount.Should().Be(count, "覆盖重放折叠后条目数不减");
        var buf = new byte[16];
        for (long k = 1; k <= count; k++)
        {
            KvTryGet(ring2, idx2, k, buf, out var len).Should().BeTrue($"key {k} 跨实例重建后应命中");
            var expected = k == 33 ? BitConverter.GetBytes(0xBEEF) : ValueOf(k);
            buf.AsSpan(0, len).ToArray().Should().BeEquivalentTo(expected, $"key {k} 应取重放折叠后的最新写");
        }
    }

    [Fact]
    public void Kv_Reopen_RebuildsFromBegin_SkipListIndex()
    {
        using var vol = new TestVolume();
        const int count = 40;

        using (var ring1 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs))
        {
            using var idx1 = NewSkipList(vol, ring1);
            for (long k = 1; k <= count; k++)
                KvPut(ring1, idx1, k, ValueOf(k));
            ring1.Prepare(seq: 1);
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs);
        using var idx2 = NewSkipList(vol, ring2,
            hints: new SortedIndexRecoveryHints(ResolveAnchor(ring2), ring2.TailAddress));

        idx2.EntryCount.Should().Be(count);
        var buf = new byte[16];
        for (long k = 1; k <= count; k++)
        {
            KvTryGet(ring2, idx2, k, buf, out var len).Should().BeTrue($"key {k} 跨实例重建后应命中");
            buf.AsSpan(0, len).ToArray().Should().BeEquivalentTo(ValueOf(k));
        }
    }

    // ═══ 水位锚点一致性（§4：锚点搭 Ring 水位同 meta 块原子提交）═══

    [Fact]
    public void Anchor_SetOpaqueMeta_RidesRingWatermark_AcrossReopen()
    {
        using var vol = new TestVolume();

        LogicalAddress anchor;
        using (var ring1 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs))
        {
            ring1.Write(1L, new byte[] { 1 });
            ring1.Write(2L, new byte[] { 2 });
            anchor = ring1.TailAddress;
            ring1.SetOpaqueMeta(MemoryMarshal.Cast<LogicalAddressWire, byte>(stackalloc[] { LogicalAddressWire.From(anchor) }));
            ring1.Prepare(seq: 1);   // 水位 + staged opaque 同块原子落盘
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs);
        var opaque = ring2.ReadOpaqueMeta();
        opaque.Length.Should().BeGreaterOrEqualTo(WireSize, "锚点应随 Ring 水位落盘并可跨实例读回");
        MemoryMarshal.Read<LogicalAddressWire>(opaque).ToAddress().Should().Be(anchor);
        ResolveAnchor(ring2).Should().Be(anchor, "有效锚点（≤尾）解析为 W");
    }

    [Fact]
    public void Anchor_BeyondRecoveredTail_IsCorrupt_FallsBackToBegin()
    {
        using var vol = new TestVolume();

        using (var ring1 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs))
        {
            ring1.Write(1L, new byte[] { 1 });
            // 伪造"未来锚点"（越过尾——违反不变量的形态）：解析侧必须守卫宁可回退全量，不重放错起点
            var bogus = new LogicalAddressWire(999, 0, 1L << 40);
            ring1.SetOpaqueMeta(MemoryMarshal.Cast<LogicalAddressWire, byte>(stackalloc[] { bogus }));
            ring1.Prepare(seq: 1);
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol, deleteOnClose: false), vol.Fs);
        ResolveAnchor(ring2).Should().Be(ring2.BeginAddress, "锚点越过恢复尾=损坏 → 回退 Begin（宁可旧多重放）");

        // 回退后全量重建数据完整
        using var idx2 = NewHash(vol, ring2,
            hints: new ProbingIndexRecoveryHints(ResolveAnchor(ring2), ring2.TailAddress));
        idx2.EntryCount.Should().Be(1);
        var buf = new byte[8];
        KvTryGet(ring2, idx2, 1L, buf, out var len).Should().BeTrue();
        buf.AsSpan(0, len).ToArray().Should().BeEquivalentTo(new byte[] { 1 });
    }

    [Fact]
    public void Anchor_DisabledMeta_SetOpaqueMeta_FailsFast()
    {
        using var vol = new TestVolume();
        using var ring = RingOfLong.Create(
            TestRingSettingsFactory.On(vol, "kv-ring", deleteOnClose: false, metaKind: MetaPolicyKind.Disabled),
            vol.Fs);

        var act = () => ring.SetOpaqueMeta(new byte[16]);
        act.Should().Throw<InvalidOperationException>("Disabled meta 写侧拦截——组合层必须显式开 meta 才能登记锚点");
    }
}
