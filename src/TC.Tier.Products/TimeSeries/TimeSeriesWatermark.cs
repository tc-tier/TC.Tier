using System.Runtime.InteropServices;

namespace TC.Tier.Products.TimeSeries;

[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct TimeSeriesWatermarkLayout
{
    [FieldOffset(0)] internal long TrimmedUntilTimestamp;
    [FieldOffset(8)] internal int TrimmedSegId;
    [FieldOffset(12)] internal int TrimmedExtension;
    [FieldOffset(16)] internal long TrimmedOffset;
    [FieldOffset(24)] internal long SampleCount;
    [FieldOffset(32)] internal long WatermarkEpoch;
}

/// <summary>
/// 时序水位状态（tc-tier-timeseries-spec §1——VersionedMetadata payload，retention 边界持久）。
/// <para>★ 崩溃语义（spec §5/§7）：截断先行、水位随后——已截断未推水位的窗口由恢复对账
///   以数据事实收口（TrimmedUntil = min(水位值, 首样本 ts)）。</para>
/// </summary>
/// <param name="TrimmedUntilTimestamp">回收边界（早于此的样本已回收；long.MinValue = 从未 trim）。</param>
/// <param name="TrimmedAddress">上次 trim 的截断锚地址（诊断/对账参考——真相源是 Ring BeginAddress）。</param>
/// <param name="SampleCount">当前存活样本数（上次持久时刻的计数）。</param>
/// <param name="WatermarkEpoch">水位提交代次（每次 trim +1）。</param>
internal readonly record struct TimeSeriesWatermarkState(
    long TrimmedUntilTimestamp,
    LogicalAddress TrimmedAddress,
    long SampleCount,
    long WatermarkEpoch)
{
    /// <summary>payload 字节数（VersionedMetadataSettings.PayloadSize 装配用）。</summary>
    internal static int PayloadSize => TimeSeriesWatermarkLayoutCodec.StructSize;

    /// <summary>首次启动的空水位（从未 trim——TrimmedUntil = long.MinValue 不拒绝任何写入）。</summary>
    internal static TimeSeriesWatermarkState Initial => new(long.MinValue, LogicalAddress.Empty, 0, 0);

    /// <summary>编码（meta.Write 前打包）。</summary>
    internal byte[] Write()
        => WriteTo(new byte[PayloadSize]);

    /// <summary>编码进调用方缓冲（零分配路径）。</summary>
    internal byte[] WriteTo(byte[] buf)
    {
        var layout = new TimeSeriesWatermarkLayout
        {
            TrimmedUntilTimestamp = TrimmedUntilTimestamp,
            TrimmedSegId = TrimmedAddress.SegId,
            TrimmedExtension = TrimmedAddress.Extension,
            TrimmedOffset = TrimmedAddress.Offset,
            SampleCount = SampleCount,
            WatermarkEpoch = WatermarkEpoch,
        };
        TimeSeriesWatermarkLayoutCodec.Write(buf, in layout);
        return buf;
    }

    /// <summary>解码（恢复载入）；false = payload 缺失/长度不足（首次启动形态）。</summary>
    internal static bool TryRead(ReadOnlySpan<byte> buf, out TimeSeriesWatermarkState state)
    {
        if (buf.Length < PayloadSize)
        {
            state = Initial;
            return false;
        }
        var layout = TimeSeriesWatermarkLayoutCodec.Read(buf);
        state = new TimeSeriesWatermarkState(
            layout.TrimmedUntilTimestamp,
            new LogicalAddress(layout.TrimmedSegId, layout.TrimmedExtension, layout.TrimmedOffset),
            layout.SampleCount,
            layout.WatermarkEpoch);
        return true;
    }
}
