namespace TC.Tier.Core.Tests.Epochs;

public class EpochProtectedVersionSchemeTests
{
    [Fact]
    public void New_RestState_Version1()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);
        var state = epvs.CurrentState();
        state.Phase.Should().Be(VersionSchemeState.Rest);
        state.Version.Should().Be(1);
    }

    [Fact]
    public void Enter_Leave_MaintainsRestState()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        var state = epvs.Enter();
        state.Phase.Should().Be(VersionSchemeState.Rest);

        epvs.Leave();
        // After leave, state should still be Rest
        epvs.CurrentState().Phase.Should().Be(VersionSchemeState.Rest);
    }

    [Fact]
    public void Enter_ReturnsNonIntermediateState()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        var state = epvs.Enter();
        state.IsIntermediate().Should().BeFalse();
        epvs.Leave();
    }

    [Fact]
    public void AdvanceVersion_IncrementsVersion()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        epvs.Enter();
        var initialState = epvs.CurrentState();
        epvs.Leave();

        long oldVersion = -1, newVersion = -1;
        bool result = epvs.AdvanceVersionWithCriticalSection(
            (from, to) => { oldVersion = from; newVersion = to; },
            spin: true);

        result.Should().BeTrue();
        oldVersion.Should().Be(initialState.Version);
        newVersion.Should().Be(initialState.Version + 1);
    }

    [Fact]
    public void AdvanceVersion_ExecutesCriticalSection()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        bool executed = false;
        epvs.AdvanceVersionWithCriticalSection((_, _) => executed = true, spin: true);
        executed.Should().BeTrue();
    }

    [Fact]
    public void AdvanceVersion_MultipleAdvances_VersionIncrementsEachTime()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        for (int i = 0; i < 5; i++)
        {
            epvs.AdvanceVersionWithCriticalSection((_, _) => { }, spin: true);
        }

        epvs.CurrentState().Version.Should().Be(6); // initial 1 + 5 advances
    }

    [Fact]
    public void AdvanceVersion_SpecificTargetVersion()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        epvs.AdvanceVersionWithCriticalSection((_, _) => { }, targetVersion: 10, spin: true);
        epvs.CurrentState().Version.Should().Be(10);
    }

    [Fact]
    public void AdvanceVersion_TargetBelowCurrent_ReturnsFalse()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        // Advance to version 10
        epvs.AdvanceVersionWithCriticalSection((_, _) => { }, targetVersion: 10, spin: true);

        // Try to advance to version 5 (below current) — should fail
        bool result = epvs.AdvanceVersionWithCriticalSection((_, _) => { }, targetVersion: 5, spin: true);
        result.Should().BeFalse();
    }

    [Fact]
    public void Refresh_AfterEnter_KeepsRestState()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        epvs.Enter();
        var state = epvs.Refresh();
        state.IsIntermediate().Should().BeFalse();
        state.Phase.Should().Be(VersionSchemeState.Rest);
        epvs.Leave();
    }

    [Fact]
    public void ExecuteStateMachine_WhileProtected_Throws()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        epvs.Enter();
        var act = () => epvs.AdvanceVersionWithCriticalSection((_, _) => { });
        act.Should().Throw<InvalidOperationException>();
        epvs.Leave();
    }

    [Fact]
    public async Task Concurrent_EnterLeaveRefresh_NoCorruption()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        int errors = 0;
        var tasks = new Task[4];

        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    try
                    {
                        epvs.Enter();
                        epvs.Refresh();
                        epvs.Leave();
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        Volatile.Read(ref errors).Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_AdvanceVersion_SomeExecuteCriticalSection()
    {
        var epoch = new LightEpoch();
        var epvs = new EpochProtectedVersionScheme(epoch);

        int criticalSectionCount = 0;
        int okCount = 0;
        var tasks = new Task[4];

        for (int t = 0; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 5; i++)
                {
                    // spin:false to avoid potential deadlock under contention
                    var status = epvs.TryAdvanceVersionWithCriticalSection(
                        (_, _) => Interlocked.Increment(ref criticalSectionCount));
                    if (status == StateMachineExecutionStatus.OK)
                        Interlocked.Increment(ref okCount);
                }
            });
        }

        await Task.WhenAll(tasks);
        // At least some should succeed
        Volatile.Read(ref okCount).Should().BeGreaterThan(0);
        Volatile.Read(ref criticalSectionCount).Should().BeGreaterThan(0);
    }
}
