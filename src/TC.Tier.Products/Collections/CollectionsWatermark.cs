using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.Collections;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 28)]
internal struct DomainAccountBlockLayout
{
    [FieldOffset(0)] internal uint DomainId;
    [FieldOffset(4)] internal long LastWriteTicks;
    [FieldOffset(12)] internal long MemberCount;
    [FieldOffset(20)] internal long BytesOccupied;
}

/// <summary>域账持久块（VersionedMetadata payload 一条目——tc-tier-collections-spec §1 keyed 块）。</summary>
/// <param name="DomainId">域标识。</param>
/// <param name="LastWriteTicks">域最近写入时刻（UTC Ticks——TTL 过期锚）。</param>
/// <param name="MemberCount">上次持久时刻的存活成员计数（快照——恢复以重放事实校正）。</param>
/// <param name="BytesOccupied">上次持久时刻的域字节占用（快照——同上）。</param>
internal readonly record struct DomainAccountBlock(
    uint DomainId, long LastWriteTicks, long MemberCount, long BytesOccupied)
{
    /// <summary>块字节数。</summary>
    internal const int BlockSize = 28;

    /// <summary>编码进调用方缓冲（写入偏移处）。</summary>
    internal void WriteTo(Span<byte> dst, int offset)
    {
        var layout = new DomainAccountBlockLayout
        {
            DomainId = DomainId,
            LastWriteTicks = LastWriteTicks,
            MemberCount = MemberCount,
            BytesOccupied = BytesOccupied,
        };
        DomainAccountBlockLayoutCodec.Write(dst[offset..], in layout);
    }

    /// <summary>从缓冲偏移处解码；false = 长度不足。</summary>
    internal static bool TryReadAt(ReadOnlySpan<byte> src, int offset, out DomainAccountBlock block)
    {
        block = default;
        if (offset + BlockSize > src.Length) return false;
        var layout = DomainAccountBlockLayoutCodec.Read(src[offset..]);
        block = new DomainAccountBlock(layout.DomainId, layout.LastWriteTicks, layout.MemberCount, layout.BytesOccupied);
        return true;
    }
}

/// <summary>
/// 域账文档（VersionedMetadata payload 分区——keyed 块化，DenseSeriesWatermarkDoc 同构）：
/// <c>[BlockCount 4B][n × 28B 域账块]</c>。
/// <para>★ 域 0（默认域）与命名域同位平等——无默认槽（TimeSeries dense 的系列 0 槽形态不适用：
///   域是纯惰性注册，无"实例级字段"需要默认槽承载）。</para>
/// <para>★ 变长写入经 <c>VersionedMetadataSettings.MaxPayloadSize</c> 档——n 上限护栏 = DomainCapacity。</para>
/// </summary>
internal static class CollectionsWatermarkDoc
{
    /// <summary>头部区长度（BlockCount 4B）。</summary>
    internal const int HeaderSize = 4;

    /// <summary>满配 payload 尺寸（装配 MaxPayloadSize 用——护栏内不预支）。</summary>
    internal static int MaxPayloadSize(uint domainCapacity)
        => HeaderSize + checked((int)domainCapacity) * DomainAccountBlock.BlockSize;

    /// <summary>编码（全体已注册域的当前侧账快照）。</summary>
    /// <param name="blocks">域账块集合（调用方从注册表收集）。</param>
    internal static byte[] Encode(IReadOnlyList<DomainAccountBlock> blocks)
    {
        var buf = new byte[HeaderSize + blocks.Count * DomainAccountBlock.BlockSize];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
            blocks[i].WriteTo(buf, HeaderSize + i * DomainAccountBlock.BlockSize);
        return buf;
    }

    /// <summary>解码；false = 头部不完整（首次启动形态）。</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> src, out List<DomainAccountBlock> blocks)
    {
        blocks = [];
        if (src.Length < HeaderSize) return false;
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(src);
        for (uint i = 0; i < count; i++)
        {
            int offset = HeaderSize + (int)i * DomainAccountBlock.BlockSize;
            if (!DomainAccountBlock.TryReadAt(src, offset, out var block)) break;   // 尾部截断容错
            blocks.Add(block);
        }
        return true;
    }
}
