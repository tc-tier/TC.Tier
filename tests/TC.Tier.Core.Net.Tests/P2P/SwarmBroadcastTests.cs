using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.P2P;
using TC.Tier.Core.Net.Transport.InProcess;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Tests.P2P;

/// <summary>
/// SwarmBroadcast gossip 语义场景（spec-06 §6 语义要点 × spec-09 矩阵 #9——
/// 全员最终收到/去重至多一次/TTL 环路面保险/fanout 有界/尽力不炸 + 帧头字节金样 + 机制 opt-in 挂载）。
/// InProcess 介质 + 委托视图——广播语义与会员管理解耦验证（spec-06 语义纪律）。
/// </summary>
public class SwarmBroadcastTests
{
    private sealed record NodeFixture(InProcessNode Node, SwarmBroadcast Broadcast, List<(NodeId Origin, byte[] Data)> Received)
    {
        public List<(NodeId Origin, byte[] Data)> ReceivedSnapshot()
        {
            lock (Received) return [.. Received];
        }
    }

    private sealed record Fixture(InProcessTransportHub Hub, NodeFixture[] Nodes) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var n in Nodes)
            {
                try { await n.Broadcast.DisposeAsync(); } catch { /* 尽力清理 */ }
                try { await n.Node.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    /// <summary>装配广播网（先注册收齐 id → buildViews 按 id 数组出每节点视图 → 委托捕获视图）。</summary>
    private static async Task<Fixture> CreateAsync(int count, Func<NodeId[], NodeId[][]> buildViews, Func<BroadcastOptions, BroadcastOptions>? tune = null)
    {
        var hub = new InProcessTransportHub();
        var nodes = new NodeFixture[count];
        var media = new InProcessNode[count];
        var disposables = new List<IAsyncDisposable>();
        try
        {
            var ids = new NodeId[count];
            for (var i = 0; i < count; i++)
            {
                media[i] = hub.Register(NodeId.NewRandom());
                disposables.Add(media[i]);
                ids[i] = media[i].Self;
            }
            var views = buildViews(ids);
            for (var i = 0; i < count; i++)
            {
                var idx = i;   // 每迭代快照——委托捕获迭代变量本身会越界
                var broadcast = new SwarmBroadcast(media[idx], () => views[idx], Options(42 + idx, tune));
                disposables.Add(broadcast);
                var received = new List<(NodeId, byte[])>();
                broadcast.MessageReceived += (origin, data) =>
                {
                    lock (received) received.Add((origin, data.ToArray()));
                };
                nodes[idx] = new NodeFixture(media[idx], broadcast, received);
            }
            return new Fixture(hub, nodes);
        }
        catch
        {
            foreach (var d in disposables) { try { await d.DisposeAsync(); } catch { /* 尽力清理 */ } }
            try { await hub.DisposeAsync(); } catch { /* 尽力清理 */ }
            throw;
        }
    }

    /// <summary>参数装配（tune 链尾接 With 返回值——record 的 with 不原地改，Action 形态会静默丢参数）。</summary>
    private static BroadcastOptions Options(int seed, Func<BroadcastOptions, BroadcastOptions>? tune = null)
    {
        var o = BroadcastOptions.Default.WithRandom(new Random(seed));
        return tune?.Invoke(o) ?? o;
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("条件未满足（超时）。");
            await Task.Delay(20);
        }
    }

    private static byte[] Payload(byte fill)
    {
        var data = new byte[32];
        data.AsSpan().Fill(fill);
        return data;
    }

    [Fact]
    public void FrameHeader_ByteGolden_AndRoundTrip()
    {
        GossipFrameHeaderCodec.StructSize.Should().Be(37);
        Span<byte> idBytes = stackalloc byte[16];
        for (var i = 0; i < 16; i++) idBytes[i] = (byte)(0x20 + i);
        Span<byte> originBytes = stackalloc byte[16];
        for (var i = 0; i < 16; i++) originBytes[i] = (byte)(0x40 + i);

        var frame = new byte[GossipFrameHeaderCodec.StructSize + 3];
        GossipFrameHeaderCodec.Write(frame, new GossipFrameHeader(new Opaque16(idBytes), new NodeId(originBytes), 7, 3));

        // 金样：[MsgId 16B 原序][Origin 16B 原序][Ttl 1B][PayloadLength 4B LE]
        frame[0].Should().Be(0x20);
        frame[15].Should().Be(0x2F);
        frame[16].Should().Be(0x40);
        frame[31].Should().Be(0x4F);
        frame[32].Should().Be((byte)7);
        frame[33].Should().Be(3);
        frame[34..37].Should().OnlyContain(b => b == 0);

        var parsed = GossipFrameHeaderCodec.Read(frame);
        parsed.MsgId.Should().Be(new Opaque16(idBytes));
        parsed.Origin.Should().Be(new NodeId(originBytes));
        parsed.Ttl.Should().Be((byte)7);
        parsed.PayloadLength.Should().Be(3);
    }

    [Fact]
    public async Task FullMesh_AllPeersReceive_Once_NoSelfDelivery()
    {
        // 全互联：A=[B,C] B=[A,C] C=[A,B]——中继环回副本被去重归一
        await using var fx = await CreateAsync(3, ids =>
        [
            new NodeId[] { ids[1], ids[2] },
            new NodeId[] { ids[0], ids[2] },
            new NodeId[] { ids[0], ids[1] },
        ]);
        await fx.Nodes[0].Broadcast.BroadcastAsync(Payload(0xAB));

        await WaitForAsync(() => fx.Nodes[1].ReceivedSnapshot().Count > 0 && fx.Nodes[2].ReceivedSnapshot().Count > 0);
        await Task.Delay(100);   // 环回副本窗口——重复投递若存在会在此落地

        foreach (var i in new[] { 1, 2 })
        {
            var got = fx.Nodes[i].ReceivedSnapshot();
            got.Should().HaveCount(1, $"节点 {i} 恰收到一次");
            got[0].Origin.Should().Be(fx.Nodes[0].Node.Self);
            got[0].Data.Should().OnlyContain(b => b == 0xAB);
        }
        fx.Nodes[0].ReceivedSnapshot().Should().BeEmpty("源头不投递给自己");
    }

    [Fact]
    public async Task ChainRelay_TwoHops_TtlDelimits()
    {
        // 链 A—B—C（视图互指）——TTL 够 = C 经 B 中继收到且 Origin 保持
        await using var fx = await CreateAsync(3, ids =>
        [
            new NodeId[] { ids[1] },
            new NodeId[] { ids[0], ids[2] },
            new NodeId[] { ids[1] },
        ]);
        await fx.Nodes[0].Broadcast.BroadcastAsync(Payload(1));
        await WaitForAsync(() => fx.Nodes[2].ReceivedSnapshot().Count > 0);
        fx.Nodes[2].ReceivedSnapshot()[0].Origin.Should().Be(fx.Nodes[0].Node.Self, "中继保持 Origin 语义");
        fx.Nodes[1].ReceivedSnapshot().Should().HaveCount(1, "中继者自身同样投递");
    }

    [Fact]
    public async Task ChainRelay_TtlOne_StopsAtFirstHop()
    {
        // TTL=1：止步首跳——二跳收不到（环路面保险）
        await using var fx = await CreateAsync(3, ids =>
        [
            new NodeId[] { ids[1] },
            new NodeId[] { ids[0], ids[2] },
            new NodeId[] { ids[1] },
        ], tune: o => o.WithTtl(1));
        await fx.Nodes[0].Broadcast.BroadcastAsync(Payload(2));
        await Task.Delay(200);
        fx.Nodes[1].ReceivedSnapshot().Should().HaveCount(1, "首跳收到");
        fx.Nodes[2].ReceivedSnapshot().Should().BeEmpty("TTL 减尽不中继");
    }

    [Fact]
    public async Task Diamond_TwoRelayPaths_DedupDeliversOnce()
    {
        // 菱形 A→{B,C}→D：D 从两条路径各收一份——去重后恰投递一次
        await using var fx = await CreateAsync(4, ids =>
        [
            new NodeId[] { ids[1], ids[2] },
            new NodeId[] { ids[0], ids[3] },
            new NodeId[] { ids[0], ids[3] },
            [],
        ]);
        await fx.Nodes[0].Broadcast.BroadcastAsync(Payload(3));
        await WaitForAsync(() => fx.Nodes[3].ReceivedSnapshot().Count > 0);
        await Task.Delay(100);   // 第二路径副本窗口

        fx.Nodes[3].ReceivedSnapshot().Should().HaveCount(1, "两路径副本去重后至多投递一次");
        fx.Nodes[1].ReceivedSnapshot().Should().HaveCount(1);
        fx.Nodes[2].ReceivedSnapshot().Should().HaveCount(1);
    }

    [Fact]
    public async Task Fanout_BoundedToLimit()
    {
        // 星型 A=[B,C,D,E]、fanout=2——恰 2 个接收者收到
        await using var fx = await CreateAsync(5, ids =>
        [
            new NodeId[] { ids[1], ids[2], ids[3], ids[4] },
            [], [], [],
        ], tune: o => o.WithFanout(2));

        await fx.Nodes[0].Broadcast.BroadcastAsync(Payload(4));
        await WaitForAsync(() => fx.Nodes.Skip(1).Count(n => n.ReceivedSnapshot().Count > 0) == 2);
        await Task.Delay(100);

        fx.Nodes.Skip(1).Count(n => n.ReceivedSnapshot().Count > 0).Should().Be(2, "fanout 有界——恰 Fanout 个目标");
        fx.Nodes[0].ReceivedSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyView_BestEffort_CompletesSilently()
    {
        await using var fx = await CreateAsync(1, _ => [[]]);
        await fx.Nodes[0].Broadcast.BroadcastAsync(Payload(5));   // 空视图——尽力语义零目标，不抛
        await Task.Delay(50);
        fx.Nodes[0].ReceivedSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void Broadcast_ZeroPayload_GoldenRoundTrip()
    {
        // 零载荷合法（心跳式信标形态）——帧宽恰为头宽
        var frame = GC.AllocateUninitializedArray<byte>(GossipFrameHeaderCodec.StructSize);
        GossipFrameHeaderCodec.Write(frame, new GossipFrameHeader(Opaque16.Empty, NodeId.Empty, 1, 0));
        var parsed = GossipFrameHeaderCodec.Read(frame);
        parsed.PayloadLength.Should().Be(0);
        parsed.MsgId.Should().Be(Opaque16.Empty);
    }

    [Fact]
    public async Task PeerMechanism_OptInBroadcast_EndToEnd()
    {
        var hub = new InProcessTransportHub();
        var nA = hub.Register(NodeId.NewRandom());
        var nB = hub.Register(NodeId.NewRandom());
        var nC = hub.Register(NodeId.NewRandom());
        var opts = PeerOptions.Default
            .WithHeartbeatInterval(TimeSpan.FromMilliseconds(50))
            .WithJoinWaitTimeout(TimeSpan.FromMilliseconds(300))
            .WithBroadcast();
        var p2pA = new PeerMechanism(null, opts);          // 种子自身
        var p2pB = new PeerMechanism(nA.Self, opts);       // JOIN A
        var p2pOff = new PeerMechanism(null);              // 未 opt-in
        try
        {
            await p2pA.MountAsync(nA);
            await p2pB.MountAsync(nB);
            await p2pOff.MountAsync(nC);

            p2pA.Broadcast.Should().NotBeNull("opt-in 开启——挂载后句柄非空");
            p2pB.Broadcast.Should().NotBeNull();
            p2pOff.Broadcast.Should().BeNull("未 opt-in——句柄 null（会员管理不含广播）");

            var received = new List<(NodeId Origin, byte[] Data)>();
            p2pB.Broadcast!.MessageReceived += (origin, data) => { lock (received) received.Add((origin, data.ToArray())); };

            await p2pA.Broadcast!.BroadcastAsync(Payload(6));
            await WaitForAsync(() => received.Count > 0);
            received[0].Origin.Should().Be(nA.Self);
            received[0].Data.Should().OnlyContain(b => b == 6);
        }
        finally
        {
            await p2pA.DisposeAsync();
            await p2pB.DisposeAsync();
            await p2pOff.DisposeAsync();
            await hub.DisposeAsync();
        }
    }
}
