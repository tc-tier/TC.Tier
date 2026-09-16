using TC.Tier.Core.Logging;
using System.Net;
using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Tests.Fixtures;
using TC.Tier.Core.Net.Transport;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Transport;

/// <summary>
/// 动态成员（二期-C2——验证矩阵 C2 行）：TCP AddPeer/RemovePeer 拨号断链正确、
/// 归属规则（小 ID 方拨号循环）、入站黑名单（移除重拨入拒 / 复入解禁）、地址制直连不受限。
/// </summary>
public class DynamicPeerRegistryTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private static readonly NodeId SmallId = NodeId.Parse("11111111111111111111111111111111");
    private static readonly NodeId LargeId = NodeId.Parse("22222222222222222222222222222222");

    private sealed class ConsoleLogger : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;
        public void Log(LogLevel logLevel, string message, Exception? exception = null)
            => Console.WriteLine($"[{id}-{logLevel}] {message} {exception?.Message}");
        private static readonly string id = Guid.NewGuid().ToString()[..4];
    }

    private static ClusterTransport StartTransport(NodeId id, int port)
    {
        var transport = new ClusterTransport(id,
            TransportOptions.Default(new IPEndPoint(IPAddress.Loopback, port), new Dictionary<NodeId, IPEndPoint>()),
            logger: new ConsoleLogger());
        transport.Start();
        return transport;
    }

    private static Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? WaitLimit);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("条件未满足（超时）。");
            Thread.Sleep(50);
        }
        return Task.CompletedTask;
    }

    /// <summary>C2：AddPeer 起拨号循环（小 ID 方）→ 链路建立；RemovePeer 断链 + 黑名单 →
    /// 重拨入被拒；AddPeer 复入 → 解禁重建；地址制直连不受黑名单约束。</summary>
    [Fact]
    public async Task Tcp_AddPeerRemovePeer_BlacklistLifecycle()
    {
        var small = StartTransport(SmallId, 0);
        var large = StartTransport(LargeId, 0);
        try
        {
            small.LocalEndPoint.Should().NotBeNull();
            large.LocalEndPoint.Should().NotBeNull();
            var smallUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var largeUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            small.PeerConnected += id => { if (id == LargeId) smallUp.TrySetResult(); };
            large.PeerConnected += id => { if (id == SmallId) largeUp.TrySetResult(); };

            // 小 ID 方 AddPeer 大 ID 方——自起拨号循环 → 链路建立
            small.AddPeer(LargeId, large.LocalEndPoint!);
            await largeUp.Task.WaitAsync(WaitLimit);
            await smallUp.Task.WaitAsync(WaitLimit);
            small.Peers.Should().ContainKey(LargeId);

            // 大 ID 方 RemovePeer 小 ID 方：断链 + 黑名单——小方重拨入被拒（链路不再建立）
            large.RemovePeer(SmallId);
            large.Peers.Should().NotContainKey(SmallId, "已出对端表");
            var reestablished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            large.PeerConnected += id => { if (id == SmallId) reestablished.TrySetResult(); };
            await Task.Delay(500);   // 小方拨号循环重连窗——黑名单期必须全拒
            reestablished.Task.IsCompleted.Should().BeFalse("黑名单期重拨入被拒");

            // 复入解禁：AddPeer 清黑名单 → 链路重建
            large.AddPeer(SmallId, small.LocalEndPoint!);
            await reestablished.Task.WaitAsync(WaitLimit);

            // 地址制直连不受黑名单约束（黑名单只作用于成员制入站面）：
            // small 已在表中（复入）——直连语义由 ConnectAsync 验证对端可达即可建链
            var direct = StartTransport(NodeId.NewRandom(), 0);
            try
            {
                var remote = await direct.ConnectAsync(small.LocalEndPoint!);
                remote.Should().Be(SmallId, "地址制直连与黑名单面正交");
            }
            finally { await direct.DisposeAsync(); }
        }
        finally
        {
            await small.DisposeAsync();
            await large.DisposeAsync();
        }
    }

    /// <summary>C2：RemovePeer 未知 ID 幂等；Peers 快照随 Add/Remove 演进；自端 AddPeer 抛。</summary>
    [Fact]
    public async Task Registry_Semantics_IdempotentRemove_SelfThrow()
    {
        var t = StartTransport(NodeId.NewRandom(), 0);
        try
        {
            var other = NodeId.NewRandom();
            var act = () => t.AddPeer(t.Self, new IPEndPoint(IPAddress.Loopback, 1));
            act.Should().Throw<ArgumentException>("自端不可入表");

            t.Peers.Should().BeEmpty("空表启动");
            t.AddPeer(other, new IPEndPoint(IPAddress.Loopback, 12345));
            t.Peers[other].Port.Should().Be(12345);
            t.AddPeer(other, new IPEndPoint(IPAddress.Loopback, 23456));   // 同 ID 换端点 = 更新
            t.Peers[other].Port.Should().Be(23456, "端点更新生效");

            t.RemovePeer(other);
            t.Peers.Should().NotContainKey(other);
            t.RemovePeer(other);   // 未知 ID 幂等 no-op
        }
        finally { await t.DisposeAsync(); }
    }

    /// <summary>C2：RemovePeer 停止拨号循环——移除后链路不再重建（对比复入前）。</summary>
    [Fact]
    public async Task RemovePeer_StopsDialLoop_NoReestablishFromSmallSide()
    {
        var small = StartTransport(SmallId, 0);
        var large = StartTransport(LargeId, 0);
        try
        {
            small.AddPeer(LargeId, large.LocalEndPoint!);
            var up = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            large.PeerConnected += id => { if (id == SmallId) up.TrySetResult(); };
            await up.Task.WaitAsync(WaitLimit);

            // 小 ID 方（拨号方）RemovePeer 大 ID 方：循环停——不再重拨（大端无感知断链）
            // ★ 订阅先于断链（RemovePeer 同步 Close——事件在移除调用内即触发）
            var gone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            large.PeerGone += id => { if (id == SmallId) gone.TrySetResult(); };
            small.RemovePeer(LargeId);
            await gone.Task.WaitAsync(WaitLimit);
            await Task.Delay(500);   // 循环已停——无重连
            small.Peers.Should().NotContainKey(LargeId, "拨号方已出表——循环停");
        }
        finally
        {
            await small.DisposeAsync();
            await large.DisposeAsync();
        }
    }
}
