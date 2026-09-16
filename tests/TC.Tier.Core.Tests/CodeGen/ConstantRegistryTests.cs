using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Tests.CodeGen;

/// <summary>
/// [ConstantRegistry] generator contract tests (spec-12 §10 E3 — duplicate-value TCSG040,
/// zone helper generation and execution semantics).
/// <para>Follows the in-memory compilation convention of TypedFrontendContractTests:
/// run the generator via CSharpGeneratorDriver, assert diagnostics, then emit the
/// post-generation compilation and reflect-invoke the generated predicates.</para>
/// </summary>
public class ConstantRegistryTests
{
    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        // Under .NET 8, typeof(object).Assembly is System.Private.CoreLib — compilation-time
        // primitives/Attribute live in the System.Runtime facade; without it attribute binding
        // fails (CS0012) and ForAttributeWithMetadataName matches nothing.
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var comp = CSharpCompilation.Create("constant-registry-test",
            new[] { tree },
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                // ns2.0 Abstractions 的 Attribute 基类落在 netstandard facade——fixture 引用集必须带上（CS0012）
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),                    // CoreLib (type definitions)
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),    // facade (Attribute/primitive forwarding)
                MetadataReference.CreateFromFile(typeof(ConstantRegistryAttribute).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new ConstantRegistryGenerator());
        driver.RunGeneratorsAndUpdateCompilation(comp, out var output, out var diagnostics);
        return (output, diagnostics);
    }

    private static List<Diagnostic> Tcsg(ImmutableArray<Diagnostic> diagnostics)
        => diagnostics.Where(d => d.Id.StartsWith("TCSG", StringComparison.Ordinal)).ToList();

    /// <summary>Emits and loads the post-generation compilation (for predicate truth-table verification).</summary>
    private static Type EmitAndLoad(Compilation compilation, string typeName)
    {
        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        result.Success.Should().BeTrue("compilation must succeed after helper generation: " +
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        pe.Position = 0;
        var asm = new AssemblyLoadContext("constant-registry-test").LoadFromStream(pe);
        return asm.GetType(typeName)!;
    }

    [Fact]
    public void ZoneHelpers_FiveZoneSample_GeneratesAndMatchesFullTruthTable()
    {
        var (output, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "IsCore:0x00-0x4F", "IsUserRegistrable:0x60-0xAF", "IsForbidden:0x50-0x5F,0xB0-0xFF" })]
            public static partial class ProtocolIds
            {
                public const byte Management = 0x00;
                public const byte Raft = 0x01;
                public const byte HyParView = 0x02;
                public const byte SwarmSync = 0x03;
                public const byte SnapshotStream = 0x04;
            }
            """);
        Tcsg(diagnostics).Should().BeEmpty();
        output.SyntaxTrees.Should().HaveCount(2, "exactly one injected helper file");

        var type = EmitAndLoad(output, "Sample.ProtocolIds");
        bool Invoke(string method, byte v) => (bool)type.GetMethod(method)!.Invoke(null, new object[] { v })!;
        for (int v = 0; v <= 255; v++)
        {
            var isCore = Invoke("IsCore", (byte)v);
            var isUser = Invoke("IsUserRegistrable", (byte)v);
            var isForbidden = Invoke("IsForbidden", (byte)v);

            isCore.Should().Be(v <= 0x4F, $"0x{v:X}: internal core zone (spec-12 §3.5)");
            isUser.Should().Be(v is >= 0x60 and <= 0xAF, $"0x{v:X}: user registrable zone");
            isForbidden.Should().Be(v is (>= 0x50 and <= 0x5F) or >= 0xB0, $"0x{v:X}: isolation zones, reserved area and 0xFF are all forbidden");
            new[] { isCore, isUser, isForbidden }.Count(b => b)
                .Should().Be(1, $"0x{v:X}: the declared zones partition the value domain");
        }
    }

    [Fact]
    public void SingleValueZone_GeneratesEqualityPredicate()
    {
        var (output, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "IsManagement:0x00" })]
            internal static partial class ChannelIds
            {
                public const byte Management = 0x00;
                public const byte MinStream = 0x01;
            }
            """);
        Tcsg(diagnostics).Should().BeEmpty();
        output.SyntaxTrees.Should().HaveCount(2, "exactly one injected helper file");

        var type = EmitAndLoad(output, "Sample.ChannelIds");
        var method = type.GetMethod("IsManagement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        method.Invoke(null, new object[] { (byte)0x00 }).Should().Be(true);
        method.Invoke(null, new object[] { (byte)0x01 }).Should().Be(false);
    }

    [Fact]
    public void DuplicateValue_ReportsTCSG040()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry]
            public static class FrameKinds
            {
                public const byte Datagram = 0x01;
                public const byte Other = 0x01;
            }
            """);
        var tcsg = Tcsg(diagnostics);
        tcsg.Select(d => d.Id).Should().BeEquivalentTo(["TCSG040"]);
        tcsg[0].Severity.Should().Be(DiagnosticSeverity.Error);
        tcsg[0].GetMessage().Should().Contain("Other").And.Contain("Datagram").And.Contain("0x1");
    }

    [Fact]
    public void DuplicateValue_AcrossDifferentIntegerTypes_StillReported()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry]
            public static class Mixed
            {
                public const byte One = 0x01;
                public const int AlsoOne = 1;
            }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG040"],
            "a registry is a numeric value space — same value across integer types is still ambiguous");
    }

    [Fact]
    public void WithoutZones_NoFileInjected_ButDuplicateCheckStillApplies()
    {
        var (output, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry]
            public static class FrameKinds
            {
                public const byte Datagram = 0x01;
                public const byte Duplicate = 0x01;
            }
            """);
        output.SyntaxTrees.Should().HaveCount(1, "no Zones declared — nothing to inject");
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG040"]);
    }

    [Fact]
    public void NotPartial_ReportsTCSG043()
    {
        var (output, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "IsCore:0x00-0x4F" })]
            public static class NotPartialRegistry
            {
                public const byte A = 0x00;
            }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG043"]);
        output.SyntaxTrees.Should().HaveCount(1, "a non-partial class cannot receive injected members");
    }

    [Theory]
    [InlineData("0core:0x00-0x4F")]        // predicate name must be a valid identifier
    [InlineData("Zone 0x00-0x4F")]          // missing colon
    [InlineData("IsCore:")]                 // empty range list
    [InlineData("IsCore:0xZZ-0x4F")]        // non-hex digits
    public void MalformedZoneDeclaration_ReportsTCSG041(string zone)
    {
        var source = $$"""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "{{zone}}" })]
            public static partial class BadZone
            {
                public const byte A = 0x00;
            }
            """;
        Tcsg(Run(source).Diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG041"]);
    }

    [Fact]
    public void DuplicatePredicateName_ReportsTCSG041()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "IsCore:0x00-0x0F", "IsCore:0x10-0x1F" })]
            public static partial class DupPredicate
            {
                public const byte A = 0x00;
            }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG041"]);
    }

    [Theory]
    [InlineData("IsX:0x50-0x4F")]      // inverted range (low > high)
    [InlineData("IsX:0x00-0x1FF")]     // exceeds the byte value domain
    public void InvalidZoneRange_ReportsTCSG042(string zone)
    {
        var source = $$"""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "{{zone}}" })]
            public static partial class BadRange
            {
                public const byte A = 0x00;
            }
            """;
        Tcsg(Run(source).Diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG042"]);
    }

    [Fact]
    public void AmbiguousDomain_MixedIntegerConstTypes_ReportsTCSG044()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "IsCore:0x00-0x0F" })]
            public static partial class MixedDomain
            {
                public const byte A = 0x00;
                public const ushort B = 0x0100;
            }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG044"]);
    }

    [Fact]
    public void AmbiguousDomain_NoIntegerConsts_ReportsTCSG044()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry(Zones = new[] { "IsCore:0x00-0x0F" })]
            public static partial class NoConsts
            {
                public const string Name = "x";
            }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG044"],
            "non-integer consts do not define the value domain — the predicate parameter type is undecidable");
    }

    [Fact]
    public void ValidRegistry_NoDiagnostics_NoInjection()
    {
        var (output, diagnostics) = Run("""
            using TC.Tier.CodeGen;

            namespace Sample;

            [ConstantRegistry]
            public static class Features
            {
                public const byte None = 0x00;
                public const byte Keepalive = 0x01;
                public const byte UdpEndpoint = 0x02;
                public const byte MutualTls = 0x04;
                public const byte ReservedMask = 0xF8;
            }
            """);
        Tcsg(diagnostics).Should().BeEmpty();
        output.SyntaxTrees.Should().HaveCount(1);
    }
}
