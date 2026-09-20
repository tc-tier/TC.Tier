using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.Analyzers;

/// <summary>
/// 治理配置读数通道（tier_layer.* / tier_forbidden.* 两规则族共用）：
/// <para>★ Roslyn 通道语义：.globalconfig（is_global=true）键进 GlobalOptions；.editorconfig
///   （含无节键）只进 per-tree options——只读 GlobalOptions 的通道在 CLI dotnet build 下对
///   .editorconfig 声明静默失效（#459）。</para>
/// <para>★ 合并契约：GlobalOptions 优先直读（.globalconfig 显式全局面），治理键再取全部语法树
///   options 并集补齐（规则键全树一致——同键异值 = 配置冲突，产出冲突错误串由调用方按
///   TCSG139 fail-fast 上报，绝静默取一）；非治理键不进合并视图（各树工具键可合法互异）。</para>
/// </summary>
internal static class TierConfigChannel
{
    /// <summary>合并全局与全树配置为一个只读视图；跨源同键异值产出冲突错误串（TCSG139 口径）。</summary>
    /// <param name="provider">分析器配置提供者。</param>
    /// <param name="trees">编译全部语法树（per-tree options 的键源）。</param>
    /// <param name="tierKeyPrefix">本规则族的键命名空间前缀（只参与本族键的并集与冲突检测——
    /// 族间配置互不越权报告）。</param>
    /// <param name="conflicts">冲突错误串收集器（调用方并入 TCSG139 报告）。</param>
    /// <returns>合并后的只读配置视图。</returns>
    public static AnalyzerConfigOptions Merge(AnalyzerConfigOptionsProvider provider,
        IEnumerable<SyntaxTree> trees, string tierKeyPrefix, List<string> conflicts)
    {
        var global = provider.GlobalOptions;
        // 编辑器配置键规范即大小写不敏感——跨源同键异大小写按同键合并/冲突检测（不按异键双记）
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in global.Keys)
        {
            if (global.TryGetValue(key, out var value)) merged[key] = value;
        }

        // 同一 options 实例只消费一次（同目录树共享 editorconfig 作用域——实例引用相同）
        var seen = new HashSet<AnalyzerConfigOptions>();
        var conflictingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in trees)
        {
            var treeOptions = provider.GetOptions(tree);
            if (!seen.Add(treeOptions)) continue;

            foreach (var key in treeOptions.Keys)
            {
                if (!key.StartsWith(tierKeyPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (!treeOptions.TryGetValue(key, out var value)) continue;

                if (merged.TryGetValue(key, out var existing))
                {
                    if (existing != value && conflictingKeys.Add(key))
                        conflicts.Add($"键 '{key}' 跨配置源值冲突（'{existing}' vs '{value}'）——治理键必须全树一致");
                }
                else
                {
                    merged[key] = value;
                }
            }
        }

        return new MergedOptions(global, merged);
    }

    /// <summary>合并只读视图：global 直读（含全部原生键），治理键走并集表兜底；
    /// Keys = 全局键 ∪（未被全局覆盖的治理并集键）——键不重复枚举。</summary>
    private sealed class MergedOptions(AnalyzerConfigOptions global, Dictionary<string, string> tierValues)
        : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
            => global.TryGetValue(key, out value!) || tierValues.TryGetValue(key, out value!);

        public override IEnumerable<string> Keys
        {
            get
            {
                foreach (var key in global.Keys) yield return key;
                foreach (var key in tierValues.Keys)
                {
                    if (!global.TryGetValue(key, out _)) yield return key;
                }
            }
        }
    }
}
