using System.Collections.Concurrent;

namespace TC.Tier.Runtime.Tests.Transactions;

/// <summary>
/// 测试假件参与者——记录 2PC 调用序列，可注入 Prepare 失败/阻塞（管线时序控制）。
/// </summary>
internal sealed class FakeParticipant : ITransactionParticipant
{
    public ConcurrentQueue<(string Op, long Seq)> Calls { get; } = new();

    /// <summary>Prepare 阻塞门（放行前管线在 Prepare 阶段挂起——排队/Abort 时序控制）。</summary>
    public ManualResetEventSlim? PrepareGate { get; set; }

    /// <summary>注入：谓词真则 Prepare 抛（单次故障注入——触发后自动清除）。</summary>
    public Func<long, bool>? FailPrepareOnce { get; set; }

    private long _lastCommittedSeq = -1;
    private long _lastPreparedSeq = -1;

    public long LastCommittedSeq => Volatile.Read(ref _lastCommittedSeq);
    public long LastPreparedSeq => Volatile.Read(ref _lastPreparedSeq);

    public void Prepare(long seq)
    {
        var gate = PrepareGate;
        if (gate != null)
        {
            Calls.Enqueue(("PrepareBegin", seq));   // 挂起信号（测试轮询"管线已进 Prepare"）
            gate.Wait();   // 时序控制：等测试放行
        }
        var fail = FailPrepareOnce;
        if (fail != null && fail(seq))
        {
            FailPrepareOnce = null;   // 单次故障
            throw new InvalidOperationException($"注入故障：Prepare({seq}) 抛（测试假件）");
        }
        Volatile.Write(ref _lastPreparedSeq, seq);
        Calls.Enqueue(("Prepare", seq));
    }

    public ValueTask PrepareAsync(long seq, CancellationToken ct)
    {
        Prepare(seq);
        return ValueTask.CompletedTask;
    }

    public void ConfirmCommitted(long seq)
    {
        long current;
        do
        {
            current = Volatile.Read(ref _lastCommittedSeq);
            if (seq <= current) return;
        } while (Interlocked.CompareExchange(ref _lastCommittedSeq, seq, current) != current);
        Calls.Enqueue(("Confirm", seq));
    }

    public void Abort(long seq)
    {
        Volatile.Write(ref _lastPreparedSeq, Volatile.Read(ref _lastCommittedSeq));
        Calls.Enqueue(("Abort", seq));
    }

    public ValueTask AbortAsync(long seq, CancellationToken ct)
    {
        Abort(seq);
        return ValueTask.CompletedTask;
    }

    public void OnCommitted(long seq, Action callback)
    {
        if (seq <= Volatile.Read(ref _lastCommittedSeq)) callback();
        // 回调登记不需要——管线测试不消费
    }
}

/// <summary>
/// SessionManager 提交管线契约测试（session-manager-design.md §8.1/8.4/8.5/8.8——TxRound 批合并核心）：
/// FIFO 串行+批合并（同批共享 seq、跨批递增）、物化序、排队中 Abort 零消耗、Dispose 有界排水、
/// 故障模型（物化抛=管线 Faulted / Prepare 抛=Abort 已 Prepare 者+续跑）。
/// </summary>
public class SessionPipelineTests
{
    private static SessionManager NewManager(params (string, ITransactionParticipant)[] participants)
    {
        var m = SessionManager.Create(MemoryFileSystem.New(new MemoryFileSystemOptions()), "test", participants: participants);
        m.Initialize();
        m.WaitForReady();
        return m;
    }

