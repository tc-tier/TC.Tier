namespace TC.Tier.Products.Wal;

/// <summary>
/// TierWAL opaque 容器（搭 Meta 边车：Meta 只提供字节容器原子持久化 + CRC 完整性，不解析内容、
/// 不管布局——容器内格式 TierWAL 自定，与 Meta 头尾格式完全无关）。
/// <para>布局：<c>[WalOpaqueHeader 54B][raft 元数据预留区]</c>——头部只记最大/最小 long 及其对应地址
///   （设计决策：段是底层的概念，上层地址空间无限；给定 index 的定位 = 内存稀疏锚点二分 + 顺序重放）。</para>
/// </summary>
internal static class WalOpaqueLayout
{
    /// <summary>容器头部字节数（WalOpaqueHeader 编译期布局）。</summary>
    public const int ContainerHeaderSize = WalOpaqueHeaderCodec.StructSize;

    /// <summary>raft 元数据可用容量 = opaque 剩余（头部固定）。</summary>
    public static int RaftMetaCapacity(int opaqueBytes) => opaqueBytes - ContainerHeaderSize;

    /// <summary>
    /// 序列化容器到目标缓冲。
    /// </summary>
    /// <exception cref="InvalidOperationException">raft 区超出 opaque 容量（调大 MetaOpaqueBytes）。</exception>
    public static void Serialize(Span<byte> dst, long tailIndex, LogicalAddress tailAddress,
        long headIndex, LogicalAddress headAddress, ReadOnlySpan<byte> raftMeta)
    {
        int required = ContainerHeaderSize + raftMeta.Length;
        if (required > dst.Length)
            throw new InvalidOperationException(
                $"TierWAL opaque 容器溢出：需要 {required} B，容量 {dst.Length} B（raft 元数据 {raftMeta.Length} B）。"
                + "请调大 TierWalOptions.MetaOpaqueBytes。");

        var header = new WalOpaqueHeader
        {
            MagicValue = WalOpaqueHeader.Magic,
            Version = WalOpaqueHeader.CurrentVersion,
            TailIndex = tailIndex,
            TailAddress = tailAddress,
            HeadIndex = headIndex,
            HeadAddress = headAddress,
        };
        WalOpaqueHeaderCodec.Write(dst, in header, validate: true);
        raftMeta.CopyTo(dst[ContainerHeaderSize..]);
    }

    /// <summary>
    /// 解析容器（恢复 O(1)：一次读最近已提交块）。失败（非本格式/长度非法）= 未提交/旧值——返回 false。
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> src, out long tailIndex, out LogicalAddress tailAddress,
        out long headIndex, out LogicalAddress headAddress, out byte[]? raftMeta)
    {
        tailIndex = 0;
        tailAddress = LogicalAddress.Empty;
        headIndex = 0;
        headAddress = LogicalAddress.Empty;
        raftMeta = null;

        if (src.Length < ContainerHeaderSize) return false;
        if (WalOpaqueHeaderCodec.Read_MagicValue(src) != WalOpaqueHeader.Magic) return false;
        if (WalOpaqueHeaderCodec.Read_Version(src) != WalOpaqueHeader.CurrentVersion) return false;

        tailIndex = WalOpaqueHeaderCodec.Read_TailIndex(src);
        tailAddress = WalOpaqueHeaderCodec.Read_TailAddress(src);
        headIndex = WalOpaqueHeaderCodec.Read_HeadIndex(src);
        headAddress = WalOpaqueHeaderCodec.Read_HeadAddress(src);
        var raft = src[ContainerHeaderSize..];
        if (!raft.IsEmpty) raftMeta = raft.ToArray();
        return true;
    }
}
