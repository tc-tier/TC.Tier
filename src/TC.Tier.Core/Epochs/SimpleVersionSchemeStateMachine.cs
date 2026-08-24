using System.Diagnostics;

namespace TC.Tier.Core.Epochs;

internal class SimpleVersionSchemeStateMachine(Action<long, long> criticalSection, long toVersion = -1)
    : VersionSchemeStateMachine(toVersion)
{
    public override bool GetNextStep(VersionSchemeState currentState, out VersionSchemeState nextState)
    {
        Debug.Assert(currentState.Phase == VersionSchemeState.Rest);
        nextState = VersionSchemeState.Make(VersionSchemeState.Rest, ToVersion() == -1 ? currentState.Version + 1 : ToVersion());
        return true;
    }

    public override void OnEnteringState(VersionSchemeState fromState, VersionSchemeState toState)
    {
        Debug.Assert(fromState.Phase == VersionSchemeState.Rest && toState.Phase == VersionSchemeState.Rest);
        criticalSection(fromState.Version, toState.Version);
    }

    public override void AfterEnteringState(VersionSchemeState state) { }
}