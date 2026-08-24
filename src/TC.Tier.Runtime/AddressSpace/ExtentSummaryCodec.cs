namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段区间表摘要编解码（VII-3 reopen extent 级保真）——把段内终态区间布局 RLE 压进 meta extension（≤4KB）。
/// <para>★ 写时机：段满 meta 写（OnSegmentFullCoreAsync）+ Dispose 尾段补写（FlushUnfinishedSegmentMeta）——
///   崩溃只丢活动尾段的洞布局，降级为现状的粗粒度重建。</para>
/// <para>★ 格式：[magic][version][count:u16][flags:u8] + 记录[start:i64][length:i64][kind:u8]（17B/条）。
///   kind：0=Committed 稠密、1=Committed+sparse、2=Wasted、3=Aborted。在途（Leased）不编码。</para>
/// <para>★ 超容量（&gt;240 条）或解码无效 → null → 调用方降级粗粒度（与无摘要等价，不失败）。</para>
/// </summary>
internal static class ExtentSummaryCodec
{
    internal const byte Magic = 0xE1;
    internal const byte Version = 1;
    private const int HeaderSize = 5;   // magic + version + count(lo,hi) + flags
    private const int RecordSize = 17;  // start(8) + length(8) + kind(1)
    internal const int MaxPayload = 4096;
    internal const int MaxRecords = (MaxPayload - HeaderSize) / RecordSize;

    private const byte KindCommittedDense = 0;
    private const byte KindCommittedSparse = 1;
    private const byte KindWasted = 2;
    private const byte KindAborted = 3;

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

        var payload = new byte[HeaderSize + count * RecordSize];
        payload[0] = Magic;
        payload[1] = Version;
        payload[2] = (byte)(count & 0xFF);
        payload[3] = (byte)(count >> 8);
        payload[4] = 0;   // flags（bit0=truncated——超限场景直接返回 null，不用截断标记）

        var offset = HeaderSize;
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            if (ExtentStateCode.IsInFlight(r.State)) continue;
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset), r.Start);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(offset + 8), r.End - r.Start);
            payload[offset + 16] = r.State switch
            {
                var s when ExtentStateCode.IsCommitted(s) => r.Sparse ? KindCommittedSparse : KindCommittedDense,
                ExtentStateCode.Wasted => KindWasted,
                ExtentStateCode.Aborted => KindAborted,
                _ => KindCommittedDense,
            };
            offset += RecordSize;
        }
        return payload;
    }

    /// <summary>解码。null = magic/version 不符或 payload 非法（调用方降级粗粒度）。</summary>
    internal static List<ExtentRecord>? Decode(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < HeaderSize || payload[0] != Magic || payload[1] != Version) return null;
        var count = payload[2] | (payload[3] << 8);
        if (payload.Length != HeaderSize + count * RecordSize) return null;

        var records = new List<ExtentRecord>(count);
        var offset = HeaderSize;
        for (var i = 0; i < count; i++)
        {
            var start = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset));
            var length = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload.Slice(offset + 8));
            var kind = payload[offset + 16];
            if (length <= 0 || kind > KindAborted) return null;   // 非法记录——整体降级
            records.Add(kind switch
            {
                KindCommittedDense => new ExtentRecord(start, start + length, ExtentStateCode.Committed, sparse: false),
                KindCommittedSparse => new ExtentRecord(start, start + length, ExtentStateCode.Committed, sparse: true),
                KindWasted => new ExtentRecord(start, start + length, ExtentStateCode.Wasted),
                _ => new ExtentRecord(start, start + length, ExtentStateCode.Aborted),
            });
            offset += RecordSize;
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
