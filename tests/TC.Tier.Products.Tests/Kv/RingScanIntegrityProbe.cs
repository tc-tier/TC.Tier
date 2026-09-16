using FluentAssertions;
using TC.Tier.Products.Kv;
using TC.Tier.Contracts.Storage;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// 诊断探针：Ring ScanAsync 游标完整性——并发写下按 SafeSnapshotTail 窗口采样对账。
/// 判据（SafeSnapshotTail 语义）：快照之前的记录全部 header 完整可见——写全部完成后，
/// 对每个历史采样窗口 [Begin, snap)：地址 &lt; snap 的记录必须全部 ∈ 该采样产出。
/// 若不等 = 游标在完整数据上漏产出（游标 bug 铁证 + 丢失 key 列表）。
/// </summary>
public class RingScanIntegrityProbe
{
    [Fact]
    public async Task Scan_WindowIntegrity_UnderConcurrentWrites()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(
            vol.Fs, TierKvOptions.Default.WithKvName("ring-scan-probe"));

        const int writers = 4, perWriter = 200;
        var total = writers * perWriter;

        // 写者后台持续写
        var writeTask = Task.Run(async () =>
        {
            var tasks = Enumerable.Range(0, writers).Select(async w =>
            {
                using var ws = kv.CreateSession(KvSessionConditions.None);
                for (long i = 1; i <= perWriter; i++)
                    await ws.PutFormattedAsync(w * perWriter + i, w * perWriter + i,
                        KvCommitPolicy.FireAndForget);
            });
            await Task.WhenAll(tasks);
        });

        // 主线程并发采样（写者进行中）
        var samples = new List<(LogicalAddress Snap, HashSet<long> Keys)>();
        while (!writeTask.IsCompleted)
        {
            var snap = kv.Ring.TakeSafeSnapshotTail();
            var keys = new HashSet<long>();
            await foreach (var (k, a, tomb) in kv.Ring.ScanAsync(kv.Ring.BeginAddress, snap))
                if (!tomb) keys.Add(k);
            samples.Add((snap, keys));
        }
        await writeTask;

        // 事后真源：key → addr（全量，此时无并发）
        var keyAddrs = new Dictionary<long, LogicalAddress>();
        await foreach (var (k, a, t) in kv.Ring.ScanAsync(kv.Ring.BeginAddress, kv.Ring.TailAddress))
            if (!t) keyAddrs[k] = a;
        keyAddrs.Should().HaveCount(total, "Ring 真源完整性");

        // 逐采样对账：addr < snap 的 key 必须全部 ∈ 采样产出
        foreach (var (snap, keys) in samples)
        {
            var inWindow = keyAddrs.Where(kv => kv.Value < snap).Select(kv => kv.Key).ToList();
            var missing = inWindow.Where(k => !keys.Contains(k)).ToList();
            missing.Should().BeEmpty(
                $"采样窗口 [Begin,{snap}) 内 {inWindow.Count} 条必须全产出（SafeSnapshotTail 语义）；漏 {missing.Count}：{string.Join(",", missing.Take(5))}");
        }
    }
}
