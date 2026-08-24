namespace TC.Tier.CodeGen;

/// <summary>
/// 标记 struct 由 BinaryLayoutGenerator 生成 XxxCodec。
/// <para>★ 要求 struct 用 [StructLayout(LayoutKind.Explicit, Size = XxxSize)] + [FieldOffset]。</para>
/// <para>★ 总字节数从 [StructLayout].Size 取值（源生成器交叉校验 Size vs 字段偏移和，不一致报 TCSG001）。</para>
/// <para>★ OrFlags/IsEmpty/Features 控制额外生成能力（替代 [GenerateOrFlags]/[GenerateIsEmpty]）。</para>
/// <para>★ 字段类型支持：基元（uint/ushort/ulong/long/int/byte）+ 同底层类型的 enum + <b>嵌套 [BinaryLayout] struct</b>。</para>
/// <para>★ 嵌套 struct（如 SegmentAddress）：字段类型必须是标了 [BinaryLayout] 的 struct——源生成器
///   靠收集表查其大小（标记驱动：只有收集表里的 struct 才支持嵌套）。未标记的 struct 作嵌套字段报 TCSG002。</para>
/// <para>★ readonly struct：Read 方法用 MemoryMarshal.Read 整体反序列化（object initializer 不能赋值 readonly 字段）。</para>
/// <para>★ 字段校验特性 [ValidEquals]/[ValidRange]/[ValidHasFlags]/[ValidNonDefault] 保留在字段上独立声明。</para>
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class BinaryLayoutAttribute : System.Attribute
{
    /// <summary>对指定字段生成 OrFlags 方法（字段须为 ushort，如 "Flags"）。</summary>
    public string? OrFlags { get; set; }

    /// <summary>对指定字段生成 IsEmptyRecord 方法（字段须为 uint，如 "MagicValue"）。</summary>
    public string? IsEmpty { get; set; }

    /// <summary>生成额外能力（StructSize / FieldConstants / FieldReaders）。</summary>
    public BinaryLayoutFeatures Features { get; set; }
}
