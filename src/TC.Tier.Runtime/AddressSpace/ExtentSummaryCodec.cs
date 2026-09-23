using TC.Tier.CodeGen;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段区间表摘要编解码（VII-3 reopen extent 级保真）——把段内终态区间布局 RLE 压进 meta extension（≤4KB）。
/// <para>★ 写时机：段满 meta 写（OnSegmentFullCoreAsync）+ Dispose 尾段补写（FlushUnfinishedSegmentMeta）——
///   崩溃只丢活动尾段的洞布局，降级为现状的粗粒度重建。</para>
/// <para>★ 格式：[头 5B][记录 17B × count]——头与记录均 [BinaryLayout] 单一布局真源（偏移/端序零手写），
///   动态维度 = count 条记录（条数变长、每条定长，外壳域循环按 <c>StructSize</c> 推进）。
///   kind：0=Committed 稠密、1=Committed+sparse、2=Wasted、3=Aborted。在途（Leased）不编码。</para>
/// <para>★ 超容量（&gt;240 条）或解码无效 → null → 调用方降级粗粒度（与无摘要等价，不失败）。</para>
/// </summary>
internal static class ExtentSummaryCodec
{
    internal const byte Magic = 0xE1;
    internal const byte Version = 1;
    internal const int MaxPayload = 4096;
    internal const int MaxRecords = (MaxPayload - ExtentSummaryHeaderCodec.StructSize) / ExtentSummaryRecordCodec.StructSize;

    private const byte KindCommittedDense = 0;
    private const byte KindCommittedSparse = 1;
    private const byte KindWasted = 2;
    private const byte KindAborted = 3;

    /// <summary>摘要头（5B 定长：magic + version + count:u16 + flags）。</summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = 5)]
    internal struct ExtentSummaryHeader
    {
        [FieldOffset(0)] public byte MagicValue;
        [FieldOffset(1)] public byte VersionValue;
        [FieldOffset(2)] public ushort Count;
        [FieldOffset(4)] public byte Flags;
    }

    /// <summary>区间记录（17B 定长：start:i64 + length:i64 + kind:u8）。</summary>
    [BinaryLayout(Features = BinaryLayoutFeatures.All)]
    [StructLayout(LayoutKind.Explicit, Size = 17)]
    internal struct ExtentSummaryRecord
    {
        [FieldOffset(0)] public long Start;
        [FieldOffset(8)] public long Length;
        [FieldOffset(16)] public byte Kind;
    }

    /// <summary>编码段区间表的终态布局。null = 超容量/无终态记录（调用方降级粗粒度）。</summary>
    internal static byte[]? Encode(IReadOnlyList<ExtentRecord> records)
    {
        var count = 0;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (ExtentStateCode.IsInFlight(r.State)) continue;   // 在途不编码（重启即弃）
            count++;
        }
        if (count == 0 || count > MaxRecords) return null;

        var payload = new byte[ExtentSummaryHeaderCodec.StructSize + count * ExtentSummaryRecordCodec.StructSize];
        ExtentSummaryHeaderCodec.Write(payload, new ExtentSummaryHeader
        {
            MagicValue = Magic,
            VersionValue = Version,
            Count = (ushort)count,
            Flags = 0,   // bit0=truncated——超限场景直接返回 null，不用截断标记
        }, validate: true);

        var offset = ExtentSummaryHeaderCodec.StructSize;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (ExtentStateCode.IsInFlight(r.State)) continue;
            ExtentSummaryRecordCodec.Write(payload.AsSpan(offset, ExtentSummaryRecordCodec.StructSize), new ExtentSummaryRecord
            {
                Start = r.Start,
                Length = r.End - r.Start,
                Kind = r.State switch
                {
                    var s when ExtentStateCode.IsCommitted(s) => r.Sparse ? KindCommittedSparse : KindCommittedDense,
                    ExtentStateCode.Wasted => KindWasted,
                    ExtentStateCode.Aborted => KindAborted,
                    _ => KindCommittedDense,
                },
            }, validate: false);
            offset += ExtentSummaryRecordCodec.StructSize;
        }
        return payload;
    }

    /// <summary>解码。null = magic/version 不符或 payload 非法（调用方降级粗粒度）。</summary>
    internal static List<ExtentRecord>? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < ExtentSummaryHeaderCodec.StructSize) return null;
        var header = ExtentSummaryHeaderCodec.Read(payload);
        if (header.MagicValue != Magic || header.VersionValue != Version) return null;
        var count = header.Count;
        if (payload.Length != ExtentSummaryHeaderCodec.StructSize + count * ExtentSummaryRecordCodec.StructSize) return null;

        var records = new List<ExtentRecord>(count);
        var offset = ExtentSummaryHeaderCodec.StructSize;
        for (var i = 0; i < count; i++)
        {
            var rec = ExtentSummaryRecordCodec.Read(payload.Slice(offset, ExtentSummaryRecordCodec.StructSize));
            offset += ExtentSummaryRecordCodec.StructSize;
            if (rec.Length <= 0 || rec.Kind > KindAborted) return null;   // 非法记录——整体降级
            records.Add(rec.Kind switch
            {
                KindCommittedDense => new ExtentRecord(rec.Start, rec.Start + rec.Length, ExtentStateCode.Committed, sparse: false),
                KindCommittedSparse => new ExtentRecord(rec.Start, rec.Start + rec.Length, ExtentStateCode.Committed, sparse: true),
                KindWasted => new ExtentRecord(rec.Start, rec.Start + rec.Length, ExtentStateCode.Wasted),
                _ => new ExtentRecord(rec.Start, rec.Start + rec.Length, ExtentStateCode.Aborted),
            });
        }
        return records;
    }
}

/// <summary>
/// 地址表 reader 可选实现的段区间摘要旁路——扫盘 reader 从 meta extension 捕获，
/// <see cref="SegmentTable"/>.LoadAddressTable 探测并安装（精确重建洞布局，VII-3）。
/// </summary>
internal interface IExtentSummaryProvider
{
    /// <summary>segId → 摘要 payload。null = 本 reader 无摘要（内存引擎/快照等）。</summary>
    IReadOnlyDictionary<int, byte[]>? ExtentSummaries { get; }
}
