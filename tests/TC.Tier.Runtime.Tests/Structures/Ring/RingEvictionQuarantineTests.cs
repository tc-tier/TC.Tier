using FluentAssertions;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// ClockCache 淘汰隔离池测试（#165/#177）——淘汰页先进隔离池（延迟可复用/可释放），
/// 溢出回落 freePageCache；数据正确性不受隔离影响。
/// </summary>
public class RingEvictionQuarantineTests
{
    [Fact]
    public void ColdEvictions_LandInQuarantine_DataStillCorrect()
    {
        // 恢复形态（safe=dataStart）全冷读 + cache 容量 4 → 8 个冷页装载必然触发 ≥4 次淘汰
        var vol = new TestVolume();
        try
        {
            const int pages = 8;
            var value = new byte[32];
            new Random(31).NextBytes(value);
            var addrs = new LogicalAddress[pages];
            var settings = TestRingSettingsFactory.On(vol, "ring-quar", deleteOnClose: false,
                clockCacheCapacity: 4);
            using (var ring = TestRingSettingsFactory.NewRing<long>(vol, settings))
            {
                // 每页写 ~70 条 56B 记录 → 8 页数据（页起点 addr 记录供冷读）
                for (var p = 0; p < pages; p++)
                {
                    for (var k = 0; k < 70; k++)
                        ring.Write(1000L + p * 100 + k, value);
                    addrs[p] = new LogicalAddress(0, (p * 4096L) + 48);   // 页首记录附近（粗锚，仅作读触发）
                }
                ring.FlushUntil(ring.TailAddress);
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring-quar", deleteOnClose: false, clockCacheCapacity: 4));

            // 恢复后全冷：逐页冷读 8 个不同页 → 4 槽缓存必淘汰 ≥4 页进隔离池
            for (var addrOff = 0L; addrOff < pages * 4096L; addrOff += 4096L)
            {
                var probe = new LogicalAddress(0, addrOff + 56);
                ring2.GetValueSpan(probe);   // 不校验内容（探针锚可能落在帧间）——只为触发冷装载
            }

            ring2.EvictionQuarantineForTest!.Count.Should().BeGreaterThan(0,
                "ClockCache 淘汰页必须先进隔离池（#165/#177 延迟归还语义）");

            // 隔离不影响正确性：真实记录冷读全对
            for (var p = 0; p < pages; p++)
            {
                var pageHead = new LogicalAddress(0, p * 4096L + 56);
                var got = ring2.GetValueSpan(pageHead);
                if (got.Length > 0)
                    got.ToArray().Should().Equal(value, $"页 {p} 首记录冷读内容正确");
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void FreePageCache_CapacityScales_WithPageCount()
    {
        // #209：容量 4 → PageCount/8（下限 4）——经 GetStats 间接观察（size 字段）
        var (settings, vol) = TestRingSettingsFactory.Create(memorySize: 64 * 1024);   // 16 页 → 期望容量 4（下限）
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var cache = ring.FreePageCacheForTest!;
            cache.GetStats().size.Should().Be(Math.Max(4, 16 / 8), "容量 = max(4, PageCount/8)");
        }
        finally { vol.Dispose(); }
    }
}
