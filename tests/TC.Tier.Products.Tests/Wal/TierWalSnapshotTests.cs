namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 快照导出/导入（注入传输面）——快照（主数据镜像帧流）→ 导出（Header N₀ + 帧流 + Footer CRC）
/// → 导入（替换安装本地快照）→ SnapshotIndex 恢复 + raft 流式重建。未注入传输面 = 抛。
/// </summary>
public class TierWalSnapshotTests
{
    [Fact]
    public async Task Snapshot_Export_Import_RestoresSnapshotIndexAndEntries()
    {
        using var vol = new TestVolume();
        var transfer = new MemoryAsyncTransferPersistence();

        // 主节点：50 条 → 一体快照（N₀=50，head 截到 51）→ 导出经注入传输面
        await using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(transfer)))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 50).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);

            await wal.ExportSnapshotAsync(default);
            transfer.CommittedImage.Should().NotBeNull();
            wal.SnapshotIndex.Should().Be(50);
        }

        // ★ 冷节点：导入（经注入传输面——同卷新实例）→ SnapshotIndex 恢复 → 快照条目 = 镜像帧流
        var seeded = new MemoryAsyncTransferPersistence();
        seeded.Seed(transfer!.CommittedImage!.Value);   // 传输来的像
        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(seeded));
        await wal2.ImportSnapshotAsync(default);

        wal2.SnapshotIndex.Should().Be(50);
        var entries = CountSnapshotFrames(await ReadSnapshotAll(wal2));
        entries.Should().Be(50, "镜像帧流 = 快照 [Head..N₀] 全部条目");
        (await TierWalTests.ReadAll(wal2, 51, default)).Should().BeEmpty("冷节点主数据无增量（leader 后续推送）");
    }

    [Fact]
    public async Task Export_WithoutSnapshot_ExportsEmptyImage()
    {
        using var vol = new TestVolume();
        var transfer = new MemoryAsyncTransferPersistence();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(transfer));

        await wal.ExportSnapshotAsync(default);   // 无快照——空像（Header N₀=0 + 0 帧）
        transfer.CommittedImage.Should().NotBeNull();
        wal.SnapshotIndex.Should().Be(0);
    }

    [Fact]
    public async Task Export_WithoutPersistence_Throws()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        var act = () => wal.ExportSnapshotAsync(default).AsTask();
        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Contain("WithSnapshotPersistence", "未注入传输面——单机形态不导出");
    }

    [Fact]
    public async Task Import_CorruptFooter_Throws()
    {
        using var vol = new TestVolume();
        var transfer = new MemoryAsyncTransferPersistence();
        await using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(transfer)))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);
            await wal.ExportSnapshotAsync(default);
        }

        // 破坏像：翻转 footer CRC 字节（尾部 24B 的最后一个字节）
        var corrupt = transfer.CommittedImage!.Value.ToArray();
        corrupt[^1] ^= 0xFF;

        var corruptTransfer = new MemoryAsyncTransferPersistence();
        corruptTransfer.Seed(corrupt);   // 注入损坏像
        await using var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(corruptTransfer));
        var act = () => wal2.ImportSnapshotAsync(default).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Import_Corrupt_Aborts_OldSnapshotPreserved()
    {
        // ★ 会话模式（失败即清理）：损坏像导入 = 事务 Abort——新段清除，旧快照完好（可继续服务）
        using var vol = new TestVolume();
        var goodTransfer = new MemoryAsyncTransferPersistence();
        long oldN0;
        await using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(goodTransfer)))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 30).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            oldN0 = await wal.SnapshotAsync(default);
            await wal.ExportSnapshotAsync(default);
        }

        // 破坏像（footer CRC 翻转）→ 导入失败 → 旧快照保留
        var corrupt = goodTransfer.CommittedImage!.Value.ToArray();
        corrupt[^1] ^= 0xFF;
        var corruptTransfer = new MemoryAsyncTransferPersistence();
        corruptTransfer.Seed(corrupt);
        await using (var wal2 = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(corruptTransfer)))
        {
            var act = () => wal2.ImportSnapshotAsync(default).AsTask();
            await act.Should().ThrowAsync<InvalidOperationException>();

            wal2.SnapshotIndex.Should().Be(oldN0, "导入失败——旧快照覆盖点保留（Abort 回滚）");
            var image = await ReadSnapshotAll(wal2);
            TierWalSnapshotTests.CountSnapshotFrames(image).Should().Be(30, "旧快照内容完好");
        }
    }

    [Fact]
    public async Task Export_SnapshotContentMatchesMirrorRange()
    {
        using var vol = new TestVolume();
        var transfer = new MemoryAsyncTransferPersistence();
        await using (var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(transfer)))
        {
            await wal.AppendBatchAsync(Enumerable.Range(1, 30).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
            await wal.CommitAsync(default);
            await wal.SnapshotAsync(default);   // 镜像 = [1..30] 条目帧流
            await wal.ExportSnapshotAsync(default);
        }

        // 读回像：Header(N₀) + payload 帧 = 镜像帧流（每条一帧——内容与主数据一致）+ footer
        var reader = await OpenReader(transfer);
        var header = new byte[WalSnapshotFormat.HeaderSize];
        (await reader.ReadHeaderAsync(header, default)).Should().Be(WalSnapshotFormat.HeaderSize);
        WalSnapshotFormat.TryReadHeader(header, out var n0).Should().BeTrue();
        n0.Should().Be(30);

        long count = 0;
        var lenBuf = new byte[WalSnapshotFormat.FrameHeaderSize];
        while (true)
        {
            int got = await reader.ReadPayloadAsync(lenBuf, default);
            if (got < WalSnapshotFormat.FrameHeaderSize) break;
            int len = BitConverter.ToInt32(lenBuf);
            if (!WalSnapshotFormat.IsValidFrameLength(len)) break;
            var payload = new byte[len];
            (await reader.ReadPayloadAsync(payload, default)).Should().Be(len);
            payload.Should().Equal(WalTestFactory.Entry((int)count + 1), "快照镜像帧 = 主数据原始条目");
            count++;
        }
        count.Should().Be(30);
        await reader.DisposeAsync();
    }

    // ═══ 导出期间并发 Append（导出 = 读内部快照——与主数据无冲突，append 不受影响）═══

    [Fact]
    public async Task Export_ConcurrentAppend_Unaffected()
    {
        using var vol = new TestVolume();
        var gated = new GatedAsyncTransferPersistence();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit,
            builder: b => b.WithSnapshotPersistence(gated));
        await wal.AppendBatchAsync(Enumerable.Range(1, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);   // N₀ = 10
        await wal.SnapshotAsync(default);

        var exportTask = wal.ExportSnapshotAsync(default).AsTask();
        await gated.HeaderWritten.WaitAsync(TimeSpan.FromSeconds(10));

        // 导出挂起期间 append 继续（导出内容 = 已快照的 10 条；(N₀, 尾] 完整保留）
        await wal.AppendBatchAsync(Enumerable.Range(11, 10).Select(i => (ReadOnlyMemory<byte>)WalTestFactory.Entry(i)).ToList(), default);
        await wal.CommitAsync(default);
        wal.AllocatedIndex.Should().Be(20);

        gated.Release();
        await exportTask;

        // 增量重放：N₀+1 起完整
        var rest = await TierWalTests.ReadAll(wal, 11, default);
        rest.Should().HaveCount(10);
    }

    // ═══ helpers ═══

    internal static async Task<byte[]> ReadSnapshotAll(TierWal wal)
    {
        using var ms = new MemoryStream();
        await foreach (var chunk in wal.ReadSnapshotEntriesAsync(default))
            ms.Write(chunk.Span);
        return ms.ToArray();
    }

    /// <summary>解析快照帧流 → 帧数（每条 = [len 4B][payload] 一帧）。</summary>
    internal static int CountSnapshotFrames(byte[] frameStream)
    {
        int count = 0;
        int off = 0;
        while (off + 4 <= frameStream.Length)
        {
            int len = BitConverter.ToInt32(frameStream, off);
            if (len <= 0 || len > WalSnapshotFormat.MaxPayloadLength || off + 4 + len > frameStream.Length) break;
            off += 4 + len;
            count++;
        }
        return count;
    }

    private static async Task<IAsyncTransferReader> OpenReader(MemoryAsyncTransferPersistence p)
    {
        var ok = await p.TryOpenReadAsync(out var reader);
        Assert.True(ok);
        return reader!;
    }
}
