using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Tests.Structures.Log;
using TC.Tier.Runtime.Tests.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures;

/// <summary>
/// TransactionLog 单元测试 + 集成测试 — 跨数据结构 2PC 原子提交。
/// <para>★ 已迁移当前 API：BlittableRing&lt;long&gt;（泛型 key 直写）/ EntryLog（TestVolume + MainEngine 选项）/
///   commit-record 引擎经 options.Builder(vol.Fs).Start()。</para>
/// </summary>
public class TransactionLogTests
{
    [Fact]
    public void Commit_AdvancesSeq_AndLoadReturnsCorrectSeq()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        txLog.LastCommittedSeq.Should().Be(0);

        txLog.Commit();
        txLog.LastCommittedSeq.Should().Be(1);

        txLog.Commit();
        txLog.LastCommittedSeq.Should().Be(2);

        ctx.ReloadCommittedSeq().Should().Be(2);
    }

    [Fact]
    public void Load_EmptyEngine_ReturnsZero()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        txLog.Load().Should().Be(0);
    }

    [Fact]
    public void Load_AfterCommit_ReturnsCommittedSeq()
    {
        using var ctx = new TransactionLogContext();

        var txLog1 = ctx.CreateTransactionLog();
        txLog1.Commit();
        txLog1.Commit();
        txLog1.Dispose();

        var txLog2 = ctx.CreateTransactionLog();
        txLog2.Load().Should().Be(2);
    }

    [Fact]
    public async Task CommitAsync_AdvancesSeq_AndLoadAsyncReturnsCorrectSeq()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        txLog.LastCommittedSeq.Should().Be(0);

        await txLog.CommitAsync(CancellationToken.None);
        txLog.LastCommittedSeq.Should().Be(1);

        long loaded = ctx.ReloadCommittedSeq();
        loaded.Should().Be(1);
    }

    [Fact]
    public void Commit_IncrementsMonotonically_AcrossMultipleCalls()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        for (int i = 1; i <= 10; i++)
        {
            txLog.Commit();
            txLog.LastCommittedSeq.Should().Be(i);
        }
    }

    [Fact]
    public void OnCommitted_Event_FiresAfterCommit()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        long? committedSeq = null;
        txLog.OnCommitted += seq => committedSeq = seq;

        txLog.Commit();
        committedSeq.Should().Be(1);

        txLog.Commit();
        committedSeq.Should().Be(2);
    }

    [Fact]
    public void Register_Participant_ConfirmCommittedCalledAfterCommit()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);

        ring.LastCommittedSeq.Should().Be(-1);

        txLog.Commit();
        ring.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public void Register_MultipleParticipants_AllConfirmCommitted()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();
        using var entryLog = ctx.CreateEntryLog(disableAutoCommit: true);

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);
        txLog.Register("entryLog", entryLog);

        ring.LastCommittedSeq.Should().Be(-1);
        entryLog.LastCommittedSeq.Should().Be(-1);

        txLog.Commit();
        ring.LastCommittedSeq.Should().Be(1);
        entryLog.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public void Register_MultipleParticipants_MultiCommit_MonotonicAdvance()
    {
        using var ctx = new TransactionLogContext();
        using var ring1 = ctx.CreateBlittableRing("ring1");
        using var ring2 = ctx.CreateBlittableRing("ring2");

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring1", ring1);
        txLog.Register("ring2", ring2);

        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(1);
        ring2.LastCommittedSeq.Should().Be(1);

        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(2);
        ring2.LastCommittedSeq.Should().Be(2);

        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(3);
        ring2.LastCommittedSeq.Should().Be(3);
    }

    [Fact]
    public void Register_LateParticipant_OnlyAdvancesAfterRegistration()
    {
        using var ctx = new TransactionLogContext();
        using var ring1 = ctx.CreateBlittableRing("ring1");
        using var ring2 = ctx.CreateBlittableRing("ring2");

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring1", ring1);

        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(1);
        ring2.LastCommittedSeq.Should().Be(-1, "late-registered participant should not see past commits");

        txLog.Register("ring2", ring2);
        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(2);
        ring2.LastCommittedSeq.Should().Be(2, "late participant sees only commits after registration");
    }

    [Fact]
    public void ConfirmCommitted_OldSeq_IsIgnored()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);

        txLog.Commit();
        ring.LastCommittedSeq.Should().Be(1);

        ring.ConfirmCommitted(0);
        ring.LastCommittedSeq.Should().Be(1, "old seq must be ignored");
    }

    [Fact]
    public void Participant_OnCommitted_CallbackFiresAfterTransactionLogCommit()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);

        bool ringCallbackFired = false;
        ring.OnCommitted(2, () => ringCallbackFired = true);

        txLog.Commit();
        ringCallbackFired.Should().BeFalse("callback for seq 2 should not fire at seq 1");

        txLog.Commit();
        ringCallbackFired.Should().BeTrue("callback for seq 2 should fire when seq 2 is committed");
    }

    [Fact]
    public void Participant_OnCommitted_FiresImmediately_IfAlreadyCommitted()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);
        txLog.Commit();
        txLog.Commit();

        bool callbackCalled = false;
        ring.OnCommitted(1, () => callbackCalled = true);
        callbackCalled.Should().BeTrue("seq 1 already committed, callback should fire immediately");
    }

    [Fact]
    public async Task Full2PC_Flow_PrepareThenCommit_ParticipantsDurable()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();
        using var entryLog = ctx.CreateEntryLog(disableAutoCommit: true);

        ring.Write(1L, new byte[] { 10, 20, 30 });
        entryLog.Append(new byte[] { 1, 2, 3, 4 });

        ring.Prepare(1);
        entryLog.Prepare(1);

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);
        txLog.Register("entryLog", entryLog);

        await txLog.CommitAsync(CancellationToken.None);

        ring.LastCommittedSeq.Should().Be(1);
        entryLog.LastCommittedSeq.Should().Be(1);
        txLog.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public async Task Full2PC_Flow_PrepareAsyncThenCommitAsync_ParticipantsDurable()
    {
        using var ctx = new TransactionLogContext();
        using var ring = ctx.CreateBlittableRing();
        using var entryLog = ctx.CreateEntryLog(disableAutoCommit: true);

        ring.Write(2L, new byte[] { 42 });
        entryLog.Append(new byte[] { 9, 8, 7 });

        await ring.PrepareAsync(1, CancellationToken.None);
        await entryLog.PrepareAsync(1, CancellationToken.None);

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring", ring);
        txLog.Register("entryLog", entryLog);

        await txLog.CommitAsync(CancellationToken.None);

        ring.LastCommittedSeq.Should().Be(1);
        entryLog.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public void Load_AfterDispose_ThenReopen_RecoversSeq()
    {
        using var ctx = new TransactionLogContext();

        long committed;
        {
            using var txLog = ctx.CreateTransactionLog();
            txLog.Commit();
            txLog.Commit();
            txLog.Commit();
            committed = txLog.LastCommittedSeq;
        }

        committed.Should().Be(3);

        ctx.ReloadCommittedSeq().Should().Be(3);
    }

    [Fact]
    public void Dispose_ReleasesResources_Safely()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();
        txLog.Commit();

        var act = () => txLog.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void DoubleDispose_DoesNotThrow()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();
        txLog.Dispose();

        var act = () => txLog.Dispose();
        act.Should().NotThrow("double-dispose should be safe");
    }

    [Fact]
    public void TransactionLog_Commit_WithThreeParticipants_AllReceiveSameSeq()
    {
        using var ctx = new TransactionLogContext();
        using var ring1 = ctx.CreateBlittableRing("ring1");
        using var ring2 = ctx.CreateBlittableRing("ring2");
        using var entryLog = ctx.CreateEntryLog(disableAutoCommit: true);

        var txLog = ctx.CreateTransactionLog();
        txLog.Register("ring1", ring1);
        txLog.Register("ring2", ring2);
        txLog.Register("entryLog", entryLog);

        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(1);
        ring2.LastCommittedSeq.Should().Be(1);
        entryLog.LastCommittedSeq.Should().Be(1);

        txLog.Commit();
        ring1.LastCommittedSeq.Should().Be(2);
        ring2.LastCommittedSeq.Should().Be(2);
        entryLog.LastCommittedSeq.Should().Be(2);
    }

    [Fact]
    public void Commit_WithoutRegisteredParticipants_Succeeds()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        txLog.Commit();
        txLog.LastCommittedSeq.Should().Be(1);
    }

    [Fact]
    public void Register_NullParticipant_Throws()
    {
        using var ctx = new TransactionLogContext();
        var txLog = ctx.CreateTransactionLog();

        var act = () => txLog.Register("null", null!);
        act.Should().Throw<ArgumentNullException>();
    }
}

