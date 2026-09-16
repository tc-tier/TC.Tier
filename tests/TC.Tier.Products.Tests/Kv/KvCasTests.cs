using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// A.2b 契约测试——CompareAndSwap（地址即版本的乐观锁，tierkv-ttl-cas-addressread-design.md §2/§4）：
/// 地址版竞争换绑 / 值版防并发插入恰一成功 / CAS×过期不存在语义 / 本地单 worker 串行等价（raft 形态）/ 批内拒绝。
/// </summary>
public class KvCasTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-a2b-" + suffix);

    [Fact]
    public async Task AddressCas_StaleExpected_FailsWithCurrent_RebindSucceeds()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("addr"));
        var addrX = await kv.PutFormattedAsync(1, 100, policy: KvCommitPolicy.Committed);
        var addrY = await kv.PutFormattedAsync(1, 200, policy: KvCommitPolicy.Committed);

        // A 持陈旧地址 x——B 已覆盖：CAS(x) 失败并返回当前绑定
        var stale = await kv.CompareAndSwapAsync(1, addrX, BitConverter.GetBytes(300L));
        stale.Swapped.Should().BeFalse("expected 已被并发写取代");
        stale.CurrentAddress.Should().Be(addrY, "失败回执携带当前绑定（失败原因自明）");
        stale.NewAddress.IsValid.Should().BeFalse("失败零写入");

        // A 重读新地址再 CAS——成功
        var ok = await kv.CompareAndSwapAsync(1, addrY, BitConverter.GetBytes(300L));
        ok.Swapped.Should().BeTrue();
        ok.NewAddress.IsValid.Should().BeTrue();
        ok.NewAddress.Should().Be(ok.CurrentAddress, "换绑后当前 = 新绑定");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(300, "CAS 成功产物可见");
    }

    [Fact]
    public async Task ValueCas_ConcurrentInsert_ExactlyOneWins()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("insert"));

        // 两并发 CAS(expectedValue=null) 同 key——恰一成功（防并发插入）
        var r1 = kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(111L)).AsTask();
        var r2 = kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(222L)).AsTask();
        await Task.WhenAll(r1, r2);

        (r1.Result.Swapped ^ r2.Result.Swapped).Should().BeTrue("恰一成功——防丢失保证对 CAS 调用方成立");
        var winner = r1.Result.Swapped ? 111L : 222L;
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(winner),
            "最终值 = 胜者产物");
    }

    [Fact]
    public async Task Cas_ExpiredBinding_TreatedAsAbsent()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("expired"));
        var addrX = await kv.PutFormattedAsync(1, 100, policy: KvCommitPolicy.Committed,
            timeToLive: TimeSpan.Zero);   // 立即过期

        // 地址版：expected 指向过期记录 → 绑定视为不存在 → false + Current=Invalid
        var byAddr = await kv.CompareAndSwapAsync(1, addrX, BitConverter.GetBytes(300L));
        byAddr.Swapped.Should().BeFalse("过期绑定 = 不存在语义（惰性读删一致）");
        byAddr.CurrentAddress.Should().Be(LogicalAddress.Invalid, "不存在回执 Invalid");

        // 值版：expectedValue=null 抢"不存在"位——成功
        var byValue = await kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(7L));
        byValue.Swapped.Should().BeTrue("过期位可被 null 预期抢占");
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(7L));
    }

    [Fact]
    public async Task Cas_ValueVersion_CompareMismatch_And_InsertGuard()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("value"));
        await kv.PutFormattedAsync(1, 10, policy: KvCommitPolicy.Committed);

        // 值不匹配——失败且零写入
        var mismatch = await kv.CompareAndSwapAsync(1, BitConverter.GetBytes(999L), BitConverter.GetBytes(20L));
        mismatch.Swapped.Should().BeFalse("expectedValue ≠ 当前值");
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(10, "失败零写入");

        // 值匹配——成功
        var match = await kv.CompareAndSwapAsync(1, BitConverter.GetBytes(10L), BitConverter.GetBytes(20L));
        match.Swapped.Should().BeTrue();
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(20);

        // 已存在 + expectedValue=null——失败（null 预期只在"不存在"位成立）
        var exists = await kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(30L));
        exists.Swapped.Should().BeFalse("键已存在——null 预期不匹配");
    }

    [Fact]
    public async Task Cas_SerializedTotalOrder_LocalSingleWorkerEquiv()
    {
        // raft 形态本地等价钉：命令经 apply 单 worker 串行 = 全局原子（A.2d 状态机命令收敛承接组内全序；
        // 本测钉单机层的串行化内核——同预期并发 CAS 恰一成功，胜者值唯一可见）。
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("serial"));

        const int competitors = 8;
        var tasks = Enumerable.Range(0, competitors)
            .Select(i => kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes((long)(1000 + i))).AsTask())
            .ToArray();
        await Task.WhenAll(tasks);

        tasks.Count(t => t.Result.Swapped).Should().Be(1, "同预期并发 CAS 全序恰一胜者");
        var winnerIndex = tasks.Select((t, i) => (t, i)).First(x => x.t.Result.Swapped).i;
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(
            BitConverter.GetBytes((long)(1000 + winnerIndex)), "全组终值 = 胜者产物（确定性 apply 同构）");
    }

    [Fact]
    public async Task SessionCas_BatchActive_Throws()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("batch"));
        using var s = kv.CreateSession();
        s.BeginAtomicBatch();

        var actAddr = () => s.CompareAndSwapAsync(1, LogicalAddress.Invalid, BitConverter.GetBytes(1L)).AsTask();
        await actAddr.Should().ThrowAsync<InvalidOperationException>("CAS 即时语义与批暂存冲突——批内不支持");
        var actValue = () => s.CompareAndSwapAsync(1, (byte[]?)null, BitConverter.GetBytes(1L)).AsTask();
        await actValue.Should().ThrowAsync<InvalidOperationException>("值版同拒");
    }

    [Fact]
    public async Task SessionCas_Success_RegistersWriteSet()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("session"));
        using var s = kv.CreateSession();

        var r = await s.CompareAndSwapAsync(1, null, BitConverter.GetBytes(5L), KvCommitPolicy.Committed);
        r.Swapped.Should().BeTrue();
        (await s.TryGetBytesAsync(1)).Value.Should().BeEquivalentTo(BitConverter.GetBytes(5L),
            "ReadMyWrites 会话自见 CAS 产物");
        kv.Ring.FlushedUntilAddress.Should().BeGreaterOrEqualTo(r.NewAddress, "Committed 档即时刷");
    }
}
