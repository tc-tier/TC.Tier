using TC.Tier.CodeGen;
using Xunit;

namespace TC.Tier.CodeGen.Tests;

/// <summary>
/// [WireArray] golden 快照——三种形态（字节块/基元元素/嵌套元素）各带前缀字段，锁定发射文本。
/// <para>★ 重新生成快照：GOLDEN_DUMP=1 跑本测试。</para>
/// </summary>
public sealed class WireArrayGoldenTests : GoldenTestBase
{
    private const string Fixtures = """
        using System;
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

            [WireArray(MaxCount = 64)]
            public readonly struct BytesPayload(byte code, byte[] detail)
            {
                public readonly byte Code = code;
                public readonly byte[] Detail = detail;
            }

            [WireArray(MaxCount = 32)]
            public readonly struct IdBatch(uint batch, int[] ids)
            {
                public readonly uint Batch = batch;
                public readonly int[] Ids = ids;
            }

            [WireArray(MaxCount = 16)]
            public readonly struct ItemPack(ulong packId, Item[] items)
            {
                public readonly ulong PackId = packId;
                public readonly Item[] Items = items;
            }
        }
        """;

    [Fact]
    public void GeneratedOutput_MatchesGoldenFiles()
    {
        var driver = RunGenerator(new WireArrayGenerator(), Fixtures, "GoldenWireArray", References());
        AssertMatchesGolden(driver, "WireArray");
    }
}
