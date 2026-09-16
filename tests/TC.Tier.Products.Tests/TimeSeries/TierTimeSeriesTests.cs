using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>
/// TierTimeSeries 核心流测试（tc-tier-timeseries-spec §11 验证矩阵 1/2/3/4/8/10/11 + 降档契约）。
/// <para>介质：mem（TestVolume 默认）——重启语义 = 同卷上 Dispose 实例后 Builder 重建（fs 存活保持数据）。</para>
/// </summary>
public sealed class TierTimeSeriesTests
{
    /// <summary>合成时间基（Ticks 量级真实，避开 now 锚定守卫的干扰——MaxOutOfOrderPast 缺省 null 不限）。</summary>
    private static long Base => DateTime.UtcNow.Ticks;

    // ══ 矩阵 1：顺序写入范围查——Append N 升序 ts → Range [a,b) 恰好时间序交付，值/地址一一对应 ══

    [Fact]
    public async Task OrderedAppend_RangeDeliversInOrder_ValuesAndAddressesMatch()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        const int n = 100;
        long b = Base;
        var addrs = new LogicalAddress[n];
        for (int i = 0; i < n; i++)
            addrs[i] = (await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default)).Address;

        var list = new List<(long Ts, byte[] V, LogicalAddress A)>();
        await foreach (var (ts, v, a) in s.RangeAsync(b, b + n * TimeSpan.TicksPerSecond, default))
            list.Add((ts, v.ToArray(), a));

        list.Should().HaveCount(n);
        list.Select(x => x.Ts).Should().BeInAscendingOrder("范围查询严格时间序交付");
        for (int i = 0; i < n; i++)
        {
            list[i].Ts.Should().Be(b + i * TimeSpan.TicksPerSecond);
            TierTimeSeriesTestFactory.ParseVal(list[i].V).Should().Be(i, "值一一对应");
            list[i].A.Should().Be(addrs[i], "地址一一对应（Append 返回 = 查询交付）");
        }
    }

    // ══ 矩阵 2：乱序吸收——乱序/同刻多样本混写 → 范围查询仍严格时间序（索引序 ≠ 地址序的正证）══

    [Fact]
    public async Task OutOfOrderAppends_RangeStillStrictlyTimeOrdered()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long b = Base;
        // 乱序写入：先写 ts=5、再写 ts=1、再写 ts=3 …（地址序 ≠ 时间序）
        var shuffled = new[] { 5, 1, 4, 0, 3, 2 };
        foreach (var i in shuffled)
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);

        var list = new List<(long Ts, LogicalAddress A)>();
        await foreach (var (ts, _, a) in s.RangeAsync(long.MinValue, long.MaxValue, default))
            list.Add((ts, a));

        list.Select(x => x.Ts).Should().Equal(
            Enumerable.Range(0, 6).Select(i => b + i * TimeSpan.TicksPerSecond),
            because: "乱序写入被 BTree 有序插入天然吸收（定案③）");
        list.Select(x => x.A).Should().NotBeInAscendingOrder("正证：交付序（时间序）≠ 地址序（写序）");
    }

    // ══ 矩阵 3：同刻多样本——同 ts 三样本 → 按写入序交付（tiebreaker=地址，定案③不覆盖）══

    [Fact]
    public async Task SameTimestampSamples_DeliveredInWriteOrder()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long ts = Base + TimeSpan.TicksPerMinute;
        var r1 = await s.AppendAsync(ts, TierTimeSeriesTestFactory.Val(1), default);
        var r2 = await s.AppendAsync(ts, TierTimeSeriesTestFactory.Val(2), default);
        var r3 = await s.AppendAsync(ts, TierTimeSeriesTestFactory.Val(3), default);

        var list = new List<(long Ts, double V, LogicalAddress A)>();
        await foreach (var (t, v, a) in s.RangeAsync(ts, ts + 1, default))
            list.Add((t, TierTimeSeriesTestFactory.ParseVal(v.ToArray()), a));

        list.Should().HaveCount(3, "同刻多样本不覆盖（定案③）");
        list.Select(x => x.V).Should().Equal(new double[] { 1, 2, 3 }, because: "按写入序交付（tiebreaker=地址单调）");
        list.Select(x => x.A).Should().Equal(r1.Address, r2.Address, r3.Address);
    }

    // ══ 矩阵 4：Latest/Floor——Backward 首条 = 最大 ts；Floor(ts) = ≤ts 最近（Prometheus 采样语义）══

    [Fact]
    public async Task LatestAndFloor_PointQueries()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long b = Base;
        for (int i = 0; i <= 10; i++)
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);

        var latest = await s.LatestAsync(default);
        latest.Should().NotBeNull();
        latest!.Value.Timestamp.Should().Be(b + 10 * TimeSpan.TicksPerSecond, "Latest = 最大 ts");
        TierTimeSeriesTestFactory.ParseVal(latest.Value.Value.ToArray()).Should().Be(10);

        // Floor 命中：≤ ts 的最近样本
        var floor = await s.FloorAsync(b + 3 * TimeSpan.TicksPerSecond + 1, default);
        floor.Should().NotBeNull();
        floor!.Value.Timestamp.Should().Be(b + 3 * TimeSpan.TicksPerSecond, "Floor = ≤ ts 最近样本（采样语义）");

        // Floor 早于全部样本：null
        (await s.FloorAsync(b - 1, default)).Should().BeNull("无 ≤ ts 样本");
    }

    [Fact]
    public async Task Latest_EmptySeries_ReturnsNull()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);
        (await s.LatestAsync(default)).Should().BeNull("空序列 Latest = null");
        (await s.FloorAsync(Base, default)).Should().BeNull();
        var stats = await s.GetStatsAsync(default);
        stats.SampleCount.Should().Be(0);
        stats.FirstTimestamp.Should().BeNull();
        stats.LastTimestamp.Should().BeNull();
    }

    // ══ 矩阵 8：乱序守卫——ts < TrimmedUntil → 拒绝；> MaxOutOfOrderPast → 拒绝（fail-fast 不静默丢）══

    [Fact]
    public async Task OutOfOrderGuards_RejectStaleSamples()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long b = Base;
        for (int i = 0; i < 10; i++)
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);

        // trim 到 ts=5（回收 [b, b+5)）
        await s.TruncateAsync(b + 5 * TimeSpan.TicksPerSecond, default);
        s.TrimmedUntilTimestamp.Should().Be(b + 5 * TimeSpan.TicksPerSecond);

        // 守卫一：写入已回收区间 → 拒绝
        Func<Task> actOld = async () =>
            await s.AppendAsync(b + 2 * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(2), default);
        await actOld.Should().ThrowAsync<InvalidOperationException>("ts < TrimmedUntil → 拒绝（数据必丢 fail-fast）");

        // 边界：ts = TrimmedUntil 恰好合法（回收边界不含）
        await s.AppendAsync(b + 5 * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(50), default);

        // 守卫二：MaxOutOfOrderPast（乱序下界保护）
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with { SeriesName = "tc.series.2", MaxOutOfOrderPast = TimeSpan.FromSeconds(10) });
        long now = DateTime.UtcNow.Ticks;
        await s2.AppendAsync(now, TierTimeSeriesTestFactory.Val(1), default);   // 当下合法
        Func<Task> actLate = async () =>
            await s2.AppendAsync(now - TimeSpan.FromSeconds(30).Ticks, TierTimeSeriesTestFactory.Val(0), default);
        await actLate.Should().ThrowAsync<InvalidOperationException>("now - ts > MaxOutOfOrderPast → 拒绝（老样本钉住索引/截断）");
    }

    // ══ 矩阵 10：溢出值——超页大值 Append/Range 往返一致 ══

    [Fact]
    public async Task OverflowValue_AppendRangeRoundtrip()
    {
        using var vol = new TestVolume();
        var payload = new byte[1 << 20];   // 1MB（>> 1KB 阈值 → 溢出引擎）
        new Random(42).NextBytes(payload);
        await using (var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            OverflowPolicy = OverflowPolicy.Enabled,
            MinOverflowSize = 1024,
        }))
        {
            long ts = Base;
            var r = await s.AppendAsync(ts, payload, default);
            r.Address.IsValid.Should().BeTrue();

            var list = new List<byte[]>();
            await foreach (var (_, v, _) in s.RangeAsync(ts, ts + 1, default))
                list.Add(v.ToArray());
            list.Should().HaveCount(1);
            list[0].Should().Equal(payload, "溢出值经溢出引擎往返一致");

            await s.FlushAsync(default);
        }

        // 重启后溢出样本仍在（冷数据回源）
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            OverflowPolicy = OverflowPolicy.Enabled,
            MinOverflowSize = 1024,
        });
        var after = new List<byte[]>();
        await foreach (var (_, v, _) in s2.RangeAsync(long.MinValue, long.MaxValue, default))
            after.Add(v.ToArray());
        after.Should().HaveCount(1);
        after[0].Should().Equal(payload, "重启后溢出值往返一致");
    }

    // ══ 矩阵 11：冷热透明——写超内存窗口量级 → 冷区回源 Range 一致（mem 小几何强制冷路径）══

    [Fact]
    public async Task ColdEviction_RangeReadsBackTransparently()
    {
        using var vol = new TestVolume();
        // 小内存窗口：2 页 × 64KB = 128KB；每样本 8KB × 40 = 320KB > 窗口 → 前段强制进冷区
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            PageSize = 64 << 10,
            MemorySize = 2 * (64 << 10),
        });

        long b = Base;
        const int n = 40;
        var payload = new byte[8 << 10];
        for (int i = 0; i < n; i++)
        {
            payload[0] = (byte)i;   // 值首字节标记样本序
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, payload, default);
        }

        var list = new List<(long Ts, byte[] V)>();
        await foreach (var (ts, v, _) in s.RangeAsync(b, b + n * TimeSpan.TicksPerSecond, default))
            list.Add((ts, v.ToArray()));

        list.Should().HaveCount(n, "冷热透明：冷区回源不丢样本");
        list[0].V[0].Should().Be(0, "最早样本已被驱逐进冷区——回源一致");
        list.Select(x => x.V[0]).Should().Equal(Enumerable.Range(0, n).Select(i => (byte)i).ToArray());
    }

    // ══ 降档契约（定案⑨）：Indexed=false——Ring 地址序交付（仅写序=时间序场景）══

    [Fact]
    public async Task UnindexedMode_ScanOrderDelivery()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            SeriesName = "tc.series.raw",
            Indexed = false,
        });

        long b = Base;
        const int n = 20;
        for (int i = 0; i < n; i++)
            await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);

        // 写序=时间序契约下：地址序交付 = 时间序交付
        var list = new List<(long Ts, double V)>();
        await foreach (var (ts, v, _) in s.RangeAsync(b, b + n * TimeSpan.TicksPerSecond, default))
            list.Add((ts, TierTimeSeriesTestFactory.ParseVal(v.ToArray())));
        list.Should().HaveCount(n);
        list.Select(x => x.V).Should().Equal(Enumerable.Range(0, n).Select(i => (double)i));

        // 降档下 Latest/Floor 走全扫（O(n)——契约明示）
        var latest = await s.LatestAsync(default);
        latest.Should().NotBeNull();
        latest!.Value.Timestamp.Should().Be(b + (n - 1) * TimeSpan.TicksPerSecond);
        var floor = await s.FloorAsync(b + 5 * TimeSpan.TicksPerSecond, default);
        floor!.Value.Timestamp.Should().Be(b + 5 * TimeSpan.TicksPerSecond);

        // 降档 stats：IndexEntryCount 恒 0
        var stats = await s.GetStatsAsync(default);
        stats.IndexEntryCount.Should().Be(0);
        stats.SampleCount.Should().Be(n);

        // 降档 trim 仍可用（Ring 流反查边界）
        var deleted = await s.TruncateAsync(b + 10 * TimeSpan.TicksPerSecond, default);
        deleted.Should().Be(10);
        (await s.GetStatsAsync(default)).SampleCount.Should().Be(10);
        s.TrimmedUntilTimestamp.Should().Be(b + 10 * TimeSpan.TicksPerSecond);
    }

    // ══ 批量追加：地址一一对应 + 范围查一致 ══

    [Fact]
    public async Task AppendBatch_AddressesCorrespondAndRangeConsistent()
    {
        using var vol = new TestVolume();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol);

        long b = Base;
        var samples = Enumerable.Range(0, 50)
            .Select(i => (b + i * TimeSpan.TicksPerSecond, (ReadOnlyMemory<byte>)TierTimeSeriesTestFactory.Val(i)))
            .ToList();
        var results = await s.AppendBatchAsync(samples, default);
        results.Should().HaveCount(50);
        results.Select(r => r.Address).Should().BeInAscendingOrder("批量地址连续推进");

        var list = new List<double>();
        await foreach (var (_, v, _) in s.RangeAsync(b, b + 50 * TimeSpan.TicksPerSecond, default))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        list.Should().Equal(Enumerable.Range(0, 50).Select(i => (double)i));

        // 守卫违规：批内第 3 条老样本 → 抛出（前 2 条已写入——地址即凭证）
        long trimmedB = b + 100 * TimeSpan.TicksPerSecond;
        var mixed = new List<(long, ReadOnlyMemory<byte>)>
        {
            (trimmedB, TierTimeSeriesTestFactory.Val(1)),
            (trimmedB + TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(2)),
            (b, TierTimeSeriesTestFactory.Val(0)),   // 老样本——不违规（序列未 trim，时间早不拒）
            (b - TimeSpan.TicksPerHour, TierTimeSeriesTestFactory.Val(-1)),   // MaxOutOfOrderPast 未开也不拒
        };
        var ok = await s.AppendBatchAsync(mixed, default);
        ok.Should().HaveCount(4, "未开守卫时历史样本合法");
    }
}
