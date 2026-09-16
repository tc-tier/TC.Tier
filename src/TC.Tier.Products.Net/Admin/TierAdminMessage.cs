using TC.Tier.CodeGen;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Products.Net.Admin;

/// <summary>
/// Tier 管理面消息族（二期-D1 NETGAP-005——最小 admin 协议·产品面）：状态导出/健康探针的
/// 远程查询（请求回调形态——单请求 → 关联应答；CorrId 由传输承载）。
/// <para>★ 协议域：0x70（注册区 0x60-0xAF——Products.Net 自管号，spec-12 §3.5）。</para>
/// <para>★ 线格式（生成物：[Tag 1B][字段声明序]，小端）：</para>
/// <code>
/// TierAdminStateReq   [Tag]
/// TierAdminStateResp  [Tag][Node][Role 1B][Term 8B][HasLeader 1B][Leader][Commit 8B][Applied 8B]
///                     [LastLog 8B][IsVoter 1B][Healthy 1B][Ready 1B][MemberCount 4B][Ids][Match]
/// TierAdminHealthReq  [Tag]
/// TierAdminHealthResp [Tag][Node][Healthy 1B][Ready 1B][Commit 8B]
/// </code>
/// </summary>
[WireMessage]
public abstract record TierAdminMessage
{
    /// <summary>成员表防御上限（畸形报文拦截界）。</summary>
    public const int MaxMemberCount = 256;
}

/// <summary>状态导出查询（Role/Term/Leader/水位/成员复制进度——GetStateSnapshot 全量）。</summary>
[WireMessageTag(0x01)]
public sealed record TierAdminStateReq : TierAdminMessage;

/// <summary>状态导出应答。</summary>
[WireMessageTag(0x02)]
public sealed record TierAdminStateResp : TierAdminMessage
{
    /// <summary>应答节点。</summary>
    public required NodeId Node { get; init; }

    /// <summary>角色（<see cref="RaftRole"/> 原始字节）。</summary>
    public required byte Role { get; init; }

    /// <summary>当前任期。</summary>
    public required long Term { get; init; }

    /// <summary>是否已知 leader。</summary>
    public required bool HasLeader { get; init; }

    /// <summary>当前已知 leader（HasLeader=false 时为 Empty）。</summary>
    public required NodeId LeaderId { get; init; }

    /// <summary>提交水位。</summary>
    public required long CommitIndex { get; init; }

    /// <summary>应用水位。</summary>
    public required long AppliedIndex { get; init; }

    /// <summary>日志尾。</summary>
    public required long LastLogIndex { get; init; }

    /// <summary>是否投票成员。</summary>
    public required bool IsVoter { get; init; }

    /// <summary>healthz——循环存活且已启动。</summary>
    public required bool Healthy { get; init; }

    /// <summary>readyz——已知 leader（含自身在位）。</summary>
    public required bool Ready { get; init; }

    /// <summary>其他成员 ID 表（leader 视角复制进度——非 leader = 空）。</summary>
    [WireMember(MaxCount = TierAdminMessage.MaxMemberCount)]
    public required NodeId[] MemberIds { get; init; }

    /// <summary>各成员 matchIndex（与 MemberIds 等长对齐）。</summary>
    [WireMember(MaxCount = TierAdminMessage.MaxMemberCount)]
    public required long[] MatchIndex { get; init; }

    /// <summary>组标识原始值（二期-I6——多组装配区分；单组 = 0）。</summary>
    public required ulong GroupIdValue { get; init; }
}

/// <summary>健康探针查询（healthz/readyz 语义——轻量）。</summary>
[WireMessageTag(0x03)]
public sealed record TierAdminHealthReq : TierAdminMessage;

/// <summary>健康探针应答。</summary>
[WireMessageTag(0x04)]
public sealed record TierAdminHealthResp : TierAdminMessage
{
    /// <summary>应答节点。</summary>
    public required NodeId Node { get; init; }

    /// <summary>healthz——循环存活且已启动。</summary>
    public required bool Healthy { get; init; }

    /// <summary>readyz——已知 leader（含自身在位）。</summary>
    public required bool Ready { get; init; }

    /// <summary>提交水位。</summary>
    public required long CommitIndex { get; init; }
}
