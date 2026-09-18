using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.Analyzers.Tests;

/// <summary>
/// 分析器配置测试替身——把字典形态的 tier_layer.*/tier_forbidden.* 键值喂给
/// AnalyzerConfigOptionsProvider（模拟 .editorconfig 全局节）。
/// </summary>
internal sealed class TestAnalyzerConfigOptions : AnalyzerConfigOptions
{
    internal static readonly TestAnalyzerConfigOptions Empty = new(new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> _values;

    public TestAnalyzerConfigOptions(IReadOnlyDictionary<string, string> values) => _values = values;

    public override bool TryGetValue(string key, out string value)
        => _values.TryGetValue(key, out value!);

    public override IEnumerable<string> Keys => _values.Keys;
}

internal sealed class TestAnalyzerConfigOptionsProvider : AnalyzerConfigOptionsProvider
{
    private readonly AnalyzerConfigOptions _global;
    private readonly AnalyzerConfigOptions _tree;

    /// <summary>globalConfig = GlobalOptions（.globalconfig 通道）；treeValues = 每树 options
    /// （.editorconfig 通道——#459 读数通道回归面；null = 空树配置）。</summary>
    public TestAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> values,
        IReadOnlyDictionary<string, string>? treeValues = null)
    {
        _global = new TestAnalyzerConfigOptions(values);
        _tree = treeValues is null ? TestAnalyzerConfigOptions.Empty : new TestAnalyzerConfigOptions(treeValues);
    }

    public override AnalyzerConfigOptions GlobalOptions => _global;

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _tree;

    public override AnalyzerConfigOptions GetOptions(AdditionalText text) => TestAnalyzerConfigOptions.Empty;
}
