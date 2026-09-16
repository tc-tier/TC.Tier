using FluentAssertions;
using Xunit;
using TC.Tier.Core.Net.P2P;
using TC.Tier.Core.Net.Transport.InProcess;

namespace TC.Tier.Core.Net.Tests.P2P;

/// <summary>
/// HyParView membership scenarios over the InProcess medium (spec-06 §7 verification plan ×
/// spec-12 §6 mechanism-plane rewiring — JOIN convergence symmetric/connected/bounded,
/// φ failure detection, churn, partition heal via periodic re-JOIN, drop injection,
/// transport PeerGone). The medium's direct dispatch also exercises the controller's
/// re-entrancy discipline (snapshot-before-send inside the view lock).
/// </summary>
public class PeerControllerTests
{
    /// <summary>测试加速参数（心跳 50ms/φ=16 抖动余量充足/JOIN 等待缩短——丢包轮次不拖时）。</summary>
    private static PeerOptions FastOptions(int seed) => PeerOptions.Default
        .WithHeartbeatInterval(TimeSpan.FromMilliseconds(50))
        .WithJoinWaitTimeout(TimeSpan.FromMilliseconds(300))
        .WithShuffleInterval(TimeSpan.FromSeconds(30))
        .WithJoinTtl(4)
        .WithPhiThreshold(16)
        .WithRandom(new Random(seed));

    private sealed record PeerFixture(InProcessTransportHub Hub, InProcessNode[] Nodes, PeerController[] Peers)
        : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            foreach (var p in Peers)
            {
                try { await p.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            foreach (var n in Nodes)
            {
                try { await n.DisposeAsync(); } catch { /* 尽力清理 */ }
            }
            await Hub.DisposeAsync();
        }
    }

    private static async Task<PeerFixture> CreateAsync(int n, int seed = 42)
    {
        var hub = new InProcessTransportHub();
        var nodes = new InProcessNode[n];
        var peers = new PeerController[n];
        try
        {
            for (var i = 0; i < n; i++)
            {
                nodes[i] = hub.Register(NodeId.NewRandom());
                peers[i] = new PeerController(nodes[i].Self, nodes[i], FastOptions(seed + i));
                await peers[i].StartAsync();
            }
            return new PeerFixture(hub, nodes, peers);
        }
        catch
        {
            foreach (var p in peers.Where(x => x is not null)) await p.DisposeAsync();
            await hub.DisposeAsync();
            throw;
        }
    }

    /// <summary>节点 0 = 种子；其余依次 JOIN（经前驱——活跃视图图沿种子边成树）。</summary>
    private static async Task JoinChainAsync(PeerFixture fx)
    {
        for (var i = 1; i < fx.Peers.Length; i++)
            await fx.Peers[i].JoinAsync(fx.Peers[i - 1].Self);
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

    /// <summary>活跃视图图的 BFS 可达数（从 peers[from] 出发）。</summary>
    private static int ReachableCount(PeerFixture fx, int from)
    {
        var seen = new HashSet<NodeId> { fx.Peers[from].Self };
        var queue = new Queue<NodeId>(seen);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            var idx = Array.FindIndex(fx.Peers, p => p.Self == cur);
            foreach (var next in fx.Peers[idx].ActiveView)
                if (seen.Add(next)) queue.Enqueue(next);
        }
        return seen.Count;
    }

    [Fact]
    public async Task Join_Converges_Symmetric_Connected_Bounded()
    {
        const int n = 8, k = 4;
        await using var fx = await CreateAsync(n);
        await JoinChainAsync(fx);

        await WaitForAsync(() => fx.Peers.All(p => p.ActiveCount > 0));

        // 对称性不变量（spec-06 §4）：A 在 B 活跃视图 ⟺ B 在 A 活跃视图
        var actives = fx.Peers.Select(p => p.ActiveView.ToHashSet()).ToArray();
        for (var a = 0; a < n; a++)
        for (var b = 0; b < n; b++)
            if (a != b)
                actives[a].Contains(fx.Peers[b].Self)
                    .Should().Be(actives[b].Contains(fx.Peers[a].Self), $"对称性 ({a},{b})");

        ReachableCount(fx, 0).Should().Be(n, "活跃视图图连通（种子边成树）");
        fx.Peers.All(p => p.ActiveCount <= k).Should().BeTrue("活跃视图有界（k=4）");
    }

