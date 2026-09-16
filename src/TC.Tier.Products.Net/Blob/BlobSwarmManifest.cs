using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Products.Net.Blob;

/// <summary>
/// Blob 内容 ↔ Swarm manifest 派生（#436 件一配套——不可变对象假设下 manifest 是 ObjectId 的
/// 稳定函数）。
/// <para>★ Id 承载 = LogicalAddress 16B 原序直拷（SegId 4B LE + Extension 4B LE + Offset 8B LE——
/// 结构布局原序，与 raft 覆点承载形态互不重叠）。</para>
/// </summary>
public static class BlobSwarmManifest
{
    /// <summary>ObjectId → 内容标识（确定性派生——同对象多副本同 Id；16B = 布局字段序直写，
    /// 字节序收口生成器 <see cref="LogicalAddressCodec"/> 单字段 Write——零手写 BinaryPrimitives）。</summary>
    public static Opaque16 IdFor(LogicalAddress objectId)
    {
        Span<byte> bytes = stackalloc byte[LogicalAddressCodec.StructSize];
        LogicalAddressCodec.Write(bytes, objectId);
        return SwarmManifestId.ForContent(bytes);
    }

    /// <summary>内容标识 → ObjectId（逆映射——holder 侧按 Id 反查本地对象；生成 codec 单字段 Read）。</summary>
    public static LogicalAddress ObjectIdFor(Opaque16 manifestId)
    {
        Span<byte> bytes = stackalloc byte[LogicalAddressCodec.StructSize];
        manifestId.CopyTo(bytes);
        return LogicalAddressCodec.Read(bytes);
    }

    /// <summary>构建清单（内容字节 + 块大小 + 内容标识——Checksums 实算自同一帧流）。</summary>
    public static SwarmManifest Build(ReadOnlyMemory<byte> content, int blockSize, Opaque16 id)
        => SwarmManifest.Build(content, blockSize, id);
}
