using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TC.Tier.Products.TimeSeries;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 36)]
internal struct DenseSeriesWatermarkBlockLayout
{
    [FieldOffset(0)] internal uint SeriesId;
    [FieldOffset(4)] internal long TrimmedUntilTimestamp;
    [FieldOffset(12)] internal int TrimmedSegId;
    [FieldOffset(16)] internal int TrimmedExtension;
    [FieldOffset(20)] internal long TrimmedOffset;
    [FieldOffset(28)] internal long SampleCount;
}

/// <summary>逐序列水位块（持久域一条目——#443 设计稿 §5.2）。</summary>
/// <param name="SeriesId">序列标识。</param>
/// <param name="TrimmedUntilTimestamp">显式 trim 水位（早于此的样本已回收）。</param>
/// <param name="TrimmedAddress">上次 trim 的截断锚地址。</param>
/// <param name="SampleCount">上次持久时刻的存活样本计数。</param>
internal readonly record struct DenseSeriesWatermarkBlock(
    uint SeriesId, long TrimmedUntilTimestamp, LogicalAddress TrimmedAddress, long SampleCount)
{
    /// <summary>块字节数。</summary>
    internal const int BlockSize = 36;

    /// <summary>编码进调用方缓冲（写入偏移处；缓冲长度须 ≥ 偏移 + <see cref="BlockSize"/>）。</summary>
    internal void WriteTo(Span<byte> dst, int offset)
    {
        var layout = new DenseSeriesWatermarkBlockLayout
        {
            SeriesId = SeriesId,
            TrimmedUntilTimestamp = TrimmedUntilTimestamp,
            TrimmedSegId = TrimmedAddress.SegId,
            TrimmedExtension = TrimmedAddress.Extension,
            TrimmedOffset = TrimmedAddress.Offset,
            SampleCount = SampleCount,
        };
        DenseSeriesWatermarkBlockLayoutCodec.Write(dst[offset..], in layout);
    }

    /// <summary>从缓冲偏移处解码；false = 长度不足。</summary>
    internal static bool TryReadAt(ReadOnlySpan<byte> src, int offset, out DenseSeriesWatermarkBlock block)
    {
        block = default;
        if (offset + BlockSize > src.Length) return false;
        var layout = DenseSeriesWatermarkBlockLayoutCodec.Read(src[offset..]);
        block = new DenseSeriesWatermarkBlock(
            layout.SeriesId,
            layout.TrimmedUntilTimestamp,
            new LogicalAddress(layout.TrimmedSegId, layout.TrimmedExtension, layout.TrimmedOffset),
            layout.SampleCount);
        return true;
    }
}

/// <summary>
/// dense 水位文档（VersionedMetadata payload 分区——#443 设计稿 §5.2 单文件 keyed 块化）：
/// <c>[默认序列槽（TimeSeriesWatermarkState 40B——seriesId 0）][BlockCount 4B][n × 36B 块]</c>。
/// <para>★ 稀疏持久：只存「显式 trim 过」的序列水位；未 trim 序列 = 派生值（首索引条目 ts，不回写）。
///   变长写入经 <c>VersionedMetadataSettings.MaxPayloadSize</c> 档——n 上限护栏 = SeriesCapacity
///   （容量守卫先行，超限抛在序列注册处；此处 n 只减不增越过护栏）。</para>
/// </summary>
internal static class DenseSeriesWatermarkDoc
{
    /// <summary>头部区长度（默认序列槽 40B + BlockCount 4B）。</summary>
    internal static int HeaderSize => TimeSeriesWatermarkState.PayloadSize + 4;

    /// <summary>编码（默认槽 + 稀疏块集合）。</summary>
    /// <param name="defaultSlot">seriesId 0 槽（两参 API 默认序列的水位 + 实例级字段）。</param>
    /// <param name="blocks">显式 trim 过的序列水位块。</param>
    internal static byte[] Encode(TimeSeriesWatermarkState defaultSlot, IReadOnlyList<DenseSeriesWatermarkBlock> blocks)
    {
        var buf = new byte[HeaderSize + blocks.Count * DenseSeriesWatermarkBlock.BlockSize];
        defaultSlot.WriteTo(buf);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(TimeSeriesWatermarkState.PayloadSize, 4),
            (uint)blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
            blocks[i].WriteTo(buf, HeaderSize + i * DenseSeriesWatermarkBlock.BlockSize);
        return buf;
    }

    /// <summary>解码；false = 头部不完整（首次启动形态）。</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> src, out TimeSeriesWatermarkState defaultSlot,
        out List<DenseSeriesWatermarkBlock> blocks)
    {
        defaultSlot = TimeSeriesWatermarkState.Initial;
        blocks = new List<DenseSeriesWatermarkBlock>();
        if (!TimeSeriesWatermarkState.TryRead(src, out defaultSlot)) return false;
        if (src.Length < HeaderSize) return true;   // 只有默认槽（无块区——从未 trim 非零序列）
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(src.Slice(TimeSeriesWatermarkState.PayloadSize, 4));
        for (uint i = 0; i < count; i++)
        {
            int offset = HeaderSize + (int)i * DenseSeriesWatermarkBlock.BlockSize;
            if (!DenseSeriesWatermarkBlock.TryReadAt(src, offset, out var block)) break;   // 尾部截断容错
            blocks.Add(block);
        }
        return true;
    }
}
