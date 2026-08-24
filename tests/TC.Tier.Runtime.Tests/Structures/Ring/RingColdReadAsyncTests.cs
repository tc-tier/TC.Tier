using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// async 冷分支测试——验证 GetKeyAsync/GetValueAsync/GetRecordAsync 在冷地址下不崩溃且返回正确数据。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt;（TKey=long 定长 key）。</para>
/// </summary>
public class RingColdReadAsyncTests
{
    [Fact]
    public async Task GetKeyAsync_ColdAddress_ReturnsCorrectKey()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(0x010203L, new byte[] { 10, 20 });
            ring.FlushUntil(ring.TailAddress);

            var rec = await ring.GetKeyAsync(addr);
            rec.Key.Should().Be(0x010203L);
            rec.ValueLength.Should().Be(2);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task GetValueAsync_ColdAddress_ReturnsCorrectValue()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20, 30, 40 });
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[10];
            int got = await ring.GetValueAsync(addr, dest);
            got.Should().Be(4);
            dest[..4].Should().Equal(new byte[] { 10, 20, 30, 40 });
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task GetKeyAsync_HotAddress_StillWorks()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(9L, new byte[] { 99 });

            var rec = await ring.GetKeyAsync(addr);
            rec.Key.Should().Be(9L);
        }
        finally { vol.Dispose(); }
    }



    [Fact]
    public void TryGetKey_HotAddress_ReturnsTypedKey()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(77L, new byte[] { 1 });

            ring.TryGetKey(addr, out long key).Should().BeTrue();
            key.Should().Be(77L);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task TryGetKeyAsync_ColdAddress_ReturnsTypedKey()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(88L, new byte[] { 1 });
            ring.FlushUntil(ring.TailAddress);

            var (key, ok) = await ring.TryGetKeyAsync(addr);
            ok.Should().BeTrue();
            key.Should().Be(88L);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>两步异步组合（GetKeyAsync + GetValueAsync）冷热读回——GetRecordAsync 退役后的等价覆盖。</summary>
    [Fact]
    public async Task GetKeyAsync_Then_GetValueAsync_ColdRoundTrip()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(0x0708L, new byte[] { 70, 80, 90 });
            ring.FlushUntil(ring.TailAddress);

            var rk = await ring.GetKeyAsync(addr);
            rk.Key.Should().Be(0x0708L);
            rk.ValueLength.Should().Be(3);

            var dest = new byte[3];
            (await ring.GetValueAsync(addr, dest)).Should().Be(3);
            dest.Should().Equal(new byte[] { 70, 80, 90 });
        }
        finally { vol.Dispose(); }
    }
}
