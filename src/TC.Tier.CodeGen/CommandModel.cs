using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TC.Tier.CodeGen;

/// <summary>
/// 命令模型——<see cref="CommandGenerator"/> 的提取/装配数据形态。
/// <para>★ 全部字段为可等价基元/字符串（增量管道跨轮缓存要求）；类型绑定信息在提取期
///   （语义模型在场）算成 <see cref="BindKind"/>，发射期零语义依赖。</para>
/// </summary>
internal static class CommandModel
{
    /// <summary>参数绑定分类（发射期按此分流 TryParse 直调形态——零反射）。</summary>
    internal enum BindKind
    {
        /// <summary>string——直取。</summary>
        String = 0,
        /// <summary>bool——true/false 字面。</summary>
        Bool,
        /// <summary>int/uint/long/ulong 整数族（InvariantCulture TryParse）。</summary>
        Integer,
        /// <summary>double/decimal 浮点族（InvariantCulture TryParse）。</summary>
        Floating,
        /// <summary>Guid。</summary>
        Guid,
        /// <summary>DateTimeOffset（InvariantCulture）。</summary>
        DateTimeOffset,
        /// <summary>任意枚举（Enum&lt;T&gt; TryParse 泛型形态 + IsDefined 校验）。</summary>
        Enum,
        /// <summary>Nullable&lt;T&gt; 包裹（InnerKind 为被包裹形态）。</summary>
        Nullable,
        /// <summary>请求体复杂类型（HTTP body JSON / CLI stdin）。</summary>
        Body,
        /// <summary>CancellationToken——框架注入特判，不参与绑定。</summary>
        CancellationToken,
    }

    /// <summary>单参数模型。</summary>
    internal sealed record ParamModel(
        string Name,
        ParamRole Role,
        BindKind Kind,
        BindKind InnerKind,
        string TypeDisplay,
        string TypeOfDisplay,
        string EnumFqn,
        int Position,
        string LongName,
        char ShortName,
        bool HasDefaultValue,
        string DefaultValueExpr,
        bool Annotated,
        string Unbindable,
        Location Loc)
    {
        /// <summary>参数声明为可空（类型显示名尾缀 '?'）——[CommandBody] 参数据此放行 JSON null 绑定。</summary>
        internal bool AllowsNull => TypeDisplay.EndsWith("?", StringComparison.Ordinal);
    }

    /// <summary>参数角色。</summary>
    internal enum ParamRole
    {
        /// <summary>位置参数（CommandArg）。</summary>
        Arg = 0,
        /// <summary>具名选项（CommandOption）。</summary>
        Option,
        /// <summary>请求体（CommandBody）。</summary>
        Body,
    }

    /// <summary>返回形态（生成器按此分流 await/直调/渲染）。</summary>
    internal enum ReturnKind
    {
        /// <summary>void。</summary>
        Void = 0,
        /// <summary>同步 T（非 void）。</summary>
        Sync,
        /// <summary>Task。</summary>
        Task,
        /// <summary>Task&lt;T&gt;。</summary>
        TaskOfT,
        /// <summary>ValueTask。</summary>
        ValueTask,
        /// <summary>ValueTask&lt;T&gt;。</summary>
        ValueTaskOfT,
    }

    /// <summary>业务异常映射（CommandError）。</summary>
    internal sealed record ErrorMap(string ExceptionFqn, int Status, int InheritanceDepth);

    /// <summary>叶子命令模型。</summary>
    internal sealed record CmdModel(
        string MethodName,
        string Name,
        string Description,
        string Route,
        string Method,
        bool IsStatic,
        string ContainerFqn,
        string ContainerAcquireExpr,
        ReturnKind Return,
        string ResultTypeDisplay,
        ImmutableArray<ParamModel> Params,
        ImmutableArray<ErrorMap> Errors,
        bool ResultSimple,
        ImmutableArray<string> IllegalErrorFqns,
        Location Loc);

    /// <summary>命令组模型（根/嵌套共用——IsRoot 区分）。</summary>
    internal sealed record GroupModel(
        string Fqn,
        string Name,
        string Description,
        bool IsRoot,
        string ParentFqn,
        string AcquireExpr,
        bool MemberLinked,
        bool FallbackInstantiable,
        ImmutableArray<CmdModel> Commands,
        Location Loc);

