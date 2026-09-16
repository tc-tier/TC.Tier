using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Tests.CodeGen;

/// <summary>
/// [WireMessage] generator contract tests (spec-12 §10 E2 — message-family encoding:
/// [Tag 1B][root fields in declaration order][subclass fields]; tag dispatch, common
/// prefix, variable members with explicit bounds, unknown tag = false).
/// <para>Encode is verified behaviorally via reflection (no Span in its signature);
/// TryDecode's defenses are asserted on the generated text (Span parameters cannot be
/// passed through reflection — behavior-level coverage lands with real consumers in D4).</para>
/// </summary>
public class WireMessageTests
{
    private const string FamilySample = """
        using System;
        using System.Runtime.InteropServices;
        using TC.Tier.CodeGen;

        namespace Sample;

        [BinaryLayout(Features = BinaryLayoutFeatures.StructSize)]
        [StructLayout(LayoutKind.Explicit, Size = 16)]
        public readonly struct Item
        {
            [FieldOffset(0)] internal readonly ulong _hi;
            [FieldOffset(8)] internal readonly ulong _lo;
            internal Item(ulong hi, ulong lo) { _hi = hi; _lo = lo; }
        }

        [WireMessage]
        public abstract record SampleMsg
        {
            public required long Version { get; init; }
        }

        [WireMessageTag(0x01)]
        public sealed record Join : SampleMsg
        {
            public required Item Origin { get; init; }
            public required int Ttl { get; init; }
        }

        [WireMessageTag(0x02)]
        public sealed record View : SampleMsg
        {
            [WireMember(MaxCount = 8)]
            public required Item[] Members { get; init; }
            public required bool Priority { get; init; }
        }

        [WireMessageTag(0x03)]
        public sealed record Payload : SampleMsg
        {
            [WireMember(MaxCount = 64)]
            public required byte[] Data { get; init; }
        }
        """;

    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var comp = CSharpCompilation.Create("wire-message-test",
            new[] { tree },
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                // ns2.0 Abstractions 的 Attribute 基类落在 netstandard facade——fixture 引用集必须带上（CS0012）
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Memory.dll")),   // System.Buffers（ArrayPool/IBufferWriter——0-Copy 生成面）
                MetadataReference.CreateFromFile(typeof(WireMessageAttribute).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new WireMessageGenerator(), new BinaryLayoutGenerator());
        driver.RunGeneratorsAndUpdateCompilation(comp, out var output, out var diagnostics);
        return (output, diagnostics);
    }

    private static List<Diagnostic> Tcsg(ImmutableArray<Diagnostic> diagnostics)
        => diagnostics.Where(d => d.Id.StartsWith("TCSG05", StringComparison.Ordinal)).ToList();

    private static string GeneratedText(Compilation output, string marker)
        => output.SyntaxTrees.Select(t => t.GetText().ToString()).Single(text => text.Contains(marker));

    [Fact]
    public void FamilySample_NoDiagnostics_GeneratesCodec()
    {
        var (output, diagnostics) = Run(FamilySample);
        Tcsg(diagnostics).Should().BeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty(
            "generated codec must compile against the sample");

        var generated = GeneratedText(output, "static class SampleMsgCodec");
        generated.Should().Contain("public const byte TagJoin = 0x01;");
        generated.Should().Contain("public const byte TagView = 0x02;");
        generated.Should().Contain("public const byte TagPayload = 0x03;");
        generated.Should().Contain("Sample.ItemCodec.Write(dest[cursor..], m.Origin);", "nested member via its generated codec");
        generated.Should().Contain("members[i] = Sample.ItemCodec.Read(source.Slice(cursor, Sample.ItemCodec.StructSize));");
        generated.Should().Contain("if ((uint)membersCount > 8) return false;", "explicit defense bound for the list member");
        generated.Should().Contain("if ((uint)dataLen > 64) return false;", "explicit defense bound for the blob member");
        generated.Should().Contain("default: return false;   // 未知 tag——向前兼容丢弃");
    }

