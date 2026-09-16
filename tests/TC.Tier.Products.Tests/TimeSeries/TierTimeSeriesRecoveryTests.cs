using TC.Tier.Runtime.Structures.Metadata;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Products.Tests.TimeSeries;

/// <summary>
/// TierTimeSeries 恢复对账测试（tc-tier-timeseries-spec §11 验证矩阵 6/7——崩溃窗口收口）。
/// <para>mem 卷重启语义：Dispose 实例（Ring Dispose flush 落盘保障）→ 同卷 Builder 重建。</para>
/// </summary>
public sealed class TierTimeSeriesRecoveryTests
{
    private static long Base => DateTime.UtcNow.Ticks;

    // ══ 矩阵 7：索引崩溃对账——写后未 flush 断电 → 重启对账重插，Range 零丢失 ══

    [Fact]
    public async Task CrashBeforeIndexPersist_ReconcileReinserts_RangeZeroLoss()
    {
        using var vol = new TestVolume();
        long b = Base;
        const int n = 30;
        await using (var s = await TierTimeSeriesTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < n; i++)
                await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);   // 数据已落盘；索引锚点帧缺省 30s 策略未及物化（= 崩溃窗口）
        }
        // Dispose 即"断电"重启：索引全量重放（resolver 逐条解 envelope）→ 对账零丢失
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol);
        var list = new List<double>();
        await foreach (var (_, v, _) in s2.RangeAsync(long.MinValue, long.MaxValue, default))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        list.Should().Equal(Enumerable.Range(0, n).Select(i => (double)i),
            because: "索引未持久崩溃窗口 → 恢复对账重插，Range 零丢失");

        // 对账后追加查询继续正常（索引可用）
        await s2.AppendAsync(b + n * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(n), default);
        var latest = await s2.LatestAsync(default);
        latest!.Value.Timestamp.Should().Be(b + n * TimeSpan.TicksPerSecond);
    }

    // ══ 矩阵 7（锚点帧已物化形态）：帧载入 + 增量重放窗口收敛 ══

    [Fact]
    public async Task CrashAfterIndexFrameDump_ReplayWindowConverges()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using (var s = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            IndexPersistencePolicy = new SortedIndexPersistencePolicy
            {
                Interval = TimeSpan.FromMilliseconds(300),
                EntryDeltaThreshold = 1,
            },
        }))
        {
            for (int i = 0; i < 10; i++)
                await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);
            await Task.Delay(700, default);   // 等锚点帧物化（W ≈ 已 flush 尾）
            for (int i = 10; i < 20; i++)
                await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);   // 帧后增量未及物化（= 增量重放窗口）
        }
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol, o => o with
        {
            IndexPersistencePolicy = new SortedIndexPersistencePolicy
            {
                Interval = TimeSpan.FromMilliseconds(300),
                EntryDeltaThreshold = 1,
            },
        });
        var list = new List<double>();
        await foreach (var (_, v, _) in s2.RangeAsync(long.MinValue, long.MaxValue, default))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        list.Should().Equal(Enumerable.Range(0, 20).Select(i => (double)i),
            because: "载帧 + 增量重放 (W, Tail) 收敛——零丢失");
    }

    // ══ 矩阵 6：trim 崩溃窗口——水位超前于数据事实 → 重启对账水位校正（§7.4c min 收口）══

    [Fact]
    public async Task TrimCrashWindow_WatermarkOverclaims_RecoveryCorrectsToDataFact()
    {
        using var vol = new TestVolume();
        long b = Base;
        const int n = 10;
        await using (var s = await TierTimeSeriesTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < n; i++)
                await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);
        }

        // ★ 模拟崩溃窗口（水位超前的形态：索引/水位已 trim 推进而 Ring 截断未生效——flush 滞后窗）：
        //   外部覆写水位 meta，声称已回收至 ts(末样本)+1（超claims——实际数据全在）
        var wmeta = new VersionedMetadata(vol.Fs, new VersionedMetadataSettings(
            new Runtime.Storage.StorageEngineOptions("tc.series.ts.water", 4L << 20,
                enableSegmentation: true, preallocateFile: false))
        { PayloadSize = TimeSeriesWatermarkState.PayloadSize });
        wmeta.Initialize();
        await wmeta.WaitForReadyAsync(default);
        var overclaimed = new TimeSeriesWatermarkState(
            b + n * TimeSpan.TicksPerSecond, LogicalAddress.Empty, 0, 5);
        wmeta.Write(overclaimed.Write());
        wmeta.Persist();
        wmeta.Dispose();

        // 重启：对账水位校正 TrimmedUntil = min(水位值, 首样本 ts) = 首样本 ts（回拉到数据事实）
        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol);
        s2.TrimmedUntilTimestamp.Should().Be(b,
            "水位超前于数据事实 → 对账收口 min(水位, 首样本 ts)（§7.4c）");

        // 全部样本仍可查（未被误拒/误回收）；边界写入合法（未推进回收边界）
        var list = new List<double>();
        await foreach (var (_, v, _) in s2.RangeAsync(long.MinValue, long.MaxValue, default))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        list.Should().HaveCount(n, "对账收口后数据零丢失");
        await s2.AppendAsync(b, TierTimeSeriesTestFactory.Val(100), default);   // ts = 首样本——守卫不误拒
    }

    // ══ 基线重启：水位持久往返（trim 后重启 TrimmedUntil 保持，不回退）══

    [Fact]
    public async Task WatermarkPersistsAcrossRestart()
    {
        using var vol = new TestVolume();
        long b = Base;
        await using (var s = await TierTimeSeriesTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 20; i++)
                await s.AppendAsync(b + i * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(i), default);
            await s.FlushAsync(default);
            var deleted = await s.TruncateAsync(b + 8 * TimeSpan.TicksPerSecond, default);
            deleted.Should().Be(8);
        }

        await using var s2 = await TierTimeSeriesTestFactory.StartAsync(vol);
        s2.TrimmedUntilTimestamp.Should().Be(b + 8 * TimeSpan.TicksPerSecond, "水位持久——重启不回退");
        var list = new List<double>();
        await foreach (var (_, v, _) in s2.RangeAsync(long.MinValue, long.MaxValue, default))
            list.Add(TierTimeSeriesTestFactory.ParseVal(v.ToArray()));
        list.Should().Equal(Enumerable.Range(8, 12).Select(i => (double)i), because: "已回收区间不再交付");

        // 重启后写入已回收区间仍被拒（守卫随水位恢复）
        Func<Task> act = async () =>
            await s2.AppendAsync(b + 2 * TimeSpan.TicksPerSecond, TierTimeSeriesTestFactory.Val(2), default);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
