using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Value 溢出（WiscKey）读写全路径测试。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt;；溢出配置走 CreateOverflow/On(overflowPolicy)。</para>
/// </summary>
public class RingOverflowTests
{


    /// <summary>R1 防御：溢出转内联超限抛异常。
    /// ★ 新模型（AddressInfo 24B + HeaderSize 40 + TKey=long 8B）：原溢出 record payload = key(8)+AddressInfo(24)=32B，
    ///   unaligned=72 对齐 8=72，inline 槽位 = 72-40-8 = 24B。newValue 32 > 24 且 ≤ MinOverflowSize=32
    ///   → 不溢出、走内联 → 超限抛异常。</summary>
    [Fact]
    public void UpdateValue_OverflowToInline_TooLarge_Throws()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);

            var act = () => ring.UpdateValue(addr, new byte[32]);
            act.Should().Throw<InvalidOperationException>();
        }
        finally { vol.Dispose(); }
    }



    /// <summary>溢出 record 冷读正确（FlushUntil 后从设备读）。</summary>
    [Fact]
    public void GetValue_Overflow_ColdAddress_ReadsFromOverflow()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[512];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(512);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>溢出 record async 冷读正确。</summary>
    [Fact]
    public async Task GetValueAsync_Overflow_ColdAddress_ReadsFromOverflow()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[512];
            int got = await ring.GetValueAsync(addr, dest);
            got.Should().Be(512);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>部分页回源（ratio=0）对溢出 record 正确。</summary>
    [Fact]
    public void Overflow_PartialPage_RatioZero_ReadsCorrectly()
    {
        var vol = new TestVolume();
        try
        {
            var settings = TestRingSettingsFactory.On(vol, "ring", coldReadRatio: 0.0,
                overflowPolicy: OverflowPolicy.Enabled, minOverflowSize: 32);
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            LogicalAddress addr = ring.Write(1L, new byte[512]);
            ring.FlushUntil(ring.TailAddress);

            var dest = new byte[512];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(512);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>OverflowPolicy=Disabled 时 Value 内联（回归）。</summary>
    [Fact]
    public void Write_OverflowDisabled_InlinesValue()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20, 30 });

            var dest = new byte[3];
            ring.GetValue(addr, dest).Should().Be(3);
            dest.Should().Equal(new byte[] { 10, 20, 30 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>OverflowPolicy=Enabled 且 Value > MinOverflowSize 时写溢出设备（两步读回验证）。
    /// 注意：AddressInfo.Size 按 512B 粒度编码，Value 长度须是 512 的倍数。</summary>
    [Fact]
    public void Write_OverflowEnabled_ValueOverMin_WritesOverflow()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);

            var rk = ring.GetKey(addr);
            rk.Key.Should().Be(1L);
            rk.ValueLength.Should().Be(512);
            rk.IsOverflow.Should().BeTrue("大 value 应溢出");
            var dest = new byte[512];
            ring.GetValue(addr, dest).Should().Be(512);
            dest.Should().AllBeEquivalentTo(0);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>OverflowPolicy=Enabled 但 Value ≤ MinOverflowSize 时仍内联。</summary>
    [Fact]
    public void Write_OverflowEnabled_ValueUnderMin_InlinesValue()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });

            var rk = ring.GetKey(addr);
            rk.IsOverflow.Should().BeFalse("小 value 应内联");
            var dest = new byte[10];
            ring.GetValue(addr, dest).Should().Be(10);
            dest.Should().Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        }
        finally { vol.Dispose(); }
    }

    /// <summary>溢出→溢出：新 Value 写溢出设备，AddressInfo 更新。</summary>
    [Fact]
    public void UpdateValue_OverflowToOverflow_UpdatesAddressInfo()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);
            ring.UpdateValue(addr, new byte[512]);

            var dest = new byte[512];
            ring.GetValue(addr, dest).Should().Be(512);
            dest.Should().AllBeEquivalentTo(0);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>内联→溢出：新 Value 超阈值时转溢出。</summary>
    [Fact]
    public void UpdateValue_InlineToOverflow_ConvertsToOverflow()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[10]);
            ring.UpdateValue(addr, new byte[512]);

            ring.GetKey(addr).ValueLength.Should().Be(512);
            var dest = new byte[512];
            ring.GetValue(addr, dest).Should().Be(512);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>R3 防御：FlushUntil 后溢出数据落盘，跨实例恢复可读（两步版）。</summary>
    [Fact]
    public void Overflow_FlushUntil_DataDurable_CrossInstance()
    {
        var vol = new TestVolume();
        try
        {
            var settings = TestRingSettingsFactory.On(vol, "ring.0",
                deleteOnClose: false, metaKind: MetaPolicyKind.Managed,
                overflowPolicy: OverflowPolicy.Enabled, minOverflowSize: 32);

            LogicalAddress addr;
            using (var ring = TestRingSettingsFactory.NewRing<long>(vol, settings))
            {
                addr = ring.Write(1L, new byte[512]);
                ring.FlushUntil(ring.TailAddress);
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var rk = ring2.GetKey(addr);
            rk.Key.Should().Be(1L);
            rk.ValueLength.Should().Be(512);
            var dest = new byte[512];
            ring2.GetValue(addr, dest).Should().Be(512);
            dest.Should().AllBeEquivalentTo(0);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>R2 防御（两步版）：溢出 record 的 Key 不被 Value 覆盖。</summary>
    [Fact]
    public void GetKey_Overflow_KeyNotCorruptedByValue()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(0xDEADBEEFL, new byte[512]);

            ring.GetKey(addr).Key.Should().Be(0xDEADBEEFL, "Key 不应被溢出 Value 覆盖");
            var dest = new byte[512];
            ring.GetValue(addr, dest).Should().Be(512);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // ★ 异步溢出路径覆盖（P0 慢路径 + P2 异步读 + P0 异步翻转）
    // ════════════════════════════════════════════════════════════

    /// <summary>WriteAsync 大 value 触发溢出 → GetValueAsync 读回一致（hot 区异步溢出全链路）。</summary>
    [Fact]
    public async Task WriteAsync_Overflow_RoundtripsHotAsync()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[512];
            for (int i = 0; i < value.Length; i++) value[i] = (byte)(i & 0xFF);

            LogicalAddress addr = await ring.WriteAsync(1L, value);

            var dest = new byte[512];
            int got = await ring.GetValueAsync(addr, dest);
            got.Should().Be(512);
            dest.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }



    /// <summary>UpdateValueAsync 溢出→溢出翻转（异步路径）。</summary>
    [Fact]
    public async Task UpdateValueAsync_OverflowToOverflow()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);

            var value = new byte[512];
            for (int i = 0; i < value.Length; i++) value[i] = (byte)(i & 0xFF);
            await ring.UpdateValueAsync(addr, value);

            var dest = new byte[512];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(512);
            dest.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>UpdateValueAsync 内联→溢出翻转（异步路径）。</summary>
    [Fact]
    public async Task UpdateValueAsync_InlineToOverflow()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[10]);

            var value = new byte[512];
            for (int i = 0; i < value.Length; i++) value[i] = (byte)(i & 0xFF);
            await ring.UpdateValueAsync(addr, value);

            var dest = new byte[512];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(512);
            dest.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>UpdateValueAsync 溢出→内联回退（异步路径）。</summary>
    [Fact]
    public async Task UpdateValueAsync_OverflowToInline()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[512]);

            // 回退到内联：值须 fit 原溢出 record 的 inline slot（key 8B + AddressInfo 24B 对齐后槽位 24B），用 16B 安全
            var value = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
            await ring.UpdateValueAsync(addr, value);

            var dest = new byte[16];
            int got = ring.GetValue(addr, dest);
            got.Should().Be(16);
            dest.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // ★ 语义属性覆盖（P1）
    // ════════════════════════════════════════════════════════════



    /// <summary>RecordKey 的 IsOverflow 语义属性。</summary>
    [Fact]
    public async Task RecordKey_IsOverflow_ReflectsOverflowFlag()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress ovAddr = ring.Write(1L, new byte[512]);

            var key = await ring.GetKeyAsync(ovAddr);
            key.IsOverflow.Should().BeTrue();
            key.ValueLength.Should().Be(512);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>RecordKeyRef（ref struct 版）的 IsOverflow 语义属性。</summary>
    [Fact]
    public void RecordInfoRef_IsOverflow_ReflectsOverflowFlag()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress ovAddr = ring.Write(1L, new byte[512]);

            var keyRef = ring.GetKey(ovAddr);
            keyRef.IsOverflow.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }
}
