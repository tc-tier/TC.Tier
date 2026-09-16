namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>
/// TierTimeSeries 降采样 Rollup 测试（tc-tier-timeseries-spec §11 验证矩阵 9——往返/级联/多窗聚合族）。
/// </summary>
public sealed class TierTimeSeriesRollupTests
{
    /// <summary>合成时间基（windowTicks 的整数倍——窗口对齐确定性）。</summary>
    private const long Base = 10_000_000L;
    private const long Window = 1_000_000L;

    private static long Ts(long offsetTicks) => Base + offsetTicks;

    // ══ 矩阵 9：Rollup 往返——源 60 样本 → 1 窗 Sum → 目标 1 样本=总和；级联二档正确 ══

    [Fact]
    public async Task Rollup_SumSixtySamplesInOneWindow_CascadeSecondStage()
    {
        using var vol = new TestVolume();
        await using var source = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.src" });
        await using var target = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.dst1" });
        await using var target2 = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.dst2" });

        // 60 样本（10_000 步距 × 60 = 590_000 Ticks < 1 窗）——全部落同一窗
        double sum = 0;
        for (int i = 0; i < 60; i++)
        {
            sum += i;
            await source.AppendAsync(Ts(i * 10_000), TierTimeSeriesTestFactory.Val(i), default);
        }

        var windows = await TimeSeriesRollup.RollupAsync(source, target, Window,
            TimeSeriesRollup.Aggregation.Sum, Base, Base + Window, default);
        windows.Should().Be(1, "60 样本同窗 → 1 窗");

        var rolled = await target.LatestAsync(default);
        rolled.Should().NotBeNull();
        rolled!.Value.Timestamp.Should().Be(Base, "窗起点 = epoch 对齐窗口下界");
        TierTimeSeriesTestFactory.ParseVal(rolled.Value.Value.ToArray()).Should().Be(sum, "目标 1 样本 = 总和");

        // 级联二档：1 窗目标再聚 2× 窗（Sum 组合律——嵌套窗口和守恒）
        var cascaded = await TimeSeriesRollup.RollupAsync(target, target2, 2 * Window,
            TimeSeriesRollup.Aggregation.Sum, Base, Base + 2 * Window, default);
        cascaded.Should().Be(1);
        var final = await target2.LatestAsync(default);
        TierTimeSeriesTestFactory.ParseVal(final!.Value.Value.ToArray()).Should().Be(sum, "级联二档正确");
    }

    // ══ 多窗 + 聚合族（Min/Max/Avg/Count/First/Last）逐窗验证 ══

    [Fact]
    public async Task Rollup_MultipleWindows_AggregationFamily()
    {
        using var vol = new TestVolume();
        await using var source = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.fam.src" });
        await using var target = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.fam.dst" });

        // 3 窗各 3 样本：窗 k 的值 = k*10 + j（j=0..2）→ Min=k10 Max=k12 Sum=3k+33 Avg=k11 First=k10 Last=k12
        for (int k = 0; k < 3; k++)
            for (int j = 0; j < 3; j++)
                await source.AppendAsync(Ts(k * Window + j * 100_000), TierTimeSeriesTestFactory.Val(k * 10 + j), default);

        // 空窗（第 3 窗无样本）不落——只 3 窗
        var written = await TimeSeriesRollup.RollupAsync(source, target, Window,
            TimeSeriesRollup.Aggregation.Avg, Base, Base + 4 * Window, default);
        written.Should().Be(3, "空窗不落（只有含样本的窗才写）");

        var list = new List<(long Ts, double V)>();
        await foreach (var (ts, v, _) in target.RangeAsync(Base, Base + 4 * Window, default))
            list.Add((ts, TierTimeSeriesTestFactory.ParseVal(v.ToArray())));
        list.Select(x => x.V).Should().Equal(new double[] { 1, 11, 21 }, because: "Avg 逐窗：均值 = 窗中值");
        list.Select(x => x.Ts).Should().Equal(new long[] { Base, Base + Window, Base + 2 * Window }, because: "窗起点 = epoch 对齐");

        // 逐聚合函数在单窗（窗 0：值 0/1/2）内的语义
        foreach (var (agg, expected) in new[]
        {
            (TimeSeriesRollup.Aggregation.Min, 0d),
            (TimeSeriesRollup.Aggregation.Max, 2d),
            (TimeSeriesRollup.Aggregation.Sum, 3d),
            (TimeSeriesRollup.Aggregation.Count, 3d),
            (TimeSeriesRollup.Aggregation.First, 0d),
            (TimeSeriesRollup.Aggregation.Last, 2d),
        })
        {
            await using var t = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = $"ts.fam.{agg}" });
            await TimeSeriesRollup.RollupAsync(source, t, Window, agg, Base, Base + Window, default);
            var r = await t.LatestAsync(default);
            TierTimeSeriesTestFactory.ParseVal(r!.Value.Value.ToArray()).Should().Be(expected, $"聚合 {agg}");
        }
    }

    // ══ 乱序源：Rollup 走 RangeAsync（时间序）——窗口聚合与写入顺序无关 ══

    [Fact]
    public async Task Rollup_OutOfOrderSource_WindowAggregationOrderIndependent()
    {
        using var vol = new TestVolume();
        await using var source = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.ooo.src" });
        await using var target = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.ooo.dst" });

        // 乱序写入两窗样本
        await source.AppendAsync(Ts(2 * Window), TierTimeSeriesTestFactory.Val(20), default);
        await source.AppendAsync(Ts(0), TierTimeSeriesTestFactory.Val(1), default);
        await source.AppendAsync(Ts(2 * Window + 1), TierTimeSeriesTestFactory.Val(22), default);
        await source.AppendAsync(Ts(1), TierTimeSeriesTestFactory.Val(2), default);

        var written = await TimeSeriesRollup.RollupAsync(source, target, Window,
            TimeSeriesRollup.Aggregation.Sum, Base, Base + 3 * Window, default);
        written.Should().Be(2);

        var list = new List<double>();
        await foreach (var (_, v, _) in target.RangeAsync(Base, Base + 3 * Window, default))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        list.Should().Equal(new double[] { 3, 42 }, because: "乱序源经时间序聚合：窗 0 = 1+2，窗 2 = 20+22");
    }

    // ══ 契约守卫：windowTicks 非正拒绝；值非 8B double 拒绝 ══

    [Fact]
    public async Task Rollup_Guards_WindowAndValueContract()
    {
        using var vol = new TestVolume();
        await using var source = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.g.src" });
        await using var target = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "ts.g.dst" });

        Func<Task> actWindow = async () => await TimeSeriesRollup.RollupAsync(source, target, 0,
            TimeSeriesRollup.Aggregation.Sum, Base, Base + Window, default);
        await actWindow.Should().ThrowAsync<ArgumentOutOfRangeException>("windowTicks ≤ 0 拒绝");

        await source.AppendAsync(Ts(0), new byte[] { 1, 2, 3 }, default);   // 非 8B
        Func<Task> actValue = async () => await TimeSeriesRollup.RollupAsync(source, target, Window,
            TimeSeriesRollup.Aggregation.Sum, Base, Base + Window, default);
        await actValue.Should().ThrowAsync<InvalidOperationException>("值编码契约 8B double fail-fast");

        // fromTs ≥ toTs → 0（空区间快速路径）
        var n = await TimeSeriesRollup.RollupAsync(source, target, Window,
            TimeSeriesRollup.Aggregation.Sum, Base + Window, Base, default);
        n.Should().Be(0);
    }
}
