namespace TC.Tier.Runtime.Transactions;

/// <summary>
/// ★ 会话读 scope（读 op 协议件——session-manager-design.md §3.2）：聚合域内全部
/// <see cref="TC.Tier.Contracts.Transactions.IEpochProtected"/> 参与者的 epoch 读保护，
/// 一次进/出防组合层漏进单结构 scope（零拷贝 span 生命周期护栏）。
/// <para>★ 保护区纪律（IEpochProtected 契约）：区内只做<b>无自保护的零拷贝读</b>
/// （如 Ring <c>GetValueSpan</c> 族）；自带 epoch 进出的 API（Ring 写路径/Index 逐次 Find/Insert）
/// 区内调用=同实例重入（Debug 绊线立即抛）——此类操作在保护区外或用"scope 内单查"形态。</para>
/// <para>暴露 <see cref="Session"/>/<see cref="State"/>——RYW 覆盖层挂点（组合层在 State 上自管
/// staged 命令表/批号映射；Runtime 只定协议不定内容）。</para>
/// </summary>
public readonly ref struct SessionReadScope
{
    private readonly TierSession _session;
    private readonly TC.Tier.Contracts.Transactions.IEpochProtected[] _holders;

    internal SessionReadScope(TierSession session,
        TC.Tier.Contracts.Transactions.IEpochProtected[] holders)
    {
        _session = session;
        _holders = holders;
        foreach (var h in holders) h.EnterEpoch();
    }

    /// <summary>本会话（覆盖层挂点与后续 Stage/Commit 入口）。</summary>
    public TierSession Session => _session;

    /// <summary>会话覆盖层（RYW——组合层自管内容的挂点）。</summary>
    public object? State => _session.State;

    /// <summary>退出保护区——对全部 epoch 读保护持有者逐一 ExitEpoch（与构造期 EnterEpoch 对称）。</summary>
    public void Dispose()
    {
        foreach (var h in _holders) h.ExitEpoch();
    }
}
