using System.Buffers.Binary;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 集群成员——(节点 ID, 端点, 角色) 映射（spec-04 §1 配置模型：节点端点有序列表 + ★ NodeId↔EndPoint 映射
/// 由配置承载——传输按 NodeId 路由，端点为装配层解析目标）。
/// </summary>
/// <param name="Id">成员节点 ID。</param>
/// <param name="EndPoint">端点描述（进程内传输 = 空串；网络传输 = host:port——装配层消费）。</param>
/// <param name="Role">成员角色（缺省 Voter；learner 不参与选举投票/不计入多数派——spec-12 §6 件 B）。</param>
public readonly record struct ClusterMember(NodeId Id, string EndPoint, ClusterMemberRole Role = ClusterMemberRole.Voter)
{
    /// <summary>端点描述（进程内传输 = 空串；网络传输 = host:port——装配层消费）。</summary>
    public string EndPoint { get; init; } = EndPoint;

    /// <summary>成员角色（缺省 Voter；learner 不参与选举投票/不计入多数派——spec-12 §6 件 B）。</summary>
    public ClusterMemberRole Role { get; init; } = Role;
}

/// <summary>
/// 集群配置（spec-04 §1 定案）——节点有序列表；版本 = 承载它的日志 index；meta 槽不存配置
/// （恢复 = 重放日志/快照帧流至配置条目——配置与状态机同构，日志即状态机）。
/// <para>★ 稳定序列化（<see cref="Serialize"/>——配置条目 payload 格式）：配置变更经日志复制
/// 跨节点传播、重启经快照/重放恢复——格式必须稳定。</para>
/// </summary>
public sealed class ClusterConfig
{
    private readonly ClusterMember[] _members;

    /// <summary>配置条目 payload 格式版本（v2 = 成员尾增 Role 1B——件 B learner；v1 全 voter 可读）。</summary>
    public const ushort FormatVersion = 2;

    // v1 兼容读（持久化日志/快照中的既有配置条目）
    private const ushort FormatVersionV1 = 1;

    /// <summary>构造（副本语义——成员列表不可变）。</summary>
    /// <param name="members">有序成员列表（顺序即确定性序列化顺序）。</param>
    public ClusterConfig(IEnumerable<ClusterMember> members)
    {
        ArgumentNullException.ThrowIfNull(members);
        _members = [.. members];
        ArgumentOutOfRangeException.ThrowIfZero(_members.Length);
        var voters = 0;
        foreach (var m in _members)
            if (m.Role is ClusterMemberRole.Voter or ClusterMemberRole.Witness) voters++;   // F2：witness 计入
        VoterCount = voters;
    }

    /// <summary>构造（单成员快捷路径——Standalone 装配）。</summary>
    public ClusterConfig(NodeId single) : this([new ClusterMember(single, "")]) { }

    /// <summary>成员只读视图。</summary>
    public IReadOnlyList<ClusterMember> Members => _members;

    /// <summary>集群大小 N（含 learner）。</summary>
    public int Count => _members.Length;

    /// <summary>投票成员数（learner 不计——选举/提交的多数派口径）。</summary>
    public int VoterCount { get; private init; }

    /// <summary>多数派阈值（⌊VoterCount/2⌋+1——Quorum 组件判定；learner 不占多数派）。</summary>
    public int MajorityThreshold => Quorum.Majority(VoterCount);

    /// <summary>指定节点是否投票成员（witness = true——二期-F2 计入选主/提交多数派；
    /// learner = false）。</summary>
    /// <param name="id">查询节点 ID。</param>
    /// <returns>true = voter/witness（参与选举投票/计入多数派）；false = learner 或不在配置中。</returns>
    public bool IsVoter(NodeId id)
    {
        foreach (var m in _members)
            if (m.Id == id) return m.Role is ClusterMemberRole.Voter or ClusterMemberRole.Witness;
        return false;
    }

    /// <summary>指定节点是否见证者（二期-F2——投票但不存全量数据、不自荐）。</summary>
    /// <param name="id">查询节点 ID。</param>
    /// <returns>true = witness 角色（投票计入多数派但不存全量数据/不自荐）；false = 其他角色或不在配置中。</returns>
    public bool IsWitness(NodeId id)
    {
        foreach (var m in _members)
            if (m.Id == id) return m.Role == ClusterMemberRole.Witness;
        return false;
    }

    /// <summary>指定节点是否全量投票成员（可承接 leader 职责——witness 不自荐/不可转让）。</summary>
    /// <param name="id">查询节点 ID。</param>
    /// <returns>true = 全量 voter（可自荐当选/承接 leader 转让）；false = witness/learner 或不在配置中。</returns>
    public bool IsFullVoter(NodeId id)
    {
        foreach (var m in _members)
            if (m.Id == id) return m.Role == ClusterMemberRole.Voter;
        return false;
    }

    /// <summary>包含指定节点。</summary>
    /// <param name="id">查询节点 ID。</param>
    /// <returns>true = 节点在配置中（voter 或 learner）。</returns>
    public bool Contains(NodeId id) => _members.Any(m => m.Id == id);

