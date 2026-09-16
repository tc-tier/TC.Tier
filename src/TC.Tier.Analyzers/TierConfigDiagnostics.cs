using Microsoft.CodeAnalysis;

namespace TC.Tier.Analyzers;

/// <summary>
/// tier_layer.* / tier_forbidden.* 共用诊断——配置非法 fail-fast（TCSG139）。
/// 单描述符共享：包内单分析器类对外，诊断 ID 不跨类重复（RS1019）。
/// </summary>
internal static class TierConfigDiagnostics
{
    internal static readonly DiagnosticDescriptor ConfigInvalidRule = new(
        id: "TCSG139",
        title: "分析器配置非法（tier_layer.*/tier_forbidden.*）",
        messageFormat: "{0}——配置错误 fail-fast，禁静默忽略。",
        category: "TierCode",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        customTags: WellKnownDiagnosticTags.CompilationEnd);
}
