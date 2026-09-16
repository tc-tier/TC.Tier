using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>
/// [WireMessage] golden 快照——全 8 种成员形态（FixedBool/FixedPrimitive/FixedNested/Blob/
/// ListOfBool/ListOfPrimitive/ListOfNested/ListOfBlob）+ 根前缀字段 + 空子类，锁定发射文本。
/// <para>★ 重新生成快照：GOLDEN_DUMP=1 跑本测试。</para>
/// </summary>
public sealed class WireMessageGoldenTests : GoldenTestBase
{
    internal const string Fixtures = """
        using System;
        using System.Collections.Generic;
        using System.Runtime.InteropServices;
        using TC.Tier.CodeGen;

        namespace Golden.Fixtures
        {
            [BinaryLayout(Features = BinaryLayoutFeatures.StructSize)]
            [StructLayout(LayoutKind.Explicit, Size = 16)]
            public readonly struct Item
            {
                [FieldOffset(0)] internal readonly ulong _hi;
                [FieldOffset(8)] internal readonly ulong _lo;
                internal Item(ulong hi, ulong lo) { _hi = hi; _lo = lo; }
            }

            [WireMessage]
            public abstract record FamilyMsg
            {
                public required long Version { get; init; }
            }

            [WireMessageTag(0x01)]
            public sealed record Full : FamilyMsg
            {
                public required bool Flag { get; init; }
                public required int Count { get; init; }
                public required Item Origin { get; init; }
                [WireMember(MaxCount = 64)]
                public required byte[] Blob { get; init; }
                [WireMember(MaxCount = 4)]
                public required ReadOnlyMemory<byte> Memory { get; init; }
                [WireMember(MaxCount = 4)]
                public required bool[] Flags { get; init; }
                [WireMember(MaxCount = 8)]
                public required int[] Ids { get; init; }
                [WireMember(MaxCount = 8)]
                public required Item[] Items { get; init; }
                [WireMember(MaxCount = 4)]
                public required IReadOnlyList<ReadOnlyMemory<byte>> Chunks { get; init; }
            }

            [WireMessageTag(0x02)]
            public sealed record Minimal : FamilyMsg
            {
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var driver = RunGenerator(new WireMessageGenerator(), Fixtures, "GoldenWireMessage", References());
        AssertMatchesGolden(driver, "WireMessage");
    }
}
