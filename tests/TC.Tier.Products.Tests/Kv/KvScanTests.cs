using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W7 契约测试——范围/前缀扫描（F1，key 字节序 BTree 范围索引 + KV 级游标 API）：
/// 前缀扫描（字节前缀 = 字节序连续区间，越界即止）、Range [start, end)、覆写最新值、
/// 删除不产出、跨重开恢复（范围索引二级启动重建）、空范围/未启用 fail-fast。
/// <para>★ long 键 LE 布局：byte0 = 最低字节——键按 byte0 先序。测试键按字节模式构造：
/// 0x10（[10,0…])、0x110（[10,01,…])、0x210（[10,02,…]) 共享前缀字节 0x10。</para>
/// </summary>
public class KvScanTests
{
    private const long PrefixB0 = 0x10;              // 单字节前缀 [10,0,…]
    private static readonly long K10 = 0x10;         // [10,00,…]
    private static readonly long K110 = 0x110;       // [10,01,…]
    private static readonly long K210 = 0x210;       // [10,02,…]
    private static readonly long K20 = 0x20;         // [20,00,…]
    private static readonly long K30 = 0x30;         // [30,00,…]

    private static TierKvOptions Opts(string suffix, bool range = true)
        => TierKvOptions.Default.WithKvName("tier-kv-w7-" + suffix).WithRangeIndex(range);

    private static async Task<TierKvOfLongLong> SeedAsync(TestVolume vol, string suffix)
    {
        var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts(suffix));
        using var s = kv.CreateSession(KvSessionConditions.None);
        foreach (var key in new[] { K10, K110, K210, K20, K30 })
            await s.PutFormattedAsync(key, key, policy: KvCommitPolicy.FireAndForget);   // 值 = key（自证）
        await s.CompletePendingAsync();
        return kv;
    }

    [Fact]
    public async Task ScanByPrefix_ByteOrder_ContiguousRange()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "prefix");

        var results = new List<(long Key, long Value)>();
        await foreach (var e in kv.ScanByPrefixAsync(PrefixB0, prefixByteLength: 1))
            results.Add((e.Key, e.Value));

        results.Should().BeEquivalentTo([(K10, K10), (K110, K110), (K210, K210)],
            o => o.WithStrictOrdering(), "共享前缀字节的 key 构成字节序连续区间，按序产出");
    }

    [Fact]
    public async Task ScanByRange_BoundsOverwriteDelete()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "range");

        // 覆写一条（scan 必产最新值）+ 删一条（scan 不产出）
        using (var s = kv.CreateSession())
        {
            await s.PutFormattedAsync(K110, 999);
            await s.DeleteAsync(K20);
        }

        var results = new List<(long Key, long Value)>();
        await foreach (var e in kv.ScanByRangeAsync(K110, K30))   // [K110, K30)：字节序 [10,01…]..[30,…)
            results.Add((e.Key, e.Value));

        results.Should().BeEquivalentTo([
            (K110, 999L),   // 覆写后最新值
            (K210, K210),
        ], o => o.WithStrictOrdering(), "范围 [start, end) 含下界不含上界（K30=上界排除）；已删 K20 不产出");

        // 全域扫描：删除的 K20 不产出，总数 4
        var all = new List<long>();
        await foreach (var e in kv.ScanByRangeAsync(long.MinValue, long.MaxValue))
            all.Add(e.Key);
        all.Should().HaveCount(4).And.NotContain(K20, "已删 key 不产出");
    }

    [Fact]
    public async Task Scan_Reopen_RangeIndexRebuilt()
    {
        using var vol = new TestVolume();
        var opts = Opts("reopen");
        await using (var kv = await SeedAsync(vol, "reopen"))
        {
            using var s = kv.CreateSession(KvSessionConditions.None);
            await s.PutFormattedAsync(0x110L + 1, 111, policy: KvCommitPolicy.FireAndForget);   // (0x111)= [11,01,…] 新前缀组
            await s.CompletePendingAsync();
        }

        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        var results = new List<(long Key, long Value)>();
        await foreach (var e in kv2.ScanByPrefixAsync(PrefixB0, prefixByteLength: 1))
            results.Add((e.Key, e.Value));
        results.Should().BeEquivalentTo([(K10, K10), (K110, K110), (K210, K210)],
            o => o.WithStrictOrdering(), "重开范围索引二级恢复重建后扫描等价（新增 0x111 属另一前缀组不产出）");
    }

    [Fact]
    public async Task Scan_RangeIndexDisabled_FailsFast()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("off", range: false));
        var act = async () => { await foreach (var _ in kv.ScanByPrefixAsync(1, 1)) { } };
        await act.Should().ThrowAsync<InvalidOperationException>("未启用范围索引——配置期缺失运行期 fail-fast");
    }

    [Fact]
    public async Task Scan_EmptyRange_YieldsNothing()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "empty");
        var results = new List<(long Key, long Value)>();
        await foreach (var e in kv.ScanByRangeAsync(5, 1))   // start >= end
            results.Add((e.Key, e.Value));
        results.Should().BeEmpty("空区间直接产出空");
    }
}