/// <summary>
/// 为 TransactionLog 测试提供共享引擎和参与者的上下文管理。
/// <para>★ 单卷共栖：commit-record 引擎与参与者结构（Ring/EntryLog）各占独立引擎名，互不干扰。</para>
/// </summary>
internal sealed class TransactionLogContext : IDisposable
{
    private readonly TestVolume _vol = new();
    private readonly IStorageEngine _txEngine;
    private readonly List<IDisposable> _ownedDisposables = new();

    public TransactionLogContext()
    {
        // commit-record 引擎：单段 4K 固定覆盖写（TransactionLog 在 seg0@0 写 24B 记录，Allocate 预留空间）
        _txEngine = new StorageEngineOptions("tx-engine", 4096, enableSegmentation: false, preallocateFile: true, deleteOnClose: true).Builder(_vol.Fs).Start();
        _txEngine.WaitForReady();
        _txEngine.Allocate(4096);
    }

    public TransactionLog CreateTransactionLog()
    {
        var txLog = new TransactionLog(_txEngine);
        _ownedDisposables.Add(txLog);
        return txLog;
    }

    public long ReloadCommittedSeq()
    {
        return _txEngine.Read(LogicalAddress.Empty, new byte[64]) switch
        {
            <= 0 => 0,
            > 0 => new TransactionLog(_txEngine).Load()
        };
    }

