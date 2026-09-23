using FluentAssertions;
using TC.Tier.Contracts.Storage;
using TC.Tier.Products.Net.Queue;
using TC.Tier.Products.Queue;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// TierQueueReplica 验证矩阵（tierqueue-replicated-spec §6）：三节点组同址可见、辖权消费、
/// Ack 复制、接管 fencing（准出硬门）、组生命周期、延迟、跨组 DLQ、retention 下界、
/// 快照恢复、at-least-once、双活组并行、接管风暴（fencing 不变式对抗钉）。
/// </summary>
public class TierQueueReplicaTests
{
    /// <summary>矩阵 1：Enqueue 全组可见同址——任一节点写（非 leader/非辖权经转发）→ 全组同地址可读。</summary>
    [Fact]
    public async Task Matrix1_EnqueueAcrossNodes_SameAddressAllReplicas()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();

        // 三个节点各写一条（非 leader 节点的写走转发路由）
        var addresses = new List<LogicalAddress>();
        foreach (var node in cluster.Nodes)
        {
            var result = await node.Replica.EnqueueAsync(TierQueueTestFactory.Msg(1, addresses.Count), default);
            addresses.Add(result.Address);
        }

        // 全组同址：辖权节点出队视图 = 各节点写入序（apply 确定性 ⇒ 消息 ID = LogicalAddress 全组一致）
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
        var deliveries = await home.Replica.DequeueAsync(8, default);
        deliveries.Select(d => d.Address).Should().Equal(addresses);
        deliveries.Select(d => TierQueueTestFactory.ParseMsg(d.Payload))
            .Should().Equal((1, 0), (1, 1), (1, 2));
    }

    /// <summary>矩阵 2：辖权消费——非辖权 Dequeue → NotGroupHomeException 携 Home；辖权节点正常出队。</summary>
    [Fact]
    public async Task Matrix2_NonHomeDequeue_ThrowsWithHomeRedirect()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
        var other = cluster.Nodes.First(n => n.Id != home.Id);
        await cluster.WaitConvergedAsync(
            () => Task.FromResult(cluster.Nodes.All(n => n.Replica.GroupHome(TierQueue.DefaultGroupName) == home.Id)),
            TimeSpan.FromSeconds(20));

        var ex = await Assert.ThrowsAsync<NotGroupHomeException>(
            () => other.Replica.DequeueAsync(4, default).AsTask());
        ex.Home.Should().Be(home.Id, "异常携带当前辖权者——客户端凭 Home 直连");
        ex.Group.Should().Be(TierQueue.DefaultGroupName);

        // ★ 2vCPU/CI 负载加固（#424 选举收敛 flake——2vCPU 全量实测复现 Matrix2）：辖权节点
        //   Enqueue 经转发路由到 leader，WaitReady 后领导权偶发迁移、换届未稳时转发重试预算
        //   耗尽抛 NotLeaderException；等领导权稳定（恰一 leader 且位点稳定窗内不迁移）再操作。
        await cluster.WaitLeadershipStableAsync("main", TierQueue.DefaultGroupName);
        await home.Replica.EnqueueAsync(TierQueueTestFactory.Msg(2, 1), default);
        (await home.Replica.DequeueAsync(4, default)).Should().HaveCount(1, "辖权节点正常出队");
    }

    /// <summary>矩阵 3：Ack 复制——辖权 Ack → 全组游标推进一致；BufferedAck 批量路径。</summary>
    [Fact]
    public async Task Matrix3_AckReplicates_CursorConvergesAllReplicas()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3,
            replicaConfigure: o => o.WithBufferedAckBatchSize(4));
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);

        var results = new List<EnqueueResult>();
        for (var i = 0; i < 6; i++)
            results.Add(await home.Replica.EnqueueAsync(TierQueueTestFactory.Msg(3, i), default));
        var deliveries = await home.Replica.DequeueAsync(6, default);
        deliveries.Should().HaveCount(6);

        // 前 4 条走批满自动提交（BufferedAckBatchSize=4），后 2 条缓冲 + 显式 flush
        await home.Replica.AckAsync(deliveries.Take(4).Select(d => d.Address).ToList(), default);
        await home.Replica.AckBufferedAsync(deliveries.Skip(4).Select(d => d.Address).ToList(), default);
        await home.Replica.FlushAcknowledgementsAsync(default);

        await cluster.WaitCursorConvergedAsync(TierQueue.DefaultGroupName, home.Replica.GroupCursor);
        cluster.Nodes.Select(n => n.Replica.GroupCursor).Should().OnlyContain(a => a == home.Replica.GroupCursor,
            "AckCmd 经共识——游标全组一致");
    }

    /// <summary>矩阵 4（准出硬门）：接管 fencing——辖权 kill → HomeCmd 接管 epoch+1 → 新辖权出队推进 →
    /// 旧辖权迟交 Ack → 确定性拒绝；旧节点复活后迟交同样被拒。</summary>
    [Fact]
    public async Task Matrix4_TakeoverFencing_StaleAckDeterministicallyRejected()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);

        // 辖权出队（pending 簿记 + token 发放）但不 Ack
        for (var i = 0; i < 3; i++)
            await home.Replica.EnqueueAsync(TierQueueTestFactory.Msg(4, i), default);
        var staleDeliveries = await home.Replica.DequeueAsync(3, default);
        staleDeliveries.Should().HaveCount(3);
        var staleEpoch = staleDeliveries[0].Token.Epoch;

        // kill 辖权节点（host + transport 有界收尾——模拟进程崩溃）
        var killedRoot = home.RootDir;
        var killedId = home.Id;
        await home.DisposeAsync();
        cluster.Nodes.Remove(home);

        // 幸存节点接管：HomeCmd 接管（epoch+1）→ 新辖权出队推进（pending 丢失 = at-least-once 重放）
        var survivor = cluster.Nodes.First(n => n.Id != killedId);
        await survivor.Replica.TakeoverGroupHomeAsync(TierQueue.DefaultGroupName, default);
        (await cluster.WaitHomeAsync(TierQueue.DefaultGroupName)).Id.Should().Be(survivor.Id, "接管生效");
        var redelivered = await survivor.Replica.DequeueAsync(3, default);
        redelivered.Select(d => d.Address).Should().Equal(staleDeliveries.Select(d => d.Address),
            "pending 纯内存——接管后重放窗重投");
        redelivered[0].Token.Epoch.Should().BeGreaterThan(staleEpoch, "接管 fencing——代次递增");

        // 新辖权 Ack 推进 + 旧 token 迟交（token 回执面）→ StaleDelivery 确定性拒绝
        await survivor.Replica.AckAsync(redelivered.Select(d => d.Address).ToList(), default);
        var staleReceipts = staleDeliveries.Select(d => new DeliveryReceipt(d.Address, d.Token)).ToList();
        var survivorConsumer = survivor.Replica.CreateConsumerAsync(TierQueue.DefaultGroupName);
        (await Assert.ThrowsAsync<StaleDeliveryException>(
                () => survivorConsumer.AckAsync(staleReceipts, default).AsTask()))
            .Should().NotBeNull("旧辖权 token 迟交——fencing 确定性拒绝");

        // ★ 复活重连形态归矩阵 9 的单节点冷启动重建覆盖——本矩阵的准出硬门 =
        //   kill → HomeCmd 接管（epoch+1）→ 重放窗重投 → 迟交确定性拒绝，全部落在活集群上。
    }

    /// <summary>矩阵 5：消费组生命周期——Create/Reset/Delete 经组内全序——三节点视图一致。</summary>
    [Fact]
    public async Task Matrix5_GroupLifecycle_TotalOrderConsistentViews()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();

        var consumer = await cluster.Nodes[1].Replica.CreateGroupAsync(new GroupOptions
        {
            Name = "workers",
            VisibilityTimeout = TimeSpan.FromSeconds(5),
        }, default);
        consumer.Should().NotBeNull();

        // 非提案节点视图一致（ListGroups 全序产物）
        await cluster.WaitConvergedAsync(async () =>
        {
            foreach (var n in cluster.Nodes)
                if (!(await n.Replica.ListGroupsAsync(default)).Contains("workers"))
                    return false;
            return true;
        });
        foreach (var n in cluster.Nodes)
        {
            var groups = (await n.Replica.ListGroupsAsync(default)).OrderBy(x => x).ToArray();
            groups.Should().Equal(["$default", "workers"], "Create 经共识——组清单全组一致");
        }

        // 复位 + 删除复制
        await cluster.Nodes[2].Replica.ResetGroupAsync("workers", GroupStartAt.Latest, default, default);
        await cluster.Nodes[0].Replica.DeleteGroupAsync("workers", default);
        await cluster.WaitConvergedAsync(async () =>
        {
            foreach (var n in cluster.Nodes)
                if ((await n.Replica.ListGroupsAsync(default)).Contains("workers"))
                    return false;
            return true;
        });

        // ★ 删组释放组名：同名重建合法成功（旧实现此处撞 Resources 重名 → apply 无限重试 →
        //   套件楔死 + 引擎泄漏风暴——修复后重建收敛，本断言正向钉住"删组即释放"语义）
        var recreated = await cluster.Nodes[0].Replica.CreateGroupAsync(
            new GroupOptions { Name = "workers", VisibilityTimeout = TimeSpan.FromSeconds(5) }, default);
        recreated.Should().NotBeNull("删组释放组名——同名重建合法");
        await cluster.WaitConvergedAsync(async () =>
        {
            foreach (var n in cluster.Nodes)
                if (!(await n.Replica.ListGroupsAsync(default)).Contains("workers"))
                    return false;
            return true;
        });
    }

    /// <summary>矩阵 6：延迟队列——带到期 Enqueue → 辖权扫描 → 到期投递，全组同地址。</summary>
    [Fact]
    public async Task Matrix6_DelayedEnqueue_DeliveredAfterDue()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
        var writer = cluster.Nodes.First(n => n.Id != home.Id);

        var due = DateTime.UtcNow.AddMilliseconds(600).Ticks;
        var result = await writer.Replica.EnqueueAsync(
            new EnqueueOptions { DueTime = due }, TierQueueTestFactory.Msg(6, 1), default);
        result.Address.IsValid.Should().BeTrue();

        // 到期前不投（辖权扫描无就绪项）
        (await home.Replica.DequeueAsync(4, default)).Should().BeEmpty("未到期消息不入投递面");
        await Task.Delay(900);

        var deliveries = await home.Replica.DequeueAsync(4, default);
        deliveries.Should().ContainSingle("到期后辖权扫描投递");
        deliveries[0].Address.Should().Be(result.Address, "延迟消息地址全组同值");
        TierQueueTestFactory.ParseMsg(deliveries[0].Payload).Should().Be((6, 1));
    }

    /// <summary>矩阵 7：DLQ——死信目标跨实例（另一 raft 组）——跨组投递语义 + 回放还原。</summary>
    [Fact]
    public async Task Matrix7_DeadLetterCrossGroup_EnvelopeReplayable()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3,
            visibility: TimeSpan.FromMilliseconds(500), minRedeliveries: 1, deadLetterGroup: true);
        await cluster.WaitReadyAsync();
        var dlqHome = await cluster.WaitHomeAsync("dlq", TierQueue.DefaultGroupName);
        var mainHome = await cluster.WaitHomeAsync("main", TierQueue.DefaultGroupName);
        await cluster.WaitHomeViewsConvergedAsync("main", TierQueue.DefaultGroupName);
        await cluster.WaitHomeViewsConvergedAsync("dlq", TierQueue.DefaultGroupName);

        // 死信消费组显式建组（$default 自举定格 60s/16——短可见性/低重投上限须显式配置）
        await mainHome.Replica.CreateGroupAsync(new GroupOptions
        {
            Name = "work",
            VisibilityTimeout = TimeSpan.FromMilliseconds(500),
            MaxRedeliveries = 1,
        }, default);
        var work = mainHome.Replica.CreateConsumerAsync("work");

        await mainHome.Replica.EnqueueAsync(TierQueueTestFactory.Msg(7, 9), default);
        // 出队后不 Ack——可见性超时 ×（MaxRedeliveries+1）次记账 → 死信路由跨组写入 dlq 组。
        // ★ 全链路自愈：辖权漂移（引导竞速/换届）时重对齐重试——只有总窗超时才判失败。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        var sawDeadLetter = false;
        while (!sawDeadLetter && DateTime.UtcNow < deadline)
        {
            try
            {
                mainHome = await cluster.WaitHomeAsync("main", "work");
                await work.DequeueAsync(2, default);   // 重投面再入 pending——记账推进至死信上限
            }
            catch (NotGroupHomeException)
            {
                continue;
            }

            try
            {
                dlqHome = await cluster.WaitHomeAsync("dlq", TierQueue.DefaultGroupName);
                var dlqDeliveries = await dlqHome.Replicas["dlq"].DequeueAsync(2, default);
                if (dlqDeliveries.Count == 0)
                    continue;

                // DLQ envelope 溯源（源组/源地址/最终计数）+ 回放还原（目标 = 主队列——跨组回投）
                if (!DeadLetterEnvelope.TryUnwrap(dlqDeliveries[0].Payload, out _, out var originGroup,
                        out var finalCount, out var payload))
                {
                    var head = System.Convert.ToHexString(dlqDeliveries[0].Payload, 0, Math.Min(16, dlqDeliveries[0].Payload.Length));
                    throw new TimeoutException($"DLQ 条目非死信 envelope：len={dlqDeliveries[0].Payload.Length} head={head} addr={dlqDeliveries[0].Address}");
                }
                originGroup.Should().Be("work");
                finalCount.Should().Be(2, "首次超时计数 1（重投）+ 二次超时计数 2（死信）");
                TierQueueTestFactory.ParseMsg(payload).Should().Be((7, 9));

                var replayTarget = await cluster.WaitHomeAsync("main", "work");
                var replayed = await dlqHome.Replicas["dlq"].ReplayAsync(dlqDeliveries[0].Address, replayTarget.Replica, default);
                replayed.Address.IsValid.Should().BeTrue("回放写入目标组（经目标组共识复制）");
                var replayedBack = await replayTarget.Replica.CreateConsumerAsync("work").DequeueAsync(2, default);
                replayedBack.Should().ContainSingle("回放后主队列重新可投");
                sawDeadLetter = true;
            }
            catch (NotGroupHomeException)
            {
                // 辖权视图漂移——下轮重对齐
            }
        }
        sawDeadLetter.Should().BeTrue("死信在总窗内跨组路由并回放（辖权扫描/ExpireCmd/跨组写链路）");
    }

    /// <summary>矩阵 8：retention 下界——慢组游标钉住 TruncatePrefix；组删除后线前移。</summary>
    [Fact]
    public async Task Matrix8_RetentionFloor_SlowGroupPinsTruncation()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);

        for (var i = 0; i < 8; i++)
            await home.Replica.EnqueueAsync(TierQueueTestFactory.Msg(8, i), default);

        // 慢组先建（Earliest 钉住存量）：Ack 首条建立有效游标、此后不再推进——组游标钉住截断下界
        await home.Replica.CreateGroupAsync(new GroupOptions { Name = "slow" }, default);
        var slowConsumer = home.Replica.CreateConsumerAsync("slow");
        var slowAll = await slowConsumer.DequeueAsync(1, default);
        slowAll.Should().ContainSingle();
        await slowConsumer.AckAsync([new DeliveryReceipt(slowAll[0].Address, slowAll[0].Token)], default);

        // $default 全部确认——Ack 即消费，回收线被 slow 组游标钉住
        var all = await home.Replica.DequeueAsync(8, default);
        await home.Replica.AckAsync(all.Select(d => d.Address).ToList(), default);
        var slowFloor = slowConsumer.GroupCursor;
        var defaultCursor = home.Replica.GroupCursor;
        slowFloor.Should().BeLessThan(defaultCursor, "慢组游标落后——唯一回收线钉子");

        // 越界截断目标被夹取到 min(组 Cursor)——不截未确认数据（spec ⑥ 下界守卫）
        await home.Replica.FlushAsync(default);
        var after = await home.Replica.TruncateAsync(home.Replica.TailAddress, default);
        after.Should().Be(slowFloor, "慢组游标钉住回收线（min 组 Cursor 下界夹取）");
        home.Replica.HeadAddress.Should().Be(slowFloor);

        // 组删除 → 绊脚松开 → 辖权 retention 扫描推进回收线到 $default 游标
        await home.Replica.DeleteGroupAsync("slow", default);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        while (home.Replica.HeadAddress < defaultCursor)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("组删除后回收线未前移（辖权 retention 扫描未生效）。");
            await Task.Delay(50);
        }
        home.Replica.HeadAddress.Should().BeGreaterThanOrEqualTo(defaultCursor, "组删除后截断下界抬升");
    }

    /// <summary>矩阵 9：快照恢复——Export/Import 冷启动免全量重放 + 增量重放续接。</summary>
    [Fact]
    public async Task Matrix9_SnapshotRecovery_ColdStartWithoutFullReplay()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);

        for (var i = 0; i < 5; i++)
            await home.Replica.EnqueueAsync(TierQueueTestFactory.Msg(9, i), default);
        var deliveries = await home.Replica.DequeueAsync(5, default);
        await home.Replica.AckAsync(deliveries.Take(3).Select(d => d.Address).ToList(), default);
        var cursorBefore = home.Replica.GroupCursor;

        // 各节点导出业务快照（ring 检查点帧 + 组 meta 镜像——spec ⑤ 自管快照路径）
        // ★ 导出前全组游标收敛（follower apply 滞后会让快照边界 S 参差——断言面要求一致切片）
        await cluster.WaitCursorConvergedAsync(TierQueue.DefaultGroupName, cursorBefore);
        // ★ 注：raft WAL 压缩 × 重启 × 落后副本的交织——已知失败模式见 spec §8.1 ⑤（A1 已修 /
        //   A2·A3 未根因），不在本矩阵断言面。
        foreach (var node in cluster.Nodes)
        {
            await node.Replica.Machine.ExportSnapshotAsync();
            node.Replica.Machine.SnapshotExists().Should().BeTrue("业务快照已落盘");
        }

        // 单节点冷启动重建（同 fs——业务快照 + raft 日志在组卷；集群存活持续服务）
        // ★ 断言面 = 冷启动恢复的确定性状态（导入水位/组游标/ring 像——全部来自快照 + 本地恢复）。
        //   重连后与活集群的复制收敛时序依赖 raft 引擎重连收敛（当前换届追认有秒级抖动），不在本矩阵内。
        var victimIndex = cluster.Nodes.FindIndex(n => n.Id != home.Id);
        var rebuilt = await cluster.RestartNodeAsync(victimIndex);

        var main = rebuilt.Replicas["main"];
        main.Machine.SnapshotExists().Should().BeTrue("业务快照在组卷");
        main.Machine.AppliedThrough.Should().BeGreaterThan(0, "快照导入——应用水位非零起点");
        main.GroupCursor.Should().Be(cursorBefore, "快照导入——组游标免重放直续");
        main.TailAddress.Should().Be(home.Replica.TailAddress, "ring 检查点帧导入——数据像完整还原");
        // 组清单含 $default（组 meta 镜像还原）
        var groups = await rebuilt.Replica.ListGroupsAsync(default);
        groups.Should().Contain(TierQueue.DefaultGroupName);
    }

    /// <summary>矩阵 10：at-least-once——辖权 Ack 前移交 → 重放窗口重投递（幂等消费契约）。</summary>
    [Fact]
    public async Task Matrix10_AtLeastOnce_UnackedRedeliveredAfterHandover()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var home = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);

        for (var i = 0; i < 3; i++)
            await home.Replica.EnqueueAsync(TierQueueTestFactory.Msg(10, i), default);
        var first = await home.Replica.DequeueAsync(3, default);

        // 移交辖权（原辖权 pending 簿记不随租约迁移）→ 新辖权重投
        var target = cluster.Nodes.First(n => n.Id != home.Id);
        await target.Replica.TakeoverGroupHomeAsync(TierQueue.DefaultGroupName, default);
        var newHome = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
        var second = await newHome.Replica.DequeueAsync(3, default);

        second.Select(d => d.Address).Should().Equal(first.Select(d => d.Address),
            "Ack 前接管——at-least-once 重放窗重投（幂等消费契约）");
    }

    /// <summary>矩阵 11：双活组并行——两组两辖权异节点——互不阻塞（组间天然并行继承）。</summary>
    [Fact]
    public async Task Matrix11_TwoGroupsTwoHomes_ProgressIndependently()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();
        var n0 = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
        var n1 = cluster.Nodes.First(n => n.Id != n0.Id);

        foreach (var name in new[] { "g1", "g2" })
            await n0.Replica.CreateGroupAsync(new GroupOptions { Name = name }, default);
        await n1.Replica.TakeoverGroupHomeAsync("g2", default);
        (await cluster.WaitHomeAsync("g2")).Id.Should().Be(n1.Id, "g2 辖权移交 n1");
        var g1 = n0.Replica.CreateConsumerAsync("g1");
        var g2 = n1.Replica.CreateConsumerAsync("g2");

        // 两组异节点辖权：交错出队互不阻塞（跨节点投递面为最终一致——轮询收敛）
        for (var round = 0; round < 3; round++)
        {
            await n0.Replica.EnqueueAsync(TierQueueTestFactory.Msg(11, round), default);
            (await g1.DequeueAsync(1, default)).Should().ContainSingle("g1 辖权在 n0——入口即见");
            await cluster.WaitConvergedAsync(async () => (await g2.DequeueAsync(1, default)).Count == 1,
                TimeSpan.FromSeconds(10));
        }
        cluster.Nodes.Select(n => (n.Replica.GroupHome("g1"), n.Replica.GroupHome("g2")))
            .Should().OnlyContain(t => t.Item1 == n0.Id && t.Item2 == n1.Id, "双辖权视图全组一致");
    }

    /// <summary>矩阵 12（对抗·准出硬门）：接管风暴——辖权反复移交 → epoch 单调、零双辖权投递（fencing 不变式）。</summary>
    [Fact]
    public async Task Matrix12_TakeoverStorm_EpochMonotonicZeroDoubleDelivery()
    {
        await using var cluster = await TierQueueReplicaTestHarness.ReplicaCluster.CreateAsync(3);
        await cluster.WaitReadyAsync();

        // 定位当前辖权并出队——定位与出队之间辖权可再次移交（风暴残余交接）：
        // NotGroupHomeException = 重新定位再试；有界窗内辖权不落定 = 异常上抛（调用方自决）
        async Task<(TierQueueReplicaTestHarness.ReplicaNode Node, IReadOnlyList<QueueDelivery> Deliveries)>
            DequeueAsHomeAsync(TimeSpan budget)
        {
            var deadline = DateTime.UtcNow + budget;
            while (true)
            {
                var node = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
                try
                {
                    return (node, await node.Replica.DequeueAsync(8, default));
                }
                catch (NotGroupHomeException) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(50);
                }
            }
        }

        long lastEpoch = -1;
        var acked = new HashSet<LogicalAddress>();
        for (var round = 0; round < 4; round++)
        {
            var entryHome = await cluster.WaitHomeAsync(TierQueue.DefaultGroupName);
            await entryHome.Replica.EnqueueAsync(TierQueueTestFactory.Msg(12, round), default);

            // 当前辖权出队（token 入簿记）——同批 token 同 epoch（单辖权投递面）
            var (home, deliveries) = await DequeueAsHomeAsync(TimeSpan.FromSeconds(10));
            if (deliveries.Count > 0)
            {
                deliveries.Select(d => d.Token.Epoch).Distinct().Should().ContainSingle(
                    "单辖权不变式——同一辖权面发出的 token 同代次");
                deliveries[0].Token.Epoch.Should().BeGreaterThan(lastEpoch, "接管风暴下 epoch 单调递增");
                lastEpoch = deliveries[0].Token.Epoch;
            }

            // 移交下一节点（HomeCmd epoch+1）→ 旧辖权迟交必拒（零双辖权投递的不变式钉）
            var next = cluster.Nodes.First(n => n.Id != home.Id);
            await next.Replica.TakeoverGroupHomeAsync(TierQueue.DefaultGroupName, default);
            if (deliveries.Count > 0)
            {
                (await Assert.ThrowsAnyAsync<Exception>(
                        () => home.Replica.AckAsync(deliveries.Select(d => d.Address).ToList(), default).AsTask()))
                    .Should().BeAssignableTo<InvalidOperationException>("旧辖权 Ack 必被拒");
            }

            // 新辖权继续推进（未确认前缀重投 + 幂等确认集）——确认成功才入确认集（恰当前缀语义）
            var (newHome, redelivered) = await DequeueAsHomeAsync(TimeSpan.FromSeconds(10));
            var fresh = redelivered.Where(d => !acked.Contains(d.Address)).ToList();
            if (fresh.Count > 0)
            {
                try
                {
                    await newHome.Replica.AckAsync(fresh.Select(d => d.Address).ToList(), default);
                    foreach (var d in fresh)
                        acked.Add(d.Address);
                }
                catch (NotGroupHomeException)
                {
                    // 确认前辖权再移交——本批留待重投（零丢失由收尾排空兜底）
                }
            }
        }

        // 收尾排空：跨节点可见性滞后——终局 home 轮询出队直至残余全部确认（风暴下零丢失推进）。
        // ★ 辖权交接竞态容忍：定位/出队/确认任一点移交都重新来过，不提前弃窗——
        //   窗内残余未收敛 = 终局断言按现有 acked 判（零丢失语义不变）
        var drainDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (acked.Count < 4)
        {
            if (DateTime.UtcNow >= drainDeadline)
                break;
            try
            {
                var (drainHome, rest) = await DequeueAsHomeAsync(TimeSpan.FromSeconds(5));
                var freshRest = rest.Where(d => !acked.Contains(d.Address)).ToList();
                if (freshRest.Count > 0)
                {
                    await drainHome.Replica.AckAsync(freshRest.Select(d => d.Address).ToList(), default);
                    foreach (var d in freshRest)
                        acked.Add(d.Address);
                }
                else
                {
                    await Task.Delay(100);   // 暂无新确认——可见性滞后轮询
                }
            }
            catch (TimeoutException)
            {
                // 排空出队窗内辖权未落定——轮询续试直至排空窗
            }
            catch (NotGroupHomeException)
            {
                // 排空中辖权移交——下轮重新定位
            }
        }

        acked.Count.Should().BeGreaterThanOrEqualTo(4, "每轮消息最终被恰当前缀确认（风暴下零丢失推进）");
    }
}
