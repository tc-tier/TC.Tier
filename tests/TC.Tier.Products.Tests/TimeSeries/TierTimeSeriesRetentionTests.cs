namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>
/// TierTimeSeries retention 测试（tc-tier-timeseries-spec §11 验证矩阵 5/12——手动 trim/TTL 自动/字节上限）。
/// </summary>
public sealed class TierTimeSeriesRetentionTests
{
    private static long Base => DateTime.UtcNow.Ticks;

    // ══ 矩阵 5：retention trim——Trim(before) → Range 老窗空 + TrimmedUntil 持久 + 索引同步清理 ══

    [Fact]
    public async Task Truncate_OldWindowEmptied_IndexCleanedInSync()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long b = Base;
        const int n = 50;
        for (int i = 0; i < n; i++)
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
        await s.FlushAsync(default);

        long before = (await s.GetStatsAsync(default)).IndexEntryCount;
        before.Should().Be(n);

        var deleted = await s.TruncateAsync(b + 30 * TimeSpan.TicksPerSecond, default);
        deleted.Should().Be(30);
        s.TrimmedUntilTimestamp.Should().Be(b + 30 * TimeSpan.TicksPerSecond);

        // 老窗空 + 新窗完整 + 索引同步清理
        var stats = await s.GetStatsAsync(default);
        stats.IndexEntryCount.Should().Be(n - 30, "索引与数据同生命周期（spec §5 步骤 2）");
        stats.SampleCount.Should().Be(n - 30);
        var old = new List<double>();
        await foreach (var (_, v, _) in s.RangeAsync(b, b + 30 * TimeSpan.TicksPerSecond, default))
            old.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        old.Should().BeEmpty("已回收区间查询为空");
        var kept = new List<double>();
        await foreach (var (_, v, _) in s.RangeAsync(b + 30 * TimeSpan.TicksPerSecond, b + n * TimeSpan.TicksPerSecond, default))
            kept.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        kept.Should().Equal(Enumerable.Range(30, n - 30).Select(i => (double)i));
    }

    [Fact]
    public async Task Truncate_AllSamples_DeletesEverything()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);
        long b = Base;
        for (int i = 0; i < 10; i++)
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
        await s.FlushAsync(default);

        var deleted = await s.TruncateAsync(long.MaxValue, default);
        deleted.Should().Be(10, "全部样本早于边界 → 全回收");
        (await s.LatestAsync(default)).Should().BeNull();
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(0);
        // 水位推进到 long.MaxValue 有守卫悖论（Append 全拒）——补一轮可见语义验证
        s.TrimmedUntilTimestamp.Should().Be(long.MaxValue);
    }

    // ══ 矩阵 12：TTL 自动——RetentionTime 极短 → 后台循环自动 trim + 水位推进 ══

    [Fact]
    public async Task TtlAutoBackgroundLoop_TrimsAndAdvancesWatermark()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            SeriesName = "tc.series.ttl",
            RetentionTime = TimeSpan.FromMilliseconds(500),
            RetentionScanInterval = TimeSpan.FromMilliseconds(150),
        });

        long b = Base;
        long step = TimeSpan.TicksPerSecond / 10;   // 100ms 步距——10 样本跨 1s（TTL 过期等待秒级收敛）
        for (int i = 0; i < 10; i++)
            await s.AppendAsync(b + i * step, TierTimeSeriesTestFactory.Val(i), default);
        await s.FlushAsync(default);

        // 轮询等待后台 trim：确定性收敛条件 = 水位越过末条样本 ts（全部样本已回收的充要可观察面）
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (s.TrimmedUntilTimestamp < b + 9 * step && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, default);
        }

        // ★ 确定性不变量：水位 > 末条样本 ts（裁末样本那轮的 cutoff 必然 > 其 ts）。
        //   不应断言水位 > b+10*step：RunRetentionAsync 空转轮次"无老于锚的样本不写水位"是
        //   既有经济性裁定（空闲不产生 meta 写放大）——水位停在裁末样本那轮的 cutoff，
        //   该点落在 (末样本 ts, 末样本 ts+TTL) 区间何处取决于轮次触发时机，非确定值。
        s.TrimmedUntilTimestamp.Should().BeGreaterThan(b + 9 * step,
            "TTL 后台循环自动 trim——水位必然越过末条样本（否则轮询不收敛）");
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(0, "全部样本早于 TTL 锚 → 已回收");
    }

    // ══ MaxBytes：字节上限同路（时间锚反查索引序——RunRetentionAsync 直调确定性验证）══

    [Fact]
    public async Task MaxBytesLimit_RetentionTrimsToTarget()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            SeriesName = "tc.series.cap",
            RetentionTime = null,      // 只测字节上限
            MaxBytes = 128 << 10,      // 128KB
            RetentionScanInterval = TimeSpan.FromMinutes(1),   // 后台不干扰——直调 RunRetentionAsync
        });

        long b = Base;
        var payload = new byte[8 << 10];   // 8KB × 40 = 320KB >> 128KB
        for (int i = 0; i < 40; i++)
        {
            payload[0] = (byte)i;
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, payload, default);
        }
        await s.FlushAsync(default);
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(40);

        await s.RunRetentionAsync(default);   // internal——IVT 直调（确定性，不赌后台节拍）

        var stats = await s.GetStatsAsync(default);
        stats.SampleCount.Should().BeLessThan(40, "超限触发反查回收");
        stats.SampleCount.Should().BeGreaterThan(0, "只回收超额所需前缀——尾部存活");
        s.TrimmedUntilTimestamp.Should().BeGreaterThan(long.MinValue);

        // 回收后字节占用回到上限内（Ring 头尾距离口径——单段 mem 卷 offset 距离即字节距离）
        (stats.TailAddress.Offset - stats.HeadAddress.Offset).Should()
            .BeLessThanOrEqualTo(128L << 10, "回收后占用 ≤ MaxBytes");

        // 幂等：再跑一轮不误伤（占用已达标 → 不动）
        var countBefore = (await s.GetStatsAsync(default)).SampleCount;
        await s.RunRetentionAsync(default);
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(countBefore, "达标后 retention no-op");
    }
}
