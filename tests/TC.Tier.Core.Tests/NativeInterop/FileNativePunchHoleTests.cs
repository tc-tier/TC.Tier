using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// FileNative.PunchHole 验证测试（V1-V7）。
/// <para>这是回收/截断设计的地基——PunchHole 必须先通过这些验证，回收代码才能合入。</para>
/// <para>验证矩阵见 spec §6.9。核心：V1（KEEP_SIZE 不缩 Length）是致命项，失败则 maxOffset 推理链失效。</para>
/// </summary>
public sealed class FileNativePunchHoleTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs) TestTempDir.TryCleanup(dir);
    }

    private string NewPath(string name = "punch.dat")
    {
        var dir = TestTempDir.Create("tc-punch");
        _dirs.Add(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>写满 length 字节的非零数据到文件，返回句柄。</summary>
    private static SafeFileHandle CreateFilledFile(string path, long length, byte seed = 0xAB)
    {
        var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, FileOptions.None);
        // 分块写非零模式
        const int ChunkSize = 64 * 1024;
        var chunk = new byte[Math.Min(length, ChunkSize)];
        for (int i = 0; i < chunk.Length; i++) chunk[i] = (byte)(seed + i);
        long written = 0;
        while (written < length)
        {
            var toWrite = (int)Math.Min(length - written, ChunkSize);
            RandomAccess.Write(handle, chunk.AsSpan(0, toWrite), written);
            written += toWrite;
        }
        RandomAccess.FlushToDisk(handle);
        return handle;
    }

    /// <summary>
    /// V1. KEEP_SIZE 不缩 Length —— 致命项。
    /// PunchHole 后 FileInfo.Length 必须不变，否则 maxOffset 推理链（§5.2）失效。
    /// </summary>
    [Fact]
    public void V1_PunchHole_KeepsLogicalSize()
    {
        var path = NewPath();
        const long FileSize = 4 * 1024 * 1024;  // 4MB
        using var handle = CreateFilledFile(path, FileSize);

        var sizeBefore = new FileInfo(path).Length;
        sizeBefore.Should().Be(FileSize);

        var result = FileNative.PunchHole(handle, offset: 1024 * 1024, length: 2 * 1024 * 1024);

        var sizeAfter = new FileInfo(path).Length;
        sizeAfter.Should().Be(FileSize,
            "PunchHole 用 KEEP_SIZE，逻辑大小必须不变——这是 maxOffset 推理链的前提（spec §5.2）");
    }

    /// <summary>
    /// V2. 磁盘块实际归还 —— 真物理回收有效性。
    /// PunchHole 后 AllocatedSize 应显著下降。tmpfs 退化（ZeroFilled）时此断言放宽。
    /// </summary>
    [Fact]
    public void V2_PunchHole_ReleasesDiskBlocks()
    {
        var path = NewPath();
        const long FileSize = 4 * 1024 * 1024;
        using var handle = CreateFilledFile(path, FileSize);

        var allocatedBefore = FileNative.GetFileAllocatedDiskSize(handle);
        var result = FileNative.PunchHole(handle, offset: 1024 * 1024, length: 2 * 1024 * 1024);
        var allocatedAfter = FileNative.GetFileAllocatedDiskSize(handle);

        if (result == PunchResult.Punched)
        {
            // 真打洞：磁盘分配应显著下降（至少归还一半）
            (allocatedBefore - allocatedAfter).Should().BeGreaterThan(
                FileSize / 4, "PunchHole=Punched 应实际归还磁盘块");
        }
        else
        {
            // ZeroFilled 退化（tmpfs/不支持）：磁盘占用不降是预期的
            // 此测试只对 Punched 断言，退化时仅记录
            result.Should().Be(PunchResult.ZeroFilled,
                "退化应是 ZeroFilled，不是 Failed");
        }
    }

    /// <summary>
    /// V3. 打洞区读返回零 —— 正确性。
    /// </summary>
    [Fact]
    public void V3_PunchedRegion_ReadsAsZero()
    {
        var path = NewPath();
        const long FileSize = 4 * 1024 * 1024;
        using var handle = CreateFilledFile(path, FileSize, seed: 0xCD);

        long punchOffset = 1024 * 1024;
        long punchLen = 2 * 1024 * 1024;
        FileNative.PunchHole(handle, punchOffset, punchLen);

        // 读打洞区中段，应全零
        var buf = new byte[4096];
        RandomAccess.Read(handle, buf, punchOffset + punchLen / 2);
        buf.Should().OnlyContain(b => b == 0, "打洞区应读返回零");
    }

    /// <summary>
    /// V4. 打洞区可重写 —— 回收区可复用。
    /// </summary>
    [Fact]
    public void V4_PunchedRegion_CanBeRewritten()
    {
        var path = NewPath();
        const long FileSize = 4 * 1024 * 1024;
        using var handle = CreateFilledFile(path, FileSize, seed: 0x11);

        long punchOffset = 1024 * 1024;
        long punchLen = 2 * 1024 * 1024;
        FileNative.PunchHole(handle, punchOffset, punchLen);

        // 在打洞区重写数据
        var writeData = new byte[4096];
        for (int i = 0; i < writeData.Length; i++) writeData[i] = (byte)(0x99 - i);
        RandomAccess.Write(handle, writeData, punchOffset + 4096);

        // 读回验证
        var readBuf = new byte[4096];
        RandomAccess.Read(handle, readBuf, punchOffset + 4096);
        readBuf.Should().Equal(writeData, "打洞区重写后应能正确读回");
    }

    /// <summary>
    /// V5. 未打洞区数据不受影响 —— PunchHole 只影响指定区间。
    /// </summary>
    [Fact]
    public void V5_UnpunchedRegion_Preserved()
    {
        var path = NewPath();
        const long FileSize = 4 * 1024 * 1024;
        using var handle = CreateFilledFile(path, FileSize, seed: 0x22);

        long punchOffset = 1024 * 1024;
        long punchLen = 2 * 1024 * 1024;
        FileNative.PunchHole(handle, punchOffset, punchLen);

        // 读未打洞区（文件头部 0~512K），应保留原数据
        var buf = new byte[4096];
        RandomAccess.Read(handle, buf, fileOffset: 0);
        buf[0].Should().Be((byte)0x22, "未打洞区数据应保留");
        buf.Any(b => b != 0).Should().BeTrue("头部未打洞区应有原始非零数据");
    }

    /// <summary>
    /// V6. PunchHole 返回值正确 —— Punched 或 ZeroFilled（不 Failed）。
    /// </summary>
    [Fact]
    public void V6_PunchHole_ReturnsValidResult()
    {
        var path = NewPath();
        const long FileSize = 2 * 1024 * 1024;
        using var handle = CreateFilledFile(path, FileSize);

        var result = FileNative.PunchHole(handle, offset: 512 * 1024, length: 1024 * 1024);

        result.Should().BeOneOf(
            new[] { PunchResult.Punched, PunchResult.ZeroFilled },
            "PunchHole 应成功（真打洞或退化归零），不应 Failed");
    }

    /// <summary>
    /// V7. 零长度 PunchHole 是 no-op。
    /// </summary>
    [Fact]
    public void V7_ZeroLength_IsNoOp()
    {
        var path = NewPath();
        const long FileSize = 1024 * 1024;
        using var handle = CreateFilledFile(path, FileSize);

        var result = FileNative.PunchHole(handle, offset: 0, length: 0);
        result.Should().Be(PunchResult.Punched, "零长度应直接返回 Punched（no-op）");

        // 文件不变
        new FileInfo(path).Length.Should().Be(FileSize);
    }
}
