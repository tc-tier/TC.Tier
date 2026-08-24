namespace TC.Tier.CodeGen;

// ══ 二进制布局字段校验特性（由 BinaryLayoutGenerator 读取，生成 Validate 方法）══
//
// 这些特性专为 struct 字段编译期校验设计，声明字段的合法值约束。SG 读它们生成
// XxxCodec.Validate(in header): bool（独立方法——调用方自行检查；注意：Write 的
// validate:true 是"ValidEquals 常量防御性补全"（不信任入参强制写常量），并非调用
// Validate()——HasFlags/Range/NonDefault 的校验请显式调 Validate()——契约澄清）。
//
// ★ 不复用 BCL System.ComponentModel.DataAnnotations——那套对 struct 字段语义不对
//   （struct 不可为 null，Required 对值类型意为"非零"，且面向反射式运行时校验，非编译期生成）。
//
// 参数用 object 接收 const 表达式（特性参数不支持泛型 T），SG 按 SpecialType 还原比较。

/// <summary>字段必须 == 指定常量。用于规范字段（MagicValue/Version/Flags 等）。</summary>
/// <example><c>[ValidEquals(LogMetaHeader.Magic)] public uint MagicValue;</c></example>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
public sealed class ValidEqualsAttribute(object expected) : Attribute
{
    /// <summary>期望的常量值（uint/ushort/ulong/long/int/byte/string 等）。</summary>
    public object Expected { get; } = expected;
}