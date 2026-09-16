using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;

namespace TC.Tier.Core.Net.Tests.Swarm;

/// <summary>
/// SwarmSync scenarios over the InProcess medium (spec-12 §6.1 verification — single-source
/// byte-exact, multi-source parallel utilization, block-level source switching self-heal,
/// corrupted-block interception, all-sources-exhausted failure).
/// </summary>
public class SwarmSyncTests
{
    /// <summary>内存块源（字典持有——坏块注入/丢块注入面）。</summary>
    private sealed class InMemoryBlockSource : ISwarmBlockSource
    {
        private readonly ConcurrentDictionary<Opaque16, Dictionary<long, byte[]>> _manifests = new();
        public readonly ConcurrentDictionary<Opaque16, HashSet<long>> CorruptedBlocks = new();   // 返回损坏数据（校验拦截面）
        public readonly ConcurrentDictionary<Opaque16, HashSet<long>> MissingBlocks = new();     // 假装无块（换源面）

        public void Add(Opaque16 id, SwarmManifest manifest, ReadOnlyMemory<byte> fullData)
        {
            var blocks = new Dictionary<long, byte[]>();
            for (var i = 0; i < manifest.BlockCount; i++)
            {
                var data = fullData.Slice((int)manifest.BlockOffset(i), (int)manifest.BlockLength(i)).ToArray();
                blocks[i] = data;
            }
            _manifests[id] = blocks;
        }

        public bool HasManifest(Opaque16 manifestId) => _manifests.ContainsKey(manifestId);

        public bool TryGetBlock(Opaque16 manifestId, long blockIndex, out ReadOnlyMemory<byte> block)
        {
            block = default;
            if (!_manifests.TryGetValue(manifestId, out var blocks)) return false;
            if (MissingBlocks.TryGetValue(manifestId, out var missing) && missing.Contains(blockIndex)) return false;
            if (!blocks.TryGetValue(blockIndex, out var data)) return false;
            if (CorruptedBlocks.TryGetValue(manifestId, out var corrupted) && corrupted.Contains(blockIndex))
            {
                var copy = (byte[])data.Clone();
                copy[0] ^= 0xFF;   // 损坏副本——校验和拦截面
                block = copy;
                return true;
            }
            block = data;
            return true;
        }
    }

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

