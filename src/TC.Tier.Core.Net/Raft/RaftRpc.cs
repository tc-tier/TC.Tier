using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// Raft RPC 消息族（spec-01..05 消息语义 × spec-12 §6/§10——[WireMessage] 声明即线格式；
/// RPC 族走请求回调形态：**RpcId 取消——CorrId 由传输承载，消息族不携带**（spec-12 §6 形态映射）。
/// <para>★ 线格式（生成物：[Tag 1B][Term 8B 公共前缀][子类字段声明序]，全部小端；NodeId = 16B
///   不透明字节串原序嵌套件）：tag 沿用类型码 0x01-0x08（PreVoteReq…InstallSnapshotResp）。
///   ★ 线格式 v3（信封之死——序列化 v3，件 B 配置 v2 先例）：AppendEntries 条目区由
///   [Terms 表][Payloads 表]（v2，载荷 = 引擎信封）改为 <see cref="AppendEntriesReq.EntriesRegion"/>
///   单 blob（区内 [Count 4B][×N: <see cref="RaftAppendEntryHeader"/> 13B + Content]——
///   kind 升格条目结构字段，存储直写单拷贝）。内部协议未发布，无跨版本共存。</para>
/// <para>★ 无响应超时重发机制（raft RPC 幂等——丢失由心跳/选举定时器自然补偿；请求回调
///   缺省 at-most-once 投递语义与此天然对齐）。</para>
/// <para>★ 降级统一规则（spec-01 §3.2）：任何状态收到更高 term 的 RPC → 先落盘再应答；
///   应答恒回带 <see cref="Term"/>（应答方 currentTerm——对端据此发现自己落后）。</para>
/// <para>★ 解码防御：未知 tag / 截断 / Count 超上限 = false（畸形报文丢弃）；已知 tag 尾部
///   扩展字节容忍（前向兼容）。条目表防御上限 <see cref="MaxEntriesPerAppend"/>（旧手写
///   codec 只查负数——生成化补齐截断防御）。</para>
/// </summary>
[WireMessage]
public abstract record RaftRpc
{
    /// <summary>线格式版本（v3 = AppendEntries 条目区 blob 化 + kind 结构字段化——信封之死）。</summary>
    public const int WireFormatVersion = 3;

    /// <summary>单次 AppendEntries 条目表防御上限（发送侧批受 ReplicationPolicy 约束（缺省 1000）；
    /// 线协议上限为畸形报文拦截界——帧 16MB 同时约束总字节）。</summary>
    public const int MaxEntriesPerAppend = 8192;

    /// <summary>EntriesRegion blob 字节防御上限（畸形报文拦截界——与传输帧上限同量级）。</summary>
    public const int MaxEntriesRegionBytes = 16 << 20;

    /// <summary>快照块清单校验和表防御上限（畸形报文拦截界——正常形态受块大小与内容规模约束）。</summary>
    public const int MaxSnapshotBlocks = 1 << 18;

    /// <summary>快照块持有者表防御上限（集群规模畸形报文拦截界）。</summary>
    public const int MaxSnapshotHolders = 256;

    /// <summary>发起方 currentTerm（应答回带应答方 currentTerm——降级统一规则）。</summary>
    public required long Term { get; init; }
}

/// <summary>PreVote 请求（spec-01 §3.3——不抬任期、零持久化；参数与真实投票相同，选举限制同样适用）。</summary>
[WireMessageTag(0x01)]
public sealed record PreVoteReq : RaftRpc
{
    /// <summary>预候选节点 ID。</summary>
    public required NodeId CandidateId { get; init; }

    /// <summary>预候选日志尾 index。</summary>
    public required long LastLogIndex { get; init; }

    /// <summary>预候选日志尾 term。</summary>
    public required long LastLogTerm { get; init; }
}

/// <summary>PreVote 应答（不落盘、不记 votedFor——spec-01 §3.3 选民处理）。</summary>
[WireMessageTag(0x02)]
public sealed record PreVoteResp : RaftRpc
{
    /// <summary>是否授权预票（裁决规则同 RequestVote）。</summary>
    public required bool Granted { get; init; }
}

/// <summary>RequestVote 请求（spec-01 §5.2——候选广播；携带日志新鲜度裁决参数）。</summary>
[WireMessageTag(0x03)]
public sealed record RequestVoteReq : RaftRpc
{
    /// <summary>候选节点 ID。</summary>
    public required NodeId CandidateId { get; init; }

    /// <summary>候选日志尾 index（AllocatedIndex——含未持久化，spec-01 §5.2）。</summary>
    public required long LastLogIndex { get; init; }

