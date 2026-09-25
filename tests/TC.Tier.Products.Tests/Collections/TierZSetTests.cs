using TC.Tier.Core.Testing;

namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierZSet 契约矩阵测试（tc-tier-collections-spec §11——ZADD/ZSCORE/ZRANGE/ZINCRBY/ZPOPMIN 语义
/// 逐动词对齐 + 域隔离/容量护栏/TTL 整域过期）。
/// </summary>
public sealed class TierZSetTests
{
    // ══ ScoreCodec：全序编码单调性 + 往返一致（±0 归一）══

    [Fact]
    public void ScoreCodec_MonotonicAndRoundTrip()
    {
        var values = new double[]
        {
            double.NegativeInfinity, -1e300, -3.5, -2, -1.5, -1, -0.5, -0.0,
            0.0, 0.5, 1, 1.5, 2, 3.5, 1e300, double.MaxValue, double.PositiveInfinity,
        };
        var encoded = values.Select(ScoreCodec.Encode).ToArray();
        for (int i = 1; i < values.Length; i++)
        {
            if (values[i - 1] == 0 && values[i] == 0)
                encoded[i].Should().Be(encoded[i - 1], "±0 编码相等（-0.0 归一）");
            else
                encoded[i - 1].Should().BeLessThan(encoded[i],
                    $"编码序单调：{values[i - 1]} → {encoded[i - 1]:X16}, {values[i]} → {encoded[i]:X16}");
        }
        for (int i = 0; i < values.Length; i++)
            ScoreCodec.Decode(encoded[i]).Should().Be(values[i], $"往返一致 idx={i}");

        var nan = () => ScoreCodec.Encode(double.NaN);
        nan.Should().Throw<ArgumentException>().WithMessage("*NaN*");
    }

    private static readonly string[] RankAll = { "e", "a", "c", "b", "d" };
    private static readonly string[] RankMid = { "a", "c", "b" };
    private static readonly string[] RankTail = { "b", "d" };
    private static readonly string[] RevAll = { "m4", "m3", "m2", "m1", "m0" };
    private static readonly string[] RevTop2 = { "m4", "m3" };
    private static readonly string[] RevLast = { "m0" };
    private static readonly string[] ScoreMid = { "m3", "m4", "m5", "m6" };

    // ══ ZADD：新增判定（覆盖 = 0——Redis 缺省口径）+ 覆盖更新 score ══

