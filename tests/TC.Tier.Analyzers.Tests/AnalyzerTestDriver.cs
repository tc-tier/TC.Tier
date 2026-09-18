using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace TC.Tier.Analyzers.Tests;

/// <summary>
/// 分析器测试驱动底座——内存编译 + CompilationWithAnalyzers 直驱（触发式验证形态，
/// 对齐 DependencyLockTests 先例）。
/// </summary>
internal static class AnalyzerTestDriver
{
    /// <summary>分析一段源码，返回全部诊断。</summary>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeAsync(
        string source, string assemblyName, IReadOnlyDictionary<string, string> config,
        params MetadataReference[] extraReferences)
    {
        var compilation = CreateCompilation(source, assemblyName, extraReferences);
        return await RunAsync(compilation, config, new TierGovernanceAnalyzer());
    }

    /// <summary>分析多棵语法树（.editorconfig 通道的并集/冲突语义需要多树形态——#459）。</summary>
    /// <param name="sources">逐树的源码。</param>
    /// <param name="assemblyName">被分析程序集名。</param>
    /// <param name="globalConfig">GlobalOptions 键值（.globalconfig 通道）。</param>
    /// <param name="treeConfig">每树 options 键值（.editorconfig 通道）。</param>
    /// <param name="extraReferences">附加元数据引用。</param>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeMultiTreeAsync(
        string[] sources, string assemblyName,
        IReadOnlyDictionary<string, string> globalConfig, IReadOnlyDictionary<string, string> treeConfig,
        params MetadataReference[] extraReferences)
    {
        var compilation = CreateCompilation(sources, assemblyName, extraReferences);
        return await RunAsync(compilation, globalConfig, treeConfig, new TierGovernanceAnalyzer());
    }

    /// <summary>分析一段源码（仅跑指定分析器——单规则族隔离验证用）。</summary>
    public static async Task<ImmutableArray<Diagnostic>> AnalyzeWithAsync(
        string source, string assemblyName, IReadOnlyDictionary<string, string> config,
        params DiagnosticAnalyzer[] analyzers)
    {
        var compilation = CreateCompilation(source, assemblyName, []);
        return await RunAsync(compilation, config, analyzers);
    }

    /// <summary>内存编译一个被引用库并返回其元数据引用（禁引用/白名单的元数据引用面 fixture）。</summary>
    public static MetadataReference BuildLibraryReference(string assemblyName, string source)
    {
        var compilation = CreateCompilation(source, assemblyName, []);
        return compilation.ToMetadataReference();
    }

    private static CSharpCompilation CreateCompilation(string source, string assemblyName,
        MetadataReference[] extraReferences)
        => CreateCompilation([source], assemblyName, extraReferences);

    private static CSharpCompilation CreateCompilation(string[] sources, string assemblyName,
        MetadataReference[] extraReferences)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            // 裸线程族 fixture 需要 System.Threading.* 类型；热路径 LINQ 判定需要 System.Linq 符号
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Thread.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Timer.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Linq.dll")),
        };
        references.AddRange(extraReferences);

        var trees = new List<SyntaxTree>();
        foreach (var source in sources) trees.Add(CSharpSyntaxTree.ParseText(source));

        return CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation, IReadOnlyDictionary<string, string> config,
        params DiagnosticAnalyzer[] analyzers)
        => await RunAsync(compilation, config, null, analyzers);

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(
        Compilation compilation, IReadOnlyDictionary<string, string> globalConfig,
        IReadOnlyDictionary<string, string>? treeConfig,
        params DiagnosticAnalyzer[] analyzers)
    {
        var options = new AnalyzerOptions(
            ImmutableArray<AdditionalText>.Empty,
            new TestAnalyzerConfigOptionsProvider(globalConfig, treeConfig));
        var withAnalyzers = compilation.WithAnalyzers(ImmutableArray.Create(analyzers), options);
        return await withAnalyzers.GetAllDiagnosticsAsync();
    }
}
