using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Storage.Compact;

/// <summary>
/// Compact commit marker 头部——24B，配合源生成器自动序列化。
/// <para>★ 写盘前算 CRC32 填回 <see cref="Crc"/> 字段；读盘时校验 magic + version + CRC。</para>
/// <para>★ BinaryLayout 源生成器自动生成 CompactMarkerHeaderCodec.Write/Read/StructSize。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct CompactMarkerHeader
{
    // ★ CRC 偏移用 SG 生成的 CompactMarkerHeaderCodec.Offset_Crc，禁止手写重复常量。

    /// <summary>Magic = "TC_COMPA"（0x54435F434F4D5041，8B）。</summary>
    private const long Magic = 0x54435F434F4D5041;

    /// <summary>当前格式版本（major=0, minor=2）。</summary>
    private const ushort VersionCurrent = 0x0002;

    [FieldOffset(0), ValidEquals(Magic)] public long MagicValue;
    [FieldOffset(8)] public ushort Version;
    [FieldOffset(10)] public CompactType CompactType;
    [FieldOffset(11)] public byte Reserved;
    [FieldOffset(12)] public int NewSegCount;
    /// <summary>旧段处置条目数（含 DeleteFile 整段删 + PunchHole 部分抹除两类）。</summary>
    [FieldOffset(16)] public int OldSegDispositionCount;
    /// <summary>CRC32——覆盖 Header（除本字段 4B）+ body（NewSegIds + OldSegDispositions）。</summary>
    [FieldOffset(20)] public uint Crc;

    /// <summary>默认构造——填充 magic + version（Crc 由调用方算后写回）。</summary>
    public CompactMarkerHeader(CompactType compactType, int newSegCount, int oldSegDispositionCount)
    {
        MagicValue = Magic;
        Version = VersionCurrent;
        CompactType = compactType;
        Reserved = 0;
        NewSegCount = newSegCount;
        OldSegDispositionCount = oldSegDispositionCount;
        Crc = 0;
    }

    /// <summary>全字段构造（源生成器 codec 反序列化用）。</summary>
    public CompactMarkerHeader(long magicValue, ushort version, CompactType compactType, byte reserved,
        int newSegCount, int oldSegDispositionCount, uint crc)
    {
        MagicValue = magicValue;
        Version = version;
        CompactType = compactType;
        Reserved = reserved;
        NewSegCount = newSegCount;
        OldSegDispositionCount = oldSegDispositionCount;
        Crc = crc;
    }

    /// <summary>是否合法 Header（magic + version 校验）。</summary>
    public bool IsValid => MagicValue == Magic && Version == VersionCurrent;
}
