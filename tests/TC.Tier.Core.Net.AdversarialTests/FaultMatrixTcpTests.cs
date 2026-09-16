namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>故障注入矩阵——TCP 复跑（spec-12 §12 同构门的注入矩阵面——同一场景同一断言换介质）。</summary>
public sealed class FaultMatrixTcpTests : FaultMatrixScenarios
{
    internal override string WireLabel => "TCP";

    internal override Task<IAdversarialRig> CreateRigAsync(int n, RaftOptions? raft = null)
        => TcpAdversarialRig.CreateAsync(n, raft);
}