    [Fact]
    public async Task SilentFailure_PhiDetected_RemovedFromActiveViews()
    {
        const int n = 6;
        await using var fx = await CreateAsync(n);
        await JoinChainAsync(fx);
        await WaitForAsync(() => fx.Peers.All(p => p.ActiveCount > 0));

        var victim = fx.Peers[^1];
        var others = fx.Peers[..^1];
        // 稳定期：受害者的心跳已被采样（φ 判定 minSamples——保证静默可检出；也为视图收敛留时间）
        await WaitForAsync(() => others.Any(o => o.PhiForTest(victim.Self) > 0), TimeSpan.FromSeconds(5));
        // 静默崩溃：停止循环（不再发心跳）——不广播 DISCONNECT（真实崩溃形态）
        await victim.StopAsync();

        await WaitForAsync(
            () => others.All(o => !o.ActiveView.Contains(victim.Self)),
            TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Churn_RepeatedJoinBounded_LeaveBroadcastRemoves()
    {
        await using var fx = await CreateAsync(6);
        await JoinChainAsync(fx);
        await WaitForAsync(() => fx.Peers.All(p => p.ActiveCount > 0));

        for (var round = 0; round < 10; round++)
        {
            foreach (var p in fx.Peers.Skip(1))
                await p.JoinAsync(fx.Peers[round % fx.Peers.Length].Self);
            // 有界 = 终态属性（视图上限协议保证）——满载下在途 join 消息未消化时瞬时超限
            //   是协议允许的中间态（判例：80ms 固定观察点被击穿）——轮询等回落，1.5s 不落为败
            var boundedDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
            while (DateTime.UtcNow < boundedDeadline && fx.Peers.Any(p => p.ActiveCount > 4))
                await Task.Delay(25);
            fx.Peers.All(p => p.ActiveCount <= 4).Should().BeTrue($"第 {round} 轮有界（终态）");
        }

        // 主动离开（DISCONNECT 广播——对端移出活跃视图）
        var leaver = fx.Peers[^1];
        var stayers = fx.Peers[..^1];
        foreach (var st in stayers)
            await leaver.LeaveAsync();
        await Task.Delay(200);
        foreach (var st in stayers)
            try
        {
            await WaitForAsync(() => stayers.All(s => !s.ActiveView.Contains(leaver.Self)), TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
                await Task.Delay(20000);
            throw;
        }
    }

    /// <summary>
    /// 分区（φ 检出路径——InProcess 分区 = 静默丢弃无 PeerGone）：已采样的跨界活跃边
    /// 全部经 φ 剔除；愈合 + 重 JOIN 后跨组活跃边对称重建。
    /// <para>★ 断言面为"分区前已采样的跨界边"：被动补位可能提升零心跳样本的对端入活跃视图，
    ///   而 φ 有 minSamples 冷启动宽限（spec-06 §5——零样本不判失败），此类"零样本边"
    ///   在静默丢包介质上不可经 φ 剔除（TCP 介质由断链 PeerGone 事件覆盖——对抗场景族归 D4-T6）。</para>
    /// </summary>
    [Fact]
    public async Task Partition_SampledCrossEdgesDropByPhi_HealRejoinRebuilds()
    {
        const int n = 4;
        await using var fx = await CreateAsync(n);
        await JoinChainAsync(fx);
        await WaitForAsync(() => fx.Peers.All(p => p.ActiveCount > 0));

        var groupA = new HashSet<NodeId> { fx.Peers[0].Self, fx.Peers[1].Self };
        var groupB = new HashSet<NodeId> { fx.Peers[2].Self, fx.Peers[3].Self };
        bool IsCross(NodeId a, NodeId b) => groupA.Contains(a) != groupA.Contains(b);

        // 稳定期：活跃视图成员心跳已全部采样（φ minSamples 冷启动门）
        await WaitForAsync(() => fx.Peers.All(p => p.ActiveView.All(m => p.PhiForTest(m) > 0)),
            TimeSpan.FromSeconds(5));

        // 记录分区前已采样的跨界活跃边——φ 剔除的合同面
        var sampledCross = fx.Peers
            .SelectMany(p => p.ActiveView.Where(m => IsCross(p.Self, m)).Select(m => (Owner: p.Self, Peer: m)))
            .ToList();
        sampledCross.Should().NotBeEmpty("分区前活跃视图已有跨界边");

        fx.Hub.Faults.Partition(groupA, groupB);

        // ★ φ 剔除窗口 = 采样节奏依赖（判例 2026-09-02：混跑调度毛刺拉长心跳采样周期——
        //   10s 击穿实锤）——分区持续存在，剔除必然发生，20s 耐心窗
        await WaitForAsync(
            () => sampledCross.All(e => !fx.Peers.Single(p => p.Self == e.Owner).ActiveView.Contains(e.Peer)),
            TimeSpan.FromSeconds(20));

        // 愈合：注入清零 + 显式重 JOIN——跨组活跃边对称重建
        fx.Hub.Faults.Reset();
        await fx.Peers[2].JoinAsync(fx.Peers[0].Self);
        await WaitForAsync(
            () => fx.Peers[0].ActiveView.Contains(fx.Peers[2].Self)
                && fx.Peers[2].ActiveView.Contains(fx.Peers[0].Self),
            TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// 丢包注入（spec-11 W2 判例的介质同构复验）：30% 定向丢包下会员仍收敛——
    /// ★ JOIN 是一次性消息（协议层不重发）：JoinMsg 整帧丢失 = 种子对加入者零认知，
    /// 收敛由使用方周期重 JOIN 驱动（尽力送达语义的合同）。
    /// </summary>
    [Fact]
    public async Task DropInjection_JoinOneShot_PeriodicRejoinConverges()
    {
        const int n = 5;
        await using var fx = await CreateAsync(n);
        foreach (var a in fx.Peers)
        foreach (var b in fx.Peers)
            if (a.Self != b.Self)
                fx.Hub.Faults.Drop(a.Self, b.Self, 0.3);

        await JoinChainAsync(fx);
        for (var round = 0; round < 10 && ReachableCount(fx, 0) < n; round++)
        {
            await Task.WhenAll(Enumerable.Range(1, n - 1)
                .Select(i => fx.Peers[i].JoinAsync(fx.Peers[i - 1].Self)));
        }

        ReachableCount(fx, 0).Should().Be(n, "尽力送达 + 周期重 JOIN 自愈——30% 丢包不破坏收敛");
        fx.Peers.All(p => p.ActiveCount <= 4).Should().BeTrue("丢包下活跃视图仍有界");
    }

    [Fact]
    public async Task NodeUnregister_PeerGone_RemovedFromViewsImmediately()
    {
        const int n = 5;
        await using var fx = await CreateAsync(n);
        await JoinChainAsync(fx);
        await WaitForAsync(() => fx.Peers.All(p => p.ActiveCount > 0));

        var victim = fx.Peers[^1];
        var survivors = fx.Peers[..^1];
        await victim.DisposeAsync();
        await fx.Nodes[^1].DisposeAsync();   // 枢纽注销 → PeerGone → 视图剔除（无 φ 等待）

        await WaitForAsync(
            () => survivors.All(s => !s.ActiveView.Contains(victim.Self)),
            TimeSpan.FromSeconds(10));
        survivors.All(s => s.ActiveCount > 0).Should().BeTrue("存活者视图仍非空（被动补位在跑）");
    }
}
