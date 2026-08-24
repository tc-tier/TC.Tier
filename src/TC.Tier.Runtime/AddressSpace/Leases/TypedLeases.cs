using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.AddressSpace.Leases;

// ═══ 类型化 lease 协议（2026-08-16 复拆）═══
// 每个操作类型一个独立 lease 协议，共用 LeaseBase 机械（占住/doneMask/Dispose）。
// 类型即协议——终态收敛由各类型表达，不做 kind 字节路由。
// ★ source/logger 经基类 protected 属性取用（Reset 可更新）——子类不自持 readonly 字段：
//   池化 Reset 无法更新 readonly，且基类构造期 RegisterLease(this) 会把子类半成品发布给诊断线程。

/// <summary>
/// Append lease 协议——追加写（占位 + 推进游标）。终态收敛：推 CommittedTail 到 End。
/// <para>★ 物理门：**有**——Append 的全部 chunk（IO 与提交）都必须等物理段 Empty→Ready。</para>
/// </summary>
public sealed class AppendLease : LeaseBase
{
    internal AppendLease(ILeaseSource source, LogicalAddress start, LogicalAddress end, ILogger? logger = null)
        : base(source, start, end, ExtentStateCode.AppendLeased, logger)
    {
    }

    /// <inheritdoc/>
    protected internal override void FinalizeTerminalCore() => Source.AppendFinalize(End);

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal override void EnterChunkPhysicalGate(ExtentLease ext)
        => Source.WaitSegmentReady(ext.OwnerSegId, LeaseLogger);
}

/// <summary>
/// Write lease 协议——随机覆写（地址已知，目标区间 ≤ CommittedTail）。整体级无段表副作用。
/// <para>★ 物理门：**有**（显式声明——目标段按定义已 Ready，门走快路径零开销，不靠隐式前提）。</para>
/// </summary>
public sealed class WriteLease : LeaseBase
{
    internal WriteLease(ILeaseSource source, LogicalAddress start, LogicalAddress end, ILogger? logger = null)
        : base(source, start, end, ExtentStateCode.WriteLeased, logger)
    {
    }

    /// <inheritdoc/>
    protected internal override void FinalizeTerminalCore()
    {
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected internal override void EnterChunkPhysicalGate(ExtentLease ext)
        => Source.WaitSegmentReady(ext.OwnerSegId, LeaseLogger);
}

/// <summary>
/// Reclaim lease 协议——中间区间打洞回收（段表不变、段水位不变，只改区间状态）。整体级无段表副作用。
/// </summary>
public sealed class ReclaimLease : LeaseBase
{
    internal ReclaimLease(ILeaseSource source, LogicalAddress start, LogicalAddress end, ILogger? logger = null)
        : base(source, start, end, ExtentStateCode.ReclaimLeased, logger)
    {
    }

    /// <inheritdoc/>
    protected internal override void FinalizeTerminalCore()
    {
    }
}

/// <summary>
/// ReclaimHead lease 协议——头部回收（跨段删 + ShrinkHead 推 MinAddress）。终态收敛：推 MinAddress 到 End。
/// </summary>
public sealed class ReclaimHeadLease : LeaseBase
{
    internal ReclaimHeadLease(ILeaseSource source, LogicalAddress start, LogicalAddress end, ILogger? logger = null)
        : base(source, start, end, ExtentStateCode.ReclaimLeased, logger)
    {
    }

    /// <inheritdoc/>
    protected internal override void FinalizeTerminalCore() => Source.ReclaimHeadFinalize(End);
}

/// <summary>
/// ReclaimTail lease 协议——尾部截断（ShrinkTail 退双尾水位）。终态收敛：双尾回退到 Start。
/// </summary>
public sealed class ReclaimTailLease : LeaseBase
{
    internal ReclaimTailLease(ILeaseSource source, LogicalAddress start, LogicalAddress end, ILogger? logger = null)
        : base(source, start, end, ExtentStateCode.ReclaimLeased, logger)
    {
    }

    /// <inheritdoc/>
    protected internal override void FinalizeTerminalCore() => Source.ReclaimTailFinalize(Start);
}
