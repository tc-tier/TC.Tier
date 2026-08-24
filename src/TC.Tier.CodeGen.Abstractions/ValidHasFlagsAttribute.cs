namespace TC.Tier.CodeGen;

/// <summary>位掩码包含校验：字段必须包含 mask 的所有位（(field &amp; mask) == mask）。
/// 用于 Flags 这种"基线位必含 + 运行时可叠加动态位"的字段（如 entry meta 标记、末页标记）。</summary>
/// <example><c>[ValidHasFlags(DeltaLogHeader.DefaultFlags)] public ushort Flags;</c>
/// —— 校验 (Flags &amp; DefaultFlags) == DefaultFlags，允许运行时 | FLAG_ENTRY_IS_META 等动态位。</example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ValidHasFlagsAttribute(object mask) : Attribute
{
    /// <summary>必须包含的基线位掩码。</summary>
    public object Mask { get; } = mask;
}