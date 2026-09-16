using System.Text;
using FluentAssertions;
using TC.Tier.Products.Kv;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// TierKvBuilder 显式装配契约（W1）：formatter 缺省 = <see cref="ValueFormatters.Auto{TValue}"/>
/// （byte/byte[]/ROM&lt;byte&gt;/unmanaged 零声明）；工厂未注入 fail-fast；Settings 经
/// <see cref="TierKvAssembly"/> 翻译（封闭形态生成物同款装配路径——RingOfLong/HashOfLong 为
/// Runtime 内建 [RingKey] 封闭形态）。
/// </summary>
public class TierKvBuilderTests
{
    // ═══ ValueFormatters.Auto<T>（D6 内建覆盖面的运行时解析臂）═══

    [Fact]
    public void Auto_Resolves_Builtins_Roundtrip()
    {
        Round(ValueFormatters.Auto<byte>(), (byte)0xAB).Should().Be((byte)0xAB);
        Round(ValueFormatters.Auto<long>(), 42L).Should().Be(42L);
        Round(ValueFormatters.Auto<byte[]>(), "payload"u8.ToArray())
            .Should().BeEquivalentTo("payload"u8.ToArray(), "byte[] 变长值应逐字节往返");
        Round(ValueFormatters.Auto<ReadOnlyMemory<byte>>(), (ReadOnlyMemory<byte>)"mem"u8.ToArray())
            .ToArray().Should().BeEquivalentTo("mem"u8.ToArray(), "ROM<byte> 应逐字节往返");

        static T Round<T>(IValueFormatter<T> formatter, T value)
        {
            var buffer = new byte[formatter.GetSize(value)];
            formatter.Format(value, buffer);
            return formatter.Parse(buffer);
        }
    }

    [Fact]
    public void Auto_ManagedType_WithoutBuiltin_Throws()
    {
        var act = () => ValueFormatters.Auto<string>();
        act.Should().Throw<InvalidOperationException>("string 无内建 formatter——须用户实现后显式注入");
    }

    // ═══ Builder 显式装配（formatter 缺省 Auto + 工厂注入）═══

    [Fact]
    public async Task Builder_FormatterOmitted_UsesAuto_StartsAndRoundtrips()
    {
        using var vol = new TestVolume();
        var options = TierKvOptions.Default.WithKvName("tier-kv-builder");
        await using var kv = await new TierKvBuilder<long, long>(vol.Fs, options)   // formatter 省略
            .WithRingFactory((fs, o) => new RingOfLong(TierKvAssembly.RingSettings(fs, o), fs))
            .WithIndexFactory((fs, o, ring) => new HashOfLong(fs, TierKvAssembly.HashSettings(fs, o), ring))
            .StartAsync();

        await kv.PutFormattedAsync(1, 7L);
        (await kv.TryGetFormattedAsync(1)).Should().Be((true, 7L));
    }

    [Fact]
    public async Task Builder_MissingFactories_FailsFast()
    {
        using var vol = new TestVolume();
        var builder = new TierKvBuilder<long, long>(vol.Fs, TierKvOptions.Default.WithKvName("tier-kv-nof"));

        await FluentActions.Awaiting(() => builder.StartAsync())
            .Should().ThrowAsync<InvalidOperationException>("Ring/索引工厂未注入——显式层必须注入（缺省层走 [KvStore] 封闭形态）");
    }
}
