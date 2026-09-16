using System.IO.Hashing;
using System.Runtime.Intrinsics;

namespace TC.Tier.Core.Tests.Primitives;

public sealed class UnifiedCrcTests
{
    // === CRC32C ===

    [Fact]
    public void Crc32C_EmptySpan_ReturnsZero()
    {
        UnifiedCrc.ComputeCrc32C(ReadOnlySpan<byte>.Empty).Should().Be(0u);
    }

    [Fact]
    public void Crc32C_SameData_ProducesSameResult()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var crc1 = UnifiedCrc.ComputeCrc32C(data);
        var crc2 = UnifiedCrc.ComputeCrc32C(data);
        crc1.Should().Be(crc2);
    }

    [Fact]
    public void Crc32C_DifferentData_ProducesDifferentResult()
    {
        byte[] a = [0x01, 0x02, 0x03, 0x04];
        byte[] b = [0x01, 0x02, 0x03, 0x05];
        UnifiedCrc.ComputeCrc32C(a).Should().NotBe(UnifiedCrc.ComputeCrc32C(b));
    }

    [Fact]
    public void Crc32C_Incremental_MatchesOneShot()
    {
        byte[] part1 = [0x01, 0x02, 0x03, 0x04];
        byte[] part2 = [0x05, 0x06, 0x07, 0x08];

        uint incremental = UnifiedCrc.ComputeCrc32C(0, part1);
        incremental = UnifiedCrc.ComputeCrc32C(incremental, part2);

        byte[] full = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        uint oneShot = UnifiedCrc.ComputeCrc32C(full);

        incremental.Should().Be(oneShot, "增量 CRC 应与一次性计算一致");
    }

    [Fact]
    public void Crc32C_LargeData_DoesNotOverflow()
    {
        byte[] data = new byte[1024 * 1024]; // 1 MB
        new Random(42).NextBytes(data);
        uint crc = UnifiedCrc.ComputeCrc32C(data);
        crc.Should().NotBe(0u);
    }

    [Fact]
    public void Crc32C_SelfConsistent_SameInputAlwaysSameOutput()
    {
        byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
        uint crc1 = UnifiedCrc.ComputeCrc32C(data);
        uint crc2 = UnifiedCrc.ComputeCrc32C(data);
        crc1.Should().Be(crc2, "相同输入应产生相同 CRC");
        crc1.Should().NotBe(0u, "CRC32C 非零输入应产生非零 CRC");
    }

    [Fact]
    public void Crc32C_SingleByte_Incremental_MatchesFull()
    {
        byte[] data = [0xAB, 0xCD, 0xEF];
        uint oneShot = UnifiedCrc.ComputeCrc32C(data);

        uint inc = 0;
        inc = UnifiedCrc.ComputeCrc32C(inc, data.AsSpan(0, 1));
        inc = UnifiedCrc.ComputeCrc32C(inc, data.AsSpan(1, 1));
        inc = UnifiedCrc.ComputeCrc32C(inc, data.AsSpan(2, 1));

        inc.Should().Be(oneShot);
    }

    // === CRC32C 折叠路径差分（PCLMULQDQ——与逐字节表驱动 oracle 逐位一致）===

    /// <summary>独立表驱动 oracle（Castagnoli 反射 0x82F63B78，canonical init/xorout）——与实现零共享。</summary>
    private static uint Crc32COracle(ReadOnlySpan<byte> data, uint initial = 0)
    {
        uint crc = ~initial;
        foreach (var b in data)
        {
            crc ^= b;
            for (int j = 0; j < 8; j++)
                crc = (crc >> 1) ^ (0x82F63B78u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    private static byte[] RandomBytes(int len, int seed)
    {
        var data = new byte[len];
        new Random(seed).NextBytes(data);
        return data;
    }

    [Fact]
    public void Crc32C_CanonicalVector_123456789()
    {
        // 标准 CRC-32C 校验向量（iSCSI 系，2026-09-02 canonical 语义修正的权威锚点）
        UnifiedCrc.ComputeCrc32C("123456789"u8).Should().Be(0xE3069283u);
    }

    [Fact]
    public void Crc32C_FoldPath_AllLengths_MatchesOracle()
    {
        // ★ 0~300 全长度逐点差分（覆盖折叠 0/1/N 步 + 尾部 0~15B 的全部组合边界），
        //   每长度 4 组随机数据防巧合——折叠路径与表驱动必须逐位一致
        for (var len = 0; len <= 300; len++)
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                var data = RandomBytes(len, seed * 1000 + len);
                UnifiedCrc.ComputeCrc32C(data).Should().Be(Crc32COracle(data),
                    $"len={len}, seed={seed}——PCLMULQDQ 折叠与 oracle 不一致");
            }
        }
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(65536 + 17)]
    public void Crc32C_FoldPath_LargeLengths_MatchesOracle(int len)
    {
        var data = RandomBytes(len, len);
        UnifiedCrc.ComputeCrc32C(data).Should().Be(Crc32COracle(data));
    }

    [Fact]
    public void Crc32C_NonZeroInitial_MatchesOracle()
    {
        var data = RandomBytes(200, 7);
        UnifiedCrc.ComputeCrc32C(0xDEADBEEFu, data).Should().Be(Crc32COracle(data, 0xDEADBEEFu));
    }

    [Fact]
    public void Crc32C_Incremental_Composition_MatchesOneShot_AllSplits()
    {
        // ★ 分段续算语义（RecordCodec.VerifyCrc 多段拼接的正确性前提），
        //   切分点遍历覆盖折叠段内/段边界/尾部各形态
        var data = RandomBytes(1000, 42);
        for (var split = 0; split <= 1000; split += 37)
        {
            var head = UnifiedCrc.ComputeCrc32C(data.AsSpan(0, split));
            var both = UnifiedCrc.ComputeCrc32C(head, data.AsSpan(split));
            both.Should().Be(Crc32COracle(data), $"split={split}");
        }
    }

    [Fact]
    public void Crc32C_RecordCodecThreeSegmentShape_MatchesWholeSpan()
    {
        // ★ Ring 记录形态（header 内 CRC @36，覆盖全记录）的读侧三段拼接 == 整段清零单遍
        var record = RandomBytes(112, 112);
        record[36] = 0; record[37] = 0; record[38] = 0; record[39] = 0;   // FillCrc 清零语义
        var expected = Crc32COracle(record);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(36), expected);

        var spliced = UnifiedCrc.ComputeCrc32C(
            UnifiedCrc.ComputeCrc32C(
                UnifiedCrc.ComputeCrc32C(record.AsSpan(0, 36)),
                new byte[4]),
            record.AsSpan(40, 72));
        spliced.Should().Be(expected, "VerifyCrc 三段拼接语义前提：分段续算 == 整段");
    }

    // === CRC32（IEEE 802.3——zlib 语义）===

    [Fact]
    public void Crc32_EmptySpan_ReturnsZero()
    {
        UnifiedCrc.ComputeCrc32(ReadOnlySpan<byte>.Empty).Should().Be(0u);
    }

    [Fact]
    public void Crc32_StandardVector_MatchesIsoHdlc()
    {
        // CRC-32/ISO-HDLC（IEEE 802.3）标准校验向量："123456789" → 0xCBF43926
        byte[] data = System.Text.Encoding.ASCII.GetBytes("123456789");
        UnifiedCrc.ComputeCrc32(data).Should().Be(0xCBF43926u);
    }

    [Fact]
    public void Crc32_SameData_ProducesSameResult()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        UnifiedCrc.ComputeCrc32(data).Should().Be(UnifiedCrc.ComputeCrc32(data));
    }

    [Fact]
    public void Crc32_DifferentData_ProducesDifferentResult()
    {
        byte[] a = [0x01, 0x02, 0x03, 0x04];
        byte[] b = [0x01, 0x02, 0x03, 0x05];
        UnifiedCrc.ComputeCrc32(a).Should().NotBe(UnifiedCrc.ComputeCrc32(b));
    }

    [Fact]
    public void Crc32_Incremental_MatchesOneShot()
    {
        byte[] part1 = [0x01, 0x02, 0x03, 0x04];
        byte[] part2 = [0x05, 0x06, 0x07, 0x08];

        uint incremental = UnifiedCrc.ComputeCrc32(0, part1);
        incremental = UnifiedCrc.ComputeCrc32(incremental, part2);

        byte[] full = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        uint oneShot = UnifiedCrc.ComputeCrc32(full);

        incremental.Should().Be(oneShot, "增量 CRC 应与一次性计算一致");
    }

    [Fact]
    public void Crc32_SingleByte_Incremental_MatchesFull()
    {
        byte[] data = [0xAB, 0xCD, 0xEF];
        uint oneShot = UnifiedCrc.ComputeCrc32(data);

        uint inc = 0;
        inc = UnifiedCrc.ComputeCrc32(inc, data.AsSpan(0, 1));
        inc = UnifiedCrc.ComputeCrc32(inc, data.AsSpan(1, 1));
        inc = UnifiedCrc.ComputeCrc32(inc, data.AsSpan(2, 1));

        inc.Should().Be(oneShot);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("123456789")]
    [InlineData("The quick brown fox jumps over the lazy dog")]
    public void Crc32_MatchSystemIoHashing_OnVectors(string text)
    {
        // 与 System.IO.Hashing.Crc32 逐位对账——迁移不改变已落盘/在途校验值
        byte[] data = System.Text.Encoding.ASCII.GetBytes(text);
        UnifiedCrc.ComputeCrc32(data).Should().Be(Crc32.HashToUInt32(data));
    }

    [Fact]
    public void Crc32_MatchSystemIoHashing_OnLargeData()
    {
        byte[] data = new byte[1024 * 1024];
        new Random(42).NextBytes(data);
        UnifiedCrc.ComputeCrc32(data).Should().Be(Crc32.HashToUInt32(data));
    }

    // === CRC64 ===

    [Fact]
    public void Crc64_EmptySpan_ReturnsNonZero()
    {
        // CRC64 空 span 计算仍返回初始值（非零，因 CRC 算法特性）
        ulong crc = UnifiedCrc.ComputeCrc64(ReadOnlySpan<byte>.Empty);
        crc.Should().Be(0u, "CRC64(empty) = 0 for Crc64.Reset() + GetCurrentHash()");
    }

    [Fact]
    public void Crc64_SameData_ProducesSameResult()
    {
        byte[] data = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        var crc1 = UnifiedCrc.ComputeCrc64(data);
        var crc2 = UnifiedCrc.ComputeCrc64(data);
        crc1.Should().Be(crc2);
    }

    [Fact]
    public void Crc64_DifferentData_ProducesDifferentResult()
    {
        byte[] a = [0x01, 0x02, 0x03, 0x04];
        byte[] b = [0x01, 0x02, 0x03, 0x05];
        UnifiedCrc.ComputeCrc64(a).Should().NotBe(UnifiedCrc.ComputeCrc64(b));
    }

    [Fact]
    public void Crc64_Incremental_MatchesOneShot()
    {
        byte[] part1 = [0x01, 0x02, 0x03, 0x04];
        byte[] part2 = [0x05, 0x06, 0x07, 0x08];

        var crc = UnifiedCrc.CreateCrc64();
        crc.Append(part1);
        crc.Append(part2);
        ulong incremental = UnifiedCrc.FinalizeCrc64(crc);

        byte[] full = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];
        ulong oneShot = UnifiedCrc.ComputeCrc64(full);

        incremental.Should().Be(oneShot, "增量 CRC64 应与一次性计算一致");
    }

    [Fact]
    public void Crc64_ThreadSafety_MultipleInstances()
    {
        byte[] data = new byte[256];
        new Random(42).NextBytes(data);

        ulong[] results = new ulong[4];
        Parallel.For(0, 4, i =>
        {
            results[i] = UnifiedCrc.ComputeCrc64(data);
        });

        // 所有线程应得到相同结果
        results.Distinct().Count().Should().Be(1, "多线程 CRC64 应一致（ThreadStatic 复用实例）");
    }

    [Fact]
    public void Crc64_Reset_ClearsPreviousState()
    {
        var crc = UnifiedCrc.CreateCrc64();
        crc.Append([0x01, 0x02, 0x03]);
        crc.Reset();
        crc.Append([0x01, 0x02, 0x03]);
        ulong final = UnifiedCrc.FinalizeCrc64(crc);

        ulong expected = UnifiedCrc.ComputeCrc64([0x01, 0x02, 0x03]);
        final.Should().Be(expected, "Reset 后 CRC 应从初始状态重新计算");
    }

    // === Constants ===

    [Fact]
    public void Crc32CLen_Is4()
    {
        UnifiedCrc.Crc32CLen.Should().Be(4);
    }

    [Fact]
    public void Crc64Len_Is8()
    {
        UnifiedCrc.Crc64Len.Should().Be(8);
    }
}
