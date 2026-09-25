using TC.Tier.Core.Testing;

namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierHash 契约矩阵测试（tc-tier-collections-spec §11——CRUD/判重幂等/覆盖语义/计数准确/扫描序/
/// 域隔离（TruncateRange 判例回归面）/容量护栏 fail-fast/字节上限 fail-fast/TTL 整域过期）。
/// <para>★ 确定性纪律：TTL 场景用 <see cref="FakeTimeProvider"/> 注入时钟 + RunRetentionAsync 直调
/// ——零真实睡等，不赌后台节拍。</para>
/// </summary>
public sealed class TierHashTests
{
    // ══ HSET：新增判定（Redis HSET 返回值语义）+ 覆盖 ══

    [Fact]
    public async Task HSet_NewThenOverwrite_NewFlagAndLatestValue()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        (await s.HSetAsync(TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v1"), default))
            .Should().BeTrue("首次写入 = 新增");
        (await s.HSetAsync(TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v2"), default))
            .Should().BeFalse("覆盖既有 field = 非新增");

        var got = await s.HGetAsync(TierHashTestFactory.Bytes("f"), default);
        got.Should().NotBeNull();
        TierHashTestFactory.Str(got!.Value).Should().Be("v2", "覆盖后取最新值");
        (await s.HLenAsync(default)).Should().Be(1, "覆盖不增计数");
    }

    // ══ HDEL/HLEN/HEXISTS：删除语义（Redis HDEL 返回值——不存在的 field 不计）══

    [Fact]
    public async Task HDel_RemovesOnlyExisting_CountsAccurate()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        foreach (var f in new[] { "a", "b", "c" })
            await s.HSetAsync(TierHashTestFactory.Bytes(f), TierHashTestFactory.Bytes("v"), default);

        (await s.HDelAsync(Mems("a", "x"), default))
            .Should().Be(1, "仅存在的 field 计入删除数");
        (await s.HExistsAsync(TierHashTestFactory.Bytes("a"), default)).Should().BeFalse();
        (await s.HExistsAsync(TierHashTestFactory.Bytes("b"), default)).Should().BeTrue();
        (await s.HLenAsync(default)).Should().Be(2);

        // 重复删除幂等——返回 0
        (await s.HDelAsync(Mems("a"), default)).Should().Be(0);
        (await s.HLenAsync(default)).Should().Be(2);
    }

    // ══ HGETALL：全域枚举——只交付存活版本（覆盖/删除收敛）══

    [Fact]
    public async Task HGetAll_YieldsOnlyLiveVersions_AfterOverwriteAndDelete()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        await s.HSetAsync(TierHashTestFactory.Bytes("f1"), TierHashTestFactory.Bytes("old1"), default);
        await s.HSetAsync(TierHashTestFactory.Bytes("f2"), TierHashTestFactory.Bytes("v2"), default);
        await s.HSetAsync(TierHashTestFactory.Bytes("f3"), TierHashTestFactory.Bytes("v3"), default);
        await s.HSetAsync(TierHashTestFactory.Bytes("f1"), TierHashTestFactory.Bytes("new1"), default);   // 覆盖
        await s.HDelAsync(Mems("f2"), default);                                           // 删除

        var all = await CollectAll(s);
        all.Should().HaveCount(2, "覆盖 + 删除后仅存活版本交付");
        all.Should().Contain(new Dictionary<string, string> { ["f1"] = "new1", ["f3"] = "v3" });
        all.Should().NotContainKey("f2");
    }

    // ══ 域隔离：同 field 跨域独立 + TruncateDomain 不波及他域（TruncateRange 判例回归面）══

    [Fact]
    public async Task DomainIsolation_TruncateOne_LeavesOthersIntact()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        foreach (var d in new uint[] { 1, 2, 3 })
        {
            await s.HSetAsync(d, TierHashTestFactory.Bytes("shared"), TierHashTestFactory.Bytes($"v{d}"), default);
            await s.HSetAsync(d, TierHashTestFactory.Bytes($"only{d}"), TierHashTestFactory.Bytes("x"), default);
        }

        (await s.TruncateDomainAsync(2, default)).Should().Be(2, "整域回收返回成员数");

        // 域 2 消失；域 1/3 完整（共享 field 名各自独立值——哈希键含域前缀）
        (await s.HLenAsync(2, default)).Should().Be(0);
        s.DomainIds.Should().BeEquivalentTo(new uint[] { 1, 3 }, "本测试未写默认域——仅 1/3 已注册");
        (await s.HGetAsync(1, TierHashTestFactory.Bytes("shared"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("v1"));
        (await s.HGetAsync(3, TierHashTestFactory.Bytes("only3"), default)).Should().NotBeNull();
        (await s.HGetAsync(3, TierHashTestFactory.Bytes("shared"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("v3"), "域 3 不受域 2 回收波及");

        // 回收后同域重写合法
        (await s.HSetAsync(2, TierHashTestFactory.Bytes("fresh"), TierHashTestFactory.Bytes("v"), default))
            .Should().BeTrue();
        (await s.HLenAsync(2, default)).Should().Be(1);
    }

    // ══ 双 API：两参 = 默认域 0；与命名域同位平等 ══

    [Fact]
    public async Task TwoParamApi_LandsDefaultDomain_LazyRegistration()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        await s.HSetAsync(TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v"), default);
        s.DomainCount.Should().Be(1, "惰性注册默认域");
        s.DomainIds.Should().Equal(new uint[] { 0 });

        // 域 1 同名 field 与默认域互不可见
        (await s.HGetAsync(1, TierHashTestFactory.Bytes("f"), default)).Should().BeNull();
        (await s.HLenAsync(1, default)).Should().Be(0);
    }

    // ══ HINCRBY：从 0 起算 / 既有值续算 / 负增量 / 非 8B 值 fail-fast / 溢出 fail-fast ══

    [Fact]
    public async Task HIncrBy_FromZeroAndExisting_OverflowFailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        (await s.HIncrByAsync(TierHashTestFactory.Bytes("n"), 5, default)).Should().Be(5, "不存在字段从 0 起算");
        (await s.HIncrByAsync(TierHashTestFactory.Bytes("n"), 3, default)).Should().Be(8);
        (await s.HIncrByAsync(TierHashTestFactory.Bytes("n"), -10, default)).Should().Be(-2, "负增量");
        (await s.HLenAsync(default)).Should().Be(1, "HIncrBy 覆盖不增计数");

        // 非 8B 既有值 fail-fast
        await s.HSetAsync(TierHashTestFactory.Bytes("txt"), TierHashTestFactory.Bytes("hello"), default);
        var act = async () => await s.HIncrByAsync(TierHashTestFactory.Bytes("txt"), 1, default);
        await act.Should().ThrowAsync<InvalidDataException>().WithMessage("*8B*");

        // int64 溢出 fail-fast
        await s.HIncrByAsync(TierHashTestFactory.Bytes("max"), long.MaxValue, default);
        var overflow = async () => await s.HIncrByAsync(TierHashTestFactory.Bytes("max"), 1, default);
        await overflow.Should().ThrowAsync<OverflowException>();
    }

    // ══ 容量护栏：DomainCapacity 超限 fail-fast（数据未写即拒绝）══

    [Fact]
    public async Task DomainCapacity_Guard_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol, o => o with { DomainCapacity = 2 });

        await s.HSetAsync(0, TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v"), default);
        await s.HSetAsync(1, TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v"), default);
        var act = async () => await s.HSetAsync(2, TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v"), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainCapacity*");
    }

    // ══ 字节上限：DomainMaxBytes 超限写侧 fail-fast ══

    [Fact]
    public async Task DomainMaxBytes_Guard_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol, o => o with { DomainMaxBytes = 40 });

        // 单条 13B 头 + field + value：先写一条 20B 级（合法），再写超限条（拒绝）
        await s.HSetAsync(0, TierHashTestFactory.Bytes("f1"), TierHashTestFactory.Bytes("12345"), default);
        var act = async () => await s.HSetAsync(0, TierHashTestFactory.Bytes("f2"),
            TierHashTestFactory.Bytes(new string('x', 100)), default);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainMaxBytes*");
        (await s.HLenAsync(default)).Should().Be(1, "被拒写入不产生副作用");
    }

    // ══ 统计快照：计数/字节/last-write/TTL 边界 ══

    [Fact]
    public async Task GetStats_ReflectsCountsBytesAndTtlBoundary()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol, o => o with
        {
            Clock = clock,
            DomainTtl = TimeSpan.FromMinutes(10),
        });

