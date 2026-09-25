namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierZSet 冷备份导出/导入测试（tc-tier-collections-spec §8——TZB1 流格式；往返一致/损坏 fail-fast/幂等）。
/// </summary>
public sealed class TierZSetBackupTests
{
    // ══ 往返：多域导出 → 新实例导入——全量一致 ══

    [Fact]
    public async Task Export_Import_RoundTrip_MultiDomain()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);

        for (int i = 0; i < 8; i++)
            await s.ZAddAsync(i * 0.5 - 2, TierZSetTestFactory.Bytes($"m{i}"), default);
        await s.ZAddAsync(3, double.NegativeInfinity, TierZSetTestFactory.Bytes("ninf"), default);
        await s.ZAddAsync(3, double.PositiveInfinity, TierZSetTestFactory.Bytes("pinf"), default);
        await s.FlushAsync(default);

        var stream = new MemoryStream();
        await TierZSetBackup.ExportAsync(s, stream, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierZSetTestFactory.StartAsync(vol2, o => o with { ZSetName = "tc.zset.restore" });
        stream.Position = 0;
        var result = await TierZSetBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(10);
        result.DomainCount.Should().Be(2);

        (await s2.ZCardAsync(default)).Should().Be(8);
        (await s2.ZCardAsync(3, default)).Should().Be(2);
        (await s2.ZScoreAsync(TierZSetTestFactory.Bytes("m0"), default)).Should().Be(-2);
        (await s2.ZScoreAsync(TierZSetTestFactory.Bytes("m7"), default)).Should().Be(1.5);
        // ninf/pinf 在域 3——三参重载（此前两参误查域 0 的 null 是正确行为，非缺陷）
        (await s2.ZScoreAsync(3, TierZSetTestFactory.Bytes("ninf"), default)).Should().Be(double.NegativeInfinity);
        (await s2.ZPopMinAsync(3, default))!.Value.Member.Should().Equal(TierZSetTestFactory.Bytes("ninf"), "有序视图完好");
        var all = await CollectAsync(s2, 0, -1);
        all.Select(t => t.Score).Should().BeInAscendingOrder();
    }

    // ══ 幂等：同流重复导入 ══

    [Fact]
    public async Task Reimport_IsIdempotent()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        await s.ZAddAsync(1.25, TierZSetTestFactory.Bytes("m"), default);

        var stream = new MemoryStream();
        await TierZSetBackup.ExportAsync(s, stream, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierZSetTestFactory.StartAsync(vol2, o => o with { ZSetName = "tc.zset.restore" });
        stream.Position = 0;
        await TierZSetBackup.ImportAsync(s2, stream, default);
        stream.Position = 0;
        await TierZSetBackup.ImportAsync(s2, stream, default);

        (await s2.ZCardAsync(default)).Should().Be(1);
        (await s2.ZScoreAsync(TierZSetTestFactory.Bytes("m"), default)).Should().Be(1.25);
    }

    // ══ 损坏流 fail-fast ══

    [Fact]
    public async Task CorruptStream_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol);
        await s.ZAddAsync(3.5, TierZSetTestFactory.Bytes("member"), default);

        var good = new MemoryStream();
        await TierZSetBackup.ExportAsync(s, good, default);
        var bytes = good.ToArray();

        var badMagic = (byte[])bytes.Clone();
        badMagic[0] ^= 0xFF;
        var act1 = () => ImportToNewAsync(new MemoryStream(badMagic));
        await act1.Should().ThrowAsync<InvalidDataException>().WithMessage("*magic*");

        var badBody = (byte[])bytes.Clone();
        badBody[HeaderSize + 20] ^= 0xFF;
        var act2 = () => ImportToNewAsync(new MemoryStream(badBody));
        await act2.Should().ThrowAsync<InvalidDataException>().WithMessage("*CRC*");

        var act3 = () => ImportToNewAsync(new MemoryStream(bytes[..^4]));
        await act3.Should().ThrowAsync<EndOfStreamException>();
    }

    private const int HeaderSize = 12;

    private static async Task ImportToNewAsync(Stream stream)
    {
        using var vol = new TestVolume();
        await using var s = await TierZSetTestFactory.StartAsync(vol, o => o with { ZSetName = "tc.zset.restore" });
        await TierZSetBackup.ImportAsync(s, stream, default);
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
