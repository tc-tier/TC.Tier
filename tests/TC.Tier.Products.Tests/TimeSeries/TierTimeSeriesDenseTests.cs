using System.Runtime.CompilerServices;
using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>
/// TierTimeSeries 稠密多序列测试（#443 设计稿 §8 测试矩阵八组）：共享性守恒 / 隔离性 / TTL 联测 /
/// retention 慢序列钉住 / 恢复重放路由 / 容量护栏 / 单序列零迁移 / Rollup 级联。
/// </summary>
public sealed class TierTimeSeriesDenseTests
{
    private static long Base => DateTime.UtcNow.Ticks;

    private static TimeSeriesOptions DenseDefaults => new()
    {
        PageSize = 64 << 10,
        MemorySize = 8 << 20,
        RetentionTime = null,
        DenseSeries = true,
        SeriesCapacity = 16,
    };

    private static Task<TierTimeSeries> StartDenseAsync(TestVolume vol,
        Func<TimeSeriesOptions, TimeSeriesOptions>? configure = null)
        => TierTimeSeriesTestFactory.StartAsync(vol, o =>
        {
            var merged = DenseDefaults with { SeriesName = o.SeriesName };
            return configure?.Invoke(merged) ?? merged;
        });

    private static async Task<int> CountAsync<T>(IAsyncEnumerable<T> source, CancellationToken ct = default)
    {
        int n = 0;
        await foreach (var _ in source.WithCancellation(ct).ConfigureAwait(false)) n++;
        return n;
    }

    private static async Task<List<double>> ReadValsAsync(TierTimeSeries s, uint sid, long from, long to,
        CancellationToken ct = default)
    {
        var list = new List<double>();
        await foreach (var (_, v, _) in s.RangeAsync(sid, from, to, ct).ConfigureAwait(false))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        return list;
    }

    // ══ 矩阵 1：共享性守恒——N 序列写入，引擎文件数恒定（ring/index/water 各一），注册表 O(1) 增长 ══

    [Fact]
    public async Task Dense_MultiSeries_EngineFilesConstant_RegistryLinear()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await StartDenseAsync(vol);

        const int n = 8;
        for (uint sid = 0; sid < n; sid++)
            for (int i = 0; i < 5; i++)
                await s.AppendAsync(sid, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);

        s.SeriesCount.Should().Be(n, "每序列一条 SeriesEntry（O(1) 侧账）");

