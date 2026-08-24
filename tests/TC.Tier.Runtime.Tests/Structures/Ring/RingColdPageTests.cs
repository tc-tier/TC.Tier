using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 冷页回源测试——验证 GetRecord/GetValue/ReadRecord 冷热透明化 + ClockCache 缓存 + 批量读。
/// <para>★ 核心验证：给地址返回数据，不关心冷热处理——冷区也能正确读。</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt;（TKey=long 定长 key）。</para>
/// </summary>
public class RingColdPageTests
{
    /// <summary>GetRecord 冷区返回正确数据——写→flush 制造冷区→GetRecord→数据正确（不再脏读）。</summary>
    [Fact]
    public void GetRecord_ColdRegion_ReturnsCorrectData()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, new byte[] { 10, 20, 30 });
            LogicalAddress addr2 = ring.Write(2L, new byte[] { 40, 50 });
            ring.FlushUntil(ring.TailAddress);   // 全部落盘 → 冷区

            // 冷区读——应返回正确数据（不再脏读）
            var rec = ring.GetKey(addr2);
            rec.Key.Should().Be(2L, "冷区 key 应正确读回");
            rec.ValueLength.Should().Be(2, "冷区 value 长度应正确");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>GetValue 冷区返回正确数据。</summary>
    [Fact]
    public void GetValue_ColdRegion_ReturnsCorrectData()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20, 30, 40 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(4, "冷区 GetValue 应返回 4 字节 value");
            dest[..4].Should().Equal(new byte[] { 10, 20, 30, 40 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>冷热混合——冷区和热区 record 都能正确读。</summary>
    [Fact]
    public void GetRecord_HotAndCold_BothCorrect()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            // 冷区
            LogicalAddress coldAddr = ring.Write(0xAAL, new byte[] { 1, 2 });
            ring.FlushUntil(ring.TailAddress);

            // 热区
            LogicalAddress hotAddr = ring.Write(0xBBL, new byte[] { 3, 4 });

            // 冷区读
            var coldRec = ring.GetKey(coldAddr);
            coldRec.Key.Should().Be(0xAAL, "冷区 key 正确");

            // 热区读
            var hotRec = ring.GetKey(hotAddr);
            hotRec.Key.Should().Be(0xBBL, "热区 key 正确");
        }
        finally { vol.Dispose(); }
    }


    /// <summary>批量读回调 handler——收集所有 record 的 key（IKeyResolver 时代的批量聚簇形态）。</summary>
    private sealed class CollectingHandler : IReadOnlyRecordHandler<long>
    {
        public readonly System.Collections.Generic.List<long> Keys = new();
        public void Handle(LogicalAddress address, long key, int valueLength, ushort flags)
            => Keys.Add(key);
    }

    /// <summary>批量读——同页 N 地址合并 I/O（回调收到全部 record）。</summary>
    [Fact]
    public void GetRecords_Batch_AllReceived()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            LogicalAddress addr1 = ring.Write(1L, new byte[] { 10 });
            LogicalAddress addr2 = ring.Write(2L, new byte[] { 20 });
            LogicalAddress addr3 = ring.Write(3L, new byte[] { 30 });
            ring.FlushUntil(ring.TailAddress);   // 冷区

            var handler = new CollectingHandler();
            ring.GetRecords(new[] { addr1, addr2, addr3 }, handler);

            handler.Keys.Should().HaveCount(3, "批量读应回调 3 次");
            handler.Keys.Should().Equal(new long[] { 1, 2, 3 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>批量读——冷区+热区混合。</summary>
    [Fact]
    public void GetRecords_Batch_MixedHotCold()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            // 冷区
            LogicalAddress addr1 = ring.Write(0xAAL, new byte[] { 1 });
            LogicalAddress addr2 = ring.Write(0xBBL, new byte[] { 2 });
            ring.FlushUntil(ring.TailAddress);

            // 热区
            LogicalAddress addr3 = ring.Write(0xCCL, new byte[] { 3 });

            var handler = new CollectingHandler();
            ring.GetRecords(new[] { addr1, addr2, addr3 }, handler);

            handler.Keys.Should().HaveCount(3, "批量读应回调 3 次");
            handler.Keys.Should().Equal(new long[] { 0xAA, 0xBB, 0xCC });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>ColdReadRatio=0 时冷读正确（强制部分页回源，不进 ClockCache）。</summary>
    [Fact]
    public void PartialPageRestore_RatioZero_ColdReadCorrect()
    {
        var (settings, vol) = TestRingSettingsFactory.Create(coldReadRatio: 0.0);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            LogicalAddress addr = ring.Write(0xAAL, new byte[] { 1, 2, 3, 4, 5 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(5);
            dest[..5].Should().Equal(new byte[] { 1, 2, 3, 4, 5 });

            var key = ring.GetKey(addr);
            key.Key.Should().Be(0xAAL);
            key.ValueLength.Should().Be(5);
        }
        finally { vol.Dispose(); }
    }



    /// <summary>record 超过 ColdRecordBufferLimit 时回退整页路径，仍正确。</summary>
    [Fact]
    public void PartialPageRestore_OverLimit_FallsBackToFullPage()
    {
        var (settings, vol) = TestRingSettingsFactory.Create(coldReadRatio: 0.0, coldRecordBufferLimit: 64);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            LogicalAddress addr = ring.Write(1L, new byte[100]);
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[100];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(100);
            dest.Should().AllBeEquivalentTo(0);
        }
        finally { vol.Dispose(); }
    }
}
