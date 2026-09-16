using Microsoft.CodeAnalysis;

namespace TC.Tier.CodeGen.Templating;

/// <summary>字段 emit 形态分类。</summary>
internal enum FieldEmitKind
{
    /// <summary>BinaryPrimitives 小端读写（uint/ushort/ulong/long/int + 同底层 enum）。</summary>
    Primitive,

    /// <summary>单字节直读写（byte + byte 底层 enum）。</summary>
    Byte,

    /// <summary>嵌套 [BinaryLayout] struct——读写委托被嵌套 struct 的 Codec。</summary>
    Nested,
}

/// <summary>字段 emit 参数集（TypeEmitter 单一事实源——旧六份重复 switch 的收敛点）。</summary>
/// <param name="Kind">字段 emit 形态分类（Primitive/Byte/Nested）。</param>
/// <param name="Primitive">BinaryPrimitives 原语名（如 "UInt32"）；Byte/Nested 形态为空串。</param>
/// <param name="Size">字段字节数（字面常量字符串，如 "4"）。</param>
/// <param name="ValueCast">写侧 cast 前缀（基元为空串，enum 为 "(uint)" 等）。</param>
/// <param name="ReadCast">读侧 cast 前缀（基元为空串，enum 转回枚举类型）。</param>
/// <param name="NestedFqn">嵌套 struct 全名（仅 Nested 形态有值，其余为空串）。</param>
internal readonly record struct FieldEmitForm(
    FieldEmitKind Kind,
    string Primitive,
    string Size,
    string ValueCast,
    string ReadCast,
    string NestedFqn);

/// <summary>
/// ★ 类型 → emit 参数单一映射表（取代旧 EmitWriteStmt/EmitWriteLine/EmitWriteNonPrimitive/
///   EmitReadExpr/EmitReadLine/EmitReadNonPrimitive 六处 switch 的重复维护）。
/// <para>byte 与 byte 底层 enum 走 Byte 直读写；其余基元与 enum 走 BinaryPrimitives
///   （模板填 Primitive 名 + cast）；嵌套 struct 走 Nested（FQN Codec 委托）。</para>
/// </summary>
internal static class TypeEmitter
{
    /// <summary>
    /// ★ Spec 27: 判断字段类型是否嵌套 unmanaged struct（读写委托给被嵌套 struct 的 Codec）。
    /// <para>★ 跨程序集可靠：用类型系统判断（IsValueType + IsUnmanagedType），不依赖
    ///   <c>[StructLayout]</c> 伪属性（跨程序集 GetAttributes 拿不到）。</para>
    /// </summary>
    /// <param name="s">待判字段类型符号。</param>
    /// <returns>true 表示该类型为非 System_* 命名空间下的 unmanaged struct（应作嵌套字段委托其 Codec）。</returns>
    public static bool IsNestedStruct(ITypeSymbol s)
        => s is { TypeKind: TypeKind.Struct, IsUnmanagedType: true }
           && !s.SpecialType.ToString().StartsWith("System_", StringComparison.Ordinal);

    /// <summary>
    /// 字段 emit 参数。resolvedSize = 生成阶段已解析的字段字节数（嵌套 struct 查收集表得来）。
    /// <para>★ 不支持的类型抛异常——TCSG003 在收集阶段已拦截，走到这里是生成器内部缺陷。</para>
    /// </summary>
    /// <param name="type">字段类型符号（基元/enum/嵌套 struct）。</param>
    /// <param name="resolvedSize">生成阶段已解析的字段字节数（嵌套 struct 查收集表得来；基元/enum 不用）。</param>
    /// <returns>该字段对应的 emit 参数集（Kind/Primitive/Size/casts/NestedFqn）。</returns>
    public static FieldEmitForm Form(ITypeSymbol type, int resolvedSize)
    {
        switch (type.SpecialType)
        {
            case SpecialType.System_UInt32: return new(FieldEmitKind.Primitive, "UInt32", "4", "", "", "");
            case SpecialType.System_UInt16: return new(FieldEmitKind.Primitive, "UInt16", "2", "", "", "");
            case SpecialType.System_UInt64: return new(FieldEmitKind.Primitive, "UInt64", "8", "", "", "");
            case SpecialType.System_Int64: return new(FieldEmitKind.Primitive, "Int64", "8", "", "", "");
            case SpecialType.System_Int32: return new(FieldEmitKind.Primitive, "Int32", "4", "", "", "");
            case SpecialType.System_Byte: return new(FieldEmitKind.Byte, "", "1", "", "", "");
        }
        if (IsNestedStruct(type))
            return new(FieldEmitKind.Nested, "", resolvedSize.ToString(), "", "", type.ToDisplayString());
        if (type is { TypeKind: TypeKind.Enum } e && e is INamedTypeSymbol named
            && named.EnumUnderlyingType is { } underlying)
        {
            var readCast = "(" + e.ToDisplayString() + ")";
            return underlying.SpecialType switch
            {
                SpecialType.System_UInt32 => new(FieldEmitKind.Primitive, "UInt32", "4", "(uint)", readCast, ""),
                SpecialType.System_UInt16 => new(FieldEmitKind.Primitive, "UInt16", "2", "(ushort)", readCast, ""),
                SpecialType.System_UInt64 => new(FieldEmitKind.Primitive, "UInt64", "8", "(ulong)", readCast, ""),
                SpecialType.System_Int64 => new(FieldEmitKind.Primitive, "Int64", "8", "(long)", readCast, ""),
                SpecialType.System_Int32 => new(FieldEmitKind.Primitive, "Int32", "4", "(int)", readCast, ""),
                SpecialType.System_Byte => new(FieldEmitKind.Byte, "", "1", "(byte)", readCast, ""),
                _ => throw new System.InvalidOperationException(
                    $"enum 底层类型 {underlying.ToDisplayString()} 不在 Emit 支持集——TCSG003 应已拦截（生成器内部缺陷）。"),
            };
        }
        throw new System.InvalidOperationException(
            $"字段类型 {type.ToDisplayString()} 不在 Emit 支持集——TCSG003 应已拦截（生成器内部缺陷）。");
    }
}
