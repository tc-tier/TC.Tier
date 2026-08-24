using TC.Tier.Runtime.Structures.Log;
using TC.Tier.Runtime.Transactions;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Log;

/// <summary>
/// EntryLog 2PC Abort 测试——回退到上一已确认提交边界（PreparedTailAddress）。
/// <para>★ 覆盖：Abort 截断悬干数据/陈旧 seq 仅复位记账/已提交 no-op/首事务无边界不截断/
///   恢复还原事务水位（悬干可见）后 Abort/TransactionLog Phase-1 失败自动 Abort。</para>
/// <para>★ 跨实例场景用 Managed meta（O(1) 水位 + seq 还原；Disabled 下 seq 不持久化——meta.md §5）。</para>
/// </summary>
public class LogAbortTests
{
    [Fact]
    public void Abort_TruncatesDanglingData_ToLastCommittedBoundary()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(metaKind: MetaPolicyKind.Managed);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);

            // tx1：append A → Prepare(1) → Confirm(1)——建立提交边界
            log.Append("A1"u8.ToArray());
            log.Append("A2"u8.ToArray());
            log.Prepare(1);
            log.ConfirmCommitted(1);
            var boundary = log.TailAddress;

            // tx2：append B → Prepare(2) → Abort(2)——悬干回退
            log.Append("B1"u8.ToArray());
            log.Append("B2"u8.ToArray());
            log.Prepare(2);
            log.Abort(2);

            log.TailAddress.Should().Be(boundary, "Abort 应回退到上一提交边界（tx1 尾）");
            log.LastPreparedSeq.Should().Be(1, "Abort 复位 prepared seq 到 committed");
            log.CommittedOffset.Should().BeLessOrEqualTo(boundary, "CommittedOffset 必须一并夹回（OnAborted）");

            int replayed = 0;
            log.Replay((payload, isMeta, addr) => replayed++);
            replayed.Should().Be(2, "回退后只剩 tx1 的两条 A");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_StaleSeq_OnlyResetsBookkeeping()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(metaKind: MetaPolicyKind.Managed);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            log.Append("A"u8.ToArray());
            log.Prepare(1);
            log.ConfirmCommitted(1);
            log.Append("B"u8.ToArray());
            log.Prepare(2);

            var tailBefore = log.TailAddress;
            log.Abort(999);   // 陈旧 seq——不属本轮窗口

            log.TailAddress.Should().Be(tailBefore, "陈旧 Abort 不截断数据");
            log.LastPreparedSeq.Should().Be(1, "记账仍复位到 committed");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_AlreadyCommittedSeq_IsNoOp()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(metaKind: MetaPolicyKind.Managed);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            log.Append("A"u8.ToArray());
            log.Prepare(1);
            log.ConfirmCommitted(1);
            var tail = log.TailAddress;

            var act = () => log.Abort(1);
            act.Should().NotThrow("已提交 seq 的 Abort 是 no-op");
            log.TailAddress.Should().Be(tail, "已提交数据不可回滚");
            log.LastCommittedSeq.Should().Be(1);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_FirstTransaction_NoBoundary_DoesNotTruncate()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(metaKind: MetaPolicyKind.Managed);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            log.Append("A"u8.ToArray());
            log.Prepare(1);
            var tail = log.TailAddress;

            log.Abort(1);   // 首事务——无既有提交边界

            log.TailAddress.Should().Be(tail, "无安全回退边界（Empty）——首事务 Abort 只复位记账不截断");
            log.LastPreparedSeq.Should().Be(-1);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Recovery_RestoresTxWatermarks_ThenAbortTruncatesDangling()
    {
        // 崩溃模拟：Prepare(2) 后（Confirm 前）直接 Dispose——悬干跨实例可见并可 Abort
        var vol = new TestVolume();
        var settings = TestLogSettingsFactory.EntryOn(vol, "entry", metaKind: MetaPolicyKind.Managed, deleteOnClose: false);
        LogicalAddress boundary;
        using (var log = TestLogSettingsFactory.NewEntryLog(vol, settings))
        {
            log.Append("A"u8.ToArray());
            log.Prepare(1);
            log.ConfirmCommitted(1);
            boundary = log.TailAddress;
            log.Append("B"u8.ToArray());   // tx2 悬干
            log.Prepare(2);
            // 不 Confirm——模拟崩溃
        }

        using (var log2 = TestLogSettingsFactory.NewEntryLog(vol, settings))
        {
            log2.LastPreparedSeq.Should().Be(2, "恢复必须还原 prepared seq（悬干可见）");
            log2.LastCommittedSeq.Should().Be(1, "恢复必须还原 committed seq");
            log2.TailAddress.Should().BeGreaterThan(boundary, "恢复的尾含悬干数据（meta tail）");

            // TransactionLog.LoadAndReconcile 同型裁决：悬干 → Abort(preparedSeq)
            log2.Abort(log2.LastPreparedSeq);

            log2.TailAddress.Should().Be(boundary, "恢复后 Abort 截断悬干到提交边界");
        }

        // 再重开：回退已持久化
        using (var log3 = TestLogSettingsFactory.NewEntryLog(vol, settings))
        {
            log3.TailAddress.Should().Be(boundary, "Abort 后的回退状态跨实例持久");
            int replayed = 0;
            log3.Replay((payload, isMeta, addr) => replayed++);
            replayed.Should().Be(1, "悬干数据跨实例已消失，只剩 tx1 一条");
        }
        vol.Dispose();
    }

    [Fact]
    public void TransactionLog_Phase1Failure_AbortsPreparedLogParticipant()
    {
        var (settings, vol) = TestLogSettingsFactory.CreateEntry(metaKind: MetaPolicyKind.Managed);
        try
        {
            using var log = TestLogSettingsFactory.NewEntryLog(vol, settings);
            // commit-record 引擎：单段 4K + Allocate 预留（TransactionLog 在 seg0@0 覆写 24B 记录——同 TransactionLogTests 形态）
            using var txnEngine = new StorageEngineOptions("txn", 4096, enableSegmentation: false, preallocateFile: true).Builder(vol.Fs).Start();
            txnEngine.Allocate(4096);
            using var txn = new TransactionLog(txnEngine);
            txn.Register("log", log);

            // tx1（seq=1）经协调器提交——建立提交边界（boom 尚未注册，tx1 正常通过）
            log.Append("A"u8.ToArray());
            txn.Commit().Should().Be(1);
            var boundary = log.TailAddress;

            // tx2（seq=2）：注册 boom → log.Prepare 成功 → boom.Prepare 抛 → Phase-1 失败自动 Abort 已 prepare 的 log
            txn.Register("boom", new ThrowingParticipant());
            log.Append("B"u8.ToArray());
            var act = () => txn.Commit();
            act.Should().Throw<InvalidOperationException>();

            log.TailAddress.Should().Be(boundary, "Phase-1 失败自动回滚到提交边界");
            log.LastPreparedSeq.Should().Be(1, "abort 复位 prepared seq");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Phase-1 故障注入参与者：Prepare 必抛。</summary>
    private sealed class ThrowingParticipant : ITransactionParticipant
    {
        public long LastCommittedSeq => -1;
        public long LastPreparedSeq => -1;
        public void Prepare(long seq) => throw new InvalidOperationException("phase-1 boom");
        public ValueTask PrepareAsync(long seq, CancellationToken ct) => throw new InvalidOperationException("phase-1 boom");
        public void ConfirmCommitted(long seq) { }
        public void OnCommitted(long seq, Action callback) { }
        public void Abort(long seq) { }
        public ValueTask AbortAsync(long seq, CancellationToken ct) => ValueTask.CompletedTask;
    }
}
