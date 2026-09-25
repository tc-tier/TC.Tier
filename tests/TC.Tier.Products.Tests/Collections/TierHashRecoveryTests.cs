namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierHash 恢复对账测试（tc-tier-collections-spec §7 三步 + §11 矩阵——恢复对账）。
/// <para>mem 卷重启语义：Dispose 实例（Ring Dispose flush 落盘保障）→ 同卷 Builder 重建。</para>
/// </summary>
public sealed class TierHashRecoveryTests
{
    // ══ 数据完好重启：索引重建 + 域账事实校正——全部 field/值/计数零丢失 ══

    [Fact]
    public async Task Reopen_DataIntact_IndexRebuilt()
    {
        using var vol = new TestVolume();
        await using (var s = await TierHashTestFactory.StartAsync(vol))
        {
            await s.HSetAsync(TierHashTestFactory.Bytes("a"), TierHashTestFactory.Bytes("1"), default);
            await s.HSetAsync(TierHashTestFactory.Bytes("b"), TierHashTestFactory.Bytes("2"), default);
            await s.HSetAsync(9, TierHashTestFactory.Bytes("c"), TierHashTestFactory.Bytes("3"), default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierHashTestFactory.StartAsync(vol);

        s2.DomainIds.Should().BeEquivalentTo(new uint[] { 0, 9 }, "域账种子化 + 零存活域注销");
        (await s2.HLenAsync(default)).Should().Be(2);
        (await s2.HLenAsync(9, default)).Should().Be(1);
        (await s2.HGetAsync(TierHashTestFactory.Bytes("a"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("1"));
        (await s2.HGetAsync(9, TierHashTestFactory.Bytes("c"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("3"));

        // 恢复后写/删继续正常
        (await s2.HSetAsync(TierHashTestFactory.Bytes("a"), TierHashTestFactory.Bytes("1x"), default))
            .Should().BeFalse("恢复后判重语义延续——已有 field = 覆盖");
        (await s2.HLenAsync(default)).Should().Be(2);
    }

    // ══ 覆盖崩溃窗口：HSET 双版本落盘——重启后最新写胜出、计数准确 ══

    [Fact]
    public async Task Reopen_AfterOverwrite_LatestWins()
    {
        using var vol = new TestVolume();
        await using (var s = await TierHashTestFactory.StartAsync(vol))
        {
            await s.HSetAsync(TierHashTestFactory.Bytes("k"), TierHashTestFactory.Bytes("old"), default);
            await s.HSetAsync(TierHashTestFactory.Bytes("k"), TierHashTestFactory.Bytes("new"), default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierHashTestFactory.StartAsync(vol);

        (await s2.HGetAsync(TierHashTestFactory.Bytes("k"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("new"), "索引重放流序折叠 = 最新写胜出");
        (await s2.HLenAsync(default)).Should().Be(1, "域账以重放事实权威化（非旧快照）");
        var all = await CollectAll(s2);
        all.Should().BeEquivalentTo(new Dictionary<string, string> { ["k"] = "new" });
    }

    // ══ 删除崩溃窗口：HDEL 后重启——field 保持已删（墓碑重放触发索引删除）══

    [Fact]
    public async Task Reopen_AfterDelete_StaysDeleted()
    {
        using var vol = new TestVolume();
        await using (var s = await TierHashTestFactory.StartAsync(vol))
        {
            await s.HSetAsync(TierHashTestFactory.Bytes("a"), TierHashTestFactory.Bytes("1"), default);
            await s.HSetAsync(TierHashTestFactory.Bytes("b"), TierHashTestFactory.Bytes("2"), default);
            await s.HDelAsync(new ReadOnlyMemory<byte>[] { TierHashTestFactory.Bytes("a") }, default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierHashTestFactory.StartAsync(vol);

        (await s2.HGetAsync(TierHashTestFactory.Bytes("a"), default)).Should().BeNull("墓碑重放——已删不复活");
        (await s2.HLenAsync(default)).Should().Be(1);
        (await s2.HExistsAsync(TierHashTestFactory.Bytes("b"), default)).Should().BeTrue();
    }

    // ══ 整域回收后重启：域保持消失（注销持久——零存活域不复活）══

    [Fact]
    public async Task Reopen_AfterTruncateDomain_StaysGone()
    {
        using var vol = new TestVolume();
        await using (var s = await TierHashTestFactory.StartAsync(vol))
        {
            await s.HSetAsync(1, TierHashTestFactory.Bytes("a"), TierHashTestFactory.Bytes("1"), default);
            await s.HSetAsync(1, TierHashTestFactory.Bytes("b"), TierHashTestFactory.Bytes("2"), default);
            await s.HSetAsync(2, TierHashTestFactory.Bytes("c"), TierHashTestFactory.Bytes("3"), default);
            await s.TruncateDomainAsync(1, default);
        }
        await using var s2 = await TierHashTestFactory.StartAsync(vol);

        s2.DomainIds.Should().NotContain(1, "整域回收注销持久——重启不复活");
        (await s2.HLenAsync(1, default)).Should().Be(0);
        (await s2.HLenAsync(2, default)).Should().Be(1);
        (await s2.HGetAsync(2, TierHashTestFactory.Bytes("c"), default)).Should().NotBeNull();
    }

    // ══ 域账 last-write 持久：重启后 TTL 锚延续（不因恢复重置）══

    [Fact]
    public async Task Reopen_LastWriteAnchorSurvives()
    {
        using var vol = new TestVolume();
        long lastWrite;
        await using (var s = await TierHashTestFactory.StartAsync(vol))
        {
            await s.HSetAsync(5, TierHashTestFactory.Bytes("a"), TierHashTestFactory.Bytes("1"), default);
            lastWrite = (await s.GetStatsAsync(5, default)).LastWriteTimestamp!.Value;
            await s.FlushAsync(default);   // 脏域账持久
        }
        await using var s2 = await TierHashTestFactory.StartAsync(vol);

        var stats = await s2.GetStatsAsync(5, default);
        stats.LastWriteTimestamp.Should().Be(lastWrite, "域账 last-write 持久——重启延续（TTL 锚不重置）");
    }

    // ══ 未 flush 写入 + 重启：Ring Dispose flush 保障数据落盘——零丢失 ══

    [Fact]
    public async Task Reopen_UnflushedWrites_DisposeFlushGuarantees()
    {
        using var vol = new TestVolume();
        await using (var s = await TierHashTestFactory.StartAsync(vol))
        {
            for (int i = 0; i < 20; i++)
                await s.HSetAsync(TierHashTestFactory.Bytes($"f{i}"), TierHashTestFactory.Bytes($"v{i}"), default);
            // 不显式 FlushAsync——Dispose 落盘保障（mem 卷重启语义）
        }
        await using var s2 = await TierHashTestFactory.StartAsync(vol);
        (await s2.HLenAsync(default)).Should().Be(20, "索引全量重放 + 域账事实校正——零丢失");
        (await s2.HGetAsync(TierHashTestFactory.Bytes("f7"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("v7"));
    }

    private static async Task<Dictionary<string, string>> CollectAll(TierHash s)
    {
        var dict = new Dictionary<string, string>();
        await foreach (var (f, v) in s.HGetAllAsync(default))
            dict[TierHashTestFactory.Str(f)] = TierHashTestFactory.Str(v);
        return dict;
    }
}
