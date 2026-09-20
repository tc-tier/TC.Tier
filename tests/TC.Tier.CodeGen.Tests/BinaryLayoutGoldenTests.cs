using FluentAssertions;
using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>
/// BinaryLayoutGenerator golden 快照——全形态覆盖：六基元 + byte、byte/uint 底层 enum、嵌套
/// [BinaryLayout] struct、ValidEquals/ValidHasFlags/ValidRange/ValidNonDefault、OrFlags/IsEmpty、
/// readonly 构造调用、嵌套包含类型、internal 可见性、无 Features 最小形态。
/// </summary>
public sealed class BinaryLayoutGoldenTests : GoldenTestBase
{
    private const string Fixtures = """
        using System.Runtime.InteropServices;
        using TC.Tier.CodeGen;

        namespace Golden.Fixtures
        {
            public enum TinyState : byte { None = 0, Ready = 1 }

            public enum WideState : uint { None = 0, Ready = 7 }

            [BinaryLayout(Features = BinaryLayoutFeatures.All, OrFlags = "Flags", IsEmpty = "Magic")]
            [StructLayout(LayoutKind.Explicit, Size = 30)]
            public struct HeaderFixture
            {
                [FieldOffset(0), ValidEquals(1145590338u)] public uint Magic;
                [FieldOffset(4), ValidEquals((ushort)512)] public ushort Version;
                [FieldOffset(6), ValidHasFlags((ushort)19)] public ushort Flags;
                [FieldOffset(8)] public ulong Seq;
                [FieldOffset(16)] public long Delta;
                [FieldOffset(24)] public int Count;
                [FieldOffset(28)] public byte Raw;
                [FieldOffset(29)] public TinyState State;
            }

            [BinaryLayout(Features = BinaryLayoutFeatures.Constants)]
            [StructLayout(LayoutKind.Explicit, Size = 16)]
            public struct AddressFixture
            {
                [FieldOffset(0)] public long High;
                [FieldOffset(8)] public long Low;
            }

            [BinaryLayout(Features = BinaryLayoutFeatures.FieldAccessors)]
            [StructLayout(LayoutKind.Explicit, Size = 46)]
            public struct ContainerFixture
            {
                [FieldOffset(0)] public AddressFixture Addr;
                [FieldOffset(16)] public WideState Mode;
                [FieldOffset(20), ValidNonDefault] public ulong Meta;
                [FieldOffset(28)] public AddressFixture Tail;
                [FieldOffset(44), ValidRange(0, 255)] public ushort Len;
            }

            [BinaryLayout(Features = BinaryLayoutFeatures.All, Endianness = LayoutEndianness.BigEndian)]
            [StructLayout(LayoutKind.Explicit, Size = 8)]
            public struct BigEndianFixture
            {
                [FieldOffset(0)] public ushort Id;
                [FieldOffset(2)] public ushort Flags;
                [FieldOffset(4)] public uint Value;
            }

            [BinaryLayout]
            [StructLayout(LayoutKind.Explicit, Size = 16)]
            public readonly struct ReadonlyFixture
            {
                [FieldOffset(0)] public readonly long High;
                [FieldOffset(8)] public readonly long Low;

                public ReadonlyFixture(long high, long low)
                {
                    High = high;
                    Low = low;
                }
            }

            public static class Holder
            {
                [BinaryLayout(Features = BinaryLayoutFeatures.All)]
                [StructLayout(LayoutKind.Explicit, Size = 8)]
                internal struct NestedHeader
                {
                    [FieldOffset(0), ValidEquals(7u)] public uint Kind;
                    [FieldOffset(4)] public uint Seq;
                }
            }

            [BinaryLayout]
            [StructLayout(LayoutKind.Explicit, Size = 8)]
            public struct PlainFixture
            {
                [FieldOffset(0)] public int A;
                [FieldOffset(4)] public int B;
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var driver = RunGenerator(new BinaryLayoutGenerator(), Fixtures, "GoldenBinaryLayout", References());
        AssertMatchesGolden(driver, "BinaryLayout");
    }

    [Fact]
    public void SizeMismatch_ReportsTCSG001()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using TC.Tier.CodeGen;

            namespace Bad
            {
                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit, Size = 8)]
                public struct BadSize
                {
                    [FieldOffset(0)] public uint A;
                }
            }
            """;

        RunForDiagnostics(new BinaryLayoutGenerator(), source)
            .Should().Contain(d => d.Id == "TCSG001");
    }

    [Fact]
    public void StructLayoutWithoutSize_ReportsTCSG005()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using TC.Tier.CodeGen;

            namespace Bad
            {
                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit)]
                public struct MissingSize
                {
                    [FieldOffset(0)] public uint A;
                }
            }
            """;

        RunForDiagnostics(new BinaryLayoutGenerator(), source)
            .Should().Contain(d => d.Id == "TCSG005");
    }

    [Fact]
    public void OverlappingFields_ReportTCSG006()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using TC.Tier.CodeGen;

            namespace Bad
            {
                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit, Size = 8)]
                public struct Overlap
                {
                    [FieldOffset(0)] public ulong A;
                    [FieldOffset(4)] public uint B;
                }
            }
            """;

        RunForDiagnostics(new BinaryLayoutGenerator(), source)
            .Should().Contain(d => d.Id == "TCSG006");
    }

    [Fact]
    public void OverlappingNestedField_ReportsTCSG006()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using TC.Tier.CodeGen;

            namespace Bad
            {
                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit, Size = 8)]
                public struct Inner
                {
                    [FieldOffset(0)] public ulong Value;
                }

                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit, Size = 12)]
                public struct Outer
                {
                    [FieldOffset(0)] public Inner Nested;
                    [FieldOffset(4)] public ulong Tail;
                }
            }
            """;

        RunForDiagnostics(new BinaryLayoutGenerator(), source)
            .Should().Contain(d => d.Id == "TCSG006");
    }

    [Fact]
    public void UnsupportedFieldType_ReportsTCSG003()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using TC.Tier.CodeGen;

            namespace Bad
            {
                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit, Size = 6)]
                public struct BadField
                {
                    [FieldOffset(0)] public uint A;
                    [FieldOffset(4)] public short B;
                }
            }
            """;

        RunForDiagnostics(new BinaryLayoutGenerator(), source)
            .Should().Contain(d => d.Id == "TCSG003");
    }

    [Fact]
    public void UnmarkedNestedStruct_ReportsTCSG002()
    {
        const string source = """
            using System.Runtime.InteropServices;
            using TC.Tier.CodeGen;

            namespace Bad
            {
                // ★ 空 struct：外部兜底字段遍历 extent=0 → 嵌套大小无法解析 → TCSG002
                public struct Unmarked
                {
                }

                [BinaryLayout]
                [StructLayout(LayoutKind.Explicit, Size = 4)]
                public struct BadNested
                {
                    [FieldOffset(0)] public Unmarked Inner;
                }
            }
            """;

        RunForDiagnostics(new BinaryLayoutGenerator(), source)
            .Should().Contain(d => d.Id == "TCSG002");
    }
}