    [Fact]
    public async Task SingleSession_Commit_AdvancesSeqMonotonically()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s = m.OpenSession();
        for (int i = 0; i < 5; i++)
        {
            s.Stage(() => { }, i);
            (await s.CommitAsync()).Should().Be(i + 1, "空域 seq 从 1 起严格递增（批合并下串行会话每回合一批）");
        }
        m.LastCommittedSeq.Should().Be(5);
        p.LastCommittedSeq.Should().Be(5, "参与者水位随 Confirm 推进");
    }

    [Fact]
    public async Task ConcurrentSessions_BatchMerge_SharedSeqPerBatchStrictlyIncreasing()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        const int sessions = 8, commits = 20;
        var seqs = new ConcurrentBag<long>();
        await Task.WhenAll(Enumerable.Range(0, sessions).Select(_ => Task.Run(async () =>
        {
            using var s = m.OpenSession();
            for (int c = 0; c < commits; c++)
            {
                s.Stage(() => { });
                seqs.Add(await s.CommitAsync());
            }
        })));

        var all = seqs.ToList();
        all.Count.Should().Be(sessions * commits, "全部回执");
        var distinct = all.Distinct().OrderBy(v => v).ToList();
        distinct.Count.Should().BeLessThan(all.Count, "批合并生效——并发回合共享批 seq");
        distinct[0].Should().Be(1);
        distinct.Should().BeInAscendingOrder();   // 跨批 seq 严格递增（无回退无重复批号）
        m.LastCommittedSeq.Should().Be(distinct[^1], "水位=最大批 seq");
    }

    [Fact]
    public async Task SameSession_MaterializersExecuteInOrder_FifoWithinRound()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        var order = new ConcurrentQueue<int>();
        using var s = m.OpenSession();
        for (int i = 0; i < 5; i++)
        {
            int v = i;   // for 循环变量捕获陷阱——物化延迟执行，须捕获每次迭代副本
            s.Stage(() => order.Enqueue(v), i);
        }
        await s.CommitAsync();

        order.Should().Equal(0, 1, 2, 3, 4);   // 回合内物化按 Stage 序（FIFO 全序）
    }

    [Fact]
    public async Task QueuedAbort_RoundDropped_ZeroSeqConsumed_StructureUntouched()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        // 管线挂在第一回合的 Prepare 上（时序控制）——后续回合排队。
        // ★ 必须等管线确实挂起（PrepareBegin）再入队 s2：批合并在 Prepare 挂起前排空积压——
        //   s2 过早入队会被吸入 s1 的批（物化+在途），Abort=等终态（合法）与未放行 gate 互锁。
        p.PrepareGate = new ManualResetEventSlim(false);
        using var s1 = m.OpenSession("inflight");
        s1.Stage(() => { });
        var t1 = s1.CommitAsync().AsTask();
        WaitForPrepareBegin(p);

        using var s2 = m.OpenSession("queued");
        var materialized = 0;
        s2.Stage(() => materialized++);
        var t2 = s2.CommitAsync().AsTask();
        s2.Abort();   // 排队中撤销（管线仍挂在 s1 的 Prepare）

        p.PrepareGate!.Set();   // 放行
        (await t1).Should().Be(1, "在途回合正常完成 seq=1");

        (await AssertThrowsAsync<OperationCanceledException>(t2))
            .Should().BeTrue("排队中 Abort=排队撤销（回执取消）");
        materialized.Should().Be(0, "结构零触碰——物化未执行");
        p.Calls.Should().NotContain(c => c.Op == "Prepare" && c.Seq == 2,
            "seq 零消耗——被撤销回合不产生第二次 Prepare");
        p.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_DrainsQueuedRounds_AllReceiptsDelivered()
    {
        var p = new FakeParticipant();
        var m = NewManager(("p1", p));

        p.PrepareGate = new ManualResetEventSlim(false);
        var results = new List<Task<long>>();
        for (int i = 0; i < 5; i++)
        {
            var s = m.OpenSession($"s{i}");
            s.Stage(() => { });
            results.Add(s.CommitAsync().AsTask());
        }

        var disposeTask = m.DisposeAsync().AsTask();
        p.PrepareGate.Set();   // 放行（Dispose 有界排水期间管线继续处理）
        await disposeTask;

        (await Task.WhenAll(results)).Should().NotBeEmpty("有界排水——已入队回合全部回执");
    }

    [Fact]
    public async Task MaterializeThrows_PipelineFaults_AllReceiptsExceptional()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s = m.OpenSession();
        s.Stage(() => throw new InvalidOperationException("物化失败（测试）"));
        var fail = s.CommitAsync().AsTask();

        // 排队后续回合（故障排水覆盖）
        using var s2 = m.OpenSession();
        s2.Stage(() => { });
        var drained = s2.CommitAsync().AsTask();

        (await AssertThrowsAsync<InvalidOperationException>(fail)).Should().BeTrue("失败回合回执原异常");
        await AssertThrowsAsync<InvalidOperationException>(drained);
        m.IsFaulted.Should().BeTrue("物化抛=管线 Faulted（防悬干洗白——域报废）");
        m.FaultReason.Should().BeOfType<InvalidOperationException>();

        var act = () => m.OpenSession();
        act.Should().Throw<InvalidOperationException>("Faulted 后禁开会话（域报废重建）");
    }

    [Fact]
    public async Task PrepareThrows_AbortsPrepared_PipelineContinues()
    {
        var p1 = new FakeParticipant();
        var p2 = new FakeParticipant { FailPrepareOnce = seq => true };
        using var m = NewManager(("p1", p1), ("p2", p2));

        using var s = m.OpenSession();
        s.Stage(() => { });
        var fail = s.CommitAsync().AsTask();
        await AssertThrowsAsync<InvalidOperationException>(fail);

        p1.Calls.Should().Contain(c => c.Op == "Abort", "已 Prepare 者（p1）被协调器自动 Abort");
        s.SessionState.Should().Be(SessionState.Faulted, "失败回合回执后会话 Faulted");

        // ★ 管线续跑（§6：Prepare 抛≠管线故障）
        using var s2 = m.OpenSession();
        s2.Stage(() => { });
        (await s2.CommitAsync()).Should().Be(2, "故障批 seq 已消耗，续跑批 seq=2（域内单调）");
    }

    [Fact]
    public async Task MultiDomain_ManagersIsolated_SeqIndependent()
    {
        var pa = new FakeParticipant();
        var pb = new FakeParticipant();
        using var ma = NewManager(("a", pa));
        using var mb = NewManager(("b", pb));

        using var sa = ma.OpenSession();
        using var sb = mb.OpenSession();
        sa.Stage(() => { });
        sb.Stage(() => { });
        var seqA = await sa.CommitAsync();
        var seqB = await sb.CommitAsync();
        seqA.Should().Be(1);
        seqB.Should().Be(1, "多域隔离——管线互不共享，各自 seq 从 1 起");
    }

    [Fact]
    public async Task OpenTxCount_TracksStagedLifecycle()
    {
        var p = new FakeParticipant();
        using var m = NewManager(("p1", p));

        using var s = m.OpenSession();
        m.OpenTxCount.Should().Be(0);
        s.Stage(() => { });
        m.OpenTxCount.Should().Be(1, "首个 Stage=开放事务");
        await s.CommitAsync();
        m.OpenTxCount.Should().Be(0, "回执终态=事务关闭");

        s.Stage(() => { });
        s.Abort();
        m.OpenTxCount.Should().Be(0, "Abort=事务关闭（未决清空）");
    }

    [Fact]
    public async Task InjectedTransactionLog_CoordinatorDrivesCommit()
    {
        // 注入档：ITransactionLog 假件作协调器——CommitBatch=txn.Commit()
        var txn = new FakeTransactionLog();
        var p = new FakeParticipant();
        using var m = SessionManager.Create(txn, ("p1", p));
        m.Initialize();
        m.WaitForReady();

        using var s = m.OpenSession();
        s.Stage(() => { });
        (await s.CommitAsync()).Should().Be(1, "注入档 seq 由 txn 分配");
        txn.CommitCount.Should().Be(1);
        txn.LoadAndReconcileCount.Should().Be(1, "Initialize 走 txn.LoadAndReconcile（注入裁决）");
    }

    /// <summary>轮询等待管线确实进入 Prepare 挂起（PrepareBegin 记录出现）——5s 上限。</summary>
    private static void WaitForPrepareBegin(FakeParticipant p)
    {
        var deadline = Environment.TickCount64 + 5_000;
        while (Environment.TickCount64 < deadline)
        {
            if (p.Calls.Any(c => c.Op == "PrepareBegin")) return;
            Thread.Sleep(10);
        }
        throw new TimeoutException("管线未在 5s 内进入 Prepare 挂起");
    }

    private static async Task<bool> AssertThrowsAsync<TEx>(Task task) where TEx : Exception
    {
        try
        {
            await task;
            return false;
        }
        catch (Exception ex) when (ex is TEx or AggregateException { InnerException: TEx })
        {
            return true;
        }
    }
}

