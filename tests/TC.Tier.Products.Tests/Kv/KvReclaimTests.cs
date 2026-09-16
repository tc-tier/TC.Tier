using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W6 契约测试——日志回收（tierkv-design.md §3.5，per-key 下界 + Compact 语义）：
/// 全表扫存活判定 → 安全下界 → TruncatePrefix 逻辑回收——回收后读安全（存活记录全数可读、
/// 被取代记录不可达）、墓碑终态保持、恢复等价（截断后重开=重放存活前缀）、索引帧互溶
/// （回收后检查点+重开仍走物化路径）。
/// </summary>
public class KvReclaimTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-w6-" + suffix);

    // ★ 写入用 FireAndForget+末尾收口（绕开 Ring flush×冷读缺陷——见 RingFlushColdReadReproTests；
    //   回收语义不依赖逐写刷盘，断言面完全一致）
    private static async Task<TierKvOfLongLong> SeedAsync(TestVolume vol, string suffix,
        params (long Key, long Value)[] items)
    {
        var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts(suffix));
        using var s = kv.CreateSession(KvSessionConditions.None);
        foreach (var (key, value) in items)
            await s.PutFormattedAsync(key, value, policy: KvCommitPolicy.FireAndForget);
        await s.CompletePendingAsync();
        return kv;
    }

    [Fact]
    public async Task Reclaim_AfterOverwrites_ReadSafe_AndBoundAtMinLive()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "basic",
            (1, 10L), (2, 20L), (3, 30L));

        // 同 key 多次覆写：key 1 五次、key 2 三次——旧记录全部被取代
        using (var s = kv.CreateSession())
        {
            for (long v = 11; v <= 15; v++) await s.PutFormattedAsync(1, v, policy: KvCommitPolicy.FireAndForget);
            for (long v = 21; v <= 23; v++) await s.PutFormattedAsync(2, v, policy: KvCommitPolicy.FireAndForget);
            await s.CompletePendingAsync();
        }

        var oldBegin = kv.Ring.BeginAddress;
        var result = await kv.ReclaimAsync();

        // 下界 = 存活最小地址（key 3 的唯一记录——最早写入即最新）
        result.SupersededCount.Should().Be(8, "key1×5 旧记录 + key2×3 旧记录被取代（11 写 − 3 存活）");
        kv.Ring.BeginAddress.Should().Be(result.NewBeginAddress);
        kv.Ring.BeginAddress.Should().BeGreaterThan(oldBegin, "前缀已推进");

        // 回收后读安全：三个 key 全数可读，值=最新
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(15L);
        (await kv.TryGetFormattedAsync(2)).Value.Should().Be(23L);
        (await kv.TryGetFormattedAsync(3)).Value.Should().Be(30L);
        kv.Count.Should().Be(3);
    }

    [Fact]
    public async Task Reclaim_TombstonedKeys_StatePreserved_AcrossReopen()
    {
        using var vol = new TestVolume();
        var opts = Opts("tomb");
        await using (var kv = await SeedAsync(vol, "tomb", (1, 10L), (2, 20L), (3, 30L)))
        {
            using var s = kv.CreateSession();
            (await s.DeleteAsync(2)).Should().BeTrue();   // key2 墓碑
            for (long v = 11; v <= 13; v++) await s.PutFormattedAsync(1, v);

            var result = await kv.ReclaimAsync();
            result.SupersededCount.Should().Be(5, "key1×3 旧记录 + key2 原记录 + key2 墓碑均被取代（7 写 − 2 存活）");
            kv.Count.Should().Be(2);
        }

        // 跨重开：回收后重开=重放存活前缀——终态等价（key2 保持删除）
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.Count.Should().Be(2, "key2 删除态经存活前缀重放保持");
        (await kv2.TryGetFormattedAsync(2)).Found.Should().BeFalse("墓碑终态不因回收复活");
        (await kv2.TryGetFormattedAsync(1)).Value.Should().Be(13L);
        (await kv2.TryGetFormattedAsync(3)).Value.Should().Be(30L);
    }

    [Fact]
    public async Task Reclaim_WithCheckpoint_FrameInterplay_FastRecoveryAfterReclaim()
    {
        using var vol = new TestVolume();
        var opts = Opts("frame");

        await using (var kv = await SeedAsync(vol, "frame", (1, 10L), (2, 20L)))
        {
            using var s = kv.CreateSession();
            for (long v = 11; v <= 60; v++) await s.PutFormattedAsync(1, v, policy: KvCommitPolicy.FireAndForget);   // 50 次覆写
            await s.CompletePendingAsync();
            await kv.CheckpointAsync();   // 帧 W 锚定覆写后
            for (long v = 61; v <= 70; v++) await s.PutFormattedAsync(1, v, policy: KvCommitPolicy.FireAndForget);
            await s.CompletePendingAsync();

            var result = await kv.ReclaimAsync();   // 回收 50+ 条被取代记录
            result.SupersededCount.Should().BeGreaterThan(40);
        }

        // 回收后重开：帧物化 + 增量重放（帧互溶——被回收的取代记录已在帧内或重放窗内重绑定）
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.IndexMainStorageAppliedLastRecovery.Should().BeTrue("回收不影响帧加速语义");
        kv2.Count.Should().Be(2);
        (await kv2.TryGetFormattedAsync(1)).Value.Should().Be(70L);
        (await kv2.TryGetFormattedAsync(2)).Value.Should().Be(20L);
    }

    [Fact]
    public async Task Reclaim_NothingSuperseded_NoOp()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "noop", (1, 10L), (2, 20L));

        var oldBegin = kv.Ring.BeginAddress;
        var result = await kv.ReclaimAsync();

        result.SupersededCount.Should().Be(0, "零覆写——无被取代记录");
        result.NewBeginAddress.Should().Be(oldBegin, "无可回收前缀——头边界不动");
    }

    [Fact]
    public async Task Reclaim_ConcurrentWithReads_AllReadsSafe()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "conc", (1, 10L), (2, 20L), (3, 30L), (4, 40L));
        using var s = kv.CreateSession();
        for (long v = 11; v <= 100; v++) await s.PutFormattedAsync(1, v);   // 大量覆写制造回收空间

        // 回收与读并发：读恒走索引最新地址 ≥ 下界——全数安全
        var reclaimTask = kv.ReclaimAsync();
        var readFailures = 0;
        for (var i = 0; i < 200; i++)
        {
            var (found, value) = await kv.TryGetFormattedAsync(1);
            if (!found || value is < 11L or > 100L) readFailures++;
            await kv.TryGetFormattedAsync(2);
        }
        await reclaimTask;

        readFailures.Should().Be(0, "回收并发读全数安全");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(100L, "回收后终态不变");
    }
}
