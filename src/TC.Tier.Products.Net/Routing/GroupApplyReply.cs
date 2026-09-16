using TC.Tier.CodeGen;
using TC.Tier.Contracts.Storage;

namespace TC.Tier.Products.Net.Routing;

/// <summary>
/// 组路由应答封套族（[WireMessage]——转发链路的线格式单点）：
/// <c>[Tag 1B][子类字段声明序]</c>，tag 值 = <see cref="GroupApplyStatus"/>（Ok=0/StaleEpoch=1/
/// Rejected=2/NotLeaderHint=3——封套状态与线 tag 同源）。Ok/NotLeaderHint 字节兼容旧手写封套；
/// Stale/Rejected = [Len 4B][UTF8] 同构；裸 Ok [0,0] 退役为全帧（Beta 未发包零兼容税）。
/// <para>★ 域类型 = <see cref="GroupApplyResult"/>（状态机/提案面）——本族只是传输面，
/// 经 <see cref="GroupApplyResultExtensions"/> 互转。</para>
/// </summary>
[WireMessage]
public abstract record GroupApplyReply
{
    /// <summary>错误文本 blob 防御上限（512B——拒绝原因均为短文本）。</summary>
    public const int MaxErrorBytes = 512;
}

/// <summary>成功回执（HasAddr=false = 无地址产物——Address/Index 随帧携带但不具意义）。</summary>
[WireMessageTag(0x00)]
public sealed record GroupApplyOkReply : GroupApplyReply
{
    /// <summary>是否携带产物地址。</summary>
    [WireMember] public bool HasAddr { get; init; }

    /// <summary>产物地址（D1 版本/fencing token）。</summary>
    [WireMember] public LogicalAddress Address { get; init; }

    /// <summary>命令日志 index（入口 read-your-writes 等待锚）。</summary>
    [WireMember] public long AppliedIndex { get; init; }
}

/// <summary>fencing 拒绝回执（组 epoch 过期确定性拒绝）。</summary>
[WireMessageTag(0x01)]
public sealed record GroupApplyStaleReply : GroupApplyReply
{
    /// <summary>拒绝原因文本（UTF8）。</summary>
    [WireMember(MaxCount = GroupApplyReply.MaxErrorBytes)]
    public ReadOnlyMemory<byte> Error { get; init; }
}

/// <summary>确定性拒绝回执（组已存在/不存在等）。</summary>
[WireMessageTag(0x02)]
public sealed record GroupApplyRejectedReply : GroupApplyReply
{
    /// <summary>拒绝原因文本（UTF8）。</summary>
    [WireMember(MaxCount = GroupApplyReply.MaxErrorBytes)]
    public ReadOnlyMemory<byte> Error { get; init; }
}

/// <summary>目标非 leader 提示回执（调用方转 NotLeaderException 重路由）。</summary>
[WireMessageTag(0x03)]
public sealed record GroupApplyLeaderReply : GroupApplyReply
{
    /// <summary>当前 leader（16B）。</summary>
    [WireMember] public NodeId Leader { get; init; }
}

/// <summary>域结果 ↔ 线封套互转（<see cref="GroupApplyResult"/> 的传输面投影）。</summary>
public static class GroupApplyResultExtensions
{
    /// <summary>域结果 → 线封套。</summary>
    /// <param name="result">apply 终局结果。</param>
    /// <returns>线封套实例（tag = 状态值）。</returns>
    public static GroupApplyReply ToReply(this GroupApplyResult result) => result.Status switch
    {
        GroupApplyStatus.Ok => new GroupApplyOkReply
        {
            HasAddr = result.Address.IsValid,
            Address = result.Address,
            AppliedIndex = result.AppliedIndex,
        },
        GroupApplyStatus.StaleEpoch => new GroupApplyStaleReply
        {
            Error = System.Text.Encoding.UTF8.GetBytes(result.Error ?? "stale epoch"),
        },
        GroupApplyStatus.Rejected => new GroupApplyRejectedReply
        {
            Error = System.Text.Encoding.UTF8.GetBytes(result.Error ?? "rejected"),
        },
        GroupApplyStatus.NotLeaderHint => new GroupApplyLeaderReply
        {
            Leader = result.Error is { } hex && NodeId.TryParse(hex, out var leader) ? leader : NodeId.Empty,
        },
        _ => throw new InvalidDataException($"封套状态不可编码：{result.Status}"),
    };

    /// <summary>线封套 → 域结果。</summary>
    /// <param name="reply">线封套实例。</param>
    /// <returns>apply 终局结果。</returns>
    public static GroupApplyResult ToResult(this GroupApplyReply reply) => reply switch
    {
        GroupApplyOkReply ok => ok.HasAddr
            ? GroupApplyResult.FromOk(ok.Address, ok.AppliedIndex)
            : GroupApplyResult.FromOk(appliedIndex: ok.AppliedIndex),
        GroupApplyStaleReply stale => new(GroupApplyStatus.StaleEpoch, default, 0,
            System.Text.Encoding.UTF8.GetString(stale.Error.Span)),
        GroupApplyRejectedReply rejected => new(GroupApplyStatus.Rejected, default, 0,
            System.Text.Encoding.UTF8.GetString(rejected.Error.Span)),
        GroupApplyLeaderReply leader => new(GroupApplyStatus.NotLeaderHint, default, 0,
            leader.Leader.ToString()),
        _ => throw new InvalidDataException("封套族未知子类。"),
    };
}
