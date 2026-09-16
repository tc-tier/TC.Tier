// netstandard2.0 目标下 init 访问器/record 所需的编译器约定类型（net5+ BCL 内建同名类型；
// 此处为同全名内部占位——零行为，仅供编译器解析 init modreq）
namespace System.Runtime.CompilerServices;

internal static class IsExternalInit
{
}
