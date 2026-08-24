using System.Diagnostics;

namespace TC.Tier.Core.Tests.NativeInterop;

/// <summary>
/// 裸文件预分配对比验证——真实预分配写 vs 稀疏按需写。
/// <para>不经过 ManagedLocalStorageDevice，直接 SafeFileHandle + RandomAccess，
///   隔离预分配本身的影响。</para>
/// <para>验证 spec 不变量第 11 条"不做真实预分配"是否成立——
///   若预分配有显著收益，该不变量需修正。</para>
/// <para>这不是 BDN benchmark，是直测（Stopwatch + 分位数），结果输出到 xUnit ITestOutputHelper。</para>
/// </summary>
public sealed class PreallocateBenchTests : IDisposable
{
    private readonly List<string> _dirs = new();

    public void Dispose()
    {
        foreach (var dir in _dirs) TestTempDir.TryCleanup(dir);
    }

    private string NewPath()
    {
        var dir = TestTempDir.Create("tc-prealloc-bench");
        _dirs.Add(dir);
        return Path.Combine(dir, "bench.dat");
    }

    /// <summary>写测试结果：吞吐 + p50/p99/max 延迟。</summary>
    private readonly record struct WriteStats(
        double ThroughputMBps, double P50Us, double P99Us, double MaxUs, long AllocatedDiskBytes);

    /// <summary>裸文件顺序写 TotalBytes，记录每块延迟。</summary>
    private static WriteStats WriteFile(string path, bool preallocate, int blockSize, long totalBytes)
    {
        using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, FileOptions.None);

        if (preallocate)
        {
            // 真实预分配（FileNative.PreallocateFile）
            FileNative.PreallocateFile(handle, totalBytes);
        }

        var buf = new byte[blockSize];
        Array.Fill(buf, (byte)0x42);
        int blocks = (int)(totalBytes / blockSize);
        var latencies = new double[blocks];
        var sw = Stopwatch.StartNew();

        for (int i = 0; i < blocks; i++)
        {
            var blockSw = Stopwatch.StartNew();
            RandomAccess.Write(handle, buf, i * blockSize);
            blockSw.Stop();
            latencies[i] = blockSw.Elapsed.TotalMicroseconds;
        }
        RandomAccess.FlushToDisk(handle);
        sw.Stop();

        Array.Sort(latencies);
        double throughput = totalBytes / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
        double p50 = latencies[latencies.Length / 2];
        double p99 = latencies[(int)(latencies.Length * 0.99)];
        double max = latencies[^1];
        long allocated = FileNative.GetFileAllocatedDiskSize(handle);

        return new WriteStats(throughput, p50, p99, max, allocated);
    }

    /// <summary>对比预分配 vs 稀疏：多种块大小，输出结果表。</summary>
    [Fact]
    public void Preallocate_vs_Sparse_RawFileComparison()
    {
        // 测试矩阵：块大小 × (预分配/稀疏)
        // 用较小的总量避免占太多磁盘，但足够暴露元数据扩展开销
        long totalBytes = 256 * 1024 * 1024;  // 256MB
        int[] blockSizes = { 4096, 65536, 1048576 };

        var lines = new List<string>
        {
            "", "═══ 裸文件预分配对比（真实预分配 vs 稀疏按需）═══",
            $"总量={totalBytes / 1024 / 1024}MB  平台={Environment.OSVersion.VersionString}",
            $"{"Block",-8} {"Mode",-10} {"MB/s",-10} {"p50us",-10} {"p99us",-10} {"Maxus",-10} {"AllocMB",-10}"
        };

        foreach (var bs in blockSizes)
        {
            // 稀疏（不预分配）
            var sparsePath = NewPath();
            var sparse = WriteFile(sparsePath, preallocate: false, bs, totalBytes);
            lines.Add($"{bs / 1024,4}K    {"Sparse",-10} {sparse.ThroughputMBps,8:F0}   {sparse.P50Us,8:F1}   {sparse.P99Us,8:F1}   {sparse.MaxUs,8:F0}   {sparse.AllocatedDiskBytes / 1024 / 1024,8:F0}");

            // 真实预分配
            var prePath = NewPath();
            var pre = WriteFile(prePath, preallocate: true, bs, totalBytes);
            lines.Add($"{bs / 1024,4}K    {"Prealloc",-10} {pre.ThroughputMBps,8:F0}   {pre.P50Us,8:F1}   {pre.P99Us,8:F1}   {pre.MaxUs,8:F0}   {pre.AllocatedDiskBytes / 1024 / 1024,8:F0}");

            // 收益计算
            var tpDelta = (pre.ThroughputMBps - sparse.ThroughputMBps) / sparse.ThroughputMBps * 100;
            var maxDelta = (pre.MaxUs - sparse.MaxUs) / sparse.MaxUs * 100;
            lines.Add($"{bs / 1024,4}K    → 吞吐 {tpDelta:+0.0;-0.0;%}  Max延迟 {maxDelta:+0.0;-0.0;%}");
            lines.Add("");
        }

        lines.Add("═══ 结论依据：吞吐正收益 + Max延迟降低 = 预分配有价值 ═══");
        var report = string.Join("\n", lines);
        Console.WriteLine(report);

        // 验证预分配确实生效（AllocatedDiskBytes 应接近 totalBytes）
        // 若预分配失效，所有对比都是噪声
        var verifyPath = NewPath();
        using (var h = File.OpenHandle(verifyPath, FileMode.CreateNew, FileAccess.ReadWrite))
        {
            FileNative.PreallocateFile(h, 64 * 1024 * 1024);
            var allocated = FileNative.GetFileAllocatedDiskSize(h);
            allocated.Should().BeGreaterThan(60 * 1024 * 1024,
                "预分配必须真实生效（磁盘占用≈分配量），否则对比是噪声");
        }
    }
}