    /// <summary>候选日志尾 term。</summary>
    public required long LastLogTerm { get; init; }
}

/// <summary>RequestVote 应答（授权前已落盘 votedFor——契约① 应答前持久化）。</summary>
[WireMessageTag(0x04)]
public sealed record RequestVoteResp : RaftRpc
{
    /// <summary>是否授权选票。</summary>
    public required bool Granted { get; init; }
}

/// <summary>
/// AppendEntries 请求（spec-02——心跳 = 条目空；携带 leaderCommit 传播提交推进）。
/// </summary>
[WireMessageTag(0x05)]
public sealed record AppendEntriesReq : RaftRpc
{
    /// <summary>leader 节点 ID。</summary>
    public required NodeId LeaderId { get; init; }

    /// <summary>新条目前一格的 index（prevLog 匹配校验）。</summary>
    public required long PrevLogIndex { get; init; }

    /// <summary>新条目前一格的 term（prevLog 匹配校验）。</summary>
    public required long PrevLogTerm { get; init; }

    /// <summary>leader 的 commitIndex（follower 据此推进本地 commitIndex）。</summary>
    public required long LeaderCommit { get; init; }

    /// <summary>条目区（线格式 v3——<see cref="RaftEntriesRegion"/> 布局：
    /// [Count 4B][×N: 固定头 13B + Content]；term/kind/content 逐条目内嵌，
    /// leader 侧由 <see cref="IRaftStore.WriteEntriesToAsync"/> 存储直写（单拷贝），
    /// follower 侧游标零拷贝切片。空 = 心跳。★ 声明序最后——条目区是帧尾区域，
    /// 为单拷贝直写路径（store 追加）保留帧尾连续性）。</summary>
    [WireMember(MaxCount = RaftRpc.MaxEntriesRegionBytes)]
    public ReadOnlyMemory<byte> EntriesRegion { get; init; }
}

/// <summary>
/// AppendEntries 应答（spec-02 §3 持久化先于应答 + ★ 汇报 N₀ = 快照 index 扩展——spec-03 §4：
/// follower 应答回带本地 <see cref="SnapshotIndex"/>，leader 发现 nextIndex ≤ N₀ 时改走快照安装）。
/// </summary>
[WireMessageTag(0x06)]
public sealed record AppendEntriesResp : RaftRpc
{
    /// <summary>是否成功（entries 已落盘 / 心跳 prevLog 匹配）。</summary>
    public required bool Success { get; init; }

    /// <summary>成功时 follower 实际匹配到的日志尾 index（= prevLogIndex + 条数；心跳 = prevLogIndex）。</summary>
    public required long MatchIndex { get; init; }

    /// <summary>失败 hint——冲突处 term（0 = 无 hint——日志比 leader 短）。</summary>
    public required long ConflictTerm { get; init; }

    /// <summary>失败 hint——该 term 首条 index（leader 一次跳到 min(nextIndex-1, 该 term 首条)——spec-02 §4）。</summary>
    public required long ConflictIndex { get; init; }

    /// <summary>follower 本地快照覆盖点 N₀（leader 据此决策快照安装——spec-03 §4）。</summary>
    public required long SnapshotIndex { get; init; }
}

/// <summary>
/// InstallSnapshot 请求（spec-03 §2）——快照已经注入传输面送达，本 RPC 只做同步点握手：
/// follower 收到后导入（读注入面）+ 重建业务状态 → 应答 N₀ → leader nextIndex = N₀+1 推增量。
/// <para>★ 握手面协调数据（spec-12 §6.1 SwarmSync × T6 接线）：<see cref="Swarm"/> = true 时
/// 携带多源块清单（<see cref="ManifestId"/>/<see cref="BlockSize"/>/<see cref="TotalBytes"/>/
/// <see cref="Checksums"/>）+ 持有者表（<see cref="Holders"/>）——follower 据此多源拉取；
/// false = 单源流式（数据面自带一切，协调字段为零值）。</para>
/// </summary>
[WireMessageTag(0x07)]
public sealed record InstallSnapshotReq : RaftRpc
{
    /// <summary>leader 节点 ID。</summary>
    public required NodeId LeaderId { get; init; }

    /// <summary>快照覆盖点（导入后 SnapshotIndex = 此值）。</summary>
    public required long SnapshotIndex { get; init; }

    /// <summary>多源块拉取形态开关（true = manifest+holders 随握手；false = 单源流式）。</summary>
    public bool Swarm { get; init; }

