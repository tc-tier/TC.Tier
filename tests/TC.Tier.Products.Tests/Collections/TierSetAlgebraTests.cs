namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// 集合代数算子测试（tc-tier-collections-spec §3——Intersect/Union/Diff 跨域合并糖）。
/// </summary>
public sealed class TierSetAlgebraTests
{
    private static readonly string[] Shared12 = { "shared1", "shared2" };
    private static readonly string[] UnionAll = { "shared1", "shared2", "onlyA", "onlyB", "extra" };
    private static readonly string[] DiffOnlyA = { "onlyA" };

    [Fact]
    public async Task Intersect_Union_Diff_Compose()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);

        await s.SAddAsync(1, Mems("shared1", "shared2", "onlyA"), default);
        await s.SAddAsync(2, Mems("shared1", "shared2", "onlyB"), default);
        await s.SAddAsync(3, Mems("extra"), default);

        var inter = await TierSetAlgebra.IntersectAsync(s, 1, 2);
        inter.Select(BytesToString).Should().BeEquivalentTo(Shared12, "两域共有成员");

        var union = await TierSetAlgebra.UnionAsync(s, new uint[] { 1, 2, 3 });
        union.Select(BytesToString).Should().BeEquivalentTo(UnionAll, "三域并集去重");
        union.Should().HaveCount(5, "去重后不膨胀");

        var diff = await TierSetAlgebra.DiffAsync(s, 1, 2);
        diff.Select(BytesToString).Should().BeEquivalentTo(DiffOnlyA, "A − B");

        // 空域边界
        var emptyInter = await TierSetAlgebra.IntersectAsync(s, 1, 9);
        emptyInter.Should().BeEmpty("未注册域 = 空集");
        var emptyUnion = await TierSetAlgebra.UnionAsync(s, Array.Empty<uint>());
        emptyUnion.Should().BeEmpty("零域并集 = 空集");
        var emptyDiff = await TierSetAlgebra.DiffAsync(s, 9, 1);
        emptyDiff.Should().BeEmpty("空域差集 = 空集");
    }

    [Fact]
    public async Task Algebra_ResultLandsViaCallerSAdd()
    {
        // spec §3 第一版口径：结果集落目标域由调用方 SAdd 承接
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);

        await s.SAddAsync(1, Mems("a", "b", "c"), default);
        await s.SAddAsync(2, Mems("b", "c", "d"), default);

        var inter = await TierSetAlgebra.IntersectAsync(s, 1, 2);
        (await s.SAddAsync(9, inter.Select(m => (ReadOnlyMemory<byte>)m).ToArray(), default)).Should().Be(2);
        (await s.SCardAsync(9, default)).Should().Be(2);
        (await s.SIsMemberAsync(9, Mem("b"), default)).Should().BeTrue();
        (await s.SIsMemberAsync(9, Mem("a"), default)).Should().BeFalse();
    }

    private static string BytesToString(byte[] b) => System.Text.Encoding.UTF8.GetString(b);

    private static ReadOnlyMemory<byte> Mem(string member) => TierSetTestFactory.Bytes(member);

    private static ReadOnlyMemory<byte>[] Mems(params string[] members)
        => members.Select(m => (ReadOnlyMemory<byte>)TierSetTestFactory.Bytes(m)).ToArray();
}
