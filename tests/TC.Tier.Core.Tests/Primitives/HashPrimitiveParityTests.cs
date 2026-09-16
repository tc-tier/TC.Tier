// 与官方 System.IO.Hashing 的逐位对账测试（oracle 保留在测试项目——产品代码已零依赖）。
// 覆盖：XxHash64 / XxHash128 / Crc64（增量与字节序）/ CRC32(IEEE) / CRC32C。

// 与官方 System.IO.Hashing 的逐位对账测试（oracle 保留在测试项目——产品代码已零依赖）。
// 覆盖：XxHash64 / XxHash128 / Crc64（增量与字节序）/ CRC32(IEEE)。
// ★ CRC32C 官方 8.0.0 无此类型（预览期移除，Crc32C 需硬件/自研）——其一致性用标准向量测试
//   （见 Crc32C_StandardVector_MatchesCastagnoli），本文件不做官方对账。

using System.IO.Hashing;
using OfficialCrc32 = System.IO.Hashing.Crc32;
using OfficialCrc64 = System.IO.Hashing.Crc64;
using OfficialXxHash64 = System.IO.Hashing.XxHash64;
using OfficialXxHash128 = System.IO.Hashing.XxHash128;

namespace TC.Tier.Core.Tests.Primitives;

public sealed class HashPrimitiveParityTests
{
    public static TheoryData<int> Lengths => new()
    {
        0, 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 63, 64, 127, 128, 129,
        239, 240, 241, 255, 256, 257, 511, 1024, 4096, 8192, 16384, 65536, 70000, 131072
    };

    private static byte[] Data(int length)
    {
        var data = new byte[length];
        new Random(0x5EED ^ length).NextBytes(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void XxHash64_MatchesOfficial_OnLengths(int length)
    {
        byte[] data = Data(length);
        UnifiedXxHash.Hash64(data).Should().Be(OfficialXxHash64.HashToUInt64(data), "XxHash64 输出须与官方逐位一致");
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void XxHash128_MatchesOfficial_OnLengths(int length)
    {
        byte[] data = Data(length);
        Span<byte> ours = stackalloc byte[UnifiedXxHash.Hash128Len];
        UnifiedXxHash.Hash128(data, ours);
        ours.ToArray().Should().Equal(OfficialXxHash128.Hash(data), "XxHash128 输出字节须与官方逐位一致");
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Crc64_MatchesOfficial_OnLengths(int length)
    {
        byte[] data = Data(length);

        var ours = new UnifiedCrc64();
        ours.Append(data);
        var official = new OfficialCrc64();
        official.Append(data);

        ours.GetCurrentHashAsUInt64().Should().Be(official.GetCurrentHashAsUInt64());
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Crc64_ChunkedAppend_MatchesOfficialOneShot_OnLengths(int length)
    {
        byte[] data = Data(length);

        var ours = new UnifiedCrc64();
        var official = new OfficialCrc64();
        int pos = 0;
        int step = 1;
        while (pos < data.Length)
        {
            int take = Math.Min(step, data.Length - pos);
            ours.Append(data.AsSpan(pos, take));
            official.Append(data.AsSpan(pos, take));
            pos += take;
            step = (step % 7) + 1;
        }

        ours.GetCurrentHashAsUInt64().Should().Be(official.GetCurrentHashAsUInt64(), "分段追加应与官方实例一致（续算语义）");
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Crc64_GetCurrentHashBytes_MatchesOfficialByteOrder(int length)
    {
        byte[] data = Data(length);

        var ours = new UnifiedCrc64();
        ours.Append(data);
        var official = new OfficialCrc64();
        official.Append(data);

        Span<byte> oursBytes = stackalloc byte[8];
        Span<byte> officialBytes = stackalloc byte[8];
        ours.GetCurrentHash(oursBytes);
        official.GetCurrentHash(officialBytes);

        oursBytes.ToArray().Should().Equal(officialBytes.ToArray(), "GetCurrentHash 字节序（Big Endian）须与官方一致");
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void Crc32Ieee_MatchesOfficial_OnLengths(int length)
    {
        byte[] data = Data(length);
        UnifiedCrc.ComputeCrc32(data).Should().Be(OfficialCrc32.HashToUInt32(data), "IEEE CRC-32 须与官方逐位一致");
    }

    [Fact]
    public void Crc32C_StandardVector_MatchesCastagnoli()
    {
        // CRC-32C（Castagnoli/ISCSI）标准校验向量："123456789" → 0xE3069283
        byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
        UnifiedCrc.ComputeCrc32C(data).Should().Be(0xE3069283u);
    }

    [Fact]
    public void Crc32C_EmptySpan_MatchesCastagnoliConvention()
    {
        UnifiedCrc.ComputeCrc32C(ReadOnlySpan<byte>.Empty).Should().Be(0u);
    }

    [Fact]
    public void Crc32C_Fold4Lane_ChunkedContinuing_MatchesOneShot()
    {
        // 折叠路径（≥8KB）与非零初值续算（快速幂分支）都要逐位等于一次性
        var data = new byte[70000];
        new Random(0xFA11).NextBytes(data);

        uint oneShot = UnifiedCrc.ComputeCrc32C(data);

        int[] splits = [9000, 20000, 4096, 32000, 4904];
        uint acc = 0;
        int pos = 0;
        foreach (int len in splits)
        {
            acc = UnifiedCrc.ComputeCrc32C(acc, data.AsSpan(pos, len));
            pos += len;
        }
        acc = UnifiedCrc.ComputeCrc32C(acc, data.AsSpan(pos));

        acc.Should().Be(oneShot, "4 链折叠 + 快速幂续算须与一次性逐位一致");
    }
}
