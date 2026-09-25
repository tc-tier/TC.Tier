using TC.Tier.Core.Testing;

namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierSet 契约矩阵测试（tc-tier-collections-spec §11——SADD 幂等判重/SREM/SCARD/SMEMBERS
/// 逐动词对齐 + 域隔离/容量护栏/TTL 整域过期）。
/// </summary>
public sealed class TierSetTests
{
    private static readonly string[] LiveAfterRemove = { "f1", "f3" };

    // ══ SADD：新增数（已存在不计——Redis SADD 返回值语义）+ 幂等 ══

    [Fact]
    public async Task SAdd_IdempotentCounting()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);

        (await s.SAddAsync(Mems("a", "b", "c"), default)).Should().Be(3);
        (await s.SAddAsync(Mems("a", "d"), default)).Should().Be(1, "仅新增成员计入");
        (await s.SAddAsync(Mems("a"), default)).Should().Be(0, "重复添加幂等");
        (await s.SCardAsync(default)).Should().Be(4);
    }

    // ══ SREM / SISMEMBER / SCARD ══

    [Fact]
    public async Task SRem_RemovesOnlyExisting()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);
        await s.SAddAsync(Mems("a", "b", "c"), default);

        (await s.SRemAsync(Mems("a", "x"), default)).Should().Be(1);
        (await s.SIsMemberAsync(Mem("a"), default)).Should().BeFalse();
        (await s.SIsMemberAsync(Mem("b"), default)).Should().BeTrue();
        (await s.SCardAsync(default)).Should().Be(2);
        (await s.SRemAsync(Mems("a"), default)).Should().Be(0, "重复删除幂等");
    }

    // ══ SMEMBERS：全域枚举——只交付存活成员（重复添加/删除收敛）══

    [Fact]
    public async Task SMembers_YieldsOnlyLiveMembers()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);
        await s.SAddAsync(Mems("f1", "f2", "f3"), default);
        await s.SRemAsync(Mems("f2"), default);

        var all = await CollectAsync(s);
        all.Should().BeEquivalentTo(LiveAfterRemove, "删除后仅存活成员交付");
    }

    // ══ 域隔离：TruncateDomain 不波及他域（TruncateRange 判例回归面）══

    [Fact]
    public async Task DomainIsolation_TruncateOne_LeavesOthersIntact()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);
        foreach (var d in new uint[] { 1, 2, 3 })
        {
            await s.SAddAsync(d, Mems("shared", $"only{d}"), default);
        }

        (await s.TruncateDomainAsync(2, default)).Should().Be(2);
        (await s.SCardAsync(2, default)).Should().Be(0);
        s.DomainIds.Should().BeEquivalentTo(new uint[] { 1, 3 });
        (await s.SIsMemberAsync(3, Mem("shared"), default)).Should().BeTrue("域 3 不受波及");
        (await s.SCardAsync(3, default)).Should().Be(2);

        // 回收后同域重写合法
        (await s.SAddAsync(2, Mems("fresh"), default)).Should().Be(1);
    }

    // ══ 双 API：两参 = 默认域 0 ══

    [Fact]
    public async Task TwoParamApi_LandsDefaultDomain()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);
        await s.SAddAsync(Mems("f"), default);
        s.DomainIds.Should().Equal(new uint[] { 0 });
        (await s.SIsMemberAsync(Mem("f"), default)).Should().BeTrue();
        (await s.SIsMemberAsync(1, Mem("f"), default)).Should().BeFalse("跨域互不可见");
    }

    // ══ 容量护栏 + 字节上限 ══

    [Fact]
    public async Task Guards_FailFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol, o => o with
        {
            DomainCapacity = 2,
            DomainMaxBytes = 40,
        });

        await s.SAddAsync(0, Mems("f1"), default);
        await s.SAddAsync(1, Mems("f1"), default);
        var capacity = async () => await s.SAddAsync(2, Mems("x"), default);
        await capacity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainCapacity*");

        var bytes = async () => await s.SAddAsync(0, Mems("xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx"), default);
        await bytes.Should().ThrowAsync<InvalidOperationException>().WithMessage("*DomainMaxBytes*");
        (await s.SCardAsync(default)).Should().Be(1, "被拒写入不产生副作用");
    }

    // ══ 统计快照 + TTL 整域过期（FakeTimeProvider 确定性）══

    [Fact]
    public async Task Stats_And_TtlExpiry()
    {
        var clock = new FakeTimeProvider();
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol, o => o with
        {
            Clock = clock,
            DomainTtl = TimeSpan.FromMinutes(10),
        });

        await s.SAddAsync(7, Mems("a", "b"), default);
        await s.SAddAsync(8, Mems("c"), default);

        // t+5min：域 8 再写一次（刷新 TTL 锚）
        clock.Advance(TimeSpan.FromMinutes(5));
        await s.SAddAsync(8, Mems("c2"), default);

        var stats = await s.GetStatsAsync(7, default);
        stats.MemberCount.Should().Be(2);
        stats.BytesOccupied.Should().Be(2 * 14, "envelope 逻辑字节口径（头 13 + member 1）×2");
        stats.TtlBoundaryTimestamp.Should().Be(stats.LastWriteTimestamp!.Value + TimeSpan.FromMinutes(10).Ticks,
            "边界 = last-write + DomainTtl（时钟已推进——按域 7 自身锚计算）");

        clock.Advance(TimeSpan.FromMinutes(6));
        await s.RunRetentionAsync(default);
        (await s.SCardAsync(7, default)).Should().Be(0, "超 TTL 域整域消失");
        s.DomainIds.Should().NotContain(7);
        (await s.SCardAsync(8, default)).Should().Be(2, "未过期域完好（c + 刷新锚写入的 c2）");
        s.GetWatermarkParticipant().Should().NotBeNull();
    }

    private static ReadOnlyMemory<byte> Mem(string member) => TierSetTestFactory.Bytes(member);

    private static ReadOnlyMemory<byte>[] Mems(params string[] members)
        => members.Select(m => (ReadOnlyMemory<byte>)TierSetTestFactory.Bytes(m)).ToArray();

    private static async Task<List<string>> CollectAsync(TierSet s, uint domain = 0)
    {
        var list = new List<string>();
        await foreach (var m in s.SMembersAsync(domain, default))
            list.Add(System.Text.Encoding.UTF8.GetString(m));
        return list;
    }
}
