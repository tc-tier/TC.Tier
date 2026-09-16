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
    /// <param name="opaqueBytes">opaque 容器总字节数（TierWalOptions.MetaOpaqueBytes）。</param>
    /// <returns>头部之外的可用容量（字节；opaqueBytes 小于头部时为负）。</returns>
    public static int RaftMetaCapacity(int opaqueBytes) => opaqueBytes - ContainerHeaderSize;

    /// <summary>
    /// 序列化容器到目标缓冲。
    /// </summary>
    /// <param name="dst">写入目标缓冲区，长度须容纳头部 + raftMeta（不足抛 InvalidOperationException）。</param>
    /// <param name="tailIndex">已分配尾 index（写入头部）。</param>
    /// <param name="tailAddress">尾 index 对应的 record 地址（写入头部）。</param>
    /// <param name="headIndex">截断头 index（写入头部）。</param>
    /// <param name="headAddress">头 index 对应的 record 地址（写入头部）。</param>
    /// <param name="raftMeta">raft 元数据字节（term/vote 等——原样追加在头部之后，内容零知识）。</param>
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
    /// <param name="src">容器字节（≥ 头部字节数）。</param>
    /// <param name="tailIndex">输出：已分配尾 index（失败 = 0）。</param>
    /// <param name="tailAddress">输出：尾 index 对应的 record 地址（失败 = Empty）。</param>
    /// <param name="headIndex">输出：截断头 index（失败 = 0）。</param>
    /// <param name="headAddress">输出：头 index 对应的 record 地址（失败 = Empty）。</param>
    /// <param name="raftMeta">输出：raft 元数据字节（容器无 raft 区 = null）。</param>
    /// <returns>true = 解析成功（magic/version/长度合法）；false = 非本格式或长度非法（输出全为默认值）。</returns>
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
