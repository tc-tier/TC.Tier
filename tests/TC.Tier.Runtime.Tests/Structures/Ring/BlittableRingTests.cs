using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// BlittableRing 读写往返测试（泛型改版后：TKey=long 定长 key）。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt; 一步生命周期。</para>
/// </summary>
public class BlittableRingTests
{
    [Fact]
    public void Write_Then_GetRecord_ReadsBack_KeyValue()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            const long key = 0x01020304;
            var value = new byte[] { 10, 20, 30, 40, 50 };

            LogicalAddress addr = ring.Write(key, value);
            // ★ 首条 record 落在新文件 page 0 offset 0（tail 初始 0）——逻辑地址 0 是合法地址，非错误码。
            addr.Should().BeGreaterThanOrEqualTo(LogicalAddress.Empty);

            var rec = ring.GetKey(addr);
            rec.Key.Should().Be(key);
            rec.ValueLength.Should().Be(value.Length);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_KeyIsValueItself_GetRecord_ReadsBack()
    {
        // 泛型改版：无 key 形态已废（TKey 恒 sizeof(TKey) 定长）——key 是值本身，直读断言
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[] { 7, 8, 9 };

            LogicalAddress addr = ring.Write(5L, value);
            var rec = ring.GetKey(addr);
            rec.Key.Should().Be(5L);
            rec.ValueLength.Should().Be(3);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void GetValue_Fills_Destination_Buffer()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[] { 1, 2, 3, 4, 5 };
            LogicalAddress addr = ring.Write(1L, value);

            var dest = new byte[5];
            int filled = ring.GetValue(addr, dest);
            filled.Should().Be(5);
            dest.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }


    [Fact]
    public void UpdateValue_Overwrites_InPlace()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var v1 = new byte[] { 1, 1 };
            LogicalAddress addr = ring.Write(1L, v1);

            var v2 = new byte[] { 2, 2, 2, 2 };
            ring.UpdateValue(addr, v2);

            var rec = ring.GetKey(addr);
            rec.ValueLength.Should().Be(4);
            var dest = new byte[4];
            ring.GetValue(addr, dest);
            dest.Should().Equal(v2);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task WriteAsync_Then_GetRecordAsync_ReadsBack()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            const long key = 0x0909;
            var value = new byte[] { 1, 2, 3 };

            LogicalAddress addr = await ring.WriteAsync(key, value);
            var rec = await ring.GetKeyAsync(addr);
            rec.Key.Should().Be(key);
            rec.ValueLength.Should().Be(3);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_ExceedsPageSize_Throws()
    {
        // PageSize=4K，写 > 4K record 应抛异常（单 record 不跨页契约）
        var (settings, vol) = TestRingSettingsFactory.Create(pageSize: AlignmentConst.Alignment4K);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            var hugeValue = new byte[5000];   // > 4K PageSize
            Action act = () => ring.Write(1L, hugeValue);
            act.Should().Throw<InvalidOperationException>();   // "Entry does not fit on page"
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Write_Multiple_CrossPage_Succeeds()
    {
        // 小页 + 多 record 触发跨页
        var (settings, vol) = TestRingSettingsFactory.Create(
            pageSize: AlignmentConst.Alignment4K, memorySize: 64 * 1024);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            // 写 20 条 512B record（总 10K，跨多个 4K 页）
            var addresses = new LogicalAddress[20];
            for (int i = 0; i < 20; i++)
            {
                addresses[i] = ring.Write(i, new byte[512]);
            }
            // 每条都能读回
            for (int i = 0; i < 20; i++)
            {
                var rec = ring.GetKey(addresses[i]);
                rec.Key.Should().Be(i);
                rec.ValueLength.Should().Be(512);
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void TailAddress_Advances_After_Write()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress tailBefore = ring.TailAddress;
            ring.Write(1L, new byte[] { 1 });
            ring.TailAddress.Should().BeGreaterThan(tailBefore);
        }
        finally { vol.Dispose(); }
    }
}
