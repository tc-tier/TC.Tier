using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// 复合键前缀扫描契约（#485 回归——IsBytePrefix 对任意 prefixByteLength ∈ [1,sizeof] 语义一致）：
/// >8 字节定长键的 8 字节前缀 = 首字段字节区间，按字节前缀匹配（尾部字节不参与），扫描/watch 同一口径。
/// <para>★ TestKey = (long Id, int Tag) 12 字节，blit 布局 Id 字节在前——同 Id 键构成字节序连续
/// 区间，按 Tag 字节序产出。</para>
/// </summary>
public class KvScanCompositeKeyTests
{
    private static readonly TestKey K11 = new(1, 1);
    private static readonly TestKey K12 = new(1, 2);
    private static readonly TestKey K21 = new(2, 1);

    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-scan-comp-" + suffix).WithRangeIndex(true);

    private static async Task<TierKvOfTestKeyTestPayload> SeedAsync(TestVolume vol, string suffix)
    {
        var kv = await TierKvOfTestKeyTestPayload.CreateAsync(vol.Fs, Opts(suffix));
        using var s = kv.CreateSession(KvSessionConditions.None);
        foreach (var key in new[] { K11, K12, K21 })
            await s.PutFormattedAsync(key, new TestPayload("v" + key.Tag), policy: KvCommitPolicy.FireAndForget);
        await s.CompletePendingAsync();
        return kv;
    }

    [Fact]
    public async Task ScanByPrefix_EightBytePrefix_MatchesIdFieldOnly()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "8b");

        var results = new List<TestKey>();
        await foreach (var e in kv.ScanByPrefixAsync(new TestKey(1, 0), prefixByteLength: 8))
            results.Add(e.Key);

        results.Should().Equal([K11, K12],
            "8 字节前缀 = Id 字段全宽：同 Id 按 Tag 序产出，异 Id（Tag 外字节差异）不产出");
    }

    [Fact]
    public async Task ScanByPrefixWithExpiry_EightBytePrefix_SameSemantics()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "8b-exp");

        var results = new List<(TestKey, string)>();
        await foreach (var e in kv.ScanByPrefixWithExpiryAsync(new TestKey(1, 0), prefixByteLength: 8))
            results.Add((e.Key, e.Value.Text));

        results.Should().Equal(new[] { (K11, "v1"), (K12, "v2") },
            "含过期维度的前缀扫描同口径：Id 字段 8 字节匹配");
    }

    [Fact]
    public async Task ScanByPrefix_TwelveBytePrefix_RequiresFullKeyMatch()
    {
        using var vol = new TestVolume();
        await using var kv = await SeedAsync(vol, "12b");

        var results = new List<TestKey>();
        await foreach (var e in kv.ScanByPrefixAsync(new TestKey(1, 1), prefixByteLength: 12))
            results.Add(e.Key);

        results.Should().Equal([K11], "前缀长度 = sizeof(TestKey) 时即整键匹配，仅键自身命中");
    }
}
