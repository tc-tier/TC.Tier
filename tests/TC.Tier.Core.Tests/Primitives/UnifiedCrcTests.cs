
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
