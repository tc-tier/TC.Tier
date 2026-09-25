namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierZSet 恢复对账测试（tc-tier-collections-spec §7——双索引各自重建、交集 = 真相；§11 矩阵）。
/// <para>mem 卷重启语义：Dispose 实例 → 同卷 Builder 重建。</para>
/// </summary>
public sealed class TierZSetRecoveryTests
{
    // ══ 数据完好重启：双索引重建 + 域账事实校正 ══

    [Fact]
    public async Task Reopen_DataIntact_DualIndexRebuilt()
    {
        using var vol = new TestVolume();
        await using (var s = await TierZSetTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 10; i++)
                await s.ZAddAsync(i * 1.5, TierZSetTestFactory.Bytes($"m{i}"), default);
            await s.ZAddAsync(9, -1, TierZSetTestFactory.Bytes("other"), default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierZSetTestFactory.StartAsync(vol);

        (await s2.ZCardAsync(default)).Should().Be(10);
        (await s2.ZCardAsync(9, default)).Should().Be(1);
        (await s2.ZScoreAsync(TierZSetTestFactory.Bytes("m3"), default)).Should().Be(4.5);
        var all = await CollectAsync(s2, 0, -1);
        all.Select(t => t.Score).Should().BeInAscendingOrder("有序视图完好");
        var d9 = await CollectAsync(s2, 0, -1, domain: 9);
        d9.Should().ContainSingle().Which.Member.Should().Equal(TierZSetTestFactory.Bytes("other"),
            "域 9 有序视图独立完好");
    }

    // ══ 覆盖崩溃窗口：旧 score 有序键残留 → 恢复对账清扫 + 最新生效 ══

    [Fact]
    public async Task Reopen_AfterOverwrite_StaleScoreKeyCleaned()
    {
        using var vol = new TestVolume();
        await using (var s = await TierZSetTestFactory.StartAsync(vol))
        {
            await s.ZAddAsync(10, TierZSetTestFactory.Bytes("m"), default);
            await s.ZAddAsync(20, TierZSetTestFactory.Bytes("m"), default);   // 覆盖——旧有序键删除
            await s.ZAddAsync(5, TierZSetTestFactory.Bytes("k"), default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierZSetTestFactory.StartAsync(vol);

        (await s2.ZScoreAsync(TierZSetTestFactory.Bytes("m"), default)).Should().Be(20, "最新写胜出");
        (await s2.ZCardAsync(default)).Should().Be(2, "域账以重放事实权威化");

        // 有序视图收敛：m 只在 score=20 出现一次（陈旧 score=10 条目被对账清扫）
        var all = await CollectAsync(s2, 0, -1);
        all.Should().HaveCount(2, "旧 score 有序键不复活");
        all.Where(t => TierZSetTestFactory.Str(t.Member) == "m").Should().ContainSingle()
            .Which.Score.Should().Be(20);
        (await s2.ZCountAsync(15, 25, default)).Should().Be(1, "分数区间视图一致");
    }

    // ══ 删除崩溃窗口：ZREM 后重启——双索引保持已删 ══

    [Fact]
    public async Task Reopen_AfterRemoval_StaysDeleted()
    {
        using var vol = new TestVolume();
        await using (var s = await TierZSetTestFactory.StartAsync(vol))
        {
            await s.ZAddAsync(1, TierZSetTestFactory.Bytes("a"), default);
            await s.ZAddAsync(2, TierZSetTestFactory.Bytes("b"), default);
            await s.ZRemAsync(new ReadOnlyMemory<byte>[] { TierZSetTestFactory.Bytes("a") }, default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierZSetTestFactory.StartAsync(vol);

        (await s2.ZScoreAsync(TierZSetTestFactory.Bytes("a"), default)).Should().BeNull("墓碑重放——已删不复活");
        (await s2.ZCardAsync(default)).Should().Be(1);
        (await s2.ZCountAsync(long.MinValue / 2.0, long.MaxValue / 2.0, default)).Should().Be(1, "有序视图一致");
    }

    // ══ 整域回收后重启：域保持消失 ══

    [Fact]
    public async Task Reopen_AfterTruncateDomain_StaysGone()
    {
        using var vol = new TestVolume();
        await using (var s = await TierZSetTestFactory.StartAsync(vol))
        {
            await s.ZAddAsync(1, 1, TierZSetTestFactory.Bytes("a"), default);
            await s.ZAddAsync(2, 2, TierZSetTestFactory.Bytes("b"), default);
            await s.TruncateDomainAsync(1, default);
        }
        await using var s2 = await TierZSetTestFactory.StartAsync(vol);

        s2.DomainIds.Should().NotContain(1);
        (await s2.ZCardAsync(1, default)).Should().Be(0);
        (await s2.ZCardAsync(2, default)).Should().Be(1);
    }

    // ══ 未 flush 写入 + 重启：零丢失 ══

    [Fact]
    public async Task Reopen_UnflushedWrites_ZeroLoss()
    {
        using var vol = new TestVolume();
        await using (var s = await TierZSetTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 15; i++)
                await s.ZAddAsync(i, TierZSetTestFactory.Bytes($"m{i:00}"), default);
        }
        await using var s2 = await TierZSetTestFactory.StartAsync(vol);
        (await s2.ZCardAsync(default)).Should().Be(15);
        (await s2.ZPopMinAsync(default))!.Value.Score.Should().Be(0, "有序极值完好");
    }

    private static async Task<List<(byte[] Member, double Score)>> CollectAsync(
        TierZSet s, long start, long stop, uint domain = 0)
    {
        var list = new List<(byte[], double)>();
        await foreach (var t in s.ZRangeAsync(domain, start, stop, default))
            list.Add(t);
        return list;
    }
}
