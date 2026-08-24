namespace TC.Tier.CodeGen;

/// <summary>字段必须非默认零值。用于"必填任意非零值"语义。</summary>
/// <example><c>[ValidNonDefault] public ulong PageId;</c></example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ValidNonDefaultAttribute : Attribute { }