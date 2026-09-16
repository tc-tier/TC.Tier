using FluentAssertions;
using TC.Tier.Products.Kv;
using TC.Tier.Core.Testing;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// TierKv 时钟缝（故障注入面补全设计 件一 + §1.3 矩阵）——假钟驱动的 TTL 语义：
/// 墙钟前跳 = 过期提前判定生效；倒流 = 已判过期项不复活（单调水位守卫）；停走 = 单调路径照常推进。
/// <para>★ 与 KvExpirySweepTests（真实钟/手动 sweep 语义）互补——本组全零真实睡等。</para>
/// </summary>
public sealed class KvFakeClockTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-fakeclock-" + suffix).WithRangeIndex(false);

    [Fact]
    public async Task WallClockForwardJump_ExpiredEarly_Deterministic()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("fwd").WithClock(clock));

        await kv.PutFormattedAsync(1, 7, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.FromSeconds(60));
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(7, "未过期读命中");

        clock.Advance(TimeSpan.FromSeconds(61));   // 墙钟前跳过过期点（单调钟同步推进——零真实睡等）

        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("墙钟前跳 = 过期判定生效（§1.3 前跳行）");
    }

    [Fact]
    public async Task WallClockBackwardJump_JudgedExpiredItemsDoNotResurrect()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("back").WithClock(clock));

        await kv.PutFormattedAsync(1, 7, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(11));   // 过期
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("已判过期");

        clock.SetWallClock(FakeTimeProvider.DefaultStart);   // 墙钟倒流回起点
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("倒流不复活已判过期项（§1.3 倒流行——单调水位守卫）");

        // 倒流期新写项的过期点不早于水位——短 TTL 也不会立即消失
        await kv.PutFormattedAsync(2, 9, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.FromSeconds(5));
        (await kv.TryGetFormattedAsync(2)).Value.Should().Be(9, "倒流期新写项读命中（过期点经守卫锚定）");
    }

    [Fact]
    public async Task WallClockDrift_LeaseDurationMeasurablyScaled()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("drift").WithClock(clock));
        clock.SetDrift(2.0);   // 墙钟走速 ×2（单调钟恒 1）

        await kv.PutFormattedAsync(1, 7, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.FromSeconds(60));
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(7, "未过期读命中");

        clock.Advance(TimeSpan.FromSeconds(30));   // 单调 30s——墙钟按走速走满 60s

        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse(
            "漂移格（§1.3）：TTL 按墙钟计量——走速 2 下 60s 租约在 30s 单调秒内到期，偏差显式可测");
    }

    [Fact]
    public async Task MonoClock_ContinuesWhenWallFrozen()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("frozen").WithClock(clock));

        await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);   // 立即过期
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("Zero TTL = 立即过期");

        // 墙钟冻结（不 Advance 不跳变）——写路径照常推进（单调钟域操作）
        await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed);
        (await kv.TryGetFormattedAsync(2)).Value.Should().Be(2, "停走场景下单调驱动路径继续推进（§1.3 停走行）");
    }
}
