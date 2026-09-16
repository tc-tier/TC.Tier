using System.Text;
using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// [KvStore] 封闭形态端到端契约（W1 阶段2 验收）：测试程序集 [assembly: KvStore] 一行声明 →
/// 生成器产出封闭类编译进本程序集——编译通过本身即生成器契约（RingClosedSpecializationTests
/// 同款验收）。本文件补功能面：CreateAsync 零代码装配（formatter 零声明）+ 类型化值面直存直取 +
/// IndexKind 双层路由（编译期缺省/运行覆盖）+ 删除/计数/未命中。
/// <para>★ mem 介质（TestVolume）——TierKvAssembly 引擎选项含 mem 不预分配守卫。</para>
/// </summary>
public class TierKvClosedFormTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-" + suffix);

    // ═══ unmanaged 值零 formatter 声明（D6 裁定验收：byte/值类型零 formatter 实现）═══

    [Fact]
    public async Task LongLong_CreateAsync_TypedRoundtrip_Overwrite_Delete()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("ll"));

        await kv.PutFormattedAsync(1, 42L);
        var (found, value) = await kv.TryGetFormattedAsync(1);
        found.Should().BeTrue();
        value.Should().Be(42L, "formatter 自动发射（Blittable）应直存直取");

        // 覆写：append-only 换绑——最新写胜出，计数不变
        await kv.PutFormattedAsync(1, 43L);
        (await kv.TryGetFormattedAsync(1)).Should().Be((true, 43L));
        kv.Count.Should().Be(1);

        // 未命中
        (await kv.TryGetFormattedAsync(-1)).Found.Should().BeFalse();

        // 删除：墓碑语义
        (await kv.DeleteAsync(1)).Should().BeTrue();
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("删除后应未命中");
        kv.Count.Should().Be(0);
    }

    [Fact]
    public async Task LongByte_CreateAsync_ValueTypeRoundtrip()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongByte.CreateAsync(vol.Fs, Opts("lb"));

        await kv.PutFormattedAsync(7, 0xAB);
        (await kv.TryGetFormattedAsync(7)).Should().Be((true, 0xAB));
    }

    // ═══ byte[] 变长值 ═══

    [Fact]
    public async Task LongByteArray_CreateAsync_VariableLengthRoundtrip()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongByteArray.CreateAsync(vol.Fs, Opts("lba"));

        var payload = "hello tier-kv"u8.ToArray();
        await kv.PutFormattedAsync(2, payload);
        var (found, value) = await kv.TryGetFormattedAsync(2);
        found.Should().BeTrue();
        value.Should().BeEquivalentTo(payload, "byte[] 变长值应原样往返");

        // 覆写变短：最新写胜出
        await kv.PutFormattedAsync(2, "hi"u8.ToArray());
        (await kv.TryGetFormattedAsync(2)).Value.Should().BeEquivalentTo("hi"u8.ToArray());
    }

    // ═══ 自定义 formatter（managed TValue + Formatter= 显式声明）═══

    [Fact]
    public async Task TestKeyTestPayload_CustomFormatter_Utf8Roundtrip()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfTestKeyTestPayload.CreateAsync(vol.Fs, Opts("tk"));

        var key = new TestKey(9, 5);
        await kv.PutFormattedAsync(key, new TestPayload("你好 tier-kv"));
        var (found, value) = await kv.TryGetFormattedAsync(key);
        found.Should().BeTrue();
        value.Text.Should().Be("你好 tier-kv", "自定义 formatter（UTF-8）应往返");

        // 双字段结构体 key 判等闭环
        (await kv.TryGetFormattedAsync(new TestKey(9, 6))).Found.Should().BeFalse("Tag 不同 = 不同 key");
    }

    // ═══ IndexKind 双层路由（§0.1：编译期缺省 + 运行覆盖）═══

    [Fact]
    public async Task IndexKind_AttributeDefault_BTree_CompiledIn()
    {
        using var vol = new TestVolume();
        // 标注 IndexKind=(int)BTree——CreateAsync 不传 options 时用编译期缺省
        await using var kv = await TierKvOfLongDouble.CreateAsync(vol.Fs);

        await kv.PutFormattedAsync(3, 3.14);
        (await kv.TryGetFormattedAsync(3)).Should().Be((true, 3.14), "IndexKind 编译期缺省装配应可读写");
    }

    [Theory]
    [InlineData(KvIndexKind.Hash)]
    [InlineData(KvIndexKind.BTree)]
    [InlineData(KvIndexKind.SkipList)]
    public async Task IndexKind_RuntimeOverride_RoutesAllThreeFamilies(KvIndexKind kind)
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("route-" + kind).WithIndexKind(kind));

        for (long k = 1; k <= 10; k++)
            await kv.PutFormattedAsync(k, k * 10);
        kv.Count.Should().Be(10);

        var buf = new byte[8];
        (await kv.TryGetAsync(5, buf)).Should().BeTrue("字节面（ITierKv）随族路由可用");
        BitConverter.ToInt64(buf).Should().Be(50);
        (await kv.TryGetFormattedAsync(7)).Value.Should().Be(70, $"IndexKind 运行覆盖路由 {kind} 应读写一致");
    }

    [Fact]
    public async Task ByteFace_ITierKv_Interface_Consumable()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("iface"));

        await ConsumeFaceAsync(kv);   // 封闭形态 IS-A TierKv——ITierKv 消费面直通（经签名消费）
    }

    /// <summary>经接口签名消费（封闭形态向上转型 ITierKv——字节层消费面直通）。</summary>
    // ★ 本测试的验收点恰是"经 ITierKv 接口消费封闭形态"——CA1859 建议的具象化与验收点相反，设计必需豁免
#pragma warning disable CA1859
    private static async ValueTask ConsumeFaceAsync(ITierKv<long> face)
    {
        await face.PutAsync(1, "raw"u8.ToArray());
        face.Count.Should().Be(1);
        (await face.TryGetBytesAsync(1)).Should().BeEquivalentTo("raw"u8.ToArray());
    }
#pragma warning restore CA1859
}
