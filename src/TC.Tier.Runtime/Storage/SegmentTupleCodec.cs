using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 段元组头——36B，配合源生成器自动序列化（<c>SegmentTupleHeaderCodec.Write/Read/StructSize</c>）。
/// <para>★ 布局：Magic(8) + Version(1) + State(1，StableState:byte) + MaxOffset/GrowthLimit/RealSize(3×8) + SummaryLength(2)。</para>
/// <para>★ 校验：magic + version 经 <see cref="IsValid"/>（写盘构造填充，读盘校验）；CRC32C 覆盖全部前导字节由 codec 层承担。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 36)]
internal struct SegmentTupleHeader
{
    /// <summary>Magic = "TC_SEGTU"（0x54435F5345475455，8B）。</summary>
    private const long Magic = 0x54435F5345475455;

    /// <summary>当前格式版本。</summary>
    private const byte VersionCurrent = 2;

    [FieldOffset(0), ValidEquals(Magic)] public long MagicValue;
    [FieldOffset(8), ValidEquals(VersionCurrent)] public byte Version;
    [FieldOffset(9)] public StableState State;
    [FieldOffset(10)] public long MaxOffset;
    [FieldOffset(18)] public long GrowthLimit;
    [FieldOffset(26)] public long RealSize;
    /// <summary>区间摘要字节数（0 = 无摘要——恢复降级粗粒度）。</summary>
    [FieldOffset(34)] public ushort SummaryLength;

    /// <summary>默认构造——填充 magic + version（Crc 由 codec 层算后追加）。</summary>
    public SegmentTupleHeader(StableState state, long maxOffset, long growthLimit, long realSize,
        ushort summaryLength)
    {
        MagicValue = Magic;
        Version = VersionCurrent;
        State = state;
        MaxOffset = maxOffset;
        GrowthLimit = growthLimit;
        RealSize = realSize;
        SummaryLength = summaryLength;
    }

    /// <summary>全字段构造（源生成器 codec 反序列化用）。</summary>
    public SegmentTupleHeader(long magicValue, byte version, StableState state, long maxOffset,
        long growthLimit, long realSize, ushort summaryLength)
    {
        MagicValue = magicValue;
        Version = version;
        State = state;
        MaxOffset = maxOffset;
        GrowthLimit = growthLimit;
        RealSize = realSize;
        SummaryLength = summaryLength;
    }

    /// <summary>是否合法 Header（magic + version 校验）。</summary>
    public bool IsValid => MagicValue == Magic && Version == VersionCurrent;
}

/// <summary>
/// 段元组编解码（Payload v2，D-13）——per-segment 状态经段文件 <c>FileExtra</c> 平面持久化。
/// <para>★ 布局：<c>[SegmentTupleHeader（源生成器）][Summary 变长][CRC32C(4)]</c>；
///   <see cref="IFileSystem.MaxFileExtraBytes"/> 硬预算 1536B 是设计前提（全介质兼容）：
///   摘要由引擎侧按条目收缩至预算内（<c>EncodeExtentSummary</c> 二分），codec 不做截断。</para>
/// <para>★ 手写面只剩变长 summary 拼接 + CRC——固定布局与校验全部由源生成器承担。</para>
/// <para>★ 解码任何不一致（magic/version/长度/CRC）→ null（非致命降级，恢复回退 fileSize 权威）。
///   SegId 不编码（文件名承载）；ExtensionLocation 不存在（摘要内联，无溢出文件）。</para>
/// </summary>
internal static class SegmentTupleCodec
{
    private const int CrcSize = 4;

    /// <summary>摘要字节预算（硬上限）。</summary>
    internal const int MaxSummary = IFileSystem.MaxFileExtraBytes - SegmentTupleHeaderCodec.StructSize - CrcSize;

    /// <summary>
    /// 编码段元组（含区间摘要——调用方保证 summary ≤ <see cref="MaxSummary"/>）。
    /// </summary>
    internal static byte[] Encode(StableState state, long maxOffset, long growthLimit, long realSize,
        ReadOnlySpan<byte> summary)
    {
        var payload = new byte[SegmentTupleHeaderCodec.StructSize + summary.Length + CrcSize];
        var header = new SegmentTupleHeader(state, maxOffset, growthLimit, realSize, (ushort)summary.Length);
        SegmentTupleHeaderCodec.Write(payload, in header);
        summary.CopyTo(payload.AsSpan(SegmentTupleHeaderCodec.StructSize));
        var crc = UnifiedCrc.ComputeCrc32C(payload.AsSpan(0, payload.Length - CrcSize));
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(payload.Length - CrcSize), crc);
        return payload;
    }

    /// <summary>
    /// 解码段元组——任何不一致（magic/version/长度/CRC）返回 null（恢复侧回退 fileSize 权威，非致命）。
    /// </summary>
    internal static (StableState State, long MaxOffset, long GrowthLimit, long RealSize, byte[] Summary)? Decode(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length < SegmentTupleHeaderCodec.StructSize + CrcSize) return null;
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(payload.Length - CrcSize));
        if (UnifiedCrc.ComputeCrc32C(payload.Slice(0, payload.Length - CrcSize)) != crc) return null;
        var header = SegmentTupleHeaderCodec.Read(payload);
        if (!header.IsValid) return null;
        if (payload.Length != SegmentTupleHeaderCodec.StructSize + header.SummaryLength + CrcSize) return null;
        var summary = header.SummaryLength == 0
            ? Array.Empty<byte>()
            : payload.Slice(SegmentTupleHeaderCodec.StructSize, header.SummaryLength).ToArray();
        return (header.State, header.MaxOffset, header.GrowthLimit, header.RealSize, summary);
    }
}