    // ══════════ 提取（语义模型在场）══════════

    /// <summary>组类型是否带 [CommandGroup] 标注。</summary>
    internal static bool HasGroupAttribute(INamedTypeSymbol type)
        => type.GetAttributes().Any(a => a.AttributeClass?.Name == "CommandGroupAttribute");

    /// <summary>组名——位置/命名实参均接受，缺省类型名 kebab-case。</summary>
    internal static string GroupName(INamedTypeSymbol type, AttributeData attr)
    {
        if (attr.ConstructorArguments.Length == 1 && attr.ConstructorArguments[0].Value is string positional
            && positional.Length > 0) return positional;
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == "Name" && named.Value.Value is string s && s.Length > 0) return s;
        }

        return Kebab(type.Name);
    }

    /// <summary>组描述。</summary>
    internal static string GroupDescription(INamedTypeSymbol type, AttributeData attr)
        => NamedString(attr, "Description") ?? string.Empty;

    internal static string? NamedString(AttributeData attr, string key)
    {
        foreach (var named in attr.NamedArguments)
        {
            if (named.Key == key && named.Value.Value is string s) return s;
        }

        return null;
    }

    /// <summary>PascalCase → kebab-case（组/命令/选项缺省名规则——缩写词按大写边界拆分）。</summary>
    internal static string Kebab(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>生成代码自有标识符（私有处理函数形参＋局部变量）的保留前缀——用户参数名占用即 TCSG060。</summary>
    internal const string ReservedLocalPrefix = "__tcsg_";

    /// <summary>生成命令函数体内根实例形参名（保留命名空间——AcquireExpr 根 token 与模板签名共用此名）。</summary>
    internal const string ReservedServiceName = "__tcsg_service";

    /// <summary>组实例的服务获取表达式（生成命令函数体内的根实例引用形态）：
    /// 根自身 → service；嵌套组沿嵌套链逐级解析（父组 → 祖辈 → 根）——链上首个持有同型
    /// public 属性/字段的宿主即命中，父链递归成链式取值（service.Sys.Discovery 形态）；
    /// 全链未命中回落可访问无参构造（服务注入与嵌套深度解耦，#454——组不必提升为根级属性）。
    /// 无可访问无参构造 → 占位表达式（TCSG061 在组声明处报——生成物不携带注定编译失败的 new）。</summary>
    internal static string AcquireExpr(INamedTypeSymbol group, INamedTypeSymbol root)
        => Acquire(group, root).Expr;

    /// <summary>组实例获取装配：表达式 + 是否经父链成员命中（TCSG061 判定面——
    /// false = 无参构造回落或占位）。</summary>
    internal static (string Expr, bool MemberLinked) Acquire(INamedTypeSymbol group, INamedTypeSymbol root)
    {
        if (SymbolEqualityComparer.Default.Equals(group, root)) return (ReservedServiceName, true);
        for (var owner = group.ContainingType; owner is not null; owner = owner.ContainingType)
        {
            var member = ServiceMemberName(owner, group);
            if (member is not null)
            {
                var ownerExpr = SymbolEqualityComparer.Default.Equals(owner, root)
                    ? ReservedServiceName
                    : AcquireExpr(owner, root);
                return ($"{ownerExpr}.{member}", true);
            }

            if (SymbolEqualityComparer.Default.Equals(owner, root)) break;   // 链到根为止——根外容器不参与
        }

        return HasAccessibleParameterlessCtor(group)
            ? ($"new global::{group.ToDisplayString()}()", false)
            : ($"default(global::{group.ToDisplayString()})!", false);
    }

    /// <summary>组类型无参构造在消费程序集内可访问判定（生成物与组声明同程序集——
    /// public/internal/protected internal 可达；private/protected 不可；抽象类不可 new）。</summary>
    internal static bool HasAccessibleParameterlessCtor(INamedTypeSymbol type)
    {
        if (type.TypeKind == TypeKind.Class && type.IsAbstract) return false;
        foreach (var ctor in type.Constructors)
        {
            if (ctor.IsStatic || ctor.Parameters.Length != 0) continue;
            switch (ctor.DeclaredAccessibility)
            {
                case Accessibility.Public:
                case Accessibility.Internal:
                case Accessibility.ProtectedOrInternal:
                    return true;
            }
        }

        return false;
    }

    /// <summary>宿主上与组类型匹配的可访问 public 属性/字段名（无则 null）。</summary>
    private static string? ServiceMemberName(INamedTypeSymbol owner, INamedTypeSymbol group)
    {
        foreach (var member in owner.GetMembers())
        {
            if (member.DeclaredAccessibility != Accessibility.Public) continue;
            if (member is IPropertySymbol p && SymbolEqualityComparer.Default.Equals(p.Type, group)) return p.Name;
            if (member is IFieldSymbol f && SymbolEqualityComparer.Default.Equals(f.Type, group)) return f.Name;
        }

        return null;
    }

    /// <summary>命令方法返回形态分类。</summary>
    internal static ReturnKind ClassifyReturn(IMethodSymbol method)
    {
        var ret = method.ReturnType;
        if (ret.SpecialType == SpecialType.System_Void) return ReturnKind.Void;
        if (ret is not INamedTypeSymbol named) return ReturnKind.Sync;
        var def = named.OriginalDefinition;
        var ns = def.ContainingNamespace?.ToDisplayString();
        if (ns == "System.Threading.Tasks" && def.Name == "Task")
            return named.TypeArguments.Length > 0 ? ReturnKind.TaskOfT : ReturnKind.Task;
        if (ns == "System.Threading.Tasks" && def.Name == "ValueTask")
            return named.TypeArguments.Length > 0 ? ReturnKind.ValueTaskOfT : ReturnKind.ValueTask;
        return ReturnKind.Sync;
    }

    /// <summary>参数绑定分类——类型集契约（编译期 TryParse 直调零反射）。</summary>
    internal static (BindKind kind, BindKind inner, string enumFqn) ClassifyParam(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol named)
        {
            var def = named.OriginalDefinition;
            if (def.SpecialType == SpecialType.System_Nullable_T && named.TypeArguments.Length == 1)
            {
                var (inner, _, enumFqn) = ClassifyParam(named.TypeArguments[0]);
                return (BindKind.Nullable, inner, enumFqn);
            }

            switch (def.SpecialType)
            {
                case SpecialType.System_String: return (BindKind.String, BindKind.String, string.Empty);
                case SpecialType.System_Boolean: return (BindKind.Bool, BindKind.Bool, string.Empty);
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                    return (BindKind.Integer, BindKind.Integer, string.Empty);
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                    return (BindKind.Floating, BindKind.Floating, string.Empty);
            }

            var display = def.ToDisplayString();
            if (display == "System.Guid") return (BindKind.Guid, BindKind.Guid, string.Empty);
            if (display == "System.DateTimeOffset") return (BindKind.DateTimeOffset, BindKind.DateTimeOffset, string.Empty);
            if (named.TypeKind == TypeKind.Enum)
                return (BindKind.Enum, BindKind.Enum, named.ToDisplayString());
        }

        return (BindKind.Body, BindKind.Body, string.Empty);
    }

    /// <summary>CancellationToken 判定（任意参数位框架注入）。</summary>
    internal static bool IsCancellationToken(ITypeSymbol type)
        => type is INamedTypeSymbol named
            && named.OriginalDefinition.SpecialType != SpecialType.System_Nullable_T
            && named.ToDisplayString() == "System.Threading.CancellationToken";

    /// <summary>默认值表达式文本（可选参数——emit 时直引，如 "5" / "\"abc\"" / "1.5m"）。</summary>
    internal static string DefaultExpr(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue) return string.Empty;
        var v = parameter.ExplicitDefaultValue;
        if (v is null) return "default";
        if (v is string s) return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        if (v is bool b) return b ? "true" : "false";
        if (v is char c) return $"'{c}'";
        return v.ToString() ?? "default";
    }
}
