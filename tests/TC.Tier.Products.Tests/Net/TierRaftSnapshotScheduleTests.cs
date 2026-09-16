using System.Collections.Concurrent;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;
using TC.Tier.Products.Wal;
using Xunit;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// 快照调度策略（二期-F4——验证矩阵 F4 行）：公开触发（绕过阈值压缩）、
/// 限速窗（SnapshotMinInterval 内宿主循环跳过）、压缩准入钩子（veto）、完成回调（GC/保留钩子）。
/// </summary>
public class TierRaftSnapshotScheduleTests
{
    private sealed class RecordingStateMachine : IStateMachine
    {
        public readonly ConcurrentDictionary<long, byte[]> Applied = new();
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Applied[index] = command.ToArray();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class HostNode : IAsyncDisposable
    {
        public required NodeId Id { get; init; }
        public required string RootDir { get; init; }
        public required InProcessNode Transport { get; init; }
        public required TierRaftHost Host { get; init; }
        public required RecordingStateMachine Machine { get; init; }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }

    private static TierRaftNodeOptions FastOptions(Action<TierRaftNodeOptions>? overrideOptions = null)
    {
        var options = TierRaftNodeOptions.Default
            .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
            .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(1200)));
        overrideOptions?.Invoke(options);
        return options;
    }

    private static async Task<(List<HostNode> Nodes, string RootDir)> CreateClusterAsync(
        int count, Func<TierRaftNodeOptions, TierRaftNodeOptions>? overrideOptions)
    {
        var root = Path.Combine(Path.GetTempPath(), "tctier-snap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var hub = new InProcessTransportHub();
        var nodes = new List<HostNode>();
        try
        {
            var members = Enumerable.Range(0, count).Select(_ => NodeId.NewRandom()).ToArray();
            var config = new ClusterConfig(members.Select(id => new ClusterMember(id, "")).ToArray());
            for (var i = 0; i < count; i++)
            {
                var id = members[i];
                var nodeRoot = Path.Combine(root, $"n{i}");
                var transport = hub.Register(id);
                transport.Start();
                var host = new TierRaftHost(transport, new TierRaftHostOptions
                {
                    GroupFileSystemFactory = gid => TierFs.OpenOrCreate(
                        $"local:///{Path.Combine(nodeRoot, gid.Value.ToString("X16")).Replace('\\', '/')}"),
                    ReconcileInterval = TimeSpan.Zero,
                });
                await host.StartAsync();
                var machine = new RecordingStateMachine();
                var nodeOptions = FastOptions();
                nodeOptions = overrideOptions?.Invoke(nodeOptions) ?? nodeOptions;   // ★ With 链返回新实例——必须接住
                await host.CreateGroupAsync(GroupA, config, machine, nodeOptions);
                nodes.Add(new HostNode { Id = id, RootDir = nodeRoot, Transport = transport, Host = host, Machine = machine });
            }
            return (nodes, root);
        }
        catch
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            await hub.DisposeAsync();
            throw;
        }
    }

    private static readonly RaftGroupId GroupA = new(0x0A0A);

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }

    /// <summary>F4：公开触发——绕过增长阈值直接压缩（N₀ 推进到已持久化尾）。</summary>
    [Fact]
    public async Task TriggerSnapshot_CompactsLog()
    {
        (var nodes, var root) = await CreateClusterAsync(1, overrideOptions: o =>
            o.WithSnapshotGrowthThresholdEntries(0));   // 关闭自动压缩——只测手动触发
        try
        {
            var node = nodes[0];
            var raft = node.Host.GetGroup(GroupA).Raft;
            await raft.ReplicateAsync(new byte[] { 0x01 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await raft.ReplicateAsync(new byte[] { 0x02 }).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

            node.Host.GetGroup(GroupA).Node.Wal.SnapshotIndex.Should().Be(0, "自动压缩已关——无快照");
            var n0 = await node.Host.GetGroup(GroupA).Node.TriggerSnapshotAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            n0.Should().BeGreaterThanOrEqualTo(2, "公开触发——N₀ 推进到已持久化尾");
            node.Host.GetGroup(GroupA).Node.Wal.SnapshotIndex.Should().Be(n0);
        }
        finally
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>F4：限速窗——SnapshotMinInterval 内宿主循环跳过压缩；窗满后恢复。</summary>
    [Fact]
    public async Task SnapshotMinInterval_ThrottlesHostLoopCompaction()
    {
        var completed = new ConcurrentQueue<long>();
        (var nodes, var root) = await CreateClusterAsync(1, overrideOptions: o =>
            o.WithSnapshotGrowthThresholdEntries(5)
             .WithHostLoopInterval(TimeSpan.FromMilliseconds(250))
             .WithSnapshotSchedule(minInterval: TimeSpan.FromSeconds(10),
                 shouldCompactHook: null,
                 completedHook: completed.Enqueue));
        try
        {
            var node = nodes[0];
            // 写 10 条——阈值（5）多次越过；限速窗（10s）内宿主循环不得压缩
            for (var i = 0; i < 10; i++)
                await node.Host.GetGroup(GroupA).Raft.ReplicateAsync(new byte[] { (byte)i })
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(500);   // 宿主循环 tick（250ms 默认）多轮

            completed.Should().BeEmpty("限速窗内宿主循环不压缩（10s 窗——测试窗内全拒）");

            // 手动触发不受限速窗"禁用"——显式运维意图（完成回调照常）
            var n0 = await node.Host.GetGroup(GroupA).Node.TriggerSnapshotAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            n0.Should().BeGreaterThanOrEqualTo(10);
            completed.Should().Contain(n0, "手动触发走完成回调（GC/保留钩子）");
        }
        finally
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>F4：压缩准入钩子 veto + 完成回调——钩子 false 跳过、true 放行并回调。</summary>
    [Fact]
    public async Task ShouldCompactHook_VetoAndComplete()
    {
        var veto = true;   // 先全程拒绝
        var completions = new ConcurrentQueue<long>();
        (var nodes, var root) = await CreateClusterAsync(1, overrideOptions: o =>
            o.WithSnapshotGrowthThresholdEntries(5)
             .WithHostLoopInterval(TimeSpan.FromMilliseconds(250))
             .WithSnapshotSchedule(minInterval: TimeSpan.Zero,
                 shouldCompactHook: (_, _, _) => !veto,
                 completedHook: completions.Enqueue));
        try
        {
            var node = nodes[0];
            for (var i = 0; i < 10; i++)
                await node.Host.GetGroup(GroupA).Raft.ReplicateAsync(new byte[] { (byte)i })
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForAsync(() => node.Machine.Applied.Count >= 10);
            await Task.Delay(500);   // 宿主循环多轮——veto 生效应零压缩
            completions.Should().BeEmpty("准入钩子 veto——零压缩");

            veto = false;   // 解除——下轮宿主循环放行
            await WaitForAsync(() => !completions.IsEmpty, TimeSpan.FromSeconds(10));
            node.Host.GetGroup(GroupA).Node.Wal.SnapshotIndex.Should().BeGreaterThanOrEqualTo(10);
        }
        finally
        {
            foreach (var n in nodes) { try { await n.DisposeAsync(); } catch { } }
            try { Directory.Delete(root, true); } catch { }
        }
    }
}


