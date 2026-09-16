using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// A.2a 契约测试——后台过期回收（水位推进式 sweep，tierkv-ttl-cas-addressread-design.md §1/§4）：
/// TTL 写读闭环 + 水位推进 + 水位持久恢复 + sweep×手动回收并发 + 过期不发事件裁定钉。
/// <para>★ sweep 截断只推已刷前缀（clamp FlushedUntil）——本文件写入显式 Committed（下界可推进）。</para>
/// </summary>
public class KvExpirySweepTests
{
    private static TierKvOptions Opts(string suffix, bool range = true)
        => TierKvOptions.Default.WithKvName("tier-kv-a2a-" + suffix).WithRangeIndex(range);

    [Fact]
    public async Task Ttl_WriteRead_HitBeforeExpiry_MissAfter()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("ttl-cycle"));
        await kv.PutFormattedAsync(1, 7, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.FromSeconds(2));
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(7, "未过期读命中");

        await Task.Delay(2300);   // 时钟推过过期点（真实钟；TTL 语义秒/分级精度无损）
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("过期 = 惰性读删未命中");
    }

    [Fact]
    public async Task ScanWithExpiry_ExposesExpiryTicks_ZeroForPermanent()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("expiry-scan"));

        var nowTicks = DateTime.UtcNow.Ticks;
        await kv.PutFormattedAsync(1, 100, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.FromMinutes(5));
        await kv.PutFormattedAsync(2, 200, policy: KvCommitPolicy.Committed);   // 永久

        // 前缀扫描（含过期维度）：TTL 条目回传过期点，永久条目 ExpiryTicksUtc = 0
        var seen = new System.Collections.Generic.Dictionary<long, long>();
        await foreach (var e in kv.ScanByPrefixWithExpiryAsync(0, 0))
            seen[e.Key] = e.ExpiryTicksUtc;

        seen.Should().ContainKeys(1, 2);
        seen[1].Should().BeGreaterThan(nowTicks, "TTL 条目——过期点 = 写入时刻 + 5 分钟（UTC Ticks）");
        seen[2].Should().Be(0, "永久条目——0 = 无过期");

        // 范围扫描变体同语义
        var rangeSeen = new System.Collections.Generic.Dictionary<long, long>();
        await foreach (var e in kv.ScanByRangeWithExpiryAsync(0, long.MaxValue))
            rangeSeen[e.Key] = e.ExpiryTicksUtc;
        rangeSeen[1].Should().Be(seen[1], "两变体同源帧解析——过期点一致");
        rangeSeen[2].Should().Be(0);
    }

    [Fact]
    public async Task Sweep_ExpiredPrefix_BeginAdvances_CounterGrows_ScanClean()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("advance"));
        await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);
        await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);
        var addr3 = await kv.PutFormattedAsync(3, 3, policy: KvCommitPolicy.Committed);

        var oldBegin = kv.Ring.BeginAddress;
        await kv.ForceExpirySweepAsync();

        kv.Ring.BeginAddress.Should().Be(addr3, "连续过期前缀 [addr1, addr3) 整体回收——下界钉在存活记录");
        kv.Ring.BeginAddress.Should().BeGreaterThan(oldBegin, "回收线已推进");
        kv.ExpiredSupersededCount.Should().Be(2, "两条过期记录被强删");
        kv.SweepWatermark.Should().Be(addr3, "扫描水位推进至截断边界");
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("过期项读未命中");
        (await kv.TryGetFormattedAsync(3)).Value.Should().Be(3, "存活记录回收后读安全");

        var keys = new List<long>();
        await foreach (var e in kv.ScanByPrefixAsync(0, 0))
            keys.Add(e.Key);
        keys.Should().BeEquivalentTo([3L], "扫描不见已回收旧项");
    }

    [Fact]
    public async Task Sweep_WatermarkPersisted_RestoreResumes()
    {
        using var vol = new TestVolume();
        var opts = Opts("persist");
        LogicalAddress wmBefore;
        await using (var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);
            await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);
            var addr3 = await kv.PutFormattedAsync(3, 3, policy: KvCommitPolicy.Committed);
            await kv.ForceExpirySweepAsync();
            wmBefore = kv.SweepWatermark;
            wmBefore.IsValid.Should().BeTrue("sweep 后水位已建立");
            wmBefore.Should().Be(addr3);
        }

        // 重启：水位自 opaque 续接（不回退 Invalid）——续扫语义恢复
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.SweepWatermark.Should().Be(wmBefore, "扫描水位随 opaque 持久化恢复");
        kv2.LastSweepScannedCount.Should().Be(0, "重启后尚未扫描");

        await kv2.ForceExpirySweepAsync();
        kv2.LastSweepScannedCount.Should().Be(1, "增量续扫只过 [水位, 尾) 存活记录——不重复全扫");
        kv2.Ring.BeginAddress.Should().Be(wmBefore, "无新增死记录——回收线不动");
        (await kv2.TryGetFormattedAsync(3)).Value.Should().Be(3);
    }

    [Fact]
    public async Task Sweep_ConcurrentWithManualReclaim_BeginMonotonic()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("conc"));
        await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed);
        using (var s = kv.CreateSession(KvSessionConditions.None))
        {
            for (long v = 11; v <= 40; v++)
                await s.PutFormattedAsync(1, v, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);
            await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed);
        }

        // sweep 与手动回收交错并发：两者同路（提交门内串行）+ 回收线单调（TruncatePrefix CAS 推进）
        var results = new List<LogicalAddress>();
        for (var round = 0; round < 3; round++)
        {
            var sweepTask = kv.ForceExpirySweepAsync();
            var reclaim = await kv.ReclaimAsync();
            results.Add(reclaim.NewBeginAddress);
            await sweepTask;
            results.Add(kv.Ring.BeginAddress);
        }

        results.Should().BeInAscendingOrder("回收线单调不回退（sweep×手动回收交错）");
        (await kv.TryGetFormattedAsync(2)).Value.Should().Be(2, "存活数据全数安全");
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("过期项读未命中");
    }

    [Fact]
    public async Task Sweep_Expired_NoDeleteEvent()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("noevent"));
        var addr1 = await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed,
            timeToLive: TimeSpan.FromMilliseconds(120));
        await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed);

        var events = new List<KvWatchEvent<long>>();
        using var cts = new CancellationTokenSource(1200);
        var watchTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var ev in kv.WatchAsync(addr1, cts.Token))
                    events.Add(ev);   // 历史补扫 (addr1, ...] + 实时直通
            }
            catch (OperationCanceledException)
            {
                // 取消收口——取证完成
            }
        });
        await Task.Delay(250);    // 过期成立
        await kv.ForceExpirySweepAsync();   // 物理强删——不发事件（裁定回归钉）
        await Task.Delay(400);
        await cts.CancelAsync();
        await watchTask;

        events.Should().NotContain(ev => ev.Kind == KvWatchEventKind.Delete,
            "过期不发事件、回收强删不发事件（裁定自洽）");
        events.Should().ContainSingle(ev => ev.Key == 2 && ev.Kind == KvWatchEventKind.Put,
            "历史补扫只含订阅点之后的存活 Put");
    }
}
