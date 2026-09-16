using System.Collections.Concurrent;
using FluentAssertions;
using TC.Tier.Core.Net.Coordination;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Core.Net.Transport.InProcess;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Coordination;

/// <summary>
/// CDC 变更导出（二期-E3 NETGAP-029——验证矩阵 E3 行"端到端"）：
/// raft 提交事件 → ChangeFeed 导出（锚点/配置豁免）→ 消费端按 offset 拉取
/// 断线续传不丢不重；延迟接入从 0 补拉全量；配置条目不进 CDC 流。
/// </summary>
public class ChangeFeedTests
{
    private const byte FeedDomain = 0x73;   // 注册区（0x60-0xAF 使用方自管号——测试域）
    private static readonly Opaque16 FeedId = new(new byte[]
        { 0xAA, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88, 0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF });

    private sealed class Rig : IAsyncDisposable
    {
        public required InProcessTransportHub Hub { get; init; }
        public required InProcessNode Transport { get; init; }
        public required RaftStateMachine Raft { get; init; }
        public required ChangeFeed Feed { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Raft.DisposeAsync();
            await Transport.DisposeAsync();
            await Hub.DisposeAsync();
        }
    }

    private static async Task<Rig> CreateAsync()
    {
        var hub = new InProcessTransportHub();
        var id = NodeId.NewRandom();
        var transport = hub.Register(id);
        transport.Start();
        var store = new Fixtures.InMemoryRaftStore();
        await store.InitializeAsync();
        var pipeline = new ApplyPipeline(store, new RecordingStateMachine(), ApplyPipelineOptions.Default);
        var options = RaftOptions.Default
            .WithElectionTimeout(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(800));
        var raft = new RaftStateMachine(id, store, transport, pipeline, options);
        pipeline.SetConfigCallback(raft.PostConfigChanged);
        await pipeline.StartAsync();
        await raft.StartAsync(new ClusterConfig([new ClusterMember(id, "")]));
        var feed = new ChangeFeed(raft, store, transport, FeedDomain, FeedId);
        return new Rig { Hub = hub, Transport = transport, Raft = raft, Feed = feed };
    }

    /// <summary>E3：端到端——5 条提交 → CDC 流恰有序导出（锚点豁免）；丢包注入下续传不丢不重。</summary>
    [Fact]
    public async Task ChangeFeed_ExportsCommands_ExactlyOnceInOrder()
    {
        await using var fx = await CreateAsync();
        await WaitForAsync(() => fx.Raft.IsLeader, TimeSpan.FromSeconds(10));

        for (var i = 1; i <= 5; i++)
            await fx.Raft.ReplicateAsync(new byte[] { (byte)(0x40 + i) }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // 10% 丢包注入——续传消化（E2 原语继承）
        fx.Transport.Faults.Drop(fx.Transport.Self, fx.Transport.Self, 0.1);

        // ★ 先等 feed 追平再消费（#424 加固——导出经提交事件异步驱动，滞后一拍时消费端
        //   "越过尾部=完成"提前返回 → seen 缺块假红；同文件 LateSubscriber 测试同款手法）
        await WaitForAsync(() => fx.Feed.Feed.RetainedCount >= 5, TimeSpan.FromSeconds(10));

        var seen = new ConcurrentDictionary<long, byte[]>();
        await ResumableFeedClient.ConsumeAsync(fx.Transport, fx.Transport.Self, FeedDomain, FeedId, 0,
            (offset, data) =>
            {
                seen[offset] = data.ToArray();
                return ValueTask.CompletedTask;
            }, pullTimeout: TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(30));

        seen.Count.Should().Be(5, "5 条命令——锚点豁免、恰一次");
        for (var i = 0; i < 5; i++)
            seen[(long)i].Should().Equal(new byte[] { (byte)(0x41 + i) }, $"CDC 第 {i} 块内容有序一致");
    }

    /// <summary>E3：延迟接入——feed 追平后从 0 补拉全量（保留窗内）。</summary>
    [Fact]
    public async Task ChangeFeed_LateSubscriber_GetsFullReplay()
    {
        await using var fx = await CreateAsync();
        await WaitForAsync(() => fx.Raft.IsLeader, TimeSpan.FromSeconds(10));
        for (var i = 1; i <= 4; i++)
            await fx.Raft.ReplicateAsync(new byte[] { (byte)(0x50 + i) }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        // feed 追平（异步 Task.Run——有界等待）
        await WaitForAsync(() => fx.Feed.Feed.RetainedCount >= 4, TimeSpan.FromSeconds(10));

        var seen = new ConcurrentDictionary<long, byte[]>();
        await ResumableFeedClient.ConsumeAsync(fx.Transport, fx.Transport.Self, FeedDomain, FeedId, 0,
            (offset, data) =>
            {
                seen[offset] = data.ToArray();
                return ValueTask.CompletedTask;
            }, pullTimeout: TimeSpan.FromSeconds(1)).WaitAsync(TimeSpan.FromSeconds(30));

        seen.Count.Should().Be(4, "延迟接入从 0 补拉全量");
        seen[(long)0].Should().Equal(new byte[] { 0x51 });
        seen[(long)3].Should().Equal(new byte[] { 0x54 });
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }
}

/// <summary>共享测试状态机（ raft 提交应用面——本测试只读 CDC 流，apply 为空实现）。</summary>
file sealed class RecordingStateMachine : IStateMachine
{
    public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        => ValueTask.CompletedTask;
}
