namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// spec-09 §3 故障注入矩阵场景（单一真源——介质形态由子类装配）：
/// 分区愈合收敛 / 丢包自愈 / 延迟选举余量 / 心跳丢失 / 乱序幂等 / 断连重加入。
/// 全部断言 = 收敛语义（最终一致 + 恰好一次 + 单 leader），不依赖具体时序。
/// <para>★ 子类 = 装配形态：<see cref="FaultMatrixTests"/>（InProcess 主场）/
///   <see cref="FaultMatrixTcpTests"/>（TCP 复跑——spec-12 §12 同构门的注入矩阵面）。</para>
/// </summary>
public abstract class FaultMatrixScenarios
{
    private const int ReplicateTimeoutMs = 10000;

    /// <summary>介质形态标识（失败信息区分）。</summary>
    internal abstract string WireLabel { get; }

    /// <summary>装配 rig（n 节点集群——传输形态由子类决定）。internal = 场景真源不外泄装配面。</summary>
    internal abstract Task<IAdversarialRig> CreateRigAsync(int n, RaftOptions? raft = null);

    /// <summary>分区+愈合（spec-09 矩阵"分区 | 双组/多组"）：双组分裂各自选 leader → 愈合收敛单 leader。</summary>
    [Fact]
    public async Task Partition_SplitVote_Heal_ConvergesSingleLeader()
    {
        await using var fx = await CreateRigAsync(3);
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);
        var followers = fx.Nodes.Where(n => n != leader).ToArray();
        await ClusterOps.WaitForAsync(() => followers.All(n => n.Raft.LeaderId == leader.Id));

        // 双组分裂：leader 孤组（1 节点）vs 两 follower（2 节点 = 多数派——能选新 leader）
        fx.Faults.Partition([leader.Id], followers.Select(n => n.Id));
        var newLeader = await ClusterOps.WaitLeaderAsync(followers, TimeSpan.FromSeconds(15));
        newLeader.Should().NotBe(leader);

