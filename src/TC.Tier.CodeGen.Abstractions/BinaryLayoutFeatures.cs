namespace TC.Tier.CodeGen;

/// <summary>
/// [BinaryLayout] 生成能力组合标志。
/// <para>默认 None：仅生成 Write/Read/Validate。
/// 按需组合 StructSize、FieldConstants、FieldReaders、FieldWriters。</para>
/// </summary>
[System.Flags]
public enum BinaryLayoutFeatures
{
    None            = 0,

    /// <summary>生成 StructSize 常量（= [StructLayout].Size 值）。</summary>
    StructSize      = 1 << 0,

    /// <summary>生成每个字段的 Offset_* / Size_* 偏移/尺寸常量。</summary>
    FieldConstants  = 1 << 1,

    /// <summary>生成每个字段的 Read_* 单值读取方法。</summary>
    FieldReaders    = 1 << 2,

    /// <summary>生成每个字段的 Write_* 单值写入方法（对称 Read_*，收口字节序，避免业务层手写 BinaryPrimitives）。</summary>
    FieldWriters    = 1 << 3,

    /// <summary>Read_* + Write_* = 全部单字段读写方法。</summary>
    FieldAccessors  = FieldReaders | FieldWriters,

    /// <summary>StructSize + FieldConstants = 全部常量。</summary>
    Constants       = StructSize | FieldConstants,

    /// <summary>全部能力。</summary>
    All             = Constants | FieldAccessors,
}
