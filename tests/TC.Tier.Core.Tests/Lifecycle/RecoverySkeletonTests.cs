namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// Shared IRecovery 骨架验证——生命周期契约 + 状态/进度。
/// </summary>
public sealed class RecoverySkeletonTests
{
    private sealed class FakeRecovery : IRecovery
    {
        public RecoveryState State { get; set; } = new() { Phase = RecoveryPhase.NotStarted };
        public int StartCalls, CompleteCalls;
        public event Action<RecoveryProgress>? RecoveryProgressChanged;

        public RecoveryState RecoveryState => State;
        public void OnRecoveryStart() => StartCalls++;
        public void OnRecoveryComplete() => CompleteCalls++;
        public bool IsReady { get; }
        public void MarkReady()
        {

        }

        public void Reset()
        {

        }

        public void RaiseProgress(RecoveryProgress p) => RecoveryProgressChanged?.Invoke(p);
    }

    [Fact]
    public void RecoveryState_Initially_NotStarted()
    {
        var r = new FakeRecovery();
        r.RecoveryState.Phase.Should().Be(RecoveryPhase.NotStarted);
    }

    [Fact]
    public void OnRecoveryStart_Complete_AreCallable()
    {
        var r = new FakeRecovery();
        r.OnRecoveryStart();
        r.OnRecoveryComplete();
        r.StartCalls.Should().Be(1);
        r.CompleteCalls.Should().Be(1);
    }

    [Fact]
    public void ProgressEvent_Fires()
    {
        var r = new FakeRecovery();
        int fired = 0;
        r.RecoveryProgressChanged += _ => fired++;
        r.RaiseProgress(new RecoveryProgress { Phase = RecoveryPhase.Recovering, Percent = 50 });
        fired.Should().Be(1);
    }

    [Fact]
    public void RecoveryState_IsImmutableSnapshot()
    {
        var r = new FakeRecovery { State = new RecoveryState { Phase = RecoveryPhase.Recovering, Percent = 30 } };
        var snapshot = r.RecoveryState;
        // record struct——修改 State 不影响已取快照
        r.State = r.State with { Percent = 60 };
        snapshot.Percent.Should().Be(30);
    }
}
