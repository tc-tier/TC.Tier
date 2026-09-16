using System.Text;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// 尾部 (index→addr) 环表契约测试——热读直达（skip=0）与生命周期不变式：
/// 命中正确性 / 回绕陈旧槽自校验 / 截尾后重 append 覆写 / 恢复期填充 / 导入重锚清表。
/// </summary>
public class TierWalTailMapTests
{
    private static byte[] Payload(string tag, long i) => Encoding.UTF8.GetBytes($"{tag}-{i}");

    private static async Task<List<(long Index, string Data)>> ReadAllAsync(TierWal wal, long from, int max = int.MaxValue)
    {
        var list = new List<(long, string)>();
        await foreach (var e in wal.ReadFromAsync(from, default))
        {
            list.Add((e.Index, Encoding.UTF8.GetString(e.Data.Span)));
            if (list.Count >= max) break;
        }
        return list;
    }

    [Fact]
    public async Task TailMap_Hit_ReturnsExactEntries_AcrossWindow()
    {
        // 3000 条（跨环表容量 2048 回绕 + 多锚点间隔）——尾部窗口内各点命中环表直达
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        for (var i = 1; i <= 3000; i++)
            await wal.AppendSingleAsync(Payload("e", i), default);
        await wal.CommitAsync(default);

        foreach (var idx in new[] { 3000L, 2999L, 2049L, 953L, 1L })
        {
            var entries = await ReadAllAsync(wal, idx, 1);
            entries.Should().HaveCount(1);
            entries[0].Index.Should().Be(idx);
            entries[0].Data.Should().Be($"e-{idx}", $"index={idx} 的数据必须精确（环表/锚点任一路径）");
        }
    }

    [Fact]
    public async Task TailMap_Wrap_StaleSlotFallsToAnchors()
    {
        // index 1 与 2049 同槽（容量 2048 回绕）——槽被 2049 占据后读 1 必须走锚点回退且数据正确
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        for (var i = 1; i <= 2049; i++)
            await wal.AppendSingleAsync(Payload("w", i), default);
        await wal.CommitAsync(default);

        (await ReadAllAsync(wal, 1, 1))[0].Data.Should().Be("w-1");
        (await ReadAllAsync(wal, 2049, 1))[0].Data.Should().Be("w-2049");
        // 全量一致（环表与锚点两路径交错无错位）
        var all = await ReadAllAsync(wal, 1);
        all.Should().HaveCount(2049);
        for (var i = 0; i < 2049; i++)
            all[i].Should().Be((i + 1, $"w-{i + 1}"));
    }

    [Fact]
    public async Task TailMap_TruncateThenReAppend_SlotOverwritten()
    {
        // 截尾到 2500 后重 append 同 index 区间——环表槽必须被新地址覆写（读到新数据非旧残留）
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        for (var i = 1; i <= 2600; i++)
            await wal.AppendSingleAsync(Payload("old", i), default);
        await wal.CommitAsync(default);
        await wal.TruncateSuffixAsync(2500, default);

        for (var i = 2501; i <= 2550; i++)
            await wal.AppendSingleAsync(Payload("new", i), default);
        await wal.CommitAsync(default);

        (await ReadAllAsync(wal, 2500, 1))[0].Data.Should().Be("old-2500");
        for (var idx = 2501; idx <= 2550; idx++)
        {
            var e = (await ReadAllAsync(wal, idx, 1))[0];
            e.Index.Should().Be(idx);
            e.Data.Should().Be($"new-{idx}", "截尾后重 append 的 index——环表槽须覆写为新地址");
        }
        wal.AllocatedIndex.Should().Be(2550);
    }

    [Fact]
    public async Task TailMap_RecoveryScan_PopulatesWindow()
    {
        // 重启恢复扫描填充环表——重启后未 append 即读尾窗口直达且正确
        using var vol = new TestVolume();
        {
            await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
            for (var i = 1; i <= 3000; i++)
                await wal.AppendSingleAsync(Payload("r", i), default);
            await wal.CommitAsync(default);
        }
        {
            await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
            foreach (var idx in new[] { 3000L, 2500L, 953L })
            {
                var e = (await ReadAllAsync(wal, idx, 1))[0];
                e.Index.Should().Be(idx);
                e.Data.Should().Be($"r-{idx}", "恢复扫描填充的环表条目必须与重放一致");
            }
        }
    }

    [Fact]
    public async Task TailMap_BatchAppend_AllIndexesMapped()
    {
        // 批追加路径（BeginAppendBatch 循环内逐条 RecordAppended）——批内全部 index 入表
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);
        var batch = new ReadOnlyMemory<byte>[500];
        for (var i = 0; i < 500; i++) batch[i] = Payload("b", i + 1);
        var r = await wal.AppendBatchAsync(batch, default);
        await wal.CommitAsync(default);
        r.StartIndex.Should().Be(1);

        var all = await ReadAllAsync(wal, 1);
        all.Should().HaveCount(500);
        for (var i = 0; i < 500; i++)
            all[i].Should().Be((i + 1, $"b-{i + 1}"));
    }
}
