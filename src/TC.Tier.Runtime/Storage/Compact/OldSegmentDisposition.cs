using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Storage.Compact;

/// <summary>
/// 旧段处置记录——Compact 后对原源段的处理方案。
/// <para>★ Mode = 0：DeleteFile 整段删（整段被覆盖）。</para>
/// <para>★ Mode = 1：PunchHole 抹除被搬迁的部分（段文件保留，区间外数据继续可读）。</para>
/// <para>★ 源生成器自动生成 OldSegmentDispositionCodec.Write/Read/StructSize。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct OldSegmentDisposition
{
    /// <summary>处置模式：0 = DeleteFile，1 = PunchHole。</summary>
    internal const byte ModeDelete = 0;
    internal const byte ModePunchHole = 1;

    [FieldOffset(0)] public int SegId;
    [FieldOffset(4)] public byte Mode;
    [FieldOffset(5)] public byte Reserved1;
    [FieldOffset(6)] public ushort Reserved2;
    /// <summary>PunchHole 起始（Mode=1 时有效；Mode=0 时为 0）。</summary>
    [FieldOffset(8)] public long PunchStart;
    /// <summary>PunchHole 终止（Mode=1 时有效；Mode=0 时为 0）。</summary>
    [FieldOffset(16)] public long PunchEnd;

    /// <summary>全字段构造（源生成器 codec 反序列化用）。</summary>
    public OldSegmentDisposition(int segId, byte mode, byte reserved1, ushort reserved2,
        long punchStart, long punchEnd)
    {
        SegId = segId;
        Mode = mode;
        Reserved1 = reserved1;
        Reserved2 = reserved2;
        PunchStart = punchStart;
        PunchEnd = punchEnd;
    }

    /// <summary>便捷构造（Reserved 默认 0）。</summary>
    public OldSegmentDisposition(int segId, byte mode, long punchStart, long punchEnd)
        : this(segId, mode, 0, 0, punchStart, punchEnd) { }

    /// <summary>是否为 DeleteFile 模式。</summary>
    public bool IsDelete => Mode == ModeDelete;

    public override string ToString()
        => IsDelete ? $"seg{SegId} Delete" : $"seg{SegId} Punch[{PunchStart},{PunchEnd})";
}