/// <summary>注入档测试假件 ITransactionLog——记录调用次数。</summary>
internal sealed class FakeTransactionLog : ITransactionLog
{
    public int CommitCount;
    public int LoadAndReconcileCount;
    private readonly Dictionary<string, ITransactionParticipant> _participants = new();
    private long _seq;

    public long LastCommittedSeq => Volatile.Read(ref _seq);
    public IReadOnlyCollection<string> ParticipantNames => _participants.Keys;

    public event Action<long>? OnCommitted { add { } remove { } }

    public void Register(string name, ITransactionParticipant participant) => _participants[name] = participant;
    public bool Unregister(string name) => _participants.Remove(name);

    public long Commit()
    {
        long seq = Interlocked.Increment(ref _seq);
        foreach (var p in _participants.Values)
        {
            p.Prepare(seq);
            p.ConfirmCommitted(seq);
        }
        CommitCount++;
        return seq;
    }

    public async ValueTask<long> CommitAsync(CancellationToken ct)
    {
        await Task.Yield();
        return Commit();
    }

    public void Abort()
    {
        foreach (var p in _participants.Values)
            if (p.LastPreparedSeq > p.LastCommittedSeq)
                p.Abort(p.LastPreparedSeq);
    }

    public long Load() => Volatile.Read(ref _seq);
    public async ValueTask<long> LoadAsync(CancellationToken ct) { await Task.Yield(); return Load(); }
    public long LoadAndReconcile() { LoadAndReconcileCount++; return Load(); }
    public async ValueTask<long> LoadAndReconcileAsync(CancellationToken ct)
    {
        await Task.Yield();
        return LoadAndReconcile();
    }

    public void Dispose() { }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