    public BlittableRing<long> CreateBlittableRing(string engineName = "ring")
    {
        var settings = TestRingSettingsFactory.On(_vol, engineName);
        var ring = TestRingSettingsFactory.NewRing<long>(_vol, settings);
        _ownedDisposables.Add(ring);
        return ring;
    }

    public EntryLog CreateEntryLog(bool disableAutoCommit = true, string engineName = "entry")
    {
        // disableAutoCommit：三维度阈值全部禁用（2PC 由 TransactionLog 驱动，不自动提交）
        var settings = new EntryLogSettings(
            new StorageEngineOptions(engineName, 8L << 20, enableSegmentation: true, preallocateFile: false, deleteOnClose: true))
        {
            CommitInterval = disableAutoCommit ? TimeSpan.FromMilliseconds(-1) : TimeSpan.FromMilliseconds(10),
            MaxUnflushedBytes = disableAutoCommit ? long.MaxValue : AlignmentConst.Alignment64K,
            MaxUnflushedCount = disableAutoCommit ? int.MaxValue : 1000,
        };
        var log = new EntryLog(_vol.Fs, settings);
        log.Initialize();
        log.WaitForReady();
        _ownedDisposables.Add(log);
        return log;
    }

    public void Dispose()
    {
        for (int i = _ownedDisposables.Count - 1; i >= 0; i--)
        {
            try { _ownedDisposables[i].Dispose(); }
            catch { /* ignore */ }
        }
        _ownedDisposables.Clear();
        try { _txEngine.Dispose(); }
        catch { /* ignore */ }
        _vol.Dispose();
    }
}
