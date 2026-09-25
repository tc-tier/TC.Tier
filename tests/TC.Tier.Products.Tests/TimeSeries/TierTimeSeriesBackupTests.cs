using System.Buffers.Binary;
using TC.Tier.Core.Testing;

namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>
/// TierTimeSeries 冷备份导出/导入测试（#521——按时间区间导出→流＋导入恢复；验收：导出→清空→导入
/// Range 一致 / retention 与 Rollup 级联共存不互吞 / retention 下内存平稳有界）。
/// <para>★ 确定性纪律：retention 有界 soak 用 <see cref="FakeTimeProvider"/> 注入时钟 + RunRetentionAsync
/// 直调——零真实睡等，不赌后台节拍。</para>
/// </summary>
public sealed class TierTimeSeriesBackupTests
{
    private static long Base => FakeTimeProvider.DefaultStart.UtcTicks;
    private const long Second = TimeSpan.TicksPerSecond;

    // ══ 验收：导出→清空→导入 Range 结果一致（单序列——同实例重放，水位按数据事实回拉）══

    [Fact]
    public async Task Export_Import_RoundTrip_SingleMode()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long b = Base;
        const int n = 50;
        for (int i = 0; i < n; i++)
            await s.AppendAsync(b + i * Second, TierTimeSeriesTestFactory.Val(i), default);
        await s.FlushAsync(default);

        var expected = await CollectAsync(s, b, b + n * Second);
        expected.Should().HaveCount(n);

        var stream = new MemoryStream();
        await TimeSeriesBackup.ExportAsync(s, stream, b, b + n * Second, default);