        // 引擎文件面恒定：{name}.ts.ring / .index / .water 族——不随序列数出现新引擎
        var paths = vol.Fs.EnumerateFiles(pattern: "*", recursive: true).Select(e => e.Name).ToList();
        var enginePaths = paths.Where(p => p.Contains(".ts.")).ToList();
        enginePaths.Should().NotBeEmpty();
        enginePaths.Should().OnlyContain(p =>
                p.Contains(".ts.ring") || p.Contains(".ts.index") || p.Contains(".ts.water"),
            because: "dense 单实例只存在 ring/index/water 三族引擎文件（共享面单份）");
    }

    // ══ 矩阵 2：隔离性——键域不重叠，逐序列 Range/Latest/Floor/Stats 精确 ══

    [Fact]
    public async Task Dense_Isolation_PerSeriesQueriesExact()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await StartDenseAsync(vol);

        // 序列间同时刻交错写入（同 ts 不同 sid——键 (sid, ts) 有序不冲突）
        for (int i = 0; i < 10; i++)
        {
            await s.AppendAsync(1, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.AppendAsync(2, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(100 + i), default);
        }

        // Range 逐序列精确——零串扰
        (await ReadValsAsync(s, 1, b, b + 10 * TimeSpan.TicksPerSecond))
            .Should().Equal(Enumerable.Range(0, 10).Select(i => (double)i),
                because: "序列 1 键域完整且不含序列 2 样本");
        (await ReadValsAsync(s, 2, b, b + 10 * TimeSpan.TicksPerSecond))
            .Should().Equal(Enumerable.Range(100, 10).Select(i => (double)i));

        // 子域 Range：序列 1 [3, 7)
        (await ReadValsAsync(s, 1, b + 3 * TimeSpan.TicksPerSecond, b + 7 * TimeSpan.TicksPerSecond))
            .Should().Equal(3, 4, 5, 6);

        // Latest / Floor 逐序列
        (await s.LatestAsync(1, default))!.Value.Timestamp.Should().Be(b + 9 * TimeSpan.TicksPerSecond);
        (await s.LatestAsync(2, default))!.Value.Timestamp.Should().Be(b + 9 * TimeSpan.TicksPerSecond);
        var f1 = await s.FloorAsync(1, b + 4 * TimeSpan.TicksPerSecond, default);
        f1!.Value.Timestamp.Should().Be(b + 4 * TimeSpan.TicksPerSecond);
        TierTimeSeriesTestFactory.ParseVal(f1.Value.Value.ToArray()).Should().Be(4);

        // 空域语义
        (await s.LatestAsync(3, default)).Should().BeNull("未写入序列 Latest = null");
        (await CountAsync(s.RangeAsync(3, long.MinValue, long.MaxValue, default))).Should().Be(0);

        // Stats 逐序列 + 全实例
        var stats1 = await s.GetStatsAsync(1, default);
        stats1.SampleCount.Should().Be(10);
        stats1.FirstTimestamp.Should().Be(b);
        stats1.LastTimestamp.Should().Be(b + 9 * TimeSpan.TicksPerSecond);
        var all = await s.GetStatsAsync(default);
        all.SampleCount.Should().Be(20, "全实例口径 = 注册表聚合");
        all.IndexEntryCount.Should().Be(20);
    }

    // ══ 矩阵 3：TTL 联测——retention TTL 轮逐序列推进，过期样本逐序列 Range 不产出 ══

    [Fact]
    public async Task Dense_TtlRetentionRound_TrimsOldSamplesPerSeries()
    {
        using var vol = new TestVolume();
        await using var s = await StartDenseAsync(vol, o => o with
        {
            SeriesName = "tc.series.dense.ttl",
            RetentionTime = TimeSpan.FromSeconds(5),
        });

        long now = DateTime.UtcNow.Ticks;
        long old = now - TimeSpan.FromSeconds(30).Ticks;   // 远早于 TTL
        for (int i = 0; i < 5; i++)
        {
            await s.AppendAsync(1, old + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.AppendAsync(2, now + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
        }
        await s.FlushAsync(default);

        // retention 单轮（内部直调——TTL cutoff = now - 5s：序列 1 全过期，序列 2 零过期）
        await s.RunRetentionAsync(default);

        (await CountAsync(s.RangeAsync(1, long.MinValue, long.MaxValue, default)))
            .Should().Be(0, "TTL 过期样本逐序列回收后 Range 不产出");
        (await CountAsync(s.RangeAsync(2, long.MinValue, long.MaxValue, default)))
            .Should().Be(5, "未过期序列不受影响");
        var stats1 = await s.GetStatsAsync(1, default);
        stats1.TrimmedUntilTimestamp.Should().BeGreaterThan(old + 5 * TimeSpan.TicksPerSecond,
            "序列 1 水位推进至 TTL cutoff");
    }

    // ══ 矩阵 4：retention——逐序列 TruncateAsync + 慢序列钉住 Ring 截断下限 + 水位块持久恢复 ══

    [Fact]
    public async Task Dense_TruncateSlowSeriesPinsRingHead_OthersUnaffected()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await StartDenseAsync(vol);

        // 快序列 1（样本在前）+ 慢序列 2（样本在后——地址更大）；记下慢序列首样本地址
        for (int i = 0; i < 10; i++)
            await s.AppendAsync(1, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
        await s.FlushAsync(default);
        var headBefore = (await s.GetStatsAsync(default)).HeadAddress;
        LogicalAddress s2First = default;
        for (int i = 0; i < 10; i++)
        {
            var r = await s.AppendAsync(2, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            if (i == 0) s2First = r.Address;
        }
        await s.FlushAsync(default);

        // 快序列全回收——Ring 头推进到慢序列最早存活样本（其前死数据正常回收，不越雷池一步）
        var deleted = await s.TruncateAsync(1, long.MaxValue, default);
        deleted.Should().Be(10);
        var headAfter = (await s.GetStatsAsync(default)).HeadAddress;
        headAfter.Should().Be(s2First, "Ring 截断下限 = 慢序列（序列 2）最早存活样本——Matrix8 同款钉住");
        headAfter.Should().BeGreaterThan(headBefore, "序列 1 已回收样本的物理空间被回收");

        // 慢序列数据完好（钉住语义：回收不越界）
        (await CountAsync(s.RangeAsync(2, long.MinValue, long.MaxValue, default))).Should().Be(10);

        // 慢序列也回收 → Ring 头推进
        await s.TruncateAsync(2, long.MaxValue, default);
        (await s.GetStatsAsync(default)).HeadAddress.Should().BeGreaterThan(headAfter,
            "全体序列钉住解除 → Ring 截断下限推进");
    }

    [Fact]
    public async Task Dense_WatermarkBlocks_PersistAcrossRestart()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using (var s = await StartDenseAsync(vol))
        {
            for (int i = 0; i < 10; i++)
                await s.AppendAsync(1, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.AppendAsync(2, b + TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(99), default);
            await s.FlushAsync(default);

            await s.TruncateAsync(1, b + 4 * TimeSpan.TicksPerSecond, default);
        }

        // 重启：逐序列水位从稀疏块恢复（序列 1 = 显式 trim 值；序列 2 = 从未 trim）
        await using var s2 = await StartDenseAsync(vol);
        var stats1 = await s2.GetStatsAsync(1, default);
        stats1.TrimmedUntilTimestamp.Should().Be(b + 4 * TimeSpan.TicksPerSecond,
            "显式 trim 序列水位从 keyed 块持久恢复");
        stats1.SampleCount.Should().Be(6);
        (await CountAsync(s2.RangeAsync(1, long.MinValue, long.MaxValue, default))).Should().Be(6);
        (await s2.GetStatsAsync(2, default)).SampleCount.Should().Be(1);

        // 已回收区间写入仍被拒绝（水位恢复语义）
        var act = async () => await s2.AppendAsync(1, b, TierTimeSeriesTestFactory.Val(0), default);
        await act.Should().ThrowAsync<InvalidOperationException>("早于序列 1 回收边界——fail-fast");
    }

    // ══ 矩阵 5：恢复——多序列交错日志 Ring 重放按 envelope SeriesId 路由重建索引 ══

    [Fact]
    public async Task Dense_Recovery_ReplayRoutesByEnvelopeSeriesId()
    {
        using var vol = new TestVolume();
        long b = Base;
        const int n = 20;
        await using (var s = await StartDenseAsync(vol))
        {
            // 三序列交错追加（地址序 ≠ 键序——重放必须逐条路由）
            for (int i = 0; i < n; i++)
                await s.AppendAsync((uint)(i % 3 + 1), b + i * TimeSpan.TicksPerSecond,
                    TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);
        }

        await using var s2 = await StartDenseAsync(vol);
        s2.SeriesCount.Should().Be(3, "重放按 envelope SeriesId 重建注册表");

        for (uint sid = 1; sid <= 3; sid++)
        {
            var expected = Enumerable.Range(0, n).Where(i => i % 3 == sid - 1).Select(i => (double)i).ToList();
            (await ReadValsAsync(s2, sid, b, b + n * TimeSpan.TicksPerSecond))
                .Should().Equal(expected, because: $"序列 {sid} 索引经重放路由重建后逐序列精确");
        }

        // 恢复后写入查询继续正常
        await s2.AppendAsync(2, b + n * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(n), default);
        (await s2.LatestAsync(2, default))!.Value.Timestamp.Should().Be(b + n * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public async Task Dense_Recovery_WatermarkOverclaim_CorrectedToDataFact()
    {
        using var vol = new TestVolume();
        long b = Base;
        const int n = 10;
        await using (var s = await StartDenseAsync(vol))
        {
            for (int i = 0; i < n; i++)
                await s.AppendAsync(1, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);
        }

        // 外部覆写 dense 水位文档：序列 1 块声称已回收至末样本之后（超前 claim）
        var wmeta = new VersionedMetadata(vol.Fs, new VersionedMetadataSettings(
            new Runtime.Storage.StorageEngineOptions("tc.series.ts.water", 4L << 20,
                enableSegmentation: true, preallocateFile: false))
        {
            PayloadSize = TimeSeriesWatermarkState.PayloadSize,
            MaxPayloadSize = TimeSeriesWatermarkState.PayloadSize + 4 + 16 * 36,
        });
        wmeta.Initialize();
        await wmeta.WaitForReadyAsync(default);
        var overSlot = new TimeSeriesWatermarkState(long.MinValue, LogicalAddress.Empty, 0, 5);
        var overBlock = new DenseSeriesWatermarkBlock(1, b + n * TimeSpan.TicksPerSecond,
            LogicalAddress.Empty, 0);
        wmeta.Write(DenseSeriesWatermarkDoc.Encode(overSlot, new[] { overBlock }));
        wmeta.Persist();
        wmeta.Dispose();

        // 重启：水位块超前于数据事实 → 逐序列 min 校正回拉（§5.5 数据事实优先）
        await using var s2 = await StartDenseAsync(vol);
        var stats = await s2.GetStatsAsync(1, default);
        stats.TrimmedUntilTimestamp.Should().BeLessThanOrEqualTo(b,
            "水位块超前 claim → 对账回拉到数据事实（首样本 ts）");
        stats.SampleCount.Should().Be(n, "数据零丢失");
        (await CountAsync(s2.RangeAsync(1, long.MinValue, long.MaxValue, default))).Should().Be(n);
    }

    // ══ 矩阵 6：容量护栏——SeriesCapacity 超限 fail-fast ══

    [Fact]
    public async Task Dense_SeriesCapacityExceeded_FailsFast()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await StartDenseAsync(vol, o => o with { SeriesCapacity = 2 });

        await s.AppendAsync(1, b, TierTimeSeriesTestFactory.Val(1), default);
        await s.AppendAsync(2, b, TierTimeSeriesTestFactory.Val(2), default);
        var act = async () => await s.AppendAsync(3, b, TierTimeSeriesTestFactory.Val(3), default);
        await act.Should().ThrowAsync<InvalidOperationException>("超出 SeriesCapacity——fail-fast（裁决点 4）");

        // 既有序列继续可写（护栏只拦新注册）
        await s.AppendAsync(1, b + TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(11), default);
        s.SeriesCount.Should().Be(2);
    }

    // ══ 矩阵 7：单序列零迁移——DenseSeries=false 既有行为（含三参守卫），dense 两参落 seriesId 0 ══

    [Fact]
    public async Task SingleSeries_Instance_RejectsNamedSeries_TwoParamUnchanged()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        // 两参 API 原样（默认序列）
        await s.AppendAsync(b, TierTimeSeriesTestFactory.Val(7), default);
        (await s.LatestAsync(default))!.Value.Timestamp.Should().Be(b);
        s.SeriesCount.Should().Be(1);

        // 单序列实例三参非零序列 = 显式拒绝（指引启用 dense）
        var act = async () => await s.AppendAsync(1, b, TierTimeSeriesTestFactory.Val(0), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
        var act2 = async () => await CountAsync(s.RangeAsync(1, long.MinValue, long.MaxValue, default));
        await act2.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Dense_TwoParamApi_LandsOnDefaultSeries_ZeroMigration()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await StartDenseAsync(vol);

        // dense 实例两参 API = seriesId 0 默认序列（裁决点 3——零迁移）
        await s.AppendAsync(b, TierTimeSeriesTestFactory.Val(7), default);
        (await s.LatestAsync(default))!.Value.Timestamp.Should().Be(b);
        (await s.LatestAsync(ITierTimeSeries.DefaultSeriesId, default))!.Value.Timestamp.Should().Be(b);
        s.SeriesCount.Should().Be(1);

        // 与命名序列同实例共存
        await s.AppendAsync(5, b + TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(8), default);
        s.SeriesCount.Should().Be(2);
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(2, "全实例口径 = 默认序列 + 命名序列");
    }

    // ══ 矩阵 8：Rollup 级联——dense 逐序列算子 + 跨序列落点 ══

    [Fact]
    public async Task Dense_Rollup_PerSeries_AndCrossSeries()
    {
        using var vol = new TestVolume();
        long windowTicks = TimeSpan.FromSeconds(10).Ticks;
        // 窗口对齐 = epoch 对齐（设计稿 §6——windowTicks 整数倍向下取整）：b 取 10s 边界
        long b = (Base / windowTicks) * windowTicks + windowTicks;
        await using var src = await StartDenseAsync(vol);
        await using var dst = await StartDenseAsync(vol, o => o with { SeriesName = "tc.series.dense.roll" });

        // 序列 1：60 样本（60s）——值 = 秒序；序列 2：噪声（rollup 序列 1 不得串入）
        for (int i = 0; i < 60; i++)
        {
            await src.AppendAsync(1, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await src.AppendAsync(2, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(-1), default);
        }

        var written = await TimeSeriesRollup.RollupAsync(src, dst, windowTicks, TimeSeriesRollup.Aggregation.Avg,
            b, b + 60 * TimeSpan.TicksPerSecond, seriesId: 1, ct: default);
        written.Should().Be(6, "60s 按 10s 分窗 = 6 窗（只含序列 1 样本）");

        // 逐序列验证：目标序列 1 六窗均值；序列 2 槽位空
        var avgs = await ReadValsAsync(dst, 1, b, b + 60 * TimeSpan.TicksPerSecond);
        avgs.Should().HaveCount(6);
        for (int w = 0; w < 6; w++)
        {
            double expect = Enumerable.Range(w * 10, 10).Average();
            avgs[w].Should().BeApproximately(expect, 1e-9, because: $"窗 {w} 均值来自序列 1");
        }
        (await CountAsync(dst.RangeAsync(2, long.MinValue, long.MaxValue, default))).Should().Be(0,
            "rollup 落点只在目标序列——零串扰");

        // 跨序列落点：源 1 → 目标 9
        var written2 = await TimeSeriesRollup.RollupAsync(src, dst, windowTicks, TimeSeriesRollup.Aggregation.Count,
            b, b + 60 * TimeSpan.TicksPerSecond, 1, 9, default);
        written2.Should().Be(6);
        (await CountAsync(dst.RangeAsync(9, long.MinValue, long.MaxValue, default))).Should().Be(6);
        var counts = await ReadValsAsync(dst, 9, b, b + 60 * TimeSpan.TicksPerSecond);
        counts.Should().OnlyContain(c => c == 10, "每窗 10 样本（Count 聚合）");
    }

    // ══ 键布局与降档补充：20B sizeof 钉死 + 键序 + TTS2 往返 + Indexed=false 降档 ══

    [Fact]
    public void DenseTimeKey_SizeIs20Bytes_AndOrderingSeriesIdFirst()
    {
        Unsafe.SizeOf<DenseTimeKey>().Should().Be(20, "#443 设计稿 §3——20B 复合键（Pack=4 紧凑布局）");
        var k1 = new DenseTimeKey(1, 100, 0);
        var k2 = new DenseTimeKey(2, 50, 0);
        new DenseTimeKeyComparer().Compare(k1, k2).Should().BeNegative("SeriesId 领先——键前缀域前提");
        new DenseTimeKeyComparer().Compare(
            new DenseTimeKey(1, 100, 5), new DenseTimeKey(1, 100, 9)).Should().BeNegative("同刻按地址稳定");
    }

    [Fact]
    public void DenseEnvelope_Tts2_RoundTrip()
    {
        TimeSeriesEnvelope.DenseHeaderSize.Should().Be(25);
        TimeSeriesEnvelope.DenseMagic.ToArray().Should().Equal("TTS2"u8.ToArray());

        var buf = TimeSeriesEnvelope.WrapDense(42, 12345, null, [1, 2, 3]);
        TimeSeriesEnvelope.TryUnwrapDense(buf, out var flags, out var sid, out var ts, out var seq, out var payload)
            .Should().BeTrue();
        sid.Should().Be((uint)42);
        ts.Should().Be(12345);
        flags.Should().Be(0);
        seq.Should().Be(0);
        payload.Should().Equal([1, 2, 3]);

        // TTS1 互不误读（布局代际守卫）
        var tts1 = TimeSeriesEnvelope.Wrap(12345, null, [1]);
        TimeSeriesEnvelope.TryUnwrapDense(tts1, out _, out _, out _, out _, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Dense_Degraded_NoIndex_RingOrderDelivery()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using var s = await StartDenseAsync(vol, o => o with
        {
            Indexed = false,
            SeriesCapacity = 8,
        });
        s.DiagnosticKeys.HasIndex.Should().BeFalse();

        for (int i = 0; i < 5; i++)
        {
            await s.AppendAsync(1, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.AppendAsync(2, b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(50 + i), default);
        }
        await s.FlushAsync(default);

        (await ReadValsAsync(s, 1, b, b + 5 * TimeSpan.TicksPerSecond)).Should().Equal(0, 1, 2, 3, 4);
        (await CountAsync(s.RangeAsync(2, long.MinValue, long.MaxValue, default))).Should().Be(5);

        // 降档 trim 仍逐序列收口
        var deleted = await s.TruncateAsync(1, b + 3 * TimeSpan.TicksPerSecond, default);
        deleted.Should().Be(3);
        (await CountAsync(s.RangeAsync(1, long.MinValue, long.MaxValue, default))).Should().Be(2);
        (await CountAsync(s.RangeAsync(2, long.MinValue, long.MaxValue, default))).Should().Be(5,
            "慢序列（序列 2）钉住——不受序列 1 trim 影响");
    }
}