        (await s.GetStatsAsync(42, default)).MemberCount.Should().Be(0, "未注册域 = 零快照");

        await s.HSetAsync(42, TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("1234567"), default);
        var stats = await s.GetStatsAsync(42, default);
        stats.MemberCount.Should().Be(1);
        stats.BytesOccupied.Should().Be(13 + 1 + 7, "envelope 逻辑字节口径（头 13 + field 1 + value 7）");
        stats.LastWriteTimestamp.Should().Be(clock.GetUtcNow().Ticks);
        stats.TtlBoundaryTimestamp.Should().Be(clock.GetUtcNow().Ticks + TimeSpan.FromMinutes(10).Ticks);
    }

    // ══ TTL：整域过期消失（定案②——Redis key TTL 语义）+ 写入刷新锚 ══

    [Fact]
    public async Task Ttl_ExpiresWholeDomain_RefreshOnWrite()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol, o => o with
        {
            Clock = clock,
            DomainTtl = TimeSpan.FromMinutes(10),
        });

        await s.HSetAsync(7, TierHashTestFactory.Bytes("a"), TierHashTestFactory.Bytes("1"), default);
        await s.HSetAsync(8, TierHashTestFactory.Bytes("b"), TierHashTestFactory.Bytes("2"), default);

        // t+6min：未过期——零动作
        clock.Advance(TimeSpan.FromMinutes(6));
        await s.RunRetentionAsync(default);
        (await s.HLenAsync(7, default)).Should().Be(1);

        // t+11min：域 7 过期整域消失（锚 = last-write）；域 8 在 t+5min 又写过——锚已刷新，存活
        clock.Advance(TimeSpan.FromMinutes(5));
        await s.HSetAsync(8, TierHashTestFactory.Bytes("b2"), TierHashTestFactory.Bytes("3"), default);
        clock.Advance(TimeSpan.FromMinutes(5));
        await s.RunRetentionAsync(default);

        (await s.HLenAsync(7, default)).Should().Be(0, "超 TTL 域整域消失");
        s.DomainIds.Should().NotContain(7);
        (await s.HGetAsync(7, TierHashTestFactory.Bytes("a"), default)).Should().BeNull();
        (await s.HLenAsync(8, default)).Should().Be(2, "写入刷新过期锚——未过期");
        s.GetWatermarkParticipant().Should().NotBeNull();
    }

    // ══ 备份终结标记保留域：导出面拒绝 ══

    [Fact]
    public async Task Export_RejectsReservedEndMarkDomain()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);
        var act = async () => await TierHashBackup.ExportAsync(s, new MemoryStream(),
            new[] { TierHashBackup.EndOfRecordsDomainId }, default);
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*终结标记*");
    }

    private static ReadOnlyMemory<byte>[] Mems(params string[] fields)
        => fields.Select(f => (ReadOnlyMemory<byte>)TierHashTestFactory.Bytes(f)).ToArray();

    private static async Task<Dictionary<string, string>> CollectAll(TierHash s, uint domain = 0)
    {
        var dict = new Dictionary<string, string>();
        await foreach (var (f, v) in s.HGetAllAsync(domain, default))
            dict[TierHashTestFactory.Str(f)] = TierHashTestFactory.Str(v);
        return dict;
    }
}
