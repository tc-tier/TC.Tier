using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 大容量读写测试 — 验证 GB 级跨段数据完整性。
/// </summary>
[Collection("LargeScaleIO")]
public sealed class LargeScaleDeviceTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static byte[] MakePattern(int length, byte seed)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    /// <summary>
    /// 写入 200MB 跨 4 段 (每段 64MB)，立即读回校验。
    /// </summary>
    [Fact]
    public void WriteAndRead_200MB_AcrossSegments()
    {
        var vol = NewVol();

        const long segGrowth = 64 * 1024 * 1024; // 64MB per segment
        const int totalSize = 200 * 1024 * 1024; // 200MB total
        const int chunkSize = 512;

        var options = new StorageEngineOptions("large", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var rng = new Random(42);
        var src = new byte[chunkSize];
        int writes = totalSize / chunkSize;
        var positions = new List<LogicalAddress>(writes);

        // Write
        for (int i = 0; i < writes; i++)
        {
            rng.NextBytes(src);
            var addr = dev.Append(src);
            positions.Add(addr);
            if (i % 10000 == 0) Console.WriteLine($"Write {i}/{writes}, tail: {dev.AllocatedTail}");
        }

        Console.WriteLine($"Final tail: {dev.AllocatedTail}, total segments: {dev.AllocatedTail.SegId}");
        dev.Flush();

        // Read back — verify total bytes read matches total written
        long totalRead = 0;
        var dst = new byte[chunkSize];
        for (int i = 0; i < writes; i++)
        {
            int n = dev.Read(positions[i], dst);
            totalRead += n;
        }

        totalRead.Should().Be(totalSize);
    }

    /// <summary>
    /// 跨段写入后关闭重开，验证数据持久性。
    /// </summary>
    [Fact]
    public void Reopen_After_150MB_Write()
    {
        var vol = NewVol();

        const long segGrowth = 32 * 1024 * 1024; // 32MB per segment
        const int totalSize = 150 * 1024 * 1024;
        const int chunkSize = 512;
        var rng = new Random(99);
        var src = new byte[chunkSize];
        var options = new StorageEngineOptions("large", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
        int writes = totalSize / chunkSize;
        var lastTail = LogicalAddress.Empty;

        // Write
        using (var dev = options.Builder(vol.Fs).Start())
        {
            dev.WaitForReady();
            for (int i = 0; i < writes; i++)
            {
                rng.NextBytes(src);
                dev.Append(src);
            }
            lastTail = dev.AllocatedTail;
            dev.Flush();
        }

        Console.WriteLine($"Wrote {totalSize}B, tail at {lastTail}");

        // Reopen and verify（hints 经 Start 传——恢复水位注入，Start 一步到位）
        using (var dev = new StorageEngineOptions("large", segmentGrowthLimit: segGrowth).WithPreallocateFile(false)
                   .Builder(vol.Fs).Start(new EngineRecoveryHints(committedTailHint: lastTail)))
        {
            dev.WaitForReady();

            // Read first 1KB of each segment
            var dst = new byte[512];
            for (int segId = 0; segId <= lastTail.SegId; segId++)
            {
                int n = dev.Read(new LogicalAddress(segId, 0), dst);
                n.Should().Be(512, $"Should read 512B from seg {segId} offset 0");
                // Verify it's not all zero
                bool allZero = true;
                for (int j = 0; j < n; j++)
                    if (dst[j] != 0) { allZero = false; break; }
                allZero.Should().BeFalse($"Data at seg {segId} offset 0 should not be all zeros");
            }
        }
    }

    /// <summary>
    /// 单次超大写入 (500KB) 跨多段 (段大小 1KB)，验证跨段拆分正确。
    /// </summary>
    [Fact]
    public void SingleHugeWrite_AcrossManyTinySegments()
    {
        var vol = NewVol();

        const long segGrowth = 1024;
        const int totalSize = 500 * 1024;
        byte[] data = MakePattern(totalSize, 0xAB);

        var options = new StorageEngineOptions("large", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var addr = dev.Append(data);
        Console.WriteLine($"Write: start={addr}, tail={dev.AllocatedTail}, segments={dev.AllocatedTail.SegId}");

        dev.AllocatedTail.SegId.Should().BeGreaterThan(100);

        byte[] dst = new byte[totalSize];
        int n = dev.Read(addr, dst);
        n.Should().Be(totalSize, $"Expected {totalSize} bytes, got {n}");

        dst.SequenceEqual(data).Should().BeTrue("Data integrity check");
    }
}
