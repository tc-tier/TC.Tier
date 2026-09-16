using System.Collections.Immutable;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>
/// golden 快照测试基座——GeneratorDriver 直跑生成器，产物与 Golden/{子目录}/*.g.cs 逐字比对。
/// <para>★ 重新生成快照：设环境变量 GOLDEN_DUMP=1 跑测试（把实际产物写回 Golden/）。</para>
/// </summary>
public abstract class GoldenTestBase
{
    /// <summary>测试宿主框架程序集全集（TPA）+ CodeGen.Abstractions 特性程序集。</summary>
    private static readonly ImmutableArray<MetadataReference> BaseReferences =
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
        .Split(Path.PathSeparator)
        .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
        .Append(MetadataReference.CreateFromFile(typeof(BinaryLayoutAttribute).Assembly.Location))
        .ToImmutableArray();

    protected static ImmutableArray<MetadataReference> References(params MetadataReference[] extra)
        => BaseReferences.Concat(extra).ToImmutableArray();

    /// <summary>内存编译 + 驱动生成器（生成器异常 = fail-fast 缺陷，返回 null）。</summary>
    protected static GeneratorDriver? RunGenerator(
        IIncrementalGenerator generator, string source, string assemblyName, ImmutableArray<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source, path: "fixtures.cs") },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator);
        try
        {
            driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        }
        catch
        {
            return null;
        }
        return driver;
    }

    /// <summary>驱动生成器并取生成期诊断（TCSGxxx 断言用）。</summary>
    protected static ImmutableArray<Diagnostic> RunForDiagnostics(
        IIncrementalGenerator generator, string source)
    {
        var compilation = CSharpCompilation.Create(
            "Diagnostics",
            new[] { CSharpSyntaxTree.ParseText(source, path: "diagnostics.cs") },
            References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        CSharpGeneratorDriver.Create(generator)
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        return diagnostics;
    }

    /// <summary>生成产物 ↔ golden 目录逐字比对（GOLDEN_DUMP=1 时先写回快照）。</summary>
    protected static void AssertMatchesGolden(GeneratorDriver? driver, string goldenSubdir)
    {
        driver.Should().NotBeNull("生成器不应抛异常（fail-fast 缺陷形态）");

        var generated = driver!.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .ToDictionary(s => s.HintName, s => s.SourceText.ToString());

        var goldenDir = Path.Combine(AppContext.BaseDirectory, "Golden", goldenSubdir);

        // ★ GOLDEN_DUMP=1：先把实际产物写回 Golden/（快照再生成工作流——随后断言即自校验）
        if (Environment.GetEnvironmentVariable("GOLDEN_DUMP") == "1")
        {
            Directory.CreateDirectory(goldenDir);
            foreach (var (name, text) in generated)
                File.WriteAllText(Path.Combine(goldenDir, name), text);
        }

        var expected = Directory.GetFiles(goldenDir)
            .ToDictionary(name => Path.GetFileName(name) ?? "", path => File.ReadAllText(path));

        generated.Keys.Should().BeEquivalentTo(expected.Keys,
            "生成产物集合应与 golden 文件集合一致（缺/多即生成器输出面漂移）");

        foreach (var (name, expectedText) in expected)
            generated[name].Should().Be(expectedText, "生成产物应与 golden 文件逐字一致：{0}", name);
    }
}
