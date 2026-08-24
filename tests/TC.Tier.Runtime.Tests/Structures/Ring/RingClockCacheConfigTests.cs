using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// ClockCache 容量配置化测试——验证 ColdReadRatio / ClockCacheCapacity 配置生效。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt; 一步生命周期。</para>
/// </summary>
public class RingClockCacheConfigTests
{
    /// <summary>默认 ColdReadRatio=0.25 时，冷读功能正常（行为等价改前）。</summary>
    [Fact]
    public void ColdReadRatio_Default_ColdReadWorks()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(2);
            dest[..2].Should().Equal(new byte[] { 10, 20 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>ColdReadRatio=0 时不崩溃，冷读仍正确。</summary>
    [Fact]
    public void ColdReadRatio_Zero_ColdReadWorks()
    {
        var (settings, vol) = TestRingSettingsFactory.Create(coldReadRatio: 0.0);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20, 30 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(3);
            dest[..3].Should().Equal(new byte[] { 10, 20, 30 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>ColdReadRatio=1.0 时不崩溃，冷读正确。</summary>
    [Fact]
    public void ColdReadRatio_One_ColdReadWorks()
    {
        var (settings, vol) = TestRingSettingsFactory.Create(coldReadRatio: 1.0);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(2);
            dest[..2].Should().Equal(new byte[] { 10, 20 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>ClockCacheCapacity 显式设置时不崩溃，冷读正确。</summary>
    [Fact]
    public void ClockCacheCapacity_Explicit_ColdReadWorks()
    {
        var (settings, vol) = TestRingSettingsFactory.Create(clockCacheCapacity: 8);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(2);
            dest[..2].Should().Equal(new byte[] { 10, 20 });
        }
        finally { vol.Dispose(); }
    }
}