    [Fact]
    public async Task ZAdd_NewThenOverwrite_ReturnsAndUpdates()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);

        (await s.ZAddAsync(1.5, TierZSetTestFactory.Bytes("m"), default)).Should().Be(1, "首次 = 新增");
        (await s.ZAddAsync(2.5, TierZSetTestFactory.Bytes("m"), default)).Should().Be(0, "覆盖 = 非新增");
        (await s.ZScoreAsync(TierZSetTestFactory.Bytes("m"), default)).Should().Be(2.5, "覆盖更新 score");
        (await s.ZCardAsync(default)).Should().Be(1, "覆盖不增计数");
    }

    [Fact]
    public async Task ZAdd_Nan_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        var act = async () => await s.ZAddAsync(double.NaN, TierZSetTestFactory.Bytes("m"), default);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*NaN*");
    }

    // ══ ZREM / ZSCORE / ZCARD：删除语义（不存在的成员不计）══

    [Fact]
    public async Task ZRem_RemovesOnlyExisting()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        await s.ZAddAsync(1, TierZSetTestFactory.Bytes("a"), default);
        await s.ZAddAsync(2, TierZSetTestFactory.Bytes("b"), default);

        (await s.ZRemAsync(Mems("a", "x"), default))
            .Should().Be(1);
        (await s.ZScoreAsync(TierZSetTestFactory.Bytes("a"), default)).Should().BeNull();
        (await s.ZCardAsync(default)).Should().Be(1);
        (await s.ZRemAsync(Mems("a"), default)).Should().Be(0, "重复删除幂等");
    }

    // ══ ZRANGE：排名区间（负数自尾部数 / 出界钳制 / 升序交付）══

    [Fact]
    public async Task ZRange_RankSemantics_AscendingOrder()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        var members = new[] { "e", "a", "c", "b", "d" };
        for (int i = 0; i < members.Length; i++)
            await s.ZAddAsync(i + 10, TierZSetTestFactory.Bytes(members[i]), default);
        // 分数序 = a(10) b(11) c(12) d(13) e(14)

        (await CollectAsync(s, 0, -1)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(RankAll, because: "ZRANGE 0 -1 = 全域升序（e=10 最低分）");
        (await CollectAsync(s, 1, 3)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(RankMid, because: "正区间");
        (await CollectAsync(s, -2, -1)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(RankTail, because: "负数自尾部数");
        (await CollectAsync(s, 3, 100)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(RankTail, because: "出界钳制");
        (await CollectAsync(s, 4, 2)).Should().BeEmpty("start > stop = 空");
    }

    // ══ ZREVRANGE：降序位序（TryGetMax + TryGetPrev 反向步进——波次 0b 原语）══

    [Fact]
    public async Task ZRevRange_DescendingPositions()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        for (int i = 0; i < 5; i++)
            await s.ZAddAsync(i, TierZSetTestFactory.Bytes($"m{i}"), default);

        var all = await CollectRevAsync(s, 0, -1);
        all.Select(t => TierZSetTestFactory.Str(t.Member)).Should().Equal(RevAll);
        (await CollectRevAsync(s, 0, 1)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(RevTop2, because: "降序前两位 = 最高分");
        (await CollectRevAsync(s, -1, -1)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(RevLast, because: "-1 = 最低分");
    }

    // ══ ZRANGEBYSCORE：分数区间（双闭；±Inf 全域）══

    [Fact]
    public async Task ZRangeByScore_InclusiveBounds()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        for (int i = 0; i < 10; i++)
            await s.ZAddAsync(i, TierZSetTestFactory.Bytes($"m{i}"), default);

        (await CollectByScoreAsync(s, 3, 6)).Select(t => TierZSetTestFactory.Str(t.Member)).Should()
            .Equal(ScoreMid, because: "双闭区间");
        (await CollectByScoreAsync(s, double.NegativeInfinity, double.PositiveInfinity)).Should()
            .HaveCount(10, "±Inf = 全域");
        (await CollectByScoreAsync(s, 6, 3)).Should().BeEmpty("min > max = 空");
        (await s.ZCountAsync(3, 6, default)).Should().Be(4);
        (await s.ZCountAsync(6, 3, default)).Should().Be(0);
    }

    // ══ 同分成员：稳定序（member 哈希序）+ 计数正确 ══

    [Fact]
    public async Task SameScore_Members_StableOrderAndCount()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        for (int i = 0; i < 20; i++)
            await s.ZAddAsync(7, TierZSetTestFactory.Bytes($"m{i:00}"), default);

        (await s.ZCardAsync(default)).Should().Be(20, "同分不覆盖");
        var sameScore = await CollectAsync(s, 0, -1);
        sameScore.Should().BeInAscendingOrder(t => t.Score);
        sameScore.Select(t => t.Score).Should().OnlyContain(v => v == 7);
    }

    // ══ ZPOPMIN/ZPOPMAX：原子取极值并移除（定案③——延迟队列消费面）══

    [Fact]
    public async Task ZPopMin_Max_AtomicExtreme()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        await s.ZAddAsync(5, TierZSetTestFactory.Bytes("mid"), default);
        await s.ZAddAsync(1, TierZSetTestFactory.Bytes("low"), default);
        await s.ZAddAsync(9, TierZSetTestFactory.Bytes("high"), default);

        var min = await s.ZPopMinAsync(default);
        min.Should().NotBeNull();
        TierZSetTestFactory.Str(min!.Value.Member).Should().Be("low");
        min.Value.Score.Should().Be(1);

        var max = await s.ZPopMaxAsync(default);
        max.Should().NotBeNull();
        TierZSetTestFactory.Str(max!.Value.Member).Should().Be("high");
        max.Value.Score.Should().Be(9);

        (await s.ZCardAsync(default)).Should().Be(1, "弹出即移除");
        (await s.ZScoreAsync(TierZSetTestFactory.Bytes("low"), default)).Should().BeNull();

        // 负分数极值
        await s.ZAddAsync(-3.5, TierZSetTestFactory.Bytes("neg"), default);
        (await s.ZPopMinAsync(default))!.Value.Score.Should().Be(-3.5);

        // 空域 = null
        await s.ZRemAsync(Mems("mid"), default);
        (await s.ZPopMinAsync(default)).Should().BeNull();
        (await s.ZPopMaxAsync(default)).Should().BeNull();
    }

    // ══ ZINCRBY：新成员 = delta 初值 / 覆盖更新 / NaN 结果 fail-fast ══

    [Fact]
    public async Task ZIncrBy_NewAndExisting_NanFailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);

        (await s.ZIncrByAsync(2.5, TierZSetTestFactory.Bytes("m"), default)).Should().Be(2.5, "新成员 delta 即初值");
        (await s.ZIncrByAsync(1.5, TierZSetTestFactory.Bytes("m"), default)).Should().Be(4.0);
        (await s.ZCardAsync(default)).Should().Be(1);
        (await s.ZScoreAsync(TierZSetTestFactory.Bytes("m"), default)).Should().Be(4.0);

        // ±Inf 相加 = NaN → fail-fast
        await s.ZIncrByAsync(double.PositiveInfinity, TierZSetTestFactory.Bytes("inf"), default);
        var act = async () => await s.ZIncrByAsync(double.NegativeInfinity, TierZSetTestFactory.Bytes("inf"), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*NaN*");
    }

    // ══ 域隔离：TruncateDomain 不波及他域（TruncateRange 判例回归面）══

    [Fact]
    public async Task DomainIsolation_TruncateOne_LeavesOthersIntact()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        foreach (var d in new uint[] { 1, 2, 3 })
        {
            await s.ZAddAsync(d, 1, TierZSetTestFactory.Bytes("shared"), default);
            await s.ZAddAsync(d, 2, TierZSetTestFactory.Bytes($"only{d}"), default);
        }

        (await s.TruncateDomainAsync(2, default)).Should().Be(2);
        (await s.ZCardAsync(2, default)).Should().Be(0);
        s.DomainIds.Should().BeEquivalentTo(new uint[] { 1, 3 });
        (await s.ZScoreAsync(3, TierZSetTestFactory.Bytes("shared"), default)).Should().Be(1, "域 3 不受波及");
        (await s.ZCardAsync(3, default)).Should().Be(2);

        // 回收后同域重写合法
        (await s.ZAddAsync(2, 9, TierZSetTestFactory.Bytes("fresh"), default)).Should().Be(1);
    }

    // ══ -0.0 归一：与 +0.0 同键语义 ══

    [Fact]
    public async Task NegativeZero_NormalizedToZero()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        (await s.ZAddAsync(-0.0, TierZSetTestFactory.Bytes("m"), default)).Should().Be(1);
        (await s.ZAddAsync(+0.0, TierZSetTestFactory.Bytes("m"), default)).Should().Be(0, "-0.0 与 +0.0 同一成员分数语义");
        (await s.ZScoreAsync(TierZSetTestFactory.Bytes("m"), default)).Should().Be(0.0);
        (await s.ZCardAsync(default)).Should().Be(1);
        (await s.ZCountAsync(-0.0, +0.0, default)).Should().Be(1, "区间含 ±0");
    }

    // ══ 容量护栏 + 统计快照 ══

    [Fact]
    public async Task DomainCapacity_Guard_And_Stats()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol, o => o with
        {
            DomainCapacity = 2,
            Clock = clock,
            DomainTtl = TimeSpan.FromMinutes(10),
        });

        await s.ZAddAsync(0, 1, TierZSetTestFactory.Bytes("a"), default);
        var act = async () => await s.ZAddAsync(1, 1, TierZSetTestFactory.Bytes("a"), default);
        await act.Should().NotThrowAsync();
        var over = async () => await s.ZAddAsync(2, 1, TierZSetTestFactory.Bytes("a"), default);
        await over.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainCapacity*");

        var stats = await s.GetStatsAsync(1, default);
        stats.MemberCount.Should().Be(1);
        stats.BytesOccupied.Should().Be(21 + 1, "envelope 逻辑字节口径（头 21 + member 1）");
        stats.TtlBoundaryTimestamp.Should().Be(clock.GetUtcNow().Ticks + TimeSpan.FromMinutes(10).Ticks);
    }

    // ══ TTL：整域过期 + 写入刷新锚（FakeTimeProvider 确定性）══

    [Fact]
    public async Task Ttl_ExpiresWholeDomain_RefreshOnWrite()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol, o => o with
        {
            Clock = clock,
            DomainTtl = TimeSpan.FromMinutes(10),
        });

        await s.ZAddAsync(7, 1, TierZSetTestFactory.Bytes("a"), default);
        await s.ZAddAsync(8, 1, TierZSetTestFactory.Bytes("b"), default);

        clock.Advance(TimeSpan.FromMinutes(5));
        await s.ZAddAsync(8, 2, TierZSetTestFactory.Bytes("b"), default);   // 域 8 锚刷新
        clock.Advance(TimeSpan.FromMinutes(6));
        await s.RunRetentionAsync(default);

        (await s.ZCardAsync(7, default)).Should().Be(0, "超 TTL 域整域消失");
        s.DomainIds.Should().NotContain(7);
        (await s.ZCardAsync(8, default)).Should().Be(1, "写入刷新锚——未过期");
    }

    private static ReadOnlyMemory<byte>[] Mems(params string[] members)
        => members.Select(m => (ReadOnlyMemory<byte>)TierZSetTestFactory.Bytes(m)).ToArray();

    private static async Task<List<(byte[] Member, double Score)>> CollectAsync(
        TierZSet s, long start, long stop, uint domain = 0)
    {
        var list = new List<(byte[], double)>();
        await foreach (var t in s.ZRangeAsync(domain, start, stop, default))
            list.Add(t);
        return list;
    }

    private static async Task<List<(byte[] Member, double Score)>> CollectRevAsync(
        TierZSet s, long start, long stop, uint domain = 0)
    {
        var list = new List<(byte[], double)>();
        await foreach (var t in s.ZRevRangeAsync(domain, start, stop, default))
            list.Add(t);
        return list;
    }

    private static async Task<List<(byte[] Member, double Score)>> CollectByScoreAsync(
        TierZSet s, double min, double max, uint domain = 0)
    {
        var list = new List<(byte[], double)>();
        await foreach (var t in s.ZRangeByScoreAsync(domain, min, max, default))
            list.Add(t);
        return list;
    }
}
