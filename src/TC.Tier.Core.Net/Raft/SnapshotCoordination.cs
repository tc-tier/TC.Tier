using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照握手协调数据（spec-12 §6.1 × spec-03 §2——InstallSnapshot RPC 握手面携带的
/// manifest + holders 交换；swarm 形态专用——单源流式形态无协调数据（数据面自带一切，
/// 接口以 null 表示）。
/// <para>★ 生产方 = leader 导出（块化后供给块清单与持有者表）；消费方 = follower 导入
/// （重建 manifest 多源拉取）。持有者表可含本端（SwarmSync 本端源支持——下载方自我供给）。</para>
/// </summary>
public sealed record SnapshotCoordination
{
    /// <summary>内容标识（构建方供给唯一性——快照 = 覆盖点 index 承载）。</summary>
    public required Opaque16 ManifestId { get; init; }

    /// <summary>块大小（末块截短——块定位纯几何推导）。</summary>
    public required int BlockSize { get; init; }

    /// <summary>内容总字节数。</summary>
    public required long TotalBytes { get; init; }

    /// <summary>每块校验和（CRC32C——按块号序，长度 = 块数）。</summary>
    public required uint[] Checksums { get; init; }

    /// <summary>块持有者表（多源拉取候选）。</summary>
    public required NodeId[] Holders { get; init; }

    /// <summary>重建块清单（follower 侧——握手面收到协调数据后还原 manifest）。</summary>
    /// <returns>由协调数据字段还原的块清单（Id/BlockSize/TotalBytes/Checksums）。</returns>
    public SwarmManifest ToManifest() => new()
    {
        Id = ManifestId,
        BlockSize = BlockSize,
        TotalBytes = TotalBytes,
        Checksums = Checksums,
    };
}
