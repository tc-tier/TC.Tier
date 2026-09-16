using System.Threading.Tasks;
using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W8 契约测试——TTL/过期（F4）：惰性读删（点查/扫描/RMW 按过期戳判定）+ 回收强删
/// （过期记录不视为存活，下界可越过其物理回收）+ 边界（无 TTL 恒存/Zero 立即过期/RMW 重置 TTL）。
/// <para>★ 帧格式：TierKv 值统一 [0xC8][8B 过期 ticks LE][payload]——全量封装无嗅探歧义。</para>
/// </summary>
public class KvTtlTests
{
    private sealed class CountingFunctions : KvFunctionsBase<long, long, long, long, string?>
    {
        public readonly List<string> Log = new();
        public override bool InitialUpdater(ref long k, ref long input, ref long v, ref long o, ref string? c)
        { Log.Add("Initial"); v = input; o = v; return true; }
        public override bool InPlaceUpdater(ref long k, ref long input, ref long v, ref long o, ref string? c)
        { Log.Add("InPlace"); v += input; o = v; return true; }
        public override bool CopyUpdater(ref long k, ref long input, ref long oldV, ref long newV, ref long o, ref string? c)
        { Log.Add("Copy"); newV = oldV + input; o = newV; return true; }
    }

    private static TierKvOptions Opts(string suffix, bool range = true)
        => TierKvOptions.Default.WithKvName("tier-kv-w8-" + suffix).WithRangeIndex(range);

    [Fact]
    public async Task Ttl_ZeroTimeSpan_ImmediatelyExpired_ReadMiss()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("zero"));
        await kv.PutFormattedAsync(1, 42, timeToLive: TimeSpan.Zero);   // 过期 = now → 立即过期（边界）
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse("过期 = 惰性读删未命中");
    }

    [Fact]
    public async Task Ttl_NotExpired_Visible_ThenExpires()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("visible"));
        await kv.PutFormattedAsync(1, 7, timeToLive: TimeSpan.FromMinutes(1));
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(7, "未过期可见");
    }

    [Fact]
    public async Task NoTtl_ValueNeverExpires()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("nottl"));
        await kv.PutFormattedAsync(1, 1);
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeTrue("无 TTL 恒存");
    }

    [Fact]
    public async Task Ttl_Scan_SkipsExpired()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("scan"));
        await kv.PutFormattedAsync(1, 1);
        await kv.PutFormattedAsync(2, 2, timeToLive: TimeSpan.Zero);   // 立即过期
        await kv.PutFormattedAsync(3, 3);

        var keys = new List<long>();
        await foreach (var e in kv.ScanByPrefixAsync(0, 0))
            keys.Add(e.Key);
        keys.Should().BeEquivalentTo([1L, 3L], "扫描跳过过期条目");
    }

    [Fact]
    public async Task Ttl_Reclaim_TreatsExpiredAsSuperseded()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("reclaim"));
        var addr1 = await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed);          // 无 TTL——存活，钉住下界
        var addr2 = await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);   // 立即过期
        // ★ 显式 Committed：回收安全下界只推已持久化前缀（默认内存档不落盘——下界不动）

        var result = await kv.ReclaimAsync();
        result.SupersededCount.Should().Be(1, "key2 过期记录=被取代（回收强删）；key1 存活");
        kv.Ring.BeginAddress.Should().Be(addr1, "下界钉在存活记录地址——过期前缀已回收");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(1, "存活记录回收后读安全");
    }

    [Fact]
    public async Task Rmw_OnExpiredKey_InitialFlowResets()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("rmw"));
        await kv.PutFormattedAsync(1, 100, timeToLive: TimeSpan.Zero);   // 立即过期
        var f = new CountingFunctions();
        (await kv.RmwAsync(1, input: 5, f, context: "ctx")).Should().Be(KvStatus.Ok);
        f.Log.Should().Contain("Initial", "过期 = 无值——RMW 走 Initial 流（重置语义）");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(5);
    }

    [Fact]
    public async Task Session_TtlWrite_ExpiredByWriteSet()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("sess"));
        using var s = kv.CreateSession();
        await s.PutFormattedAsync(1, 7, timeToLive: TimeSpan.Zero);
        (await s.TryGetFormattedAsync(1)).Found.Should().BeFalse("写集条目过期——本会话读未命中");
    }

    [Fact]
    public async Task GroupCommit_ParallelCommittedWriters_AllDurable()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("group"));
        const int writers = 8, perWriter = 25;

        var lastAddrs = new System.Collections.Concurrent.ConcurrentBag<LogicalAddress>();
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            using var s = kv.CreateSession();
            for (var i = 0; i < perWriter; i++)
            {
                var key = (long)(w * perWriter + i + 1);
                var addr = await s.PutFormattedAsync(key, key, policy: KvCommitPolicy.Committed);
                lastAddrs.Add(addr);
            }
        })));

        var total = writers * perWriter;
        kv.Count.Should().Be(total, "并发 Committed 写全数应用");
        kv.Ring.FlushedUntilAddress.Should().BeGreaterOrEqualTo(lastAddrs.Min(),
            "组提交等待覆盖自己地址——返回即已落盘（水位 ≥ 各写者末地址）");
        for (var k = 1; k <= total; k++)
            (await kv.TryGetFormattedAsync(k)).Value.Should().Be((long)k);
    }

    [Fact]
    public async Task ByteFace_TtlFraming_Transparent()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("byte"));
        var payload = "hello"u8.ToArray();
        await kv.PutFormattedAsync(1, 1);
        await kv.PutAsync(2, payload, timeToLive: TimeSpan.Zero, ct: default);   // 字节面 + TTL（policy 缺省）
        (await kv.TryGetBytesAsync(2)).Should().BeNull("字节面过期同样惰性读删");
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(1L), "无 TTL 字节面不受帧影响");
    }
}
