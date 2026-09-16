using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.Net.Routing;

/// <summary>组提案 apply 终局状态（命令在共识后的确定性结果——全组同判）。</summary>
public enum GroupApplyStatus : byte
{
    /// <summary>成功（产物地址随结果携带）。</summary>
    Ok = 0,
    /// <summary>fencing 拒绝（组 epoch 过期确定性拒绝——重放同判）。</summary>
    StaleEpoch = 1,
    /// <summary>确定性拒绝（组已存在/不存在等——重放同判）。</summary>
    Rejected = 2,
    /// <summary>目标非 leader 提示（转发链路专用——Error 字段携 leader 文本，调用方重路由）。</summary>
    NotLeaderHint = 3,
}

/// <summary>
/// 组提案 apply 结果（提案方经结果表领取——通用封套，产品无关）：
/// 地址 = <see cref="LogicalAddress"/>（D1「地址即版本」——产物/fencing token），
/// <see cref="AppliedIndex"/> = 共识进度（入口 read-your-writes 等待锚）。
/// <para>★ 线格式承载的即这两个产品无关概念；命令语义与失败异常形态（如队列迟交）归各产品层映射。</para>
/// </summary>
/// <param name="Status">终局状态。</param>
/// <param name="Address">产物地址（Ok 携带；非 Ok = default）。</param>
/// <param name="AppliedIndex">命令的日志 index（转发链路回传；非 Ok = 0）。</param>
/// <param name="Error">拒绝原因/leader 提示文本（Ok = null）。</param>
public readonly record struct GroupApplyResult(GroupApplyStatus Status, LogicalAddress Address,
    long AppliedIndex, string? Error)
{
    /// <summary>成功结果。</summary>
    /// <param name="address">产物地址（无地址产物命令 = Empty）。</param>
    /// <param name="appliedIndex">命令日志 index（转发回传用）。</param>
    /// <returns>Ok 结果。</returns>
    public static GroupApplyResult FromOk(LogicalAddress address = default, long appliedIndex = 0)
        => new(GroupApplyStatus.Ok, address, appliedIndex, null);
}