    [Fact]
    public void Encode_EmitsTagPrefixAndFieldsInDeclarationOrder()
    {
        var (output, diagnostics) = Run(FamilySample);
        Tcsg(diagnostics).Should().BeEmpty();

        var asm = EmitAndLoad(output);
        var codec = asm.GetType("Sample.SampleMsgCodec")!;
        var joinType = asm.GetType("Sample.Join")!;
        var itemType = asm.GetType("Sample.Item")!;

        var join = Activator.CreateInstance(joinType)!;
        joinType.GetProperty("Version")!.SetValue(join, 7L);
        var origin = itemType.GetConstructor(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            [typeof(ulong), typeof(ulong)])!.Invoke([0x0102030405060708UL, 0x0F0E0D0C0B0A0908UL]);
        joinType.GetProperty("Origin")!.SetValue(join, origin);
        joinType.GetProperty("Ttl")!.SetValue(join, 42);

        var encoded = (byte[])codec.GetMethod("Encode", [joinType])!.Invoke(null, [join])!;

        // [Tag 1B][Version 8B LE][Origin 16B 原序][Ttl 4B LE] = 29B
        encoded.Length.Should().Be(1 + 8 + 16 + 4);
        encoded[0].Should().Be(0x01);
        System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(encoded.AsSpan(1)).Should().Be(7L);
        encoded[9].Should().Be(0x08, "Origin byte 0 verbatim");
        encoded[24].Should().Be(0x0F, "Origin byte 15 verbatim");
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(25)).Should().Be(42);
    }

    [Fact]
    public void DuplicateTag_ReportsTCSG050()
    {
        var (_, diagnostics) = Run(FamilySample.Replace("0x02", "0x01"));
        Tcsg(diagnostics).Select(d => d.Id).Should().Contain("TCSG050");
    }

    [Fact]
    public void UnsupportedMember_ReportsTCSG051()
    {
        var (_, diagnostics) = Run(FamilySample.Replace("public required int Ttl { get; init; }",
            "public required string Name { get; init; }"));
        Tcsg(diagnostics).Select(d => d.Id).Should().Contain("TCSG051");
    }

    [Fact]
    public void VariableMemberWithoutMaxCount_ReportsTCSG052()
    {
        var (_, diagnostics) = Run(FamilySample.Replace("[WireMember(MaxCount = 8)]", string.Empty));
        Tcsg(diagnostics).Select(d => d.Id).Should().Contain("TCSG052",
            "the defense bound must be declared explicitly");
    }



    [Fact]
    public void OrphanTag_ReportsTCSG053()
    {
        var (_, diagnostics) = Run(FamilySample + """

            [WireMessageTag(0x7F)]
            public sealed record Stranger { public required long X { get; init; } }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().Contain("TCSG053");
    }

    private static int _alcCounter;

    private static Assembly EmitAndLoad(Compilation compilation)
    {
        using var pe = new MemoryStream();
        var result = compilation.Emit(pe);
        result.Success.Should().BeTrue(string.Join("; ",
            result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        pe.Position = 0;
        // 每次 load 独立 ALC——同 ALC 内重名程序集 FileLoadException
        var alc = new System.Runtime.Loader.AssemblyLoadContext($"wire-message-test-{_alcCounter++}");
        return alc.LoadFromStream(pe);
    }

    [Fact]
    public void EncodePooled_MatchesEncodeBytes_ZeroHeapAllocation()
    {
        var (output, _) = Run(FamilySample);
        var asm = EmitAndLoad(output);
        var codec = asm.GetType("Sample.SampleMsgCodec")!;
        var joinType = asm.GetType("Sample.Join")!;

        var join = Activator.CreateInstance(joinType)!;
        joinType.GetProperty("Version")!.SetValue(join, 9L);
        joinType.GetProperty("Ttl")!.SetValue(join, 7);

        var expected = (byte[])codec.GetMethod("Encode", [joinType])!.Invoke(null, [join])!;
        var args = new object?[] { join, null };
        var len = (int)codec.GetMethod("EncodePooled")!.Invoke(null, args)!;   // out 参数为 ByRef——按名单取唯一重载
        var rented = (byte[])args[1]!;

        len.Should().Be(expected.Length);
        rented.AsSpan(0, len).ToArray().Should().BeEquivalentTo(expected, "池化形态与 Encode 产物逐字节一致");
        rented.Length.Should().BeGreaterThanOrEqualTo(len, "池化租借缓冲允许超配");
        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
    }

    [Fact]
    public void EncodeToIBufferWriter_MatchesEncodeBytes()
    {
        var (output, _) = Run(FamilySample);
        var asm = EmitAndLoad(output);
        var codec = asm.GetType("Sample.SampleMsgCodec")!;
        var joinType = asm.GetType("Sample.Join")!;

        var join = Activator.CreateInstance(joinType)!;
        joinType.GetProperty("Version")!.SetValue(join, 11L);
        joinType.GetProperty("Ttl")!.SetValue(join, 3);

        var expected = (byte[])codec.GetMethod("Encode", [joinType])!.Invoke(null, [join])!;
        var writer = new System.Buffers.ArrayBufferWriter<byte>();
        codec.GetMethod("Encode", [typeof(System.Buffers.IBufferWriter<byte>), joinType])!
            .Invoke(null, [writer, join]);

        writer.WrittenSpan.ToArray().Should().BeEquivalentTo(expected, "IBufferWriter 形态与 Encode 产物逐字节一致");
    }
}
