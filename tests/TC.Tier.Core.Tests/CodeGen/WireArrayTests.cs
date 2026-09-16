using System.Collections.Immutable;
using System.Linq;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Tests.CodeGen;

/// <summary>
/// [WireArray] generator contract tests (spec-12 §10 E1 — variable-length collection encoding:
/// [fixed prefix fields][Count 4B][item×N]; declaration-order layout, MaxCount defense).
/// <para>Diagnostics asserted via in-memory compilation; generated source asserted by text
/// (behavior-level coverage lives in Core.Net.Tests against the real generated codecs).</para>
/// </summary>
public class WireArrayTests
{
    private static (Compilation Output, ImmutableArray<Diagnostic> Diagnostics) Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var comp = CSharpCompilation.Create("wire-array-test",
            new[] { tree },
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                // ns2.0 Abstractions 的 Attribute 基类落在 netstandard facade——fixture 引用集必须带上（CS0012）
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "netstandard.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Memory.dll")),   // System.Buffers（ArrayPool/IBufferWriter——0-Copy 生成面）
                MetadataReference.CreateFromFile(typeof(WireArrayAttribute).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(new WireArrayGenerator(), new BinaryLayoutGenerator());
        driver.RunGeneratorsAndUpdateCompilation(comp, out var output, out var diagnostics);
        return (output, diagnostics);
    }

    private static List<Diagnostic> Tcsg(ImmutableArray<Diagnostic> diagnostics)
        => diagnostics.Where(d => d.Id.StartsWith("TCSG", StringComparison.Ordinal)
            && d.Id is "TCSG045" or "TCSG046" or "TCSG047" or "TCSG048").ToList();

    private const string ByteBlockSample = """
        using TC.Tier.CodeGen;

        namespace Sample;

        [WireArray(MaxCount = 64)]
        public readonly struct ErrorPayload
        {
            public readonly byte Code;
            public readonly byte[] Detail;
            public ErrorPayload(byte code, byte[] detail) { Code = code; Detail = detail; }
        }
        """;

    private const string NestedItemsSample = """
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

        [WireArray(MaxCount = 256)]
        public readonly struct ItemList
        {
            public readonly byte Kind;
            public readonly Item[] Items;
            public ItemList(byte kind, Item[] items) { Kind = kind; Items = items; }
        }
        """;

    [Fact]
    public void ByteBlockSample_NoDiagnostics_GeneratesCodec()
    {
        var (output, diagnostics) = Run(ByteBlockSample);
        Tcsg(diagnostics).Should().BeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty(
            "generated codec must compile against the sample");

        var generated = output.SyntaxTrees.Select(t => t.GetText().ToString())
            .Single(text => text.Contains("static class ErrorPayloadCodec"));
        generated.Should().Contain("public const int MaxCount = 64;");
        generated.Should().Contain("BinaryPrimitives.WriteInt32LittleEndian(dest[cursor..], count);");
        generated.Should().Contain("if ((uint)count > 64) return false;");
        generated.Should().Contain("value.Detail.CopyTo(dest[cursor..]);", "byte[] item = byte block copy");
        generated.Should().Contain("public static int EncodePooled(", "零分配热路径形态");
        generated.Should().Contain("public static void Encode(System.Buffers.IBufferWriter<byte> writer", "零拷贝汇形态");
        generated.Should().Contain("public static int ComputeLength(in ErrorPayload value)", "长度预算口");
    }

    [Fact]
    public void EncodePooled_MatchesSpanEncode_WithByteBlockFixture()
    {
        var (output, _) = Run(ByteBlockSample);
        using var pe = new MemoryStream();
        var emit = output.Emit(pe);
        emit.Success.Should().BeTrue();
        pe.Position = 0;
        var alc = new System.Runtime.Loader.AssemblyLoadContext("wire-array-pooled-test");
        var asm = alc.LoadFromStream(pe);

        var codec = asm.GetType("Sample.ErrorPayloadCodec")!;
        var payloadType = asm.GetType("Sample.ErrorPayload")!;
        var payload = Activator.CreateInstance(payloadType, (byte)0x7F, new byte[] { 0x11, 0x22, 0x33 })!;

        // 对照基准 = byte[] 便捷重载（Span 是 ref struct——反射 Invoke 不可行）；
        // Encode(Span) 的行为等价性由 Core.Net 真实消费方测试覆盖
        var arrEncode = codec.GetMethods().Single(m => m.Name == "Encode" && m.ReturnType == typeof(byte[]));
        var expected = (byte[])arrEncode.Invoke(null, [payload])!;

        var pooled = codec.GetMethods().Single(m => m.Name == "EncodePooled");
        var args = new object?[] { payload, null };
        var pooledLen = (int)pooled.Invoke(null, args)!;
        var rented = (byte[])args[1]!;

        pooledLen.Should().Be(expected.Length);
        rented.AsSpan(0, pooledLen).ToArray().Should().BeEquivalentTo(expected,
            "池化形态与 Encode 产物逐字节一致");
        System.Buffers.ArrayPool<byte>.Shared.Return(rented);
    }

    [Fact]
    public void NestedItemsSample_NoDiagnostics_ItemSizeViaStructSize()
    {
        var (output, diagnostics) = Run(NestedItemsSample);
        Tcsg(diagnostics).Should().BeEmpty();
        output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();

        var generated = output.SyntaxTrees.Select(t => t.GetText().ToString())
            .Single(text => text.Contains("static class ItemListCodec"));
        generated.Should().Contain("Sample.ItemCodec.Write(dest[cursor..], item);");
        generated.Should().Contain("cursor += Sample.ItemCodec.StructSize;",
            "nested item size comes from the sibling generated codec — this generator never recomputes another layout");
        generated.Should().Contain("items[i] = Sample.ItemCodec.Read(source.Slice(cursor, Sample.ItemCodec.StructSize));");
    }

    [Fact]
    public void MissingArrayField_ReportsTCSG045()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;
            namespace Sample;
            [WireArray(MaxCount = 8)]
            public readonly struct NoArray { public readonly byte A; public NoArray(byte a) { A = a; } }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG045"]);
    }

    [Fact]
    public void UnsupportedItemType_ReportsTCSG046()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;
            namespace Sample;
            [WireArray(MaxCount = 8)]
            public readonly struct StringItems { public readonly string[] Items; public StringItems(string[] items) { Items = items; } }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().Contain("TCSG046");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveMaxCount_ReportsTCSG047(int maxCount)
    {
        var source = $$"""
            using TC.Tier.CodeGen;
            namespace Sample;
            [WireArray(MaxCount = {{maxCount}})]
            public readonly struct Blob { public readonly byte[] Data; public Blob(byte[] data) { Data = data; } }
            """;
        Tcsg(Run(source).Diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG047"],
            "the defense bound must be declared explicitly");
    }

    [Fact]
    public void ConstructorMismatch_ReportsTCSG048()
    {
        var (_, diagnostics) = Run("""
            using TC.Tier.CodeGen;
            namespace Sample;
            [WireArray(MaxCount = 8)]
            public readonly struct BadCtor
            {
                public readonly byte A;
                public readonly byte[] Data;
                public BadCtor(byte[] data) { A = 0; Data = data; }
            }
            """);
        Tcsg(diagnostics).Select(d => d.Id).Should().BeEquivalentTo(["TCSG048"],
            "generated TryDecode constructs via (prefix fields in declaration order + array field)");
    }
}
