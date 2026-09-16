using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.IO;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// N=1 Standalone × 真实 TierWAL（local:// 磁盘）回归——修复前实证：alloc/persisted 推进而
/// commit 恒 0、ReplicateAsync 永挂（memory: 路径 InMemoryRaftStore 单测覆盖不到真存储面）。
/// </summary>
public class StandaloneLocalDiskTests
{
    private sealed class CountingMachine : IStateMachine
    {
        public long Applied;
        public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Applied);
            return ValueTask.CompletedTask;
        }
    }

    private static int ReservePort()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)socket.LocalEndPoint!).Port;
        socket.Close();
        return port;
    }

    private static TierRaftNodeOptions FastOptions() => TierRaftNodeOptions.Default
        .WithWal(TierWalOptions.Default.WithCommitInterval(TimeSpan.FromMilliseconds(-1)))
        .WithRaft(RaftOptions.Default.WithElectionTimeout(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)));

    /// <summary>N=1 + TCP 组装（真部署形态）+ local:// 真盘——rafthost 云端挂起场景的最小复现。</summary>
    [Fact]
    public async Task Standalone_LocalDisk_TCP_Replicate_Commits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-n1-" + Guid.NewGuid().ToString("N"));
        var port = ReservePort();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, $"127.0.0.1:{port}")]);
        var machine = new CountingMachine();
        await using var node = await TierRaftNodeBuilder.Create(id, TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"),
                config, machine)
            .WithClusterTransport(new IPEndPoint(IPAddress.Any, port),
                new Dictionary<NodeId, IPEndPoint> { [id] = new(IPAddress.Loopback, port) })
            .WithOptions(FastOptions())
            .StartAsync();

        node.Raft.IsLeader.Should().BeTrue("N=1 启动即 Leader（Standalone 快捷路径）");

        // ★ 换届/就绪窗口容忍（标准客户端模式）：Standalone 捷径在循环内异步当选——
        //   StartAsync 返回与 BecomeLeader 完成间存在竞态窗（NotLeader 重试消解）
        long idx = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                idx = await node.Raft.ReplicateAsync(new byte[] { 0x01 }).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                break;
            }
            catch (NotLeaderException) when (attempt < 40) { await Task.Delay(50); }
        }
        idx.Should().BeGreaterThanOrEqualTo(1); // ★ 二期-B 锚点感知：leader 任期锚点 no-op 占 index 1——首写位随锚点后移
        await WaitForAsync(() => Interlocked.Read(ref machine.Applied) >= 1);
        node.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>变体 C：Default 选项 + 可取消 ct——rafthost 的精确镜像（自动提交器开启）。
    /// ★ V0.1 根修回归门（2026-09-14）：raft 侧补 wal 自动提交轨的推进观察点 +
    ///   TierWal persisted 宣称诚实边界（见 VALIDATIONS V0.1）——Release 下 2 核 pin 多轮验证。</summary>
    [Fact]
    public async Task Standalone_LocalDisk_DefaultOptions_Cancelable_Replicate_Commits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-n1-" + Guid.NewGuid().ToString("N"));
        var port = ReservePort();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, $"127.0.0.1:{port}")]);
        var machine = new CountingMachine();
        await using var node = await TierRaftNodeBuilder.Create(id, TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"),
                config, machine)
            .WithClusterTransport(new IPEndPoint(IPAddress.Any, port),
                new Dictionary<NodeId, IPEndPoint> { [id] = new(IPAddress.Loopback, port) })
            .StartAsync();   // TierRaftNodeOptions.Default——rafthost 同款

        var idx = await node.Raft.ReplicateAsync(new byte[] { 0x01 }, new CancellationTokenSource().Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));   // 可取消 ct = TCS 兜底路径
        idx.Should().BeGreaterThanOrEqualTo(1); // ★ 二期-B 锚点感知：leader 任期锚点 no-op 占 index 1——首写位随锚点后移
        await WaitForAsync(() => Interlocked.Read(ref machine.Applied) >= 1);
        node.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>变体 D：FastOptions + 可取消 ct——二分「选项 vs 调用路径」。</summary>
    [Fact]
    public async Task Standalone_LocalDisk_FastOptions_Cancelable_Replicate_Commits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-n1-" + Guid.NewGuid().ToString("N"));
        var port = ReservePort();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, $"127.0.0.1:{port}")]);
        var machine = new CountingMachine();
        await using var node = await TierRaftNodeBuilder.Create(id, TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"),
                config, machine)
            .WithClusterTransport(new IPEndPoint(IPAddress.Any, port),
                new Dictionary<NodeId, IPEndPoint> { [id] = new(IPAddress.Loopback, port) })
            .WithOptions(FastOptions())
            .StartAsync();

        var idx = await node.Raft.ReplicateAsync(new byte[] { 0x01 }, new CancellationTokenSource().Token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        idx.Should().BeGreaterThanOrEqualTo(1); // ★ 二期-B 锚点感知：leader 任期锚点 no-op 占 index 1——首写位随锚点后移
        await WaitForAsync(() => Interlocked.Read(ref machine.Applied) >= 1);
        node.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>对照组：N=1 + InProcess 注入 + local:// 真盘——二分传输面/存储面。</summary>
    [Fact]
    public async Task Standalone_LocalDisk_InProcess_Replicate_Commits()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tctier-n1-" + Guid.NewGuid().ToString("N"));
        await using var hub = new InProcessTransportHub();
        var id = NodeId.NewRandom();
        var config = new ClusterConfig([new ClusterMember(id, "")]);
        var machine = new CountingMachine();
        await using var node = await TierRaftNodeBuilder.Create(id, TierFs.OpenOrCreate($"local:///{dir.Replace('\\', '/')}"),
                config, machine)
            .WithTransport(hub.Register(id))
            .WithOptions(FastOptions())
            .StartAsync();

        // ★ 换届/就绪窗口容忍（标准客户端模式）：Standalone 捷径在循环内异步当选——
        //   StartAsync 返回与 BecomeLeader 完成间存在竞态窗（NotLeader 重试消解）
        long idx = 0;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                idx = await node.Raft.ReplicateAsync(new byte[] { 0x01 }).AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                break;
            }
            catch (NotLeaderException) when (attempt < 40) { await Task.Delay(50); }
        }
        idx.Should().BeGreaterThanOrEqualTo(1); // ★ 二期-B 锚点感知：leader 任期锚点 no-op 占 index 1——首写位随锚点后移
        await WaitForAsync(() => Interlocked.Read(ref machine.Applied) >= 1);
        node.Raft.CommitIndex.Should().BeGreaterThanOrEqualTo(1);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(50);
        }
    }
}
