using FluentAssertions;
using Xunit;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Blob;
using TC.Tier.Products.Net.Blob;
using TC.Tier.Runtime.Tests;

namespace TC.Tier.Products.Tests.Blob;

/// <summary>
/// Blob 段分发端到端（#436 件一/件二）：CP 侧 Put → holder 块源挂载 → DP 侧清单发现 + K=1
/// 顺序下载灌 Blob 写会话 → 字节精确 + 幂等直返 + 退化直读路径。
/// </summary>
public class BlobSwarmTests
{
    private sealed record Node2(InProcessTransportHub Hub, InProcessNode Node, SwarmSync Sync)
        : IAsyncDisposable
    {
        public NodeId Self => Node.Self;

        public async ValueTask DisposeAsync()
        {
            try { await Sync.DisposeAsync(); } catch { /* 尽力清理 */ }
            try { await Node.DisposeAsync(); } catch { /* 尽力清理 */ }
        }
    }

    private static async Task<Node2> StartNodeAsync(InProcessTransportHub hub)
    {
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var sync = new SwarmSync(node);
        await sync.StartAsync();
        return new Node2(hub, node, sync);
    }

    [Fact]
    public Task ManifestId_DeterministicAndRoundtrip()
    {
        var objectId = new LogicalAddress(3, 7, 0x1234);
        var id1 = BlobSwarmManifest.IdFor(objectId);
        var id2 = BlobSwarmManifest.IdFor(objectId);
        id1.Should().Be(id2, "不可变对象假设——同 ObjectId 确定性派生同 Id");
        BlobSwarmManifest.ObjectIdFor(id1).Should().Be(objectId, "逆映射原序还原");
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Import_MultiSource_ByteExact_AndRegistersHolder()
    {
        var hub = new InProcessTransportHub();
        await using var cpNode = await StartNodeAsync(hub);
        await using var dpNode = await StartNodeAsync(hub);
        using var cpVol = new TestVolume();
        using var dpVol = new TestVolume();
        await using var cpBlob = await TierBlobTestFactory.StartAsync(cpVol);
        await using var dpBlob = await TierBlobTestFactory.StartAsync(dpVol);

        // CP 发布 artifact
        var content = new byte[200 * 1024 + 17];   // 跨多块 + 非整块尾
        new Random(7).NextBytes(content);
        var put = await cpBlob.PutAsync(content);
        var objectId = put.ObjectId;

        // CP 侧挂分发面
        var cpSource = new BlobSwarmBlockSource(cpBlob);
        await cpSource.AttachAsync(objectId);
        cpNode.Sync.AttachSource(cpSource);

        // DP 侧导入
        var dpSource = new BlobSwarmBlockSource(dpBlob);
        var importer = new BlobSwarmImporter(dpBlob, dpNode.Sync, dpSource);
        var result = await importer.ImportAsync(objectId, content.Length, [cpNode.Self]);

        result.Skipped.Should().BeFalse();
        result.BytesDownloaded.Should().Be(content.Length);

        // 字节精确
        var roundtrip = new byte[content.Length];
        await dpBlob.GetAsync(objectId, roundtrip);
        roundtrip.Should().Equal(content, "K=1 顺序下载灌写会话——字节精确");

        // DP 侧导入后自动挂分发面（多源从字面成立）——第三方可从 DP 拉取
        dpSource.HasManifest(result.ManifestId).Should().BeTrue();
        dpSource.TryGetManifest(result.ManifestId, out var dpManifest).Should().BeTrue();
        dpManifest.TotalBytes.Should().Be(content.Length);
    }

    [Fact]
    public async Task Import_Idempotent_SkipsWhenAlreadyHeld()
    {
        var hub = new InProcessTransportHub();
        await using var cpNode = await StartNodeAsync(hub);
        await using var dpNode = await StartNodeAsync(hub);
        using var cpVol = new TestVolume();
        using var dpVol = new TestVolume();
        await using var cpBlob = await TierBlobTestFactory.StartAsync(cpVol);
        await using var dpBlob = await TierBlobTestFactory.StartAsync(dpVol);

        var content = TierBlobTestFactory.MakeData(64 * 1024, 0x55);
        var put = await cpBlob.PutAsync(content);
        var objectId = put.ObjectId;

        var cpSource = new BlobSwarmBlockSource(cpBlob);
        await cpSource.AttachAsync(objectId);
        cpNode.Sync.AttachSource(cpSource);

        var importer = new BlobSwarmImporter(dpBlob, dpNode.Sync);
        (await importer.ImportAsync(objectId, content.Length, [cpNode.Self])).Skipped.Should().BeFalse();

        // 二次导入：本地已持有同 Id 同长度 → 零下载直返
        var again = await importer.ImportAsync(objectId, content.Length, [cpNode.Self]);
        again.Skipped.Should().BeTrue("幂等前置：本地已持有 = 零动作");
        again.BytesDownloaded.Should().Be(0);
    }

    [Fact]
    public async Task Import_DegradedRead_CpDirectPath()
    {
        var hub = new InProcessTransportHub();
        await using var cpNode = await StartNodeAsync(hub);
        await using var dpNode = await StartNodeAsync(hub);
        using var cpVol = new TestVolume();
        using var dpVol = new TestVolume();
        await using var cpBlob = await TierBlobTestFactory.StartAsync(cpVol);
        await using var dpBlob = await TierBlobTestFactory.StartAsync(dpVol);

        // CP 发布但 NO Swarm 分发面（未 Attach——种子应答为空）
        var content = TierBlobTestFactory.MakeData(32 * 1024, 0x77);
        var put = await cpBlob.PutAsync(content);
        var objectId = put.ObjectId;

        var importer = new BlobSwarmImporter(dpBlob, dpNode.Sync);
        importer.AttachDegradedRead(async (oid, expected, ct) =>
        {
            var data = new byte[expected];
            var n = await cpBlob.GetAsync(oid, data, ct);
            return data[..n];   // 直连 CP 退化路径——Blob 读取代理
        });

        var result = await importer.ImportAsync(objectId, content.Length, [cpNode.Self]);
        result.Skipped.Should().BeFalse("退化路径同样完成导入");
        var roundtrip = new byte[content.Length];
        await dpBlob.GetAsync(objectId, roundtrip);
        roundtrip.Should().Equal(content, "退化直读字节精确");
    }

    [Fact]
    public async Task Import_SourceExhausted_NoDegraded_Throws()
    {
        var hub = new InProcessTransportHub();
        await using var dpNode = await StartNodeAsync(hub);
        using var dpVol = new TestVolume();
        await using var dpBlob = await TierBlobTestFactory.StartAsync(dpVol);

        var importer = new BlobSwarmImporter(dpBlob, dpNode.Sync);
        await Assert.ThrowsAsync<NetIOException>(async () =>
            await importer.ImportAsync(new LogicalAddress(9, 0, 100), 4096, [dpNode.Self]));
    }
}