    private sealed record SwarmFixture(InProcessTransportHub Hub, SwarmSync[] Syncs, InProcessNode[] Nodes)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var s in Syncs)
            {
                try { await s.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            foreach (var n in Nodes)
            {
                try { await n.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    /// <summary>n 节点装配（每节点 SwarmSync 挂载 0x03——holder 与下载方同构，内容经 SetSource 注入）。</summary>
    private static async Task<SwarmFixture> CreateAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var nodes = new InProcessNode[count];
        var syncs = new SwarmSync[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                nodes[i] = hub.Register(NodeId.NewRandom());
                nodes[i].Start();
                syncs[i] = new SwarmSync(nodes[i]);
                await syncs[i].StartAsync();
            }
            return new SwarmFixture(hub, syncs, nodes);
        }
        catch
        {
            foreach (var n in nodes.Where(x => x is not null)) await n.DisposeAsync();
            await hub.DisposeAsync();
            throw;
        }
    }

    /// <summary>下载全量（收集块流——按块号重建完整内容）。</summary>
    private static async Task<byte[]> DownloadAllAsync(SwarmSync downloader, SwarmManifest manifest,
        IReadOnlyList<NodeId> holders, Action<long, NodeId>? onBlock = null)
    {
        var blocks = new Dictionary<long, byte[]>();
        await foreach (var (index, block) in downloader.DownloadAsync(manifest, holders, onBlock))
            blocks[index] = block;
        blocks.Keys.Should().HaveCount(manifest.BlockCount, "全块下载");
        var result = new byte[manifest.TotalBytes];
        foreach (var (index, block) in blocks)
            block.CopyTo(result.AsSpan((int)manifest.BlockOffset(index)));
        return result;
    }

    /// <summary>单源（spec-12 §6.1 两步走单源基线）：1 holder 全量——逐字节一致。</summary>
    [Fact]
    public async Task SingleSource_ByteExact()
    {
        await using var fx = await CreateAsync(2);
        var data = Data(1000);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xAA));
        var source = new InMemoryBlockSource();
        source.Add(manifest.Id, manifest, data);
        fx.Syncs[0].SetSource(source);

        var result = await DownloadAllAsync(fx.Syncs[1], manifest, [fx.Nodes[0].Self]);
        result.Should().Equal(data, "逐字节一致（内容寻址基线——装配点幂等基础）");
    }

    /// <summary>多源并行（§6.1 并行度 K）：3 holder 全量正确 + 多源利用率 &gt; 1（结构性断言——
    /// 时间比较归 D5-T3 性能门）。</summary>
    [Fact]
    public async Task MultiSource_ParallelUtilization_ByteExact()
    {
        await using var fx = await CreateAsync(4);
        var data = Data(2000);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xBB));
        for (var i = 0; i < 3; i++)
        {
            var source = new InMemoryBlockSource();
            source.Add(manifest.Id, manifest, data);
            fx.Syncs[i].SetSource(source);
        }
        var sourcesUsed = new ConcurrentDictionary<NodeId, byte>();
        var holders = fx.Nodes.Take(3).Select(n => n.Self).ToList();

        var result = await DownloadAllAsync(fx.Syncs[3], manifest, holders,
            (_, from) => sourcesUsed.TryAdd(from, 0));
        result.Should().Equal(data);
        sourcesUsed.Count.Should().BeGreaterThan(1, "K 并行多源拉取——多个 holder 被利用（块级换源轮转）");
    }

    /// <summary>换源自愈（§6.1 块级换源）：源 A 丢块 2（轮转起点=源 A——缺块路径真实走过）→ 轮转至源 B 补上。</summary>
    [Fact]
    public async Task SourceSwitch_MissingBlock_HealsFromNextSource()
    {
        await using var fx = await CreateAsync(3);
        var data = Data(512);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xCC));
        var sourceA = new InMemoryBlockSource();
        sourceA.Add(manifest.Id, manifest, data);
        sourceA.MissingBlocks[manifest.Id] = [2];
        fx.Syncs[0].SetSource(sourceA);
        var sourceB = new InMemoryBlockSource();
        sourceB.Add(manifest.Id, manifest, data);
        fx.Syncs[1].SetSource(sourceB);

        var result = await DownloadAllAsync(fx.Syncs[2], manifest,
            [fx.Nodes[0].Self, fx.Nodes[1].Self]);
        result.Should().Equal(data, "缺块源被换过——块 2 从源 B 取回");
    }

    /// <summary>错块拦截（§6.1 校验和防错源）：源 A 块 4 返回损坏数据 → 拦截换源 + BadBlockCount 计数。
    /// <para>★ 块 4 的轮转起点 = holders[4 % 2 = 0] = 源 A——损坏块先被尝试（确定性轮转语义）。</para></summary>
    [Fact]
    public async Task CorruptedBlock_Intercepted_BadBlockCounted()
    {
        await using var fx = await CreateAsync(3);
        var data = Data(512);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xDD));
        var sourceA = new InMemoryBlockSource();
        sourceA.Add(manifest.Id, manifest, data);
        sourceA.CorruptedBlocks[manifest.Id] = [4];
        fx.Syncs[0].SetSource(sourceA);
        var sourceB = new InMemoryBlockSource();
        sourceB.Add(manifest.Id, manifest, data);
        fx.Syncs[1].SetSource(sourceB);

        var result = await DownloadAllAsync(fx.Syncs[2], manifest,
            [fx.Nodes[0].Self, fx.Nodes[1].Self]);
        result.Should().Equal(data, "错块被拦截——正确块从源 B 取回");
        fx.Syncs[2].BadBlockCount.Should().Be(1, "错块拦截计数（校验和失败被拒）");
    }

    /// <summary>源全失效（§6.1）：唯一 holder 丢块 → 整体失败（SwarmDownloadException 携失败块号）。</summary>
    [Fact]
    public async Task AllSourcesExhausted_FailsWithBlockSet()
    {
        await using var fx = await CreateAsync(2);
        var data = Data(256);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xEE));
        var source = new InMemoryBlockSource();
        source.Add(manifest.Id, manifest, data);
        source.MissingBlocks[manifest.Id] = [2];
        fx.Syncs[0].SetSource(source);

        var act = async () =>
        {
            await foreach (var _ in fx.Syncs[1].DownloadAsync(manifest, [fx.Nodes[0].Self]))
            {
            }
        };
        (await act.Should().ThrowAsync<SwarmDownloadException>()).Which.FailedBlocks.Should().Contain(2);
    }

    /// <summary>未持内容（§6.1 正常形态）：holder HasManifest=false → 下载方换源（无其他源=失败）。</summary>
    [Fact]
    public async Task HolderWithoutContent_AllSourcesExhausted_Fails()
    {
        await using var fx = await CreateAsync(2);
        var data = Data(64);
        var manifest = SwarmManifest.Build(data, blockSize: 32, id: Id(0xFF));
        // fx.Syncs[0] 未 SetSource——不持任何内容

        var act = async () =>
        {
            await foreach (var _ in fx.Syncs[1].DownloadAsync(manifest, [fx.Nodes[0].Self]))
            {
            }
        };
        await act.Should().ThrowAsync<SwarmDownloadException>();
    }

    /// <summary>ServeBlocks=false 全链（spec-12 §6.1 增量 S5——设计稿测试计划 #6）：纯消费者节点
    /// 被静态 holders 指名拉取——GetBlock 无应答（handler 未注册）→ 拉取方换源跳过，从正常
    /// holder 拿到全量（源分布不含纯消费者）。</summary>
    [Fact]
    public async Task ServeBlocksFalse_Node_SkippedBySourceSwitching()
    {
        await using var fx = await CreateAsync(3);
        var data = Data(1000);
        var manifest = SwarmManifest.Build(data, blockSize: 64, id: Id(0xBB));
        var blockSource = new InMemoryBlockSource();
        blockSource.Add(manifest.Id, manifest, data);
        fx.Syncs[0].SetSource(blockSource);
        var observedSources = new ConcurrentDictionary<NodeId, byte>();

        // holders = [纯消费者（未注册 handler——GetBlock 不应答）, 正常 holder]——换源跳过
        var holders = new[] { fx.Nodes[1].Self, fx.Nodes[0].Self };
        var rebuilt = await DownloadAllAsync(fx.Syncs[2], manifest, holders,
            onBlock: (_, from) => observedSources.TryAdd(from, 0));

        rebuilt.Should().Equal(data, "换源跳过无应答节点——正常 holder 供全量");
        observedSources.Keys.Should().NotContain(fx.Nodes[1].Self, "纯消费者不服务块");
    }
}
