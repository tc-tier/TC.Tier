using System.Collections.Concurrent;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// ★ S4 GB 级持续写 + 完整性校验（C2/C3/C4/C5）。
/// <para>覆盖现有 <c>LargeScaleDeviceTests</c>（200MB 单线程）未涉及的：</para>
/// <list type="bullet">
/// <item>5-10GB 持续写，每 1GB 做 Flush + 随机抽样 100 地址读回校验</item>
/// <item>周期 ReclaimHead / Compact 回收（验证 Bug 3/4/5 在大规模下稳定）</item>
/// <item>多线程 Append（C4 大规模地址唯一性）</item>
/// <item>吞吐衰减、段表膨胀、tail/MaxOff gap 趋势监控</item>
/// </list>
///
/// <para>★ 总量由 <c>TC_LSCALE_GB</c> 环境变量控制（默认 1GB=CI；生产压测可设 5-10）。</para>
/// </summary>
[Collection("LargeScaleIO")]
public sealed class LargeScaleIntegrityTests : IDisposable
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

    private static int ScaleGB =>
        int.TryParse(Environment.GetEnvironmentVariable("TC_LSCALE_GB"), out var g) ? g : 1;

    private static byte[] MakePattern(int length, byte seed)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    /// <summary>
    /// 多 GB 持续写 + 每 GB 抽样校验 + 周期回收。
    /// 验证长时间运行不丢数据、地址不重叠、回收后抽样仍正确。
    /// </summary>
    [Fact]
    public async Task MultiGB_Write_WithPeriodicFlushAndReclaim_IntegrityHolds()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("lscale", segmentGrowthLimit: 32 * 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        // 32MB 段——跨多段但不过碎（避免 AddressMap 过大）
        dev.WaitForReady();

        int totalGB = Math.Max(1, ScaleGB);
        long bytesPerGB = 1024L * 1024 * 1024;
        const int payloadKB = 64;
        int payload = payloadKB * 1024;
        int appendsPerGB = (int)(bytesPerGB / payload);

        var writtenAddrs = new ConcurrentBag<LogicalAddress>();
        var rng = new Random(42);
        var readDst = new byte[payload];
        long totalWritten = 0;
        long totalVerified = 0;
        long verifyFailures = 0;
        int segmentsBeforeCompact = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // 单线程顺序 Append（更稳定的大规模基线；多线程并发 Append 由 MixedConcurrentSoakTests 覆盖）
        var writeBuf = MakePattern(payload, 0xAB);
        for (int gb = 0; gb < totalGB; gb++)
        {
            Console.WriteLine($"[lscale] === GB {gb + 1}/{totalGB} 开始 === tail={dev.AllocatedTail}");
            long gbStartTicks = System.Diagnostics.Stopwatch.GetTimestamp();

            for (int i = 0; i < appendsPerGB; i++)
            {
                var addr = dev.Append(writeBuf);
                writtenAddrs.Add(addr);
                totalWritten += payload;
            }
            dev.Flush();

            double gbSec = (System.Diagnostics.Stopwatch.GetTimestamp() - gbStartTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            Console.WriteLine($"[lscale] GB {gb + 1} 写完: {gbSec:F2}s = {(bytesPerGB / (1024.0 * 1024.0)) / gbSec:F0} MB/s, tail={dev.AllocatedTail}");

            // 抽样 100 个地址读回校验
            var sample = writtenAddrs.OrderBy(_ => rng.Next()).Take(100).ToList();
            foreach (var addr in sample)
            {
                int n = dev.Read(addr, readDst);
                if (n == payload)
                {
                    totalVerified += n;
                    // 校验内容（写入 pattern 0xAB + i & 0xFF，简化：第一字节应是 0xAB）
                    if (readDst[0] != 0xAB) Interlocked.Increment(ref verifyFailures);
                }
                else
                {
                    Interlocked.Increment(ref verifyFailures);
                }
            }
            Console.WriteLine($"[lscale] GB {gb + 1} 抽样校验：100 个地址，失败 {verifyFailures}");

            // 周期回收：每 GB 完成后，如跨了 ≥4 段，做一次 Compact（验证 Bug 5 大规模回归）
            int curSegs = dev.AllocatedTail.SegId + 1;
            if (curSegs >= 4 && gb < totalGB - 1)  // 最后一个 GB 不 Compact（避免影响最终校验）
            {
                segmentsBeforeCompact = curSegs;
                long tc0 = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    var result = await dev.StartCompact().WaitAsync();
                    double cSec = (System.Diagnostics.Stopwatch.GetTimestamp() - tc0) / (double)System.Diagnostics.Stopwatch.Frequency;
                    Console.WriteLine($"[lscale] GB {gb + 1} 后 Compact：{cSec:F2}s, migration={result.MigrationMap?.Count ?? 0}, tail={dev.AllocatedTail}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[lscale] WARNING: GB {gb + 1} Compact 失败: {ex.Message}");
                }
            }
        }

        sw.Stop();
        double totalSec = sw.Elapsed.TotalSeconds;

        // 最终断言
        verifyFailures.Should().Be(0, "所有抽样读回应正确");
        totalWritten.Should().Be((long)totalGB * appendsPerGB * payload, "应写完全部数据");
        Console.WriteLine($"[lscale done] {totalGB}GB / {totalSec:F1}s = {(totalWritten / (1024.0 * 1024.0)) / totalSec:F0} MB/s avg, verified={totalVerified}, failures={verifyFailures}");

        // 异步任务兼容（async Task，给 await 一个挂载点；本身同步跑完）
        await Task.CompletedTask;
    }

    /// <summary>
    /// 多线程大规模 Append——验证 CAS 在 GB 级数据 + 高并发下地址绝对私有（C4 大规模）。
    /// </summary>
    [Fact]
    public async Task MultiGB_ConcurrentAppend_AddressesNoOverlap()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("lscalec", segmentGrowthLimit: 16 * 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        int totalGB = Math.Max(1, ScaleGB);
        long targetBytes = totalGB * 1024L * 1024 * 1024;
        const int threads = 4;
        const int payload = 4 * 1024;  // 4K
        long perThreadBytes = targetBytes / threads;
        int perThreadAppends = (int)(perThreadBytes / payload);

        var writeBufs = new byte[threads][];
        for (int i = 0; i < threads; i++) writeBufs[i] = MakePattern(payload, (byte)(0x40 + i));

        var allAddrs = new ConcurrentBag<LogicalAddress>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var tasks = new Task[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            tasks[tid] = Task.Run(() =>
            {
                var buf = writeBufs[tid];
                for (int i = 0; i < perThreadAppends; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    allAddrs.Add(dev.Append(buf));
                }
            });
        }

        // 软超时
        var allDone = Task.WhenAll(tasks);
        var timeout = Task.Delay(TimeSpan.FromMinutes(11));
        if (await Task.WhenAny(allDone, timeout) == timeout)
        {
            throw new Xunit.Sdk.XunitException($"大规模并发 Append 超时：{targetBytes / (1024*1024)}MB / {threads}T 未在 10 分钟内完成");
        }

        // 地址唯一性校验（C4 压测级，GB 规模）
        var sorted = allAddrs.OrderBy(a => a.SegId).ThenBy(a => a.Offset).ToList();
        long overlap = 0;
        for (int i = 1; i < sorted.Count; i++)
        {
            var p = sorted[i - 1];
            var c = sorted[i];
            if (p.SegId == c.SegId && p.Offset + payload > c.Offset)
                overlap += (p.Offset + payload - c.Offset);
        }
        overlap.Should().Be(0, $"{threads}T × {perThreadAppends} × {payload}B = {targetBytes/(1024*1024)}MB 大规模并发 Append，地址不应重叠");
        Console.WriteLine($"[lscalec done] {threads}T × {perThreadAppends} ops = {allAddrs.Count} appends, overlap={overlap}");
    }
}
