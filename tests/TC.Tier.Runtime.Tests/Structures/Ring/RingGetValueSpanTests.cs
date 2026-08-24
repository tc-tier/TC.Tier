using FluentAssertions;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// GetValueSpan（零拷贝值交付）契约测试——1:1 于 RingBase.GetRecords 的 GetValueSpan/ReadScope。
/// <para>★ 契约面：热/冷切片内容 == GetValue 拷贝口径；溢出 record 回退拷贝交付；scope 生命周期护栏（形态）。</para>
/// </summary>
public class RingGetValueSpanTests
{
    [Fact]
    public void HotSpan_EqualsCopiedValue()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[64];
            new Random(7).NextBytes(value);
            var addr = ring.Write(42L, value);

            using (ring.EnterReadScope())
            {
                var span = ring.GetValueSpan(addr);
                span.Length.Should().Be(64);
                span.ToArray().Should().Equal(value, "零拷贝切片须与拷贝口径同字节");
            }

            var copied = new byte[64];
            ring.GetValue(addr, copied).Should().Be(64);
            copied.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void ColdSpan_EqualsCopiedValue()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[64];
            new Random(11).NextBytes(value);
            var addr = ring.Write(42L, value);
            ring.FlushUntil(ring.TailAddress);   // 推 FlushUntil → 冷路径（ClockCache 缓存页切片）

            using (ring.EnterReadScope())
            {
                var span = ring.GetValueSpan(addr);
                span.Length.Should().Be(64);
                span.ToArray().Should().Equal(value, "冷区经缓存页切片须与拷贝口径同字节");
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void OverflowRecord_SpanFallsBackToCopy()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[512];
            new Random(13).NextBytes(value);
            var addr = ring.Write(1L, value);

            using (ring.EnterReadScope())
            {
                var span = ring.GetValueSpan(addr);
                span.Length.Should().Be(512);
                span.ToArray().Should().Equal(value, "溢出 record 回退拷贝交付——内容同字节");
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void MultipleRecords_SpansAllCorrect()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            const int n = 100;
            var values = new byte[n][];
            var addrs = new LogicalAddress[n];
            for (long k = 0; k < n; k++)
            {
                values[k] = new byte[16 + (int)k];
                new Random((int)k).NextBytes(values[k]);
                addrs[k] = ring.Write(k, values[k]);
            }

            using (ring.EnterReadScope())
                for (int k = 0; k < n; k++)
                    ring.GetValueSpan(addrs[k]).ToArray().Should().Equal(values[k]);
        }
        finally { vol.Dispose(); }
    }
}
