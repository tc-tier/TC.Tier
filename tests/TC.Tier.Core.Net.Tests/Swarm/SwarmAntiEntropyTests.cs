using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;

namespace TC.Tier.Core.Net.Tests.Swarm;

/// <summary>
/// Swarm 反熵对账测试（spec-12 §6.1 增量——复制家族设计件 C：漂移检出修复/一致零传输/
/// 比对流量有界/Swarm-only 双向收敛）。
/// </summary>
public class SwarmAntiEntropyTests
{
    private static Opaque16 Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new Opaque16(bytes);
    }

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++) data[i] = (byte)(i * 13);
        return data;
    }

    /// <summary>可变块源（测试夹具——对账/修复双能力 + 漂移注入面）。</summary>
    private sealed class MutableSource : ISwarmBlockSource
    {
        private readonly SwarmManifest _manifest;
        private readonly Dictionary<long, byte[]> _blocks;

        public MutableSource(SwarmManifest manifest, byte[] data)
        {
            _manifest = manifest;
            _blocks = [];
            for (var i = 0; i < manifest.BlockCount; i++)
                _blocks[i] = data.AsSpan((int)manifest.BlockOffset(i), (int)manifest.BlockLength(i)).ToArray();
        }

        /// <summary>本端校验和（断言面——全局根对照）。</summary>
        public uint[] ChecksumsFor(Opaque16 manifestId)
        {
            TryGetChecksums(manifestId, out var checksums).Should().BeTrue();
            return checksums;
        }

        public bool HasManifest(Opaque16 manifestId) => manifestId == _manifest.Id;

        public bool TryGetBlock(Opaque16 manifestId, long blockIndex, out ReadOnlyMemory<byte> block)
        {
            block = default;
            if (manifestId != _manifest.Id || !_blocks.TryGetValue(blockIndex, out var dataBytes)) return false;
            block = dataBytes;
            return true;
        }

        public bool TryGetChecksums(Opaque16 manifestId, out uint[] checksums)
        {
            // 实际内容逐块实算（漂移可观测的根基——回放清单记录值会把漂移隐藏成"一致"）
            checksums = [];
            if (manifestId != _manifest.Id) return false;
            checksums = new uint[_manifest.BlockCount];
            for (var i = 0; i < _manifest.BlockCount; i++)
                checksums[i] = TC.Tier.Core.Primitives.UnifiedCrc.ComputeCrc32C(_blocks[i]);
            return true;
        }

        public bool TryWriteBlock(Opaque16 manifestId, long blockIndex, ReadOnlyMemory<byte> block)
        {
            if (manifestId != _manifest.Id || !_blocks.ContainsKey(blockIndex)) return false;
            if (!_manifest.VerifyBlock(blockIndex, block.Span)) return false;
            _blocks[blockIndex] = block.ToArray();
            return true;
        }

        /// <summary>漂移注入（直接改副本某块字节——设计稿测试 1 注入面）。</summary>
        public void Corrupt(long blockIndex) => _blocks[blockIndex][0] ^= 0xFF;
    }

    private sealed record Rig(InProcessNode Node, SwarmSync Sync, MutableSource Source, SwarmAntiEntropy AntiEntropy)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Sync.DisposeAsync();
            await Node.DisposeAsync();
        }
    }

    private static async Task<Rig> CreateRigAsync(InProcessTransportHub hub, SwarmManifest manifest, byte[] data)
    {
        var node = hub.Register(NodeId.NewRandom());
        node.Start();
        var sync = new SwarmSync(node);
        await sync.StartAsync();
        var source = new MutableSource(manifest, data);
        sync.SetSource(source);
        return new Rig(node, sync, source, new SwarmAntiEntropy(sync));
    }

    /// <summary>漂移注入 → 反熵检出并修复 → 全局根一致（设计稿 §4.3 测试 1）。</summary>
    [Fact]
    public async Task RunOnce_DriftInjected_DetectedAndRepaired()
    {
        await using var hub = new InProcessTransportHub();
        var data = Data(1000);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xCC));
        await using var peer = await CreateRigAsync(hub, manifest, data);
        await using var local = await CreateRigAsync(hub, manifest, data);
        local.Source.Corrupt(3);

        var repaired = await local.AntiEntropy.RunOnceAsync(local.Source, peer.Node.Self, manifest);

        repaired.Should().Equal([3L], "漂移块被检出并修复（只传差异块——1 块拉取）");
        SwarmMerkle.ComputeGlobalRoot(local.Source.ChecksumsFor(manifest.Id))
            .Should().Be(SwarmMerkle.ComputeGlobalRoot(peer.Source.ChecksumsFor(manifest.Id)), "修复后全局根一致");
    }

    /// <summary>完全一致副本 → 对账零块传输（设计稿 §4.3 测试 2——全局根相等即短路）。</summary>
    [Fact]
    public async Task RunOnce_IdenticalContent_ZeroBlockTransfer()
    {
        await using var hub = new InProcessTransportHub();
        var data = Data(1000);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xDD));
        await using var peer = await CreateRigAsync(hub, manifest, data);
        await using var local = await CreateRigAsync(hub, manifest, data);

        var repaired = await local.AntiEntropy.RunOnceAsync(local.Source, peer.Node.Self, manifest);

        repaired.Should().BeEmpty("已一致——全局根相等零块传输");
    }

    /// <summary>大 manifest 分层比对（设计稿 §4.3 测试 3）：64 子树 × 64 块（4 MiB）漂移 1 块——
    /// 只传差异块（块传输 = 1），比对请求 = 1 根 + 子树数（≤ 块数/fanout + 1，流量 O(块数)×4B）。</summary>
    [Fact]
    public async Task RunOnce_LargeManifest_BoundedProbeTraffic()
    {
        await using var hub = new InProcessTransportHub();
        var data = Data(1024 * SwarmMerkle.SubtreeFanout * SwarmMerkle.SubtreeFanout);   // 4 MiB
        var manifest = SwarmManifest.Build(data, blockSize: 1024, id: Id(0xEE));
        await using var peer = await CreateRigAsync(hub, manifest, data);
        await using var local = await CreateRigAsync(hub, manifest, data);
        var driftIndex = SwarmMerkle.SubtreeFanout * (SwarmMerkle.SubtreeFanout - 1) + 5;   // 末子树内
        local.Source.Corrupt(driftIndex);

        var repaired = await local.AntiEntropy.RunOnceAsync(local.Source, peer.Node.Self, manifest);

        repaired.Should().Equal([driftIndex], "只传差异块——单块拉取修复");
        SwarmMerkle.ComputeSubtreeRoots(manifest.Checksums).Length.Should().Be(SwarmMerkle.SubtreeFanout,
            "比对分层 = 根 + 子树区间批——轮次有界于 O(log₆₄ blocks)");
    }

    /// <summary>Swarm-only 档（设计稿 §4.3 测试 4）：两节点互为对账发起方——双向收敛；
    /// 且对端坏块经校验拦截不覆盖本端好数据。</summary>
    [Fact]
    public async Task RunOnce_SwarmOnly_BidirectionalConvergence()
    {
        await using var hub = new InProcessTransportHub();
        var data = Data(1000);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xFF));
        await using var nodeA = await CreateRigAsync(hub, manifest, data);
        await using var nodeB = await CreateRigAsync(hub, manifest, data);
        nodeB.Source.Corrupt(7);   // B 漂移

        // A 发起（local=A 无漂移）：分歧检出 → 从 B 拉 7 → B 的坏块被校验拦截（单源耗尽）
        // → 本轮放弃且 A 的好数据未被覆盖
        var repairedA = await nodeA.AntiEntropy.RunOnceAsync(nodeA.Source, nodeB.Node.Self, manifest);
        repairedA.Should().BeEmpty("对端坏块校验拦截——不覆盖本端好数据");

        // B 发起（local=B 漂移方）：从 A 拉好块修复
        var repairedB = await nodeB.AntiEntropy.RunOnceAsync(nodeB.Source, nodeA.Node.Self, manifest);
        repairedB.Should().Equal([7L], "漂移方发起对账——检出并修复");

        SwarmMerkle.ComputeGlobalRoot(nodeA.Source.ChecksumsFor(manifest.Id))
            .Should().Be(SwarmMerkle.ComputeGlobalRoot(nodeB.Source.ChecksumsFor(manifest.Id)), "双向收敛后根一致");
    }
}
