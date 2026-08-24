using FluentAssertions;
using TC.Tier.Runtime.Structures.Snapshot;

namespace TC.Tier.Runtime.Tests.Structures.Snapshot;

/// <summary>
/// SnapshotBase 恢复部分专属契约测试（1:1 对应 src/.../Snapshot/SnapshotBase.Recovery.cs）。
/// 钉住：状态机前置、hints 优先、Failed 可观测、取消重试。
/// Backward 扫尾/悬干裁决的 happy path 见 <see cref="StreamSnapshotTests"/>。
/// </summary>
public class SnapshotBaseRecoveryTests
{
    [Fact]
    public void RecoveryState_NotStarted_BeforeInitialize()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            using var snap = new StreamSnapshot(vol.Fs, settings);
            snap.RecoveryState.Phase.Should().Be(RecoveryPhase.NotStarted);
            snap.IsReady.Should().BeFalse();
        }
        finally { vol.Dispose(); }
    }

    /// <summary>第一级 hints 优先：注入已知写尾，恢复直接采信（跳过扫盘）。</summary>
    [Fact]
    public async Task Hints_WriteAddress_WinsOverScan()
    {
        var vol = new TestVolume();
        var mk = (bool del) => new StreamSnapshotSettings(
            new StorageEngineOptions("test.0", 1L << 24, enableSegmentation: false).WithDeleteOnClose(del));

        LogicalAddress tail;
        await using (var snap1 = new StreamSnapshot(vol.Fs, mk(false)))
        {
            snap1.Initialize();
            snap1.WaitForReady();
            var data = new byte[4096];
            Array.Fill(data, (byte)0x33);
            snap1.Append(data);
            tail = snap1.WriteAddress;
        }

        using var snap2 = new StreamSnapshot(vol.Fs, mk(true));
        snap2.Initialize(new SnapshotRecoveryHints { WriteAddress = tail });
        snap2.WaitForReady();
        snap2.WriteAddress.Should().Be(tail, "hints 采信（与扫盘结果一致性由调用方保证）");

        var dst = new byte[4096];
        snap2.Read(LogicalAddress.Empty, dst);
        dst[0].Should().Be(0x33);
        vol.Dispose();
    }

    /// <summary>恢复失败：Failed + Error 可观测；WaitForReady 重抛。</summary>
    [Fact]
    public void InjectedRecovery_Failure_ObservableAndWaitForReadyRethrows()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            var scripted = new ScriptedRecovery((run, ct) => throw new InvalidOperationException("boom"));
            using var snap = new StreamSnapshot(vol.Fs, settings, recovery: scripted);
            snap.Initialize();

            SpinWait.SpinUntil(() => snap.RecoveryState.Phase == RecoveryPhase.Failed, 5000)
                .Should().BeTrue();
            snap.IsReady.Should().BeFalse();
            snap.RecoveryState.Error.Should().BeOfType<InvalidOperationException>()
                .Which.Message.Should().Be("boom");

            var act = () => snap.WaitForReady();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*恢复任务已失败*")
                .WithInnerException<InvalidOperationException>()
                .Which.Message.Should().Be("boom");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>取消后可重试：同一恢复实例重跑达 Ready。</summary>
    [Fact]
    public void CancelledRecovery_CanRetryInitialize_ReachReady()
    {
        var (settings, vol) = TestSnapshotSettingsFactory.CreateSettings();
        try
        {
            var scripted = new ScriptedRecovery((run, ct) =>
                run == 1 ? new ValueTask(Task.Delay(Timeout.Infinite, ct)) : ValueTask.CompletedTask);
            using var snap = new StreamSnapshot(vol.Fs, settings, recovery: scripted);
            snap.Initialize();
            snap.CancelRecovery();

            SpinWait.SpinUntil(() => snap.RecoveryState.Phase == RecoveryPhase.Failed, 5000)
                .Should().BeTrue("取消并入 Failed");

            var retry = () => snap.Initialize();
            retry.Should().NotThrow();
            SpinWait.SpinUntil(() => snap.IsReady, 30000).Should().BeTrue();
            scripted.Runs.Should().Be(2);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>脚本化恢复——RecoveryBase 模板派生，按跑次执行脚本。</summary>
    private sealed class ScriptedRecovery(Func<int, CancellationToken, ValueTask> script)
        : RecoveryBase<SnapshotRecoveryHints>
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        protected override ValueTask OnRecoveryCoreAsync(SnapshotRecoveryHints hints, CancellationToken ct)
        {
            var run = Interlocked.Increment(ref _runs);
            return script(run, ct);
        }
    }
}
