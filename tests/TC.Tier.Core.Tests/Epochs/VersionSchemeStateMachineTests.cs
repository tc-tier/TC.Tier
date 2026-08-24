using TC.Tier.Core.Epochs;

namespace TC.Tier.Core.Tests.Epochs;

public sealed class VersionSchemeStateMachineTests
{
    // === VersionSchemeState (struct) ===

    [Fact]
    public void State_Rest_PhaseIsZero()
    {
        var s = VersionSchemeState.Make(VersionSchemeState.Rest, 42);
        s.Phase.Should().Be(VersionSchemeState.Rest);
        s.Version.Should().Be(42);
        s.IsIntermediate().Should().BeFalse();
    }

    [Fact]
    public void State_Intermediate_IsIntermediateTrue()
    {
        var s = VersionSchemeState.Make(VersionSchemeState.Rest, 1);
        var inter = VersionSchemeState.Make((byte)(s.Phase | 0x80), s.Version);
        inter.IsIntermediate().Should().BeTrue();
    }

    [Fact]
    public void State_MakeVersionZero_VersionZero()
    {
        var s = VersionSchemeState.Make(VersionSchemeState.Rest, 0);
        s.Version.Should().Be(0);
    }

    [Fact]
    public void State_Equality_SameWord_Equal()
    {
        var a = VersionSchemeState.Make(7, 100);
        var b = VersionSchemeState.Make(7, 100);
        a.Equals(b).Should().BeTrue();
    }

    [Fact]
    public void State_Equality_DifferentPhase_NotEqual()
    {
        var a = VersionSchemeState.Make(0, 100);
        var b = VersionSchemeState.Make(1, 100);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void State_Equality_DifferentVersion_NotEqual()
    {
        var a = VersionSchemeState.Make(0, 100);
        var b = VersionSchemeState.Make(0, 200);
        a.Equals(b).Should().BeFalse();
    }

    [Fact]
    public void State_GetHashCode_SameWord_SameHash()
    {
        var a = VersionSchemeState.Make(3, 42);
        var b = VersionSchemeState.Make(3, 42);
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void State_ToString_ContainsPhaseAndVersion()
    {
        var s = VersionSchemeState.Make(5, 99);
        s.ToString().Should().Be("[5,99]");
    }

    [Fact]
    public void State_Copy_ProducesIdenticalState()
    {
        var original = VersionSchemeState.Make(2, 77);
        var copy = VersionSchemeState.Make(original.Phase, original.Version);
        copy.Should().Be(original);
    }

    // === SimpleVersionSchemeStateMachine ===

    [Fact]
    public void SimpleMachine_WithExplicitVersion_ToVersionCorrect()
    {
        var machine = new SimpleVersionSchemeStateMachine((_, _) => { }, toVersion: 10);
        machine.ToVersion().Should().Be(10);
        machine.ActualToVersion.Should().Be(10);
    }

    [Fact]
    public void SimpleMachine_WithNegativeOne_IncrementsVersion()
    {
        var machine = new SimpleVersionSchemeStateMachine((_, _) => { }, toVersion: -1);
        machine.ToVersion().Should().Be(-1);
    }

    [Fact]
    public void SimpleMachine_GetNextStep_TransitionsToNextVersion()
    {
        var machine = new SimpleVersionSchemeStateMachine((_, _) => { }, toVersion: 5);
        var current = VersionSchemeState.Make(VersionSchemeState.Rest, 3);
        bool canTransition = machine.GetNextStep(current, out var next);
        canTransition.Should().BeTrue();
        next.Phase.Should().Be(VersionSchemeState.Rest);
        next.Version.Should().Be(5); // explicit toVersion
    }

    [Fact]
    public void SimpleMachine_GetNextStep_AutoIncrement()
    {
        var machine = new SimpleVersionSchemeStateMachine((_, _) => { }, toVersion: -1);
        var current = VersionSchemeState.Make(VersionSchemeState.Rest, 8);
        bool canTransition = machine.GetNextStep(current, out var next);
        canTransition.Should().BeTrue();
        next.Version.Should().Be(9); // current + 1
    }

    [Fact]
    public void SimpleMachine_OnEnteringState_InvokesCriticalSection()
    {
        long fromVer = 0, toVer = 0;
        var machine = new SimpleVersionSchemeStateMachine((f, t) => { fromVer = f; toVer = t; }, toVersion: 7);
        var from = VersionSchemeState.Make(VersionSchemeState.Rest, 3);
        var to = VersionSchemeState.Make(VersionSchemeState.Rest, 7);
        machine.OnEnteringState(from, to);
        fromVer.Should().Be(3);
        toVer.Should().Be(7);
    }

    [Fact]
    public void SimpleMachine_AfterEnteringState_DoesNotThrow()
    {
        var machine = new SimpleVersionSchemeStateMachine((_, _) => { }, toVersion: 1);
        var state = VersionSchemeState.Make(VersionSchemeState.Rest, 1);
        Action act = () => machine.AfterEnteringState(state);
        act.Should().NotThrow();
    }

    // === VersionSchemeStateMachine base ===

    [Fact]
    public void BaseMachine_ActualToVersion_CanBeSet()
    {
        var machine = new SimpleVersionSchemeStateMachine((_, _) => { }, toVersion: -1);
        machine.ActualToVersion = 15;
        machine.ActualToVersion.Should().Be(15);
    }
}
