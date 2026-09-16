namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>故障注入矩阵——InProcess 主场（spec-12 §7 同构基准）。</summary>
public sealed class FaultMatrixTests : FaultMatrixScenarios
{
    internal override string WireLabel => "InProcess";

    internal override Task<IAdversarialRig> CreateRigAsync(int n, RaftOptions? raft = null)
        => InProcessAdversarialRig.CreateAsync(n, raft);
}
