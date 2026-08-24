using System.Collections.Concurrent;

namespace TC.Tier.Runtime.Tests.Transactions;

/// <summary>
/// ReplicatedRound + CheckpointRound 契约测试（session-manager-design.md §8.6 与检查点 op）：
/// 决策 true→Confirm-all 推水位；false/异常→D2 截断回滚回执 RollbackException；慢决策不阻塞他域；
/// 检查点与事务回合天然串行（水位传递 + 串行时序 + plan 异常续跑）。
/// </summary>
public class SessionReplicatedCheckpointTests
{
    private static SessionManager NewManager(params (string, ITransactionParticipant)[] participants)
    {
        var m = SessionManager.Create(MemoryFileSystem.New(new MemoryFileSystemOptions()),
            "test", participants: participants);
        m.Initialize();
        m.WaitForReady();
        return m;
    }

    [Fact]
    public async Task Replicated_DecisionTrue_ConfirmsAll_AdvancesWatermark()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s = m.OpenSession();
        s.Stage(() => { });
        long seq = await s.CommitReplicatedAsync((candidate, ct) => ValueTask.FromResult(true));

        seq.Should().Be(1);
        p.LastCommittedSeq.Should().Be(1, "决策 true → Confirm-all 推水位（不可回退点）");
        p.Calls.Should().Contain(c => c.Op == "Confirm" && c.Seq == 1);
        m.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public async Task Replicated_DecisionFalse_RollbackException_ParticipantAborted()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s = m.OpenSession();
        s.Stage(() => { });
        var act = () => s.CommitReplicatedAsync((candidate, ct) => ValueTask.FromResult(false)).AsTask();
        (await act.Should().ThrowAsync<RollbackException>()).Which.Seq.Should().Be(1, "候选 seq 已随回滚作废");

        p.Calls.Should().Contain(c => c.Op == "Abort" && c.Seq == 1, "已 Prepare 者被 Abort（D2 截断）");
        p.LastCommittedSeq.Should().Be(-1, "水位未推进（回滚到上一提交边界）");
        s.SessionState.Should().Be(SessionState.Faulted, "回滚回执后会话 Faulted");

        // 管线续跑
        using var s2 = m.OpenSession();
        s2.Stage(() => { });
        (await s2.CommitAsync()).Should().Be(2, "回滚批 seq 已消耗，续跑 seq=2");
    }

    [Fact]
    public async Task Replicated_DecisionThrows_RollbackWithInner()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s = m.OpenSession();
        s.Stage(() => { });
        var act = () => s.CommitReplicatedAsync((candidate, ct)
            => ValueTask.FromException<bool>(new TimeoutException("共识超时（测试）"))).AsTask();
        var ex = (await act.Should().ThrowAsync<RollbackException>()).Which;
        ex.Seq.Should().Be(1);
        ex.InnerException.Should().BeOfType<TimeoutException>("决策异常内联");
        p.Calls.Should().Contain(c => c.Op == "Abort" && c.Seq == 1);
    }

    [Fact]
    public async Task Replicated_SlowDecision_DoesNotBlockOtherDomains()
    {
        var pa = new FakeParticipant();
        var pb = new FakeParticipant();
        using var ma = NewManager(("a", pa));
        using var mb = NewManager(("b", pb));

        var gate = new ManualResetEventSlim(false);
        using var sa = ma.OpenSession();
        sa.Stage(() => { });
        var slow = sa.CommitReplicatedAsync((candidate, ct)
            => new ValueTask<bool>(Task.Run(() => { gate.Wait(); return true; }))).AsTask();

        // 域 A 决策挂起期间，域 B 正常提交
        using var sb = mb.OpenSession();
        sb.Stage(() => { });
        (await sb.CommitAsync()).Should().Be(1, "慢决策不阻塞他域（管线互不共享）");

        gate.Set();
        (await slow).Should().Be(1);
    }

    [Fact]
    public async Task Checkpoint_ReceivesCurrentWatermark_SerializedWithTxRounds()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s1 = m.OpenSession();
        s1.Stage(() => { });
        await s1.CommitAsync();   // seq=1

        // 检查点 plan 挂起期间入队新事务——plan 只能观察到挂起前水位（串行：tx2 不可能插队物化）
        var planGate = new ManualResetEventSlim(false);
        long observedWatermark = -1;
        var checkpoint = m.EnqueueCheckpoint(seq =>
        {
            observedWatermark = seq;
            planGate.Wait();
        }).AsTask();

        using var s2 = m.OpenSession();
        s2.Stage(() => { });
        var tx2 = s2.CommitAsync().AsTask();

        planGate.Set();
        (await checkpoint).Should().Be(1, "回执=plan 观察的水位");
        observedWatermark.Should().Be(1, "plan 收到当前已提交水位（不消耗新 seq）");
        (await tx2).Should().Be(2, "检查点回合后的下一个事务批 seq=2（天然串行不插队）");
    }

    [Fact]
    public async Task Checkpoint_PlanThrows_ReceiptExceptional_PipelineContinues()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        var act = () => m.EnqueueCheckpoint(
            seq => throw new InvalidOperationException("快照失败（测试）")).AsTask();
        await act.Should().ThrowAsync<InvalidOperationException>();

        using var s = m.OpenSession();   // 管线续跑
        s.Stage(() => { });
        (await s.CommitAsync()).Should().Be(1, "检查点 plan 抛≠管线故障（无结构悬干）");
    }
}
