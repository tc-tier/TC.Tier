using System.Text.RegularExpressions;
using FluentAssertions;
using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>
/// KvStoreGenerator 生成器契约测试（tierkv-design.md §4 W1 验收）：golden 快照（六发射分支钉死：
/// byte/byte[]/ROM&lt;byte&gt;/unmanaged/自定义 formatter/IndexKind 缺省）+ 编译期校验诊断
/// （TCSG021 key 非 unmanaged / TCSG022 value 无 formatter / TCSG023 formatter 未实现契约）+ 重复标注去重。
/// </summary>
public sealed class KvStoreGoldenTests : GoldenTestBase
{
    private const string Fixtures = """
        using System;
        using TC.Tier.CodeGen;
        using TC.Tier.Products.Kv;

        [assembly: KvStore(typeof(Golden.Fixtures.OrderKey), typeof(Golden.Fixtures.Position))]
        [assembly: KvStore(typeof(Golden.Fixtures.OrderKey), typeof(byte))]
        [assembly: KvStore(typeof(Golden.Fixtures.OrderKey), typeof(byte[]))]
        [assembly: KvStore(typeof(Golden.Fixtures.OrderKey), typeof(ReadOnlyMemory<byte>))]
        [assembly: KvStore(typeof(Golden.Fixtures.OrderKey), typeof(Golden.Fixtures.Payload),
            Formatter = typeof(Golden.Fixtures.PayloadFormatter))]
        [assembly: KvStore(typeof(long), typeof(Golden.Fixtures.Position), IndexKind = 1)]

        namespace Golden.Fixtures
        {
            public struct OrderKey
            {
                public long Id;
            }

            public struct Position
            {
                public long Qty;
                public long Price;
            }

            public struct Payload
            {
                public long Seq;
            }

            public sealed class PayloadFormatter : IValueFormatter<Payload>
            {
                public int GetSize(Payload value) => sizeof(long);
                public void Format(Payload value, System.Span<byte> destination) => BitConverter.GetBytes(value.Seq).CopyTo(destination);
                public Payload Parse(ReadOnlySpan<byte> source) => new Payload { Seq = BitConverter.ToInt64(source.ToArray(), 0) };
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var driver = RunGenerator(new KvStoreGenerator(), Fixtures, "GoldenKvStore", References());
        AssertMatchesGolden(driver, "KvStore");
    }

    // ═══ 编译期校验诊断 ═══

    [Fact]
    public void ManagedKey_ReportsTCSG021()
    {
        const string source = """
            using TC.Tier.CodeGen;
            [assembly: KvStore(typeof(string), typeof(long))]
            """;

        var diagnostics = RunForDiagnostics(new KvStoreGenerator(), source);

        diagnostics.Should().Contain(d => d.Id == "TCSG021", "string key 不满足 unmanaged——编译期报错非运行时炸");
    }

    [Fact]
    public void CustomManagedValueWithoutFormatter_ReportsTCSG022()
    {
        const string source = """
            using TC.Tier.CodeGen;
            [assembly: KvStore(typeof(long), typeof(string))]
            """;

        var diagnostics = RunForDiagnostics(new KvStoreGenerator(), source);

        diagnostics.Should().Contain(d => d.Id == "TCSG022", "string 不在内建覆盖面且未声明 Formatter");
    }

    [Fact]
    public void FormatterNotImplementingContract_ReportsTCSG023()
    {
        const string source = """
            using TC.Tier.CodeGen;
            [assembly: KvStore(typeof(long), typeof(Golden.Fixtures.Payload),
                Formatter = typeof(Golden.Fixtures.NotAFormatter))]

            namespace Golden.Fixtures
            {
                public struct Payload { public long Seq; }
                public sealed class NotAFormatter { }
            }
            """;

        var diagnostics = RunForDiagnostics(new KvStoreGenerator(), source);

        diagnostics.Should().Contain(d => d.Id == "TCSG023", "Formatter 未实现 IValueFormatter<TValue>——编译期报错");
    }

    [Fact]
    public void ValidSpecs_EmitNoDiagnostics()
    {
        const string source = """
            using System;
            using TC.Tier.CodeGen;
            using TC.Tier.Products.Kv;
            [assembly: KvStore(typeof(long), typeof(byte[]), IndexKind = 1)]
            [assembly: KvStore(typeof(long), typeof(string),
                Formatter = typeof(Golden.Fixtures.StringFormatter))]

            namespace Golden.Fixtures
            {
                public sealed class StringFormatter : IValueFormatter<string>
                {
                    public int GetSize(string value) => System.Text.Encoding.UTF8.GetByteCount(value);
                    public void Format(string value, Span<byte> destination) => System.Text.Encoding.UTF8.GetBytes(value).CopyTo(destination);
                    public string Parse(ReadOnlySpan<byte> source) => System.Text.Encoding.UTF8.GetString(source);
                }
            }
            """;

        var diagnostics = RunForDiagnostics(new KvStoreGenerator(), source);

        diagnostics.Where(d => d.Id.StartsWith("TCSG", StringComparison.Ordinal)).Should().BeEmpty(
            "合法标注（byte[] 内建 + string 值自定义 formatter 接口实现）零诊断");
    }

    // ═══ 重复标注去重（同 (Key,Value) 对只发射一个封闭类）═══

    [Fact]
    public void DuplicatePair_EmitsSingleClass()
    {
        const string source = """
            using TC.Tier.CodeGen;
            [assembly: KvStore(typeof(long), typeof(long))]
            [assembly: KvStore(typeof(long), typeof(long))]
            """;

        var driver = RunGenerator(new KvStoreGenerator(), source, "DedupeKv", References());
        driver.Should().NotBeNull();

        var generated = driver!.GetRunResult().Results
            .SelectMany(r => r.GeneratedSources)
            .Single(s => s.HintName == "KvStoreClosed.g.cs");

        Regex.Matches(generated.SourceText.ToString(), @"sealed class TierKvOfLongLong")
            .Should().HaveCount(1, "同 (Key,Value) 重复标注应去重——两个同型封闭类 = CS0101");
    }
}
