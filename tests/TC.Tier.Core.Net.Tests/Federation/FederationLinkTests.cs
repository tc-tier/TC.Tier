using System.Collections.Concurrent;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.Federation;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Core.Net.Tests.Raft;

namespace TC.Tier.Core.Net.Tests.Federation;

/// <summary>
/// 跨集群联邦（二期-F6——DDR-F6 验收「跨集群同步测试」）：
/// ① 双集群收敛——源集群写入经链路在目标集群重放（applied 内容一致）；
/// ② 断链续传——链路停拍期间源继续写，恢复后补齐（不丢不重——去重水位）；
/// ③ ClusterTag 拒链——标签错配 Granted=0，目标零消费。
/// 两个独立内存 hub = 两个独立 raft 域（各自法定人数）；拉取委托直连
/// （跨进程部署 = 传输 0x06 域请求回调——装配层工作，线信封同一套）。
/// </summary>
public class FederationLinkTests
{
    private static readonly int[] s_expectedPayloads = { 0, 1, 2, 3, 4 };
    private const uint SourceClusterTag = 0xFED1_0001;
    private const uint TargetClusterTag = 0xFED1_0002;

    private static async Task<(RaftEngineTests.ClusterRig Rig, RaftEngineTests.TestNode Leader)> CreateClusterAsync()
    {
        var rig = await RaftEngineTests.CreateClusterAsync(3);
        await RaftEngineTests.WaitForAsync(() => rig.Nodes.Any(n => n.Raft.IsLeader), TimeSpan.FromSeconds(15));
        var leader = rig.Nodes.First(n => n.Raft.IsLeader);
        return (rig, leader);
    }

    private static FederationLink CreateLink(RaftEngineTests.TestNode targetLeader,
        RaftEngineTests.TestNode sourceLeader, uint sourceTag, uint declaredSourceTag,
        IFederationWatermarkStore watermarks, TimeSpan? interval = null)
    {
        var source = new FederationSourceHandler(sourceTag, sourceLeader.Store);
        return new FederationLink(
            declaredSourceTag,
            RaftGroupId.Empty,
            targetLeader.Raft,
            watermarks,
            async (request, ct) => (ReadOnlyMemory<byte>)await source.HandlePullAsync(request, ct),
            interval);
    }

    /// <summary>验收①：双集群收敛——目标 applied 内容与源写入一致。</summary>
    [Fact]
    public async Task TwoClusters_SourceWritesConvergeOnTarget()
    {
        var (sourceRig, sourceLeader) = await CreateClusterAsync();
        var (targetRig, targetLeader) = await CreateClusterAsync();
        await using var s = sourceRig;
        await using var t = targetRig;

        // 源集群写 5 条
        var payloadSet = new List<byte[]>();
        for (var i = 0; i < 5; i++)
        {
            var payload = new byte[] { 0xAA, (byte)i };
            await RaftEngineTests.ReplicateRetryAsync(sourceLeader, payload);
            payloadSet.Add(payload);
        }

        var watermarks = new InMemoryFederationWatermarkStore();
        var link = CreateLink(targetLeader, sourceLeader, SourceClusterTag, SourceClusterTag, watermarks);
        var pollErrors = new List<Exception>();
        link.OnPollError = ex => pollErrors.Add(ex);
        for (var poll = 0; poll < 10 && watermarks.Get(SourceClusterTag, RaftGroupId.Empty) < sourceLeader.Store.LastLogIndex; poll++)
            await link.PollOnceAsync(CancellationToken.None);
        watermarks.Get(SourceClusterTag, RaftGroupId.Empty).Should().Be(sourceLeader.Store.LastLogIndex,
            $"源前沿={sourceLeader.Store.LastLogIndex} 水位={watermarks.Get(SourceClusterTag, RaftGroupId.Empty)} " +
            $"目标applied={targetLeader.Machine.Applied.Count} 目标leader={targetLeader.Raft.IsLeader} " +
            $"poll错误=[{string.Join(";", pollErrors.Select(e => e.Message))}]");

        // 目标集群收敛：全部 payload 重放为 Command 并 applied（幂等去重后内容集合一致）
        var targetPayloads = targetLeader.Machine.Applied.Values
            .Where(b => b.Length == 2 && b[0] == 0xAA).Select(b => b[1]).ToHashSet();
        targetPayloads.Should().BeEquivalentTo(s_expectedPayloads, "跨集群已提交条目收敛（内容一致）");
    }

