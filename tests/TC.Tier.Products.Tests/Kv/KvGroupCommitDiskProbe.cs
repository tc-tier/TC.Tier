using System.Diagnostics;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>磁盘组提交摊薄探针（DIO+WT，Committed 并发写均摊延迟）。</summary>
public class KvGroupCommitDiskProbe
{
    [Fact]
    public Task Probe_GC_W1() => Probe_GroupCommit_Amortized(1);

    [Fact]
    public Task Probe_GC_W8() => Probe_GroupCommit_Amortized(8);

    [Fact]
    public Task Probe_GC_W32() => Probe_GroupCommit_Amortized(32);

    private async Task Probe_GroupCommit_Amortized(int writers)
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tierkv-gc-" + Guid.NewGuid().ToString("N"));
        var fs = TierFs.New("local:" + root);
        var options = TierKvOptions.Default
            .WithKvName($"tierkv-gc-{writers}")
            .WithHints(FileOpenHints.NoBuffering | FileOpenHints.WriteThrough);
        await using var kv = await TierKvOfLongLong.CreateAsync(fs, options);

        // 预填 2000 条建立页池热态
        using (var s = kv.CreateSession(KvSessionConditions.None))
        {
            for (var i = 0; i < 2_000; i++)
                await s.PutFormattedAsync(i, i, KvCommitPolicy.Committed);
            await s.CompletePendingAsync();
        }

        const int perWriter = 512;
        var sw = Stopwatch.StartNew();
        // ★ 纯异步并发（严禁 Parallel.For + GetResult——线程池饥饿雪崩，TCSG137 同源）
        await Task.WhenAll(Enumerable.Range(0, writers).Select(async w =>
        {
            using var ws = kv.CreateSession(KvSessionConditions.None);
            for (var i = 0; i < perWriter; i++)
            {
                var key = 10_000_000L + w * perWriter + i;
                await ws.PutFormattedAsync(key, key, KvCommitPolicy.Committed);
            }
        }));
        sw.Stop();
        var total = (double)writers * perWriter;
        var amortizedUs = sw.Elapsed.TotalMilliseconds * 1000 / total;
        var throughput = total / sw.Elapsed.TotalSeconds / 1000;

        Console.WriteLine($"[gc] writers={writers}: 均摊 {amortizedUs:F0} µs/条, 吞吐 {throughput:F0} Kops/s, 总 {sw.Elapsed.TotalMilliseconds:F0} ms");

        fs.Dispose();
        if (System.IO.Directory.Exists(root))
            System.IO.Directory.Delete(root, true);

        Assert.True(amortizedUs > 0);
    }
}
