namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierSet 冷备份导出/导入测试（tc-tier-collections-spec §8——TSE1 流格式；往返一致/损坏 fail-fast/幂等）。
/// </summary>
public sealed class TierSetBackupTests
{
    [Fact]
    public async Task Export_Import_RoundTrip_MultiDomain()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);

        await s.SAddAsync(Mems(Enumerable.Range(0, 10).Select(i => $"m{i}").ToArray()), default);
        await s.SAddAsync(3, Mems("x", "y"), default);
        await s.FlushAsync(default);

        var stream = new MemoryStream();
        await TierSetBackup.ExportAsync(s, stream, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierSetTestFactory.StartAsync(vol2, o => o with { SetName = "tc.set.restore" });
        stream.Position = 0;
        var result = await TierSetBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(12);
        result.DomainCount.Should().Be(2);

        (await s2.SCardAsync(default)).Should().Be(10);
        (await s2.SCardAsync(3, default)).Should().Be(2);
        (await s2.SIsMemberAsync(3, TierSetTestFactory.Bytes("x"), default)).Should().BeTrue();
        (await s2.SAddAsync(3, Mems("x"), default)).Should().Be(0, "导入后判重语义延续");
    }

    [Fact]
    public async Task Reimport_IsIdempotent()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);
        await s.SAddAsync(Mems("m"), default);

        var stream = new MemoryStream();
        await TierSetBackup.ExportAsync(s, stream, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierSetTestFactory.StartAsync(vol2, o => o with { SetName = "tc.set.restore" });
        stream.Position = 0;
        await TierSetBackup.ImportAsync(s2, stream, default);
        stream.Position = 0;
        await TierSetBackup.ImportAsync(s2, stream, default);

        (await s2.SCardAsync(default)).Should().Be(1, "幂等重放——计数不膨胀");
    }

    [Fact]
    public async Task CorruptStream_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol);
        await s.SAddAsync(Mems("member"), default);

        var good = new MemoryStream();
        await TierSetBackup.ExportAsync(s, good, default);
        var bytes = good.ToArray();

        var badMagic = (byte[])bytes.Clone();
        badMagic[0] ^= 0xFF;
        var act1 = () => ImportToNewAsync(new MemoryStream(badMagic));
        await act1.Should().ThrowAsync<InvalidDataException>().WithMessage("*magic*");

        var badBody = (byte[])bytes.Clone();
        badBody[HeaderSize + 10] ^= 0xFF;   // member 字节区（CRC 兜底）
        var act2 = () => ImportToNewAsync(new MemoryStream(badBody));
        await act2.Should().ThrowAsync<InvalidDataException>().WithMessage("*CRC*");

        var act3 = () => ImportToNewAsync(new MemoryStream(bytes[..^4]));
        await act3.Should().ThrowAsync<EndOfStreamException>();
    }

    private const int HeaderSize = 12;

    private static async Task ImportToNewAsync(Stream stream)
    {
        using var vol = new TestVolume();
        await using var s = await TierSetTestFactory.StartAsync(vol, o => o with { SetName = "tc.set.restore" });
        await TierSetBackup.ImportAsync(s, stream, default);
    }

    private static ReadOnlyMemory<byte>[] Mems(params string[] members)
        => members.Select(m => (ReadOnlyMemory<byte>)TierSetTestFactory.Bytes(m)).ToArray();

    private static ReadOnlyMemory<byte>[] Mems(IEnumerable<string> members)
        => members.Select(m => (ReadOnlyMemory<byte>)TierSetTestFactory.Bytes(m)).ToArray();
}