    /// <summary>内容标识（swarm 时有效；构建方供给唯一性——快照 = 覆盖点 index 承载）。</summary>
    public Opaque16 ManifestId { get; init; }

    /// <summary>块大小（swarm 时有效；末块截短——块定位纯几何推导）。</summary>
    public int BlockSize { get; init; }

    /// <summary>内容总字节数（swarm 时有效）。</summary>
    public long TotalBytes { get; init; }

    /// <summary>每块校验和（CRC32C——按块号序，长度 = 块数；swarm 时有效）。</summary>
    [WireMember(MaxCount = RaftRpc.MaxSnapshotBlocks)]
    public uint[] Checksums { get; init; } = [];

    /// <summary>块持有者表（swarm 时有效；可含本端——多源拉取候选）。</summary>
    [WireMember(MaxCount = RaftRpc.MaxSnapshotHolders)]
    public NodeId[] Holders { get; init; } = [];
}

/// <summary>InstallSnapshot 应答（成功 = 快照已导入 + 业务状态已重建到 N₀）。</summary>
[WireMessageTag(0x08)]
public sealed record InstallSnapshotResp : RaftRpc
{
    /// <summary>是否成功。</summary>
    public required bool Success { get; init; }

    /// <summary>导入后的快照覆盖点（失败 = 0）。</summary>
    public required long SnapshotIndex { get; init; }
}

/// <summary>加入集群请求（Standby 引导——新节点向 leader 递；leader 按申请档入组：
/// 缺省 learner（<see cref="AutoPromote"/> 追平后晋级）∥ <see cref="AsWitness"/> witness 档
/// （投票不存数据，永不晋级））。bootstrap 端点非 leader = 应答携带其已知 leader
/// 提示（Accepted=false——调用方轮转重试）。</summary>
[WireMessageTag(0x09)]
public sealed record JoinReq : RaftRpc
{
    /// <summary>申请加入的节点 ID。</summary>
    public required NodeId CandidateId { get; init; }

    /// <summary>追平后自动晋级 voter（false = 保持 learner——观察副本形态；
    /// <see cref="AsWitness"/> = true 时忽略——witness 永不晋级）。</summary>
    public required bool AutoPromote { get; init; }

    /// <summary>加入方监听地址（host:port 的 UTF-8——leader 据此注册拨号表回连复制；
    /// 进程内/已互联传输形态 = 空）。</summary>
    [WireMember(MaxCount = 64)]
    public ReadOnlyMemory<byte> EndPointBytes { get; init; }

    /// <summary>witness 档（三期-F2 装配面）：受理即以 witness 入组——投票计多数派、
    /// 不存日志体（断言流推进）、永不晋级。</summary>
    public bool AsWitness { get; init; }
}

/// <summary>加入集群应答。</summary>
[WireMessageTag(0x0A)]
public sealed record JoinResp : RaftRpc
{
    /// <summary>是否受理（true = 已入组——learner 或本就是成员；false = 本端非 leader）。</summary>
    public required bool Accepted { get; init; }

    /// <summary>应答方已知 leader（未选出 = Empty——调用方轮转其他 bootstrap 端点）。</summary>
    public required NodeId LeaderId { get; init; }
}

/// <summary>follower 线性读转发请求（二期-B §6.2——本地非 leader 时转发当前已知 leader；
/// 根 Term 供 leader 侧任期核对/转发方抬任期）。</summary>
[WireMessageTag(0x0B)]
public sealed record ReadIndexReq : RaftRpc;

/// <summary>领导权定向转让（二期-D2——leader → 目标的 TimeoutNow：目标跳过预票立即发起
/// 真选举；根 Term 供目标核对在位权威）。</summary>
[WireMessageTag(0x0D)]
public sealed record TransferLeaderReq : RaftRpc;

/// <summary>领导权转让应答（Accepted = 目标已受理并发起选举——诊断面，调用方经换届观察完成）。</summary>
[WireMessageTag(0x0E)]
public sealed record TransferLeaderResp : RaftRpc
{
    /// <summary>是否受理（true = 已发起真选举；false = 应答方非 voter follower/任期不符）。</summary>
    public bool Accepted { get; init; }
}

/// <summary>ReadIndex 转发应答（二期-B §6.2——ReadIndex &lt; 0 = 应答方非 leader/任期落后，
/// 调用方按 leader 变更重试）。</summary>
[WireMessageTag(0x0C)]
public sealed record ReadIndexResp : RaftRpc
{
    /// <summary>readIndex（应答方 commitIndex；&lt; 0 = 应答方非 leader——调用方重试）。</summary>
    public long ReadIndex { get; init; }
}