    /// <summary>验收②：断链续传——停拍期间源继续写，恢复后从持久化水位补齐。</summary>
    [Fact]
    public async Task LinkOutage_ResumesFromPersistedWatermark_NoLossNoDup()
    {
        var (sourceRig, sourceLeader) = await CreateClusterAsync();
        var (targetRig, targetLeader) = await CreateClusterAsync();
        await using var s = sourceRig;
        await using var t = targetRig;

        var watermarks = new InMemoryFederationWatermarkStore();
        var link = CreateLink(targetLeader, sourceLeader, SourceClusterTag, SourceClusterTag, watermarks);

        // 第一批：2 条收敛（手动 poll——确定性驱动）
        for (var i = 0; i < 2; i++)
            await RaftEngineTests.ReplicateRetryAsync(sourceLeader, new byte[] { 0xBB, (byte)i });
        for (var poll = 0; poll < 10 && watermarks.Get(SourceClusterTag, RaftGroupId.Empty) < sourceLeader.Store.LastLogIndex; poll++)
            await link.PollOnceAsync(CancellationToken.None);
        watermarks.Get(SourceClusterTag, RaftGroupId.Empty).Should().Be(sourceLeader.Store.LastLogIndex, "第一批收敛");
        await link.DisposeAsync();   // 断链（水位已持久化在 watermarks）

        // 断链期间源继续写 3 条
        for (var i = 0; i < 3; i++)
            await RaftEngineTests.ReplicateRetryAsync(sourceLeader, new byte[] { 0xCC, (byte)i });
        var outageHigh = sourceLeader.Store.LastLogIndex;
        outageHigh.Should().BeGreaterThan(watermarks.Get(SourceClusterTag, RaftGroupId.Empty), "断链期间源有增量");

        // 恢复：新链路接同一持久化水位面——从断点续拉
        var link2 = CreateLink(targetLeader, sourceLeader, SourceClusterTag, SourceClusterTag, watermarks);
        var resumeErrors = new List<Exception>();
        link2.OnPollError = ex => resumeErrors.Add(ex);
        for (var poll = 0; poll < 20 && watermarks.Get(SourceClusterTag, RaftGroupId.Empty) < outageHigh; poll++)
            await link2.PollOnceAsync(CancellationToken.None);
        watermarks.Get(SourceClusterTag, RaftGroupId.Empty).Should().Be(outageHigh,
            $"恢复续拉：水位={watermarks.Get(SourceClusterTag, RaftGroupId.Empty)} 目标高={outageHigh} " +
            $"错误=[{string.Join(";", resumeErrors.Select(e => e.Message))}]");

        var consumed = targetLeader.Machine.Applied.Values
            .Where(b => b.Length == 2 && b[0] is 0xBB or 0xCC)
            .Select(b => (b[0], b[1])).ToList();
        consumed.Count.Should().Be(5, "断链前后全部条目补齐（不丢）");
        consumed.Distinct().Count().Should().Be(5, "去重水位防重复重放（不重）");
    }

    /// <summary>验收③：ClusterTag 错配拒链——Granted=0、目标零消费。</summary>
    [Fact]
    public async Task ClusterTagMismatch_RejectsLink_NoConsumption()
    {
        var (sourceRig, sourceLeader) = await CreateClusterAsync();
        var (targetRig, targetLeader) = await CreateClusterAsync();
        await using var s = sourceRig;
        await using var t = targetRig;

        await RaftEngineTests.ReplicateRetryAsync(sourceLeader, new byte[] { 0xDD, 0x01 });

        var appliedBefore = targetLeader.Machine.Applied.Count;
        var watermarks = new InMemoryFederationWatermarkStore();
        var link = CreateLink(targetLeader, sourceLeader,
            sourceTag: SourceClusterTag, declaredSourceTag: SourceClusterTag + 1,   // 链路声明的源标签错配（配置错误形态）
            watermarks: watermarks);
        link.Start();

        await Task.Delay(600);   // 多拍 poll——全部被拒

        watermarks.Get(SourceClusterTag + 1, RaftGroupId.Empty).Should().Be(0, "拒链不推进水位（声明域零消费）");
        targetLeader.Machine.Applied.Count.Should().Be(appliedBefore, "拒链零重放");
    }

    /// <summary>线信封编解码往返 + 畸形拒绝（单元面）。</summary>
    [Fact]
    public void FederationWire_CodecRoundTrip()
    {
        var pull = FederationWire.EncodePull(0x1234_5678, new RaftGroupId(0xAB), 42, 100);
        FederationWire.TryDecodePull(pull, out var tag, out var group, out var wm, out var max).Should().BeTrue();
        tag.Should().Be(0x1234_5678);
        group.Value.Should().Be(0xABu);
        wm.Should().Be(42);
        max.Should().Be(100);

        var batch = FederationWire.EncodeBatch(7u, new[]
        {
            (1L, 1L, RaftEntryKind.Command, (ReadOnlyMemory<byte>)new byte[] { 1, 2 }),
            (2L, 1L, RaftEntryKind.Command, (ReadOnlyMemory<byte>)Array.Empty<byte>()),
        });
        FederationWire.TryDecodeBatch(batch, out var granted, out var btag, out var entries).Should().BeTrue();
        granted.Should().BeTrue();
        btag.Should().Be(7u);
        entries.Should().HaveCount(2);
        entries[0].Index.Should().Be(1);
        entries[0].Content.Should().Equal(1, 2);
        entries[1].Content.Should().BeEmpty();

        FederationWire.TryDecodePull(new byte[3], out _, out _, out _, out _).Should().BeFalse("畸形请求拒绝");
        FederationWire.TryDecodeBatch(new byte[] { 1, 0, 0, 0, 0, 9, 0, 0, 0 }, out granted, out _, out _).Should().BeFalse("截断批次拒绝");
        var reject = FederationWire.EncodeReject(9u);
        FederationWire.TryDecodeBatch(reject, out granted, out btag, out var empty).Should().BeTrue();
        granted.Should().BeFalse("拒链帧 Granted=0");
        btag.Should().Be(9u);
    }
}
