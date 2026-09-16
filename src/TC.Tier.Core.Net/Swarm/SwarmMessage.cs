using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// SwarmSync 消息族（spec-12 §6.1 × §6 形态映射——取块 = 请求回调：
/// 单请求 → 关联应答 → 超时；CorrId 由传输承载，消息族不携带）。
/// <para>★ 线格式（生成物：[Tag 1B][字段声明序]，Opaque16 原序嵌套件/小端/Data 变长 blob）：</para>
/// <code>
/// GetBlockReq        [Tag][ManifestId 16B][BlockIndex 8B]
/// GetBlockResp       [Tag][ManifestId 16B][BlockIndex 8B][HasBlock 1B][Len 4B][Data]
/// SourceAnnounceMsg  [Tag][ManifestId 16B]
/// </code>
/// <para>★ 未持块应答 = HasBlock=false（Len 0 无 Data——holder 侧块缺失是正常形态非错误；
/// 下载方据此换源）。</para>
/// </summary>
[WireMessage]
public abstract record SwarmMessage
{
    /// <summary>单块线协议防御上限（1 MiB——块大小由构建方定，此为畸形报文拦截界）。</summary>
    public const int MaxBlockBytes = 1 << 20;

    /// <summary>单清单块数防御上限（2 的 18 次方——与 raft 侧 Checksums 上界同口径，畸形清单拦截界）。</summary>
    public const int MaxChecksumCount = 1 << 18;
}

/// <summary>取块请求（块定位 = 内容标识 + 块号——holder 按本地源命中）。</summary>
[WireMessageTag(0x01)]
public sealed record GetBlockReq : SwarmMessage
{
    /// <summary>内容标识。</summary>
    public required Opaque16 ManifestId { get; init; }

    /// <summary>块号。</summary>
    public required long BlockIndex { get; init; }
}

/// <summary>取块应答（HasBlock=false = holder 无此内容/无此块——下载方换源）。</summary>
[WireMessageTag(0x02)]
public sealed record GetBlockResp : SwarmMessage
{
    /// <summary>内容标识（回显——请求配对校验）。</summary>
    public required Opaque16 ManifestId { get; init; }

    /// <summary>块号（回显）。</summary>
    public required long BlockIndex { get; init; }

    /// <summary>是否持有该块（false = 无内容/块缺失——正常形态非错误）。</summary>
    public required bool HasBlock { get; init; }

    /// <summary>块数据（HasBlock=false 时为空）。</summary>
    [WireMember(MaxCount = SwarmMessage.MaxBlockBytes)]
    public required byte[] Data { get; init; }
}

/// <summary>
/// 持有者上报（spec-12 §6.1 增量——多源快照分发完整化）：follower/重启节点 → 当前 leader 的
/// 单边声明"我持有此基线"——leader 端 manifest 代际持有表登记（重复上报幂等）。
/// <para>★ 错报无害：内容寻址 + 块级 CRC32C 校验（拉取侧防线）——错报只损失带宽分担不产生错误数据。
/// ★ 上报 = 尽力送达（丢失由下次上报/换届重报覆盖）；应答 = 传输闭环空应答（非语义确认）。</para>
/// </summary>
[WireMessageTag(0x03)]
public sealed record SourceAnnounceMsg : SwarmMessage
{
    /// <summary>持有的基线内容标识（快照换代 = 新 Id——持有表按代二元组 (节点, manifestId)）。</summary>
    public required Opaque16 ManifestId { get; init; }
}

/// <summary>
/// 反熵对账消息（spec-12 §6.1 增量——复制家族设计件 C：副本漂移检出）。
/// <code>
/// EntropyProbeReq  [Tag][ManifestId 16B][Level 1B][RangeStart 4B][RangeCount 4B]
/// EntropyProbeResp [Tag][Count 4B][Hashes ×N 4B]
/// </code>
/// <para>★ 只读比对协议：请求某层某区间的哈希集（Level 0 = 全局根 1 个；Level 1 = 叶子区间
/// 每块 CRC32C）——对账消息不触发任何写路径。RangeCount ≤ 64（一个子树）。</para>
/// </summary>
[WireMessageTag(0x04)]
public sealed record EntropyProbeReq : SwarmMessage
{
    /// <summary>对账内容标识。</summary>
    public required Opaque16 ManifestId { get; init; }

    /// <summary>比阶层（0 = 全局根；1 = 叶子区间）。</summary>
    public required byte Level { get; init; }

    /// <summary>叶子区间起始块号（Level 1 有效）。</summary>
    public required int RangeStart { get; init; }

    /// <summary>叶子区间块数（≤ <see cref="SwarmMerkle.SubtreeFanout"/>；Level 0 忽略）。</summary>
    public required int RangeCount { get; init; }
}

/// <summary>对账应答（请求区间的哈希集——Count 0 = 对端未持该内容）。</summary>
[WireMessageTag(0x05)]
public sealed record EntropyProbeResp : SwarmMessage
{
    /// <summary>哈希集（Level 0 = 全局根单值；Level 1 = 区间叶子 CRC——按块号序）。</summary>
    [WireMember(MaxCount = SwarmMerkle.SubtreeFanout)]
    public required uint[] Hashes { get; init; }
}


/// <summary>清单发现请求（#436 件三——按内容标识拉清单，任何 holder 可应答；空应答 = 未持有）。</summary>
[WireMessageTag(0x06)]
public sealed record SwarmManifestReq : SwarmMessage
{
    /// <summary>内容标识。</summary>
    public required Opaque16 ManifestId { get; init; }
}

/// <summary>
/// 清单发现应答（HasManifest=false = 未持有——拉取方换下一种子/退化直连源）。
/// <para>★ 清单字段拍平在线形态（线编解码成员支持集无嵌套 record——HasManifest 门控有效性）。</para>
/// </summary>
[WireMessageTag(0x07)]
public sealed record SwarmManifestResp : SwarmMessage
{
    /// <summary>是否持有（false 时清单字段无意义）。</summary>
    public required bool HasManifest { get; init; }

    /// <summary>内容标识。</summary>
    public required Opaque16 Id { get; init; }

    /// <summary>内容总字节。</summary>
    public required long TotalBytes { get; init; }

    /// <summary>块大小（末块截短——拉取方无需预知）。</summary>
    public required int BlockSize { get; init; }

    /// <summary>每块 CRC32C（按块号序——Checksums 上界沿用 raft 侧 2 的 18 次方口径）。</summary>
    [WireMember(MaxCount = SwarmMessage.MaxChecksumCount)]
    public required uint[] Checksums { get; init; }
}