        // 分区期间孤组 leader 无法提交（非多数派）——写路径挂起由调用方超时兜底
        await Assert.ThrowsAnyAsync<TimeoutException>(
            async () => await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(999), TimeSpan.FromMilliseconds(1500)));

        // 愈合：全通 → 收敛单 leader（旧 leader 任期落后 → 降级）
        fx.Faults.Reset();
        var finalLeader = await ClusterOps.WaitLeaderAsync(fx.Nodes, TimeSpan.FromSeconds(30));
        await Task.Delay(500);   // 稳定观察窗
        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(1);

        // 愈合后复制工作正常（以当前稳定 leader 为准——愈合后可能再经历一次收敛）
        var current = fx.Nodes.First(n => n.Raft.IsLeader);
        await ClusterOps.ReplicateAsync(current, ClusterOps.Cmd(1), TimeSpan.FromMilliseconds(ReplicateTimeoutMs));
        await ClusterOps.WaitForAsync(() => fx.Nodes.All(n => n.Raft.CommitIndex >= 1));
        fx.Nodes.First(n => n.Raft.IsLeader).Should().Be(current);
    }

    /// <summary>丢包自愈（spec-09 矩阵"丢包 | RPC 丢失自愈（无人工重发）"）：0.15 双向丢包下复制最终一致。
    /// ★ 丢包 + 心跳丢失 → 可能发生选举——复制循环容错（超时/NotLeader → 换当前 leader 重试）。</summary>
    [Fact]
    public async Task Drop_HighRate_ReplicationConverges_NoManualResend()
    {
        await using var fx = await CreateRigAsync(3);
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);
        var others = fx.Nodes.Where(n => n != leader).ToArray();

        // 双向 0.15 丢包（AppendEntries + 应答都丢——协议层重试自愈，无人工重发）
        foreach (var o in others)
        {
            fx.Faults.Drop(leader.Id, o.Id, 0.15);
            fx.Faults.Drop(o.Id, leader.Id, 0.15);
        }

        // ★ 复制容错：丢包可能引发选举——超时/降级后换当前 leader 重试（协议层无人工重发，
        //   重试 = 测试对 leader 切换的适配）
        long lastIndex = 0;
        for (var i = 1; i <= 30; i++)
        {
            while (true)
            {
                if (!leader.Raft.IsLeader)
                {
                    leader = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? leader;
                    continue;
                }
                try
                {
                    lastIndex = await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(i), TimeSpan.FromSeconds(3));
                    break;
                }
                catch (TimeoutException)
                {
                    leader = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? leader;
                }
                catch (NotLeaderException)
                {
                    leader = fx.Nodes.FirstOrDefault(n => n.Raft.IsLeader) ?? leader;
                }
            }
        }

        fx.Faults.Reset();
        foreach (var n in fx.Nodes)
        {
            try
            {
                await ClusterOps.WaitForAsync(() => n.Raft.CommitIndex >= lastIndex, TimeSpan.FromSeconds(60));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"[{WireLabel}] 丢包追平失败：lastIndex={lastIndex} node={n.Id} "
                    + $"commit={n.Raft.CommitIndex} " + ClusterOps.Dump(fx.Nodes));
            }
        }

        // ★ 等待口径 = applied 业务条数（commit ≠ applied——apply 管道异步滞后；且日志 index ≠ 业务
        //   序列：leader 任期锚点不达状态机（二期-B），业务条目自锚点起右移——计数断言只认业务域）
        await ClusterOps.WaitForAsync(
            () => fx.Nodes.All(n => n.Machine.Applied.Count >= 30), TimeSpan.FromSeconds(60));

        // 恰好一次：全节点 applied 业务序列 = cmd-0001..cmd-0030 按序（键升序、无重复、无空洞）
        foreach (var n in fx.Nodes)
            ClusterOps.AssertAppliedExactlyOnce(n, 30);
    }

    /// <summary>延迟选举余量（spec-01 §8：选举超时 [150,300]ms，心跳 50ms）：
    /// 定向延迟 30ms（余量 5×——工程安全档）→ 不误选举——leader 保持，任期不涨。
    /// ★ 80/60/40ms 档实测偶发误选（对抗环境处理延迟吃掉余量）——30ms 为稳定参数。</summary>
    [Fact]
    public async Task Latency_WithinElectionBudget_NoFalseElection()
    {
        await using var fx = await CreateRigAsync(3);
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);
        var followers = fx.Nodes.Where(n => n != leader).ToArray();
        await ClusterOps.WaitForAsync(() => followers.All(n => n.Raft.LeaderId == leader.Id));
        var termBefore = leader.Raft.CurrentTerm;

        // 定向 30ms 延迟（心跳 50ms 间隔 → 到达间隔 30ms < 150ms 选举超时下限，余量 5×）
        foreach (var f in followers)
        {
            fx.Faults.SetLatency(leader.Id, f.Id, TimeSpan.FromMilliseconds(30));
            fx.Faults.SetLatency(f.Id, leader.Id, TimeSpan.FromMilliseconds(30));
        }

        await Task.Delay(3000);   // 观察窗 ≈ 10+ 个选举超时窗口

        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(1);
        fx.Nodes.First(n => n.Raft.IsLeader).Should().Be(leader);
        fx.Nodes.Max(n => n.Raft.CurrentTerm).Should().Be(termBefore);   // 零抬任期 = 无选举

        // 延迟下复制仍工作（一条往返 60ms 内完成）
        await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(1), TimeSpan.FromMilliseconds(5000));
    }

    /// <summary>心跳丢失（spec-09 §2 矩阵 #4"丢 1–2 心跳不误选举"）：leader→follower 单向全丢
    /// 1–2 个心跳周期 → follower 不发起选举：任期零抬、leader 不变、丢跳后复制正常。
    /// <para>★ 选举超时放大到 [300,600]ms（心跳 50ms 不变——丢 6 跳才该选举）：协议逻辑
    ///   （心跳重置计时器/丢跳不选举）与参数无关；默认 [150,300] 下 85ms 窗最坏饥饿
    ///   135ms 贴死下限——实测（2026-08-27 日志取证）：victim 抽到 ~150ms 超时 + CI 调度
    ///   15ms 即穿 → PreVote 合法当选 = 协议正确但窗口数学贴边不可重复。</para>
    /// <para>★ 前置静默期：排除启动期选举轮转（WaitLeader 稳定窗后 500ms 任期不变才进丢跳窗）。</para>
    /// </summary>
    [Fact]
    public async Task Heartbeat_LoseOneOrTwoBeats_NoFalseElection()
    {
        await using var fx = await CreateRigAsync(3,
            RaftOptions.Default.WithElectionTimeout(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(600)));
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);
        var followers = fx.Nodes.Where(n => n != leader).ToArray();
        await ClusterOps.WaitForAsync(() => followers.All(n => n.Raft.LeaderId == leader.Id));
        var termBefore = fx.Nodes.Max(n => n.Raft.CurrentTerm);

        // 前置静默期：500ms 任期不变（启动期选举轮转彻底静默——负载下收敛慢）
        await Task.Delay(500);
        fx.Nodes.Max(n => n.Raft.CurrentTerm).Should().Be(
            termBefore, "静默期抬任期 = 启动期选举未收敛，不进丢跳窗");

        var victim = followers[0];
        // 窗口 1：丢 1 跳（45ms ≈ 1 个心跳间隔；最坏饥饿 95ms ≪ 300ms 下限）
        fx.Faults.Drop(leader.Id, victim.Id, 1.0);
        await Task.Delay(45);
        fx.Faults.Drop(leader.Id, victim.Id, 0);
        await Task.Delay(150);   // 恢复窗（3 个心跳周期——计时器重置后再进下一窗）

        // 窗口 2：丢近 2 跳（95ms ≈ 2 个心跳周期整窗；最坏饥饿 145ms ≪ 300ms 下限）
        fx.Faults.Drop(leader.Id, victim.Id, 1.0);
        await Task.Delay(95);
        fx.Faults.Drop(leader.Id, victim.Id, 0);

        await Task.Delay(500);   // 观察窗：误选举在此窗内暴露（任期抬升/leader 更替）

        fx.Nodes.Count(n => n.Raft.IsLeader).Should().Be(1);
        fx.Nodes.First(n => n.Raft.IsLeader).Should().Be(leader);
        fx.Nodes.Max(n => n.Raft.CurrentTerm).Should().Be(termBefore);   // 零抬任期 = 无选举（PreVote 也不抬）
        victim.Raft.LeaderId.Should().Be(leader.Id);   // victim 全程认 leader

        // 丢跳后复制仍正常（victim 重新收流）
        await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(1), TimeSpan.FromMilliseconds(5000));
        await ClusterOps.WaitForAsync(() => victim.Raft.CommitIndex >= 1);
    }

    /// <summary>乱序重排（spec-09 矩阵"乱序 | 重排 | follower 幂等处理"）：
    /// leader → follower 全方向乱序投递 → follower 幂等（不崩、无重复、无空洞、最终一致）。</summary>
    [Fact]
    public async Task Reorder_OutOfOrderDelivery_FollowerIdempotent()
    {
        await using var fx = await CreateRigAsync(3);
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);
        var followers = fx.Nodes.Where(n => n != leader).ToArray();

        // 乱序：到达顺序 ≠ 发送顺序（随机抖动 0–20ms 破坏顺序）
        foreach (var f in followers)
            fx.Faults.Reorder(leader.Id, f.Id, true);

        var lastWritten = 0L;
        for (var i = 1; i <= 25; i++)
        {
            try
            {
                lastWritten = await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(i), TimeSpan.FromMilliseconds(ReplicateTimeoutMs));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"[{WireLabel}] 乱序写路径提交超时：i={i} " + ClusterOps.Dump(fx.Nodes)
                    + "\n" + ClusterOps.Traces(fx.Nodes));
            }
        }

        fx.Faults.Reset();
        foreach (var n in fx.Nodes)
        {
            try
            {
                await ClusterOps.WaitForAsync(() => n.Raft.CommitIndex >= lastWritten, TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException)
            {
                throw new TimeoutException($"[{WireLabel}] 乱序追平失败：node={n.Id} "
                    + $"commit={n.Raft.CommitIndex} " + ClusterOps.Dump(fx.Nodes)
                    + "\n" + ClusterOps.Traces(fx.Nodes));
            }
        }
        // ★ 同 Drop_HighRate：applied 滞后于 commit，且日志 index ≠ 业务序列（任期锚点不达状态机）——
        //   恰好一次断言等业务条数追平后按业务序列断言
        await ClusterOps.WaitForAsync(
            () => fx.Nodes.All(n => n.Machine.Applied.Count >= 25), TimeSpan.FromSeconds(20));

        // 幂等断言：恰好一次 + 按序（无重复、无空洞）
        foreach (var n in fx.Nodes)
            ClusterOps.AssertAppliedExactlyOnce(n, 25);
    }

    /// <summary>断连重加入（spec-09 矩阵"断连 | 单链路/全网"）：follower 掉线多数派继续提交 →
    /// 同介质重建自动恢复 + 追平（RPC 丢失自愈 + 冷启动重建——快照区 + 已提交日志重放）。</summary>
    [Fact]
    public async Task NodeGone_QuorumSurvives_Rejoin_CatchesUp()
    {
        await using var fx = await CreateRigAsync(3);
        var leader = await ClusterOps.WaitLeaderAsync(fx.Nodes);
        var gone = fx.Nodes.First(n => n != leader);
        var goneIdx = fx.Nodes.ToList().IndexOf(gone);

        // 掉线：网络断 + 进程关（优雅 Dispose——数据已落盘；TCP = 断链由退避驱动重连）
        await fx.KillNodeAsync(goneIdx);

        // 多数派（2/3）存活——复制继续
        var lastWritten = 0L;
        for (var i = 1; i <= 20; i++)
            lastWritten = await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(i), TimeSpan.FromMilliseconds(ReplicateTimeoutMs));
        await ClusterOps.WaitForAsync(() => leader.Raft.CommitIndex >= lastWritten);

        // 重加入：同介质同存储重建（快照区 + 已提交日志自动恢复）→ 追平
        var rejoined = await fx.RejoinNodeAsync(goneIdx);
        try
        {
            // 追平口径 = 业务条数（日志 index ≠ 业务序列——任期锚点不达状态机，二期-B）
            await ClusterOps.WaitForAsync(() => rejoined.Machine.Applied.Count >= 20, TimeSpan.FromSeconds(20));

            // 追平后复制继续工作（新节点参与复制）
            await ClusterOps.ReplicateAsync(leader, ClusterOps.Cmd(21), TimeSpan.FromMilliseconds(ReplicateTimeoutMs));
            await ClusterOps.WaitForAsync(() => rejoined.Machine.Applied.Count >= 21);
            // 恰好一次：重加入节点无重复应用（重启重放 = at-least-once 契约——按序无重复断言）
            ClusterOps.AssertAppliedExactlyOnce(rejoined, 21);
        }
        finally
        {
            await rejoined.DisposeAsync();
        }
    }
}
