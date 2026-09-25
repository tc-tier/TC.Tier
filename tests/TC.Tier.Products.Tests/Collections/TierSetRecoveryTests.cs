namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierSet 恢复对账测试（tc-tier-collections-spec §7 + §11 矩阵）。
/// mem 卷重启语义：Dispose 实例 → 同卷 Builder 重建。
/// </summary>
public sealed class TierSetRecoveryTests
{
    [Fact]
    public async Task Reopen_DataIntact_IndexRebuilt()
    {
        using var vol = new TestVolume();
        await using (var s = await TierSetTestFactory.StartAsync(vol))
        {
            await s.SAddAsync(Mems("a", "b"), default);
            await s.SAddAsync(9, Mems("c"), default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierSetTestFactory.StartAsync(vol);

        s2.DomainIds.Should().BeEquivalentTo(new uint[] { 0, 9 });
        (await s2.SCardAsync(default)).Should().Be(2);
        (await s2.SCardAsync(9, default)).Should().Be(1);
        (await s2.SIsMemberAsync(Mem("a"), default)).Should().BeTrue();

        // 恢复后写/删继续正常（判重语义延续）
        (await s2.SAddAsync(Mems("a"), default)).Should().Be(0, "恢复后判重延续——已有成员不重复计");
    }

    [Fact]
    public async Task Reopen_AfterRemoval_StaysDeleted()
    {
        using var vol = new TestVolume();
        await using (var s = await TierSetTestFactory.StartAsync(vol))
        {
            await s.SAddAsync(Mems("a", "b"), default);
            await s.SRemAsync(Mems("a"), default);
            await s.FlushAsync(default);
        }
        await using var s2 = await TierSetTestFactory.StartAsync(vol);

        (await s2.SIsMemberAsync(Mem("a"), default)).Should().BeFalse("墓碑重放——已删不复活");
        (await s2.SCardAsync(default)).Should().Be(1);
    }

    [Fact]
    public async Task Reopen_AfterTruncateDomain_StaysGone()
    {
        using var vol = new TestVolume();
        await using (var s = await TierSetTestFactory.StartAsync(vol))
        {
            await s.SAddAsync(1, Mems("a", "b"), default);
            await s.SAddAsync(2, Mems("c"), default);
            await s.TruncateDomainAsync(1, default);
        }
        await using var s2 = await TierSetTestFactory.StartAsync(vol);

        s2.DomainIds.Should().NotContain(1);
        (await s2.SCardAsync(1, default)).Should().Be(0);
        (await s2.SCardAsync(2, default)).Should().Be(1);
    }

    [Fact]
    public async Task Reopen_UnflushedWrites_ZeroLoss()
    {
        using var vol = new TestVolume();
        await using (var s = await TierSetTestFactory.StartAsync(vol))
        {
            var members = Enumerable.Range(0, 20).Select(i => (ReadOnlyMemory<byte>)TierSetTestFactory.Bytes($"m{i:00}")).ToArray();
            await s.SAddAsync(members, default);
        }
        await using var s2 = await TierSetTestFactory.StartAsync(vol);
        (await s2.SCardAsync(default)).Should().Be(20);
    }

    private static ReadOnlyMemory<byte> Mem(string member) => TierSetTestFactory.Bytes(member);

    private static ReadOnlyMemory<byte>[] Mems(params string[] members)
        => members.Select(m => (ReadOnlyMemory<byte>)TierSetTestFactory.Bytes(m)).ToArray();
}
