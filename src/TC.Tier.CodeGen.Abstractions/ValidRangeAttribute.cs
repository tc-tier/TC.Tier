namespace TC.Tier.CodeGen;

/// <summary>字段必须在 [min, max] 范围内。用于有界数值字段（PayloadLength/Length 等）。</summary>
/// <example><c>[ValidRange(0, LogMetaHeader.MaxEntrySize)] public ushort PayloadLength;</c></example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ValidRangeAttribute(object min, object max) : Attribute
{
    public object Min { get; } = min;
    public object Max { get; } = max;
}