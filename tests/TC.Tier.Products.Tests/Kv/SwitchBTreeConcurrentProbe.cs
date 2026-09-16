using FluentAssertions;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Products.Kv;
using Xunit;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// 诊断探针：切换到 BTree 后的 FF 并发写对账（BTree 并发 Insert 短板验证）——
/// 不切换（Hash，CAS）与切换后（BTree，单写者假设）的并发写丢失对照。
/// </summary>
public class SwitchBTreeConcurrentProbe
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-probe-" + suffix);

    private static Func<TierKvOptions, RingBase<long>, IIndex<long>> BTreeFactory(TestVolume vol)
        => (o, ring) => new ByteOrderBTreeIndex<long>(vol.Fs, TierKvAssembly.BTreeSettings(vol.Fs, o), keyResolver: ring);

    [Fact]
    public async Task Probe_HashVsBTree_ConcurrentPutLoss()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("probe"));
        const int writers = 4, perWriter = 50;

        // ── 阶段 1：Hash（起始装配，CAS 并发安全）1000 条并发写对账 ──
        for (var batch = 0; batch < 10; batch++)
        {
            var baseKey = batch * writers * perWriter;
            var tasks = Enumerable.Range(0, writers).Select(async w =>
            {
                using var ws = kv.CreateSession(KvSessionConditions.None);
                for (long i = 1; i <= perWriter; i++)
                    await ws.PutFormattedAsync(baseKey + w * perWriter + i,
                        baseKey + w * perWriter + i, KvCommitPolicy.FireAndForget);
            });
            await Task.WhenAll(tasks);
        }
        var hashLoss = 2000L - kv.Count;
        hashLoss.Should().Be(0, "Hash（CAS）并发写应零丢失");

        // ── 阶段 2：切 BTree 后同强度 1000 条并发写对账 ──
        await kv.SwitchIndexAsync(BTreeFactory(vol));
        for (var batch = 0; batch < 10; batch++)
        {
            var baseKey = 2000L + batch * writers * perWriter;
            var tasks = Enumerable.Range(0, writers).Select(async w =>
            {
                using var ws = kv.CreateSession(KvSessionConditions.None);
                for (long i = 1; i <= perWriter; i++)
                    await ws.PutFormattedAsync(baseKey + w * perWriter + i,
                        baseKey + w * perWriter + i, KvCommitPolicy.FireAndForget);
            });
            await Task.WhenAll(tasks);
        }
        var btreeLoss = 4000L - kv.Count;
        btreeLoss.Should().Be(0, "BTree 并发写若丢失=单写者假设在 FF 形态下被打破（已知短板实锤）");

        // 输出对照结论（探针报告）
        Console.WriteLine($"[probe] Hash loss={hashLoss}, BTree loss={btreeLoss}");
    }
}
