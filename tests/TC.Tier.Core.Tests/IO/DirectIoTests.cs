using System.Numerics;

namespace TC.Tier.Core.Tests.IO;

/// <summary>
/// DirectIo（DIO 缓冲对齐地板）契约测试——1:1 于 src/TC.Tier.Core/IO/DirectIo.cs。
/// <para>★ 契约：Windows 地板 ≥ max(系统页, 扇区)（页池/帧池租用据此免对齐错）；
/// Linux 地板 = 扇区；任意输入地板 ≥ 扇区且为 2 的幂（扇区恒 2^n 前提下单调不减）。</para>
/// </summary>
public class DirectIoTests
{
    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    [InlineData(0)]        // mem 卷表达无对齐要求——地板不得为 0/负（租用 API 契约）
    public void Floor_IsAtLeastSectorSize_AndPowerOfTwo(int sector)
    {
        var floor = DirectIo.BufferAlignmentFloor(sector);

        floor.Should().BeGreaterThanOrEqualTo(Math.Max(sector, 1));
        BitOperations.IsPow2(floor).Should().BeTrue("对齐粒度须 2 的幂（位掩码加速依赖）");
    }

    [Fact]
    public void Floor_OnWindows_AtLeastSystemPageSize()
    {
        var floor = DirectIo.BufferAlignmentFloor(512);

        if (OperatingSystem.IsWindows())
            floor.Should().BeGreaterThanOrEqualTo(Environment.SystemPageSize,
                "Win DIO 缓冲地址须系统页对齐——512 扇区卷按扇区租页 7/8 概率失配（Crash_DioMode 实锤根因）");
    }

    [Theory]
    [InlineData(512)]
    [InlineData(4096)]
    public void Floor_MonotonicInSector(int sector)
    {
        DirectIo.BufferAlignmentFloor(sector).Should().BeGreaterThanOrEqualTo(DirectIo.BufferAlignmentFloor(512));
    }
}