        // 导入到新实例——Range 逐条一致
        using var vol2 = new TestVolume();
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol2, o => o with { SeriesName = "tc.series.restore" });
        stream.Position = 0;
        var result = await TimeSeriesBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(n);
        result.SeriesCount.Should().Be(1);

        var restored = await CollectAsync(s2, b, b + n * Second);
        restored.Should().Equal(expected, "导出→导入 Range 结果一致");
    }

    [Fact]
    public async Task Export_Clear_Import_SameInstance_WatermarkReconciledToDataFact()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "tc.series.clear" });

        long b = Base;
        const int n = 50;
        for (int i = 0; i < n; i++)
            await s.AppendAsync(b + i * Second, TierTimeSeriesTestFactory.Val(i), default);
        await s.FlushAsync(default);

        var expected = await CollectAsync(s, b, b + n * Second);
        var stream = new MemoryStream();
        await TimeSeriesBackup.ExportAsync(s, stream, b, b + n * Second, default);

        // 清空：全量回收（水位推到 long.MaxValue——live 追加守卫全拒的守卫悖论形态）
        (await s.TruncateAsync(long.MaxValue, default)).Should().Be(n);
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(0);

        // 同实例重放：水位按导入事实回拉（恢复对账 §4c 同款）——历史样本复活且可继续追加
        stream.Position = 0;
        var result = await TimeSeriesBackup.ImportAsync(s, stream, default);
        result.ImportedCount.Should().Be(n);
        s.TrimmedUntilTimestamp.Should().Be(b, "回收边界回拉到导入最早样本时刻（数据事实优先）");

        var restored = await CollectAsync(s, b, b + n * Second);
        restored.Should().Equal(expected, "清空→导入 Range 结果一致");

        // 持久一致：flush + 重启——水位已持久为回拉值，恢复不误判（老样本存活）
        await s.FlushAsync(default);
        await s.DisposeAsync();
        await using var s3 = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "tc.series.clear" });
        var afterRestart = await CollectAsync(s3, b, b + n * Second);
        afterRestart.Should().Equal(expected, "重启后导入数据完整（水位回拉持久一致）");
    }

    // ══ dense 多序列：全序列导出（升序分组）→ 新实例导入——逐序列 Range 一致 ══

    [Fact]
    public async Task Export_Import_Dense_MultiSeries()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol,
            o => o with { SeriesName = "tc.series.dense.bk", DenseSeries = true });

        long b = Base;
        var expected = new Dictionary<uint, List<double>>();
        foreach (var sid in new uint[] { 3, 1, 2 })   // 注册序乱序——SeriesIds 升序交付
        {
            var vals = new List<double>();
            for (int i = 0; i < 20; i++)
            {
                double v = sid * 100 + i;
                await s.AppendAsync(sid, b + i * Second, TierTimeSeriesTestFactory.Val(v), default);
                vals.Add(v);
            }
            expected[sid] = vals;
        }
        await s.FlushAsync(default);
        s.SeriesIds.Should().Equal(new uint[] { 1, 2, 3 }, "已注册序列快照升序");

        var stream = new MemoryStream();
        await TimeSeriesBackup.ExportAsync(s, stream, b, b + 20 * Second, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol2,
            o => o with { SeriesName = "tc.series.dense.rs", DenseSeries = true });
        stream.Position = 0;
        var result = await TimeSeriesBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(60);
        result.SeriesCount.Should().Be(3);
        s2.SeriesCount.Should().Be(3);

        foreach (var (sid, vals) in expected)
        {
            var restored = new List<double>();
            await foreach (var (_, v, _) in s2.RangeAsync(sid, b, b + 20 * Second, default))
                restored.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
            restored.Should().Equal(vals, $"序列 {sid} 导出→导入 Range 一致");
        }
    }

    // ══ 部分时间区间导出：只交付窗口内样本 ══

    [Fact]
    public async Task Export_PartialTimeRange_OnlyWindowDelivered()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "tc.series.win" });

        long b = Base;
        for (int i = 0; i < 30; i++)
            await s.AppendAsync(b + i * Second, TierTimeSeriesTestFactory.Val(i), default);

        var stream = new MemoryStream();
        await TimeSeriesBackup.ExportAsync(s, stream, b + 10 * Second, b + 20 * Second, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol2, o => o with { SeriesName = "tc.series.win.rs" });
        stream.Position = 0;
        var result = await TimeSeriesBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(10);

        var restored = await CollectAsync(s2, b, b + 30 * Second);
        restored.Should().Equal(Enumerable.Range(10, 10).Select(i => (double)i), "仅窗口 [10s, 20s) 交付");
    }

    // ══ 损坏流 fail-fast：magic/version/记录数/CRC/截断 ══

    [Fact]
    public async Task Import_CorruptStream_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "tc.series.bad" });
        long b = Base;
        for (int i = 0; i < 10; i++)
            await s.AppendAsync(b + i * Second, TierTimeSeriesTestFactory.Val(i), default);

        var good = new MemoryStream();
        await TimeSeriesBackup.ExportAsync(s, good, b, b + 10 * Second, default);

        // magic 不符
        var corrupt = new MemoryStream();
        corrupt.Write(new byte[] { 0xEE, 0xEE, 0xEE, 0xEE });
        corrupt.Write(good.ToArray(), 4, good.ToArray().Length - 4);
        corrupt.Position = 0;
        var act1 = async () => await TimeSeriesBackup.ImportAsync(s, corrupt, default);
        await act1.Should().ThrowAsync<InvalidDataException>();

        // 版本不符
        var verBytes = good.ToArray();
        verBytes[4] = 0x7F;
        var badVersion = new MemoryStream(verBytes);
        badVersion.Position = 0;
        var act2 = async () => await TimeSeriesBackup.ImportAsync(s, badVersion, default);
        await act2.Should().ThrowAsync<InvalidDataException>();

        // CRC 不符（翻转记录 2 值域字节——sid 字段不动，纯 CRC 覆盖面损坏）
        var crcBytes = good.ToArray();
        crcBytes[12 + 16 + 8 + 8] ^= 0xFF;   // 记录 1（28B：16 头 + 8 值 + 对齐）后、记录 2 值域内
        var badCrc = new MemoryStream(crcBytes);
        badCrc.Position = 0;
        var act3 = async () => await TimeSeriesBackup.ImportAsync(s, badCrc, default);
        await act3.Should().ThrowAsync<InvalidDataException>();

        // 尾部截断（Footer 不完整）
        var truncated = new MemoryStream(good.ToArray(), 0, (int)good.Length - 4);
        var act4 = async () => await TimeSeriesBackup.ImportAsync(s, truncated, default);
        await act4.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task Import_SingleModeTarget_RejectsNonZeroSeries()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol,
            o => o with { SeriesName = "tc.series.xmode", DenseSeries = true });
        long b = Base;
        await s.AppendAsync(7, b, TierTimeSeriesTestFactory.Val(1), default);
        var stream = new MemoryStream();
        await TimeSeriesBackup.ExportAsync(s, stream, b, b + Second, default);

        using var vol2 = new TestVolume();
        await using var single = await TierTimeSeriesTestFactory.StartAsync(vol2, o => o with { SeriesName = "tc.series.xmode.rs" });
        stream.Position = 0;
        var act = async () => await TimeSeriesBackup.ImportAsync(single, stream, default);
        await act.Should().ThrowAsync<InvalidOperationException>("单序列实例遇非零 seriesId fail-fast（同 Append 守卫）");
    }

    // ══ 验收：retention 与 Rollup 级联共存不互吞（dense 逐序列 trim——源回收不吞降采样目标）══

    [Fact]
    public async Task Retention_TrimSource_RollupTargetIntact()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol,
            o => o with { SeriesName = "tc.series.cascade", DenseSeries = true });

        long b = Base;
        const long window = 60 * Second;
        for (int i = 0; i < 120; i++)
            await s.AppendAsync(1, b + i * Second, TierTimeSeriesTestFactory.Val(i + 1), default);
        await s.FlushAsync(default);

        int rolled = await TimeSeriesRollup.RollupAsync(s, s, window, TimeSeriesRollup.Aggregation.Avg,
            b, b + 120 * Second, sourceSeriesId: 1, targetSeriesId: 2, default);
        rolled.Should().Be(2, "两窗（[0,60) / [60,120)）");

        // 源序列 retention 推进：首窗回收
        (await s.TruncateAsync(1, b + window, default)).Should().Be(60);

        // 目标序列不受源回收影响（逐序列 trim——Ring 截断下限由钉住 min 收口）
        var target = new List<(long Ts, double V)>();
        await foreach (var (ts, v, _) in s.RangeAsync(2, b, b + 120 * Second, default))
            target.Add((ts, TierTimeSeriesTestFactory.ParseVal(v.ToArray())));
        target.Should().HaveCount(2, "降采样目标序列不被源 retention 吞掉");
        target[0].V.Should().Be(30.5);
        target[1].V.Should().Be(90.5);

        // 源老窗空、新窗存活；两序列回收边界独立（目标序列边界不动——未 trim 形态）
        (await s.GetStatsAsync(1, default)).TrimmedUntilTimestamp.Should().Be(b + window);
        (await s.GetStatsAsync(2, default)).TrimmedUntilTimestamp.Should().BeLessThan(b,
            "目标序列从未 trim——回收边界不随源推进");
        var srcOld = new List<double>();
        await foreach (var (_, v, _) in s.RangeAsync(1, b, b + window, default))
            srcOld.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        srcOld.Should().BeEmpty();

        // 级联续跑：剩余源窗再 rollup 不受影响
        int rolled2 = await TimeSeriesRollup.RollupAsync(s, s, window, TimeSeriesRollup.Aggregation.Avg,
            b + window, b + 120 * Second, sourceSeriesId: 1, targetSeriesId: 2, default);
        rolled2.Should().Be(1);
    }

    // ══ 验收：恒速写入 + retention=N 秒 → 内存平稳有界（假钟确定性 soak——dense 96 序列形态缩样）══

    [Fact]
    public async Task Retention_ConstantRateWrite_MemoryBounded_FakeClock()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            SeriesName = "tc.series.bounded",
            DenseSeries = true,
            RetentionTime = TimeSpan.FromSeconds(2),   // TTL = 2 轮窗口
            RetentionScanInterval = TimeSpan.FromMinutes(1),   // 后台不干扰——RunRetentionAsync 直调
            Clock = clock,
        });

        long t0 = Base;
        long phase = TimeSpan.FromMilliseconds(500).Ticks;   // 半秒相位——错开样本首 ts 与 cutoff 整秒对齐
                                                             //（firstTs == cutoff 命中严格 >= 跳过分支，属测试对齐伪影）
        const int rounds = 12;
        const uint seriesCount = 4;
        const int perSeriesPerRound = 50;
        const long roundTicks = 1 * Second;

        // 恒速写入：每轮 4 序列 × 50 样本（步距 20ms）→ 快进 1s → retention 扫一轮
        for (int r = 0; r < rounds; r++)
        {
            for (uint sid = 1; sid <= seriesCount; sid++)
            {
                for (int i = 0; i < perSeriesPerRound; i++)
                    await s.AppendAsync(sid, t0 + phase + r * roundTicks + i * (roundTicks / perSeriesPerRound),
                        TierTimeSeriesTestFactory.Val(sid * 1000 + r * 100 + i), default);
            }
            clock.Advance(TimeSpan.FromTicks(roundTicks));
            await s.RunRetentionAsync(default);

            // 有界不变式（计数口径——确定性强，不依赖 Ring 页打包）：
            // TTL 2s + 严格 before-exclusive 截断 + 等值跳过 → 稳态 ≤ 5 轮瞬态（250/序列）；
            // 无 retention 时 12 轮后 ≥ 550/序列——界 300 判别"有界 vs 只进不出"
            var stats = await s.GetStatsAsync(1, default);
            stats.SampleCount.Should().BeLessThanOrEqualTo(300,
                $"轮 {r}：TTL 回收追平写入——样本数有界");
            (stats.TailAddress.Offset - stats.HeadAddress.Offset).Should()
                .BeLessThanOrEqualTo(256 * 1024,
                    "Ring 头尾距离有界（内存卷占用口径——页打包量化敏感，稳态 ~77-173KB；无 retention ≈ 6×，界可判别）");
        }

        // 全域有界：写入停 + 快进过 TTL → 全部回收
        clock.Advance(TimeSpan.FromSeconds(5));
        await s.RunRetentionAsync(default);
        (await s.GetStatsAsync(1, default)).SampleCount.Should().Be(0, "写入停止后 retention 收敛到空");
    }

    // ══ helpers ══

    private static async Task<List<double>> CollectAsync(TierTimeSeries s, long from, long to)
    {
        var vals = new List<double>();
        await foreach (var (_, v, _) in s.RangeAsync(from, to, default))
            vals.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        return vals;
    }
}