    /// <summary>成员端点（NodeId↔EndPoint 映射查询）。</summary>
    /// <param name="id">查询节点 ID。</param>
    /// <returns>该成员的端点字符串（进程内传输 = 空串；网络传输 = host:port）。</returns>
    /// <exception cref="KeyNotFoundException">节点 <paramref name="id"/> 不在活动配置中。</exception>
    public string GetEndPoint(NodeId id)
    {
        foreach (var m in _members)
            if (m.Id == id) return m.EndPoint;
        throw new KeyNotFoundException($"节点 {id} 不在活动配置中。");
    }

    /// <summary>加成员（single-server 变更第一步——C_old ∪ {new}）。</summary>
    /// <param name="id">新成员节点 ID。</param>
    /// <param name="endPoint">端点（进程内传输 = 空串；网络 = host:port——装配层消费）。</param>
    /// <returns>新配置（已含新成员，角色为 voter）；若已存在则返回原配置。</returns>
    public ClusterConfig Add(NodeId id, string endPoint = "")
    {
        if (Contains(id)) return this;
        return new ClusterConfig(_members.Concat([new ClusterMember(id, endPoint)]));
    }

    /// <summary>加 learner（观察副本/引导形态——收日志复制可读，不投票不计多数派；已存在 = 原样返回）。</summary>
    /// <param name="id">新 learner 节点 ID。</param>
    /// <param name="endPoint">端点（进程内传输 = 空串；网络 = host:port——装配层消费）。</param>
    /// <returns>新配置（已含新成员，角色为 learner）；若已存在则返回原配置。</returns>
    public ClusterConfig AddLearner(NodeId id, string endPoint = "")
    {
        if (Contains(id)) return this;
        return new ClusterConfig(_members.Concat([new ClusterMember(id, endPoint, ClusterMemberRole.Learner)]));
    }

    /// <summary>晋级 voter（learner → voter——引导流追平后转正；缺席或已是 voter = 原样返回）。</summary>
    /// <param name="id">要晋级的节点 ID。</param>
    /// <returns>新配置（该成员角色切换为 voter）；缺席或已是 voter 则返回原配置。</returns>
    public ClusterConfig Promote(NodeId id)
    {
        if (!Contains(id) || IsVoter(id)) return this;
        return new ClusterConfig(_members.Select(m => m.Id == id ? m with { Role = ClusterMemberRole.Voter } : m));
    }

    /// <summary>移除成员（single-server 变更第二步——C_new）。</summary>
    /// <param name="id">要移除的节点 ID。</param>
    /// <returns>新配置（不再含该成员）；不存在的成员 = 原样返回。</returns>
    public ClusterConfig Remove(NodeId id)
    {
        if (!Contains(id)) return this;
        return new ClusterConfig(_members.Where(m => m.Id != id));
    }

    // ═══ 序列化（配置条目 payload——spec-04 §1 稳定格式）═══

    /// <summary>
    /// 序列化为配置条目 payload（v2）：
    /// <code>[Version 2B LE][Count 1B][成员 × N：Id 16B + EpLen 1B + EndPoint UTF8 + Role 1B]</code>
    /// </summary>
    /// <returns>稳定序列化字节（配置条目 payload——跨节点传播/重启恢复用）。</returns>
    public byte[] Serialize()
    {
        var epBytes = _members.Select(m => System.Text.Encoding.UTF8.GetBytes(m.EndPoint)).ToArray();
        var buf = new byte[3 + _members.Length * 18 + epBytes.Sum(b => b.Length)];
        var span = buf.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, FormatVersion);
        span[2] = (byte)_members.Length;
        var p = 3;
        for (var i = 0; i < _members.Length; i++)
        {
            _members[i].Id.CopyTo(span.Slice(p, 16));
            p += 16;
            span[p] = (byte)epBytes[i].Length;
            p += 1;
            epBytes[i].CopyTo(span.Slice(p));
            p += epBytes[i].Length;
            span[p] = (byte)_members[i].Role;
            p += 1;
        }
        return buf;
    }

    /// <summary>反序列化配置条目 payload（v2 本格；v1 兼容读——成员 role 全 Voter）。</summary>
    /// <param name="payload">配置条目 payload 字节（v2/v1 兼容）。</param>
    /// <returns>解析得到的集群配置。</returns>
    /// <exception cref="FormatException">payload 过短/版本不支持/成员截断/端点截断。</exception>
    public static ClusterConfig Deserialize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 3) throw new FormatException("配置条目 payload 过短。");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (version is not (FormatVersion or FormatVersionV1)) throw new FormatException($"配置条目版本不支持：{version}。");
        var count = payload[2];
        var roleSize = version == FormatVersion ? 1 : 0;
        var members = new ClusterMember[count];
        var p = 3;
        for (var i = 0; i < count; i++)
        {
            if (p + 17 + roleSize > payload.Length) throw new FormatException("配置条目成员截断。");
            var id = new NodeId(payload.Slice(p, 16));
            p += 16;
            var epLen = payload[p];
            p += 1;
            if (p + epLen + roleSize > payload.Length) throw new FormatException("配置条目端点截断。");
            var ep = System.Text.Encoding.UTF8.GetString(payload.Slice(p, epLen));
            p += epLen;
            var role = roleSize == 1 ? (ClusterMemberRole)payload[p] : ClusterMemberRole.Voter;
            p += roleSize;
            members[i] = new ClusterMember(id, ep, role);
        }
        return new ClusterConfig(members);
    }
}
