using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Core.Net.Transport.InProcess;

namespace TC.Tier.Core.Net.Tests.Swarm;

/// <summary>
/// SwarmSync 域化持有表与多源路由测试（#436 件四）：raft/Blob 域隔离（换届清表不误伤）、
/// 附加源注册路由、清单发现（0x06/0x07）应答与客户端、Content 域持有上报。
/// </summary>
public class SwarmSyncDomainTests
{
    /// <summary>内存清单源（块 + 全量清单——ManifestReq 应答面）。</summary>
    private sealed class InMemoryManifestSource : ISwarmBlockSource, ISwarmManifestSource
    {
        private readonly ConcurrentDictionary<Opaque16, SwarmManifest> _manifests = new();
        private readonly ConcurrentDictionary<Opaque16, byte[]> _contents = new();

        public void Add(SwarmManifest manifest, ReadOnlyMemory<byte> fullData)
        {
            _manifests[manifest.Id] = manifest;
            _contents[manifest.Id] = fullData.ToArray();
        }

        public bool HasManifest(Opaque16 manifestId) => _manifests.ContainsKey(manifestId);

        public bool TryGetBlock(Opaque16 manifestId, long blockIndex, out ReadOnlyMemory<byte> block)
        {
            block = default;
            if (!_manifests.TryGetValue(manifestId, out var manifest)) return false;
            if (!_contents.TryGetValue(manifestId, out var content)) return false;
            if ((ulong)blockIndex >= (ulong)manifest.BlockCount) return false;
            block = content.AsMemory((int)manifest.BlockOffset(blockIndex), (int)manifest.BlockLength(blockIndex));
            return true;
        }

        public bool TryGetChecksums(Opaque16 manifestId, out ReadOnlyMemory<uint> checksums)
        {
            checksums = default;
            if (!_manifests.TryGetValue(manifestId, out var manifest)) return false;
            checksums = manifest.Checksums;
            return true;
        }

        public bool TryGetManifest(Opaque16 manifestId, out SwarmManifest manifest)
            => _manifests.TryGetValue(manifestId, out manifest!);
    }

    private sealed record Fixture(InProcessTransportHub Hub, SwarmSync[] Syncs, InProcessNode[] Nodes)
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

    private static async Task<Fixture> CreateAsync(int count)
    {
        var hub = new InProcessTransportHub();
        var nodes = new InProcessNode[count];
        var syncs = new SwarmSync[count];
        for (var i = 0; i < count; i++)
        {
            nodes[i] = hub.Register(NodeId.NewRandom());
            nodes[i].Start();
            syncs[i] = new SwarmSync(nodes[i]);
            await syncs[i].StartAsync();
        }

        return new Fixture(hub, syncs, nodes);
    }

    private static Opaque16 Id(byte fill)
    {
        Span<byte> bytes = stackalloc byte[16];
        bytes.Fill(fill);
        return new Opaque16(bytes);
    }

    [Fact]
    public async Task OnLeaderLost_ClearsRaftDomain_KeepsContentDomain()
    {
        await using var fixture = await CreateAsync(1);
        var sync = fixture.Syncs[0];
        var raftId = Id(0x01);
        var contentId = Id(0x02);

        sync.RegisterSource(SwarmContentDomain.Raft, raftId, fixture.Nodes[0].Self);
        sync.RegisterSource(SwarmContentDomain.Content, contentId, fixture.Nodes[0].Self);

        sync.OnLeaderLost();

        // raft 域清空后回落 [本端] 兜底（v1 行为——无上报退化为单源）
        sync.GetHolders(SwarmContentDomain.Raft, raftId).Should().ContainSingle()
            .And.Contain(fixture.Nodes[0].Self);
        // Content 域不受换届影响
        sync.GetHolders(SwarmContentDomain.Content, contentId).Should().Contain(fixture.Nodes[0].Self);
    }

    [Fact]
    public async Task DomainTables_AreIsolated()
    {
        await using var fixture = await CreateAsync(1);
        var sync = fixture.Syncs[0];
        var sameId = Id(0x07);   // 同 Id 不同域——互不串表

        sync.RegisterSource(SwarmContentDomain.Raft, sameId, fixture.Nodes[0].Self);
        sync.GetHolders(SwarmContentDomain.Content, sameId).Should().ContainSingle();

        sync.ResetHolders(SwarmContentDomain.Content, sameId);
        sync.GetHolders(SwarmContentDomain.Raft, sameId).Should().Contain(fixture.Nodes[0].Self);
    }

    [Fact]
    public async Task AttachSource_RoutesGetBlock()
    {
        await using var fixture = await CreateAsync(2);
        var holder = fixture.Syncs[0];
        var downloader = fixture.Syncs[1];

        var content = new byte[3 * 64 * 1024];
        Array.Fill(content, (byte)0xAB);
        var manifest = SwarmManifest.Build(content, 64 * 1024, Id(0x11));

        var source = new InMemoryManifestSource();
        source.Add(manifest, content);
        holder.AttachSource(source);   // 附加源路由（不经 SetSource 单槽）

        var blocks = new System.Collections.Generic.Dictionary<long, byte[]>();
        await foreach (var (index, block) in downloader.DownloadAsync(manifest, [fixture.Nodes[0].Self]))
            blocks[index] = block;
        blocks.Should().HaveCount(manifest.BlockCount, "附加源按 manifestId 路由命中——取块成功");
    }

    [Fact]
    public async Task ManifestReq_AnsweredByManifestSource()
    {
        await using var fixture = await CreateAsync(2);
        var holder = fixture.Syncs[0];
        var requester = fixture.Syncs[1];

        var content = new byte[128 * 1024];
        new Random(42).NextBytes(content);
        var manifest = SwarmManifest.Build(content, 64 * 1024, Id(0x22));

        var source = new InMemoryManifestSource();
        source.Add(manifest, content);
        holder.AttachSource(source);

        // 持有 → 全量清单应答
        var got = await requester.GetManifestAsync(fixture.Nodes[0].Self, manifest.Id);
        got.Should().NotBeNull();
        got!.Id.Should().Be(manifest.Id);
        got.TotalBytes.Should().Be(manifest.TotalBytes);
        got.BlockSize.Should().Be(manifest.BlockSize);
        got.Checksums.Should().Equal(manifest.Checksums);

        // 未持有 → null
        (await requester.GetManifestAsync(fixture.Nodes[0].Self, Id(0x33))).Should().BeNull();
    }

    [Fact]
    public async Task AnnounceContentAsync_RegistersOnSeedContentDomain()
    {
        await using var fixture = await CreateAsync(2);
        var announcer = fixture.Syncs[0];
        var seed = fixture.Syncs[1];
        var contentId = Id(0x44);

        await announcer.AnnounceContentAsync([fixture.Nodes[1].Self], contentId);

        // 尽力送达的应答闭环异步回写——轮询等待登记可见
        for (var i = 0; i < 50 && !seed.GetHolders(SwarmContentDomain.Raft, contentId)
                 .Contains(fixture.Nodes[0].Self); i++)
        {
            await Task.Delay(20);
        }

        seed.GetHolders(SwarmContentDomain.Raft, contentId)
            .Should().Contain(fixture.Nodes[0].Self, "内容域上报经 0x03 通道——入站登记（wire 形态同 raft）");
    }
}
