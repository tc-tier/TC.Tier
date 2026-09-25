using System.Text;

namespace TC.Tier.Products.Tests.Collections;

/// <summary>
/// TierHash 冷备份导出/导入测试（tc-tier-collections-spec §8——THB1 流格式；验收：导出→新实例导入
/// 全域一致 / 损坏流 fail-fast / 幂等重导入）。
/// </summary>
public sealed class TierHashBackupTests
{
    // ══ 往返：多域导出 → 新实例导入——全量一致 ══

    [Fact]
    public async Task Export_Import_RoundTrip_MultiDomain()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        for (int i = 0; i < 10; i++)
            await s.HSetAsync(TierHashTestFactory.Bytes($"f{i}"), TierHashTestFactory.Bytes($"v{i}"), default);
        await s.HSetAsync(3, TierHashTestFactory.Bytes("x"), TierHashTestFactory.Bytes("y"), default);
        await s.HSetAsync(3, TierHashTestFactory.Bytes("x2"), new byte[] { 0, 1, 2, 0xFF }, default);
        await s.FlushAsync(default);

        var stream = new MemoryStream();
        await TierHashBackup.ExportAsync(s, stream, default);

        // 导入到全新实例——全域一致
        using var vol2 = new TestVolume();
        await using var s2 = await TierHashTestFactory.StartAsync(vol2, o => o with { HashName = "tc.hash.restore" });
        stream.Position = 0;
        var result = await TierHashBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(12);
        result.DomainCount.Should().Be(2);

        (await s2.HLenAsync(default)).Should().Be(10);
        (await s2.HLenAsync(3, default)).Should().Be(2);
        for (int i = 0; i < 10; i++)
        {
            var v = await s2.HGetAsync(TierHashTestFactory.Bytes($"f{i}"), default);
            TierHashTestFactory.Str(v!.Value).Should().Be($"v{i}");
        }
        (await s2.HGetAsync(3, TierHashTestFactory.Bytes("x2"), default))!.Value.ToArray()
            .Should().Equal(new byte[] { 0, 1, 2, 0xFF }, "二进制值逐字节一致");

        // 导入后写/删继续正常
        (await s2.HSetAsync(3, TierHashTestFactory.Bytes("x"), TierHashTestFactory.Bytes("z"), default))
            .Should().BeFalse("导入后判重语义延续");
    }

    // ══ 幂等：同流重复导入——覆盖语义、计数不膨胀 ══

    [Fact]
    public async Task Reimport_IsIdempotent()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);
        await s.HSetAsync(TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("v"), default);

        var stream = new MemoryStream();
        await TierHashBackup.ExportAsync(s, stream, default);

        using var vol2 = new TestVolume();
        await using var s2 = await TierHashTestFactory.StartAsync(vol2, o => o with { HashName = "tc.hash.restore" });
        stream.Position = 0;
        await TierHashBackup.ImportAsync(s2, stream, default);
        stream.Position = 0;
        await TierHashBackup.ImportAsync(s2, stream, default);

        (await s2.HLenAsync(default)).Should().Be(1, "幂等重放——计数不膨胀");
        (await s2.HGetAsync(TierHashTestFactory.Bytes("f"), default))!.Value.ToArray()
            .Should().Equal(TierHashTestFactory.Bytes("v"));
    }

    // ══ 损坏流 fail-fast：magic / CRC / 记录数 ══

    [Fact]
    public async Task CorruptStream_FailsFast()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);
        await s.HSetAsync(TierHashTestFactory.Bytes("f"), TierHashTestFactory.Bytes("value"), default);

        var good = new MemoryStream();
        await TierHashBackup.ExportAsync(s, good, default);
        var bytes = good.ToArray();

        // magic 破坏
        var badMagic = (byte[])bytes.Clone();
        badMagic[0] ^= 0xFF;
        var act1 = () => ImportToNewAsync(new MemoryStream(badMagic));
        await act1.Should().ThrowAsync<InvalidDataException>().WithMessage("*magic*");

        // 记录体破坏（CRC 兜底）
        var badBody = (byte[])bytes.Clone();
        badBody[HeaderSize + 13] ^= 0xFF;
        var act2 = () => ImportToNewAsync(new MemoryStream(badBody));
        await act2.Should().ThrowAsync<InvalidDataException>().WithMessage("*CRC*");

        // 尾部截断（流提前终止——Footer 读取处精确读满失败）
        var act3 = () => ImportToNewAsync(new MemoryStream(bytes[..^5]));
        await act3.Should().ThrowAsync<EndOfStreamException>();
    }

    // ══ 空实例：合法空备份流（Header + 零记录 + Footer）══

    [Fact]
    public async Task Export_EmptyInstance_ValidEmptyStream()
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol);

        var stream = new MemoryStream();
        await TierHashBackup.ExportAsync(s, stream, default);
        stream.Length.Should().Be(12 + 12 + 12, "Header + EndMark + Footer");

        using var vol2 = new TestVolume();
        await using var s2 = await TierHashTestFactory.StartAsync(vol2, o => o with { HashName = "tc.hash.restore" });
        stream.Position = 0;
        var result = await TierHashBackup.ImportAsync(s2, stream, default);
        result.ImportedCount.Should().Be(0);
        s2.DomainCount.Should().Be(0);
    }

    private const int HeaderSize = 12;

    private static async Task ImportToNewAsync(Stream stream)
    {
        using var vol = new TestVolume();
        await using var s = await TierHashTestFactory.StartAsync(vol, o => o with { HashName = "tc.hash.restore" });
        await TierHashBackup.ImportAsync(s, stream, default);
    }
}
