using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// 复制档前置件·件二契约测试——CAS+TTL 原子化（replica-prerequisites-design.md §3/§4 矩阵 #3~#7）：
/// 「比较 + 写入 + 挂过期」单一原子单元（_casGate 临界区内比较通过后计算 expiry——获取即租约的
/// 锁配方语义源，fencing token = NewAddress）。默认回归锚：timeToLive=null 与既有 CAS 逐字节同行为。
/// </summary>
public class KvCasTtlTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-cas-ttl-" + suffix);

    /// <summary>读盘上值帧（Ring 原始字节——过期断言直读帧头，不经惰性读删折叠）。</summary>
    private static async Task<long> StoredExpiryTicksAsync(TierKv<long, long> kv, LogicalAddress addr)
    {
        var buf = new byte[KvValueFraming.HeaderSize + sizeof(long)];
        await kv.Ring.GetValueAsync(addr, buf);
        KvValueFraming.IsFramed(buf).Should().BeTrue("CAS 产物恒带值帧");
        return KvValueFraming.ExpiryTicks(buf.AsSpan());
    }

    // ── 矩阵 #3：CAS+TTL 写读——未过期命中；时钟推过（Zero 边界）未命中 ──

    [Fact]
    public async Task CasWithTtl_VisibleBeforeExpiry_MissAfterExpiry()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("write-read"));

        var ok = await kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(100L),
            timeToLive: TimeSpan.FromMinutes(1));
        ok.Swapped.Should().BeTrue();
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(100L),
            "TTL 未过期——读命中");

        var expired = await kv.CompareAndSwapAsync(2, null, BitConverter.GetBytes(200L),
            timeToLive: TimeSpan.Zero);   // 过期 = now → 立即过期（时钟推过边界）
        expired.Swapped.Should().BeTrue("写入本身成功——过期是值属性");
        (await kv.TryGetBytesAsync(2)).Should().BeNull("过期 = 惰性读删未命中");
    }

    // ── 矩阵 #4：并发 NX+TTL 恰一成功；盘上帧 expiry 非 0 ──

    [Fact]
    public async Task ConcurrentNxCasWithTtl_ExactlyOneWins_FrameExpiryNonZero()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("nx-once"));

        var r1 = kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(111L),
            timeToLive: TimeSpan.FromMinutes(1)).AsTask();
        var r2 = kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(222L),
            timeToLive: TimeSpan.FromMinutes(1)).AsTask();
        await Task.WhenAll(r1, r2);

        (r1.Result.Swapped ^ r2.Result.Swapped).Should().BeTrue("恰一成功——获取即租约恰一持有者");
        var winner = r1.Result.Swapped ? r1.Result : r2.Result;
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(
            r1.Result.Swapped ? 111L : 222L));

        var expiry = await StoredExpiryTicksAsync(kv, winner.NewAddress);
        expiry.Should().BeGreaterThan(0, "盘上帧携带非 0 过期戳（挂过期入帧）");
    }

    // ── 矩阵 #5：会话 ReadMyWrites——CAS+TTL 自见且过期语义与盘上帧一致 ──

    [Fact]
    public async Task SessionCasWithTtl_ReadMyWrites_ExpiryConsistent()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("session"));
        using var s = kv.CreateSession();

        var ok = await s.CompareAndSwapAsync(1, null, BitConverter.GetBytes(5L),
            KvCommitPolicy.Committed, TimeSpan.FromMinutes(1));
        ok.Swapped.Should().BeTrue();
        (await s.TryGetBytesAsync(1)).Value.Should().BeEquivalentTo(BitConverter.GetBytes(5L),
            "写集自见 CAS 产物（TTL 未过期）");

        var expired = await s.CompareAndSwapAsync(2, null, BitConverter.GetBytes(6L),
            KvCommitPolicy.Committed, TimeSpan.Zero);
        expired.Swapped.Should().BeTrue();
        (await s.TryGetBytesAsync(2)).Found.Should().BeFalse("写集过期语义与盘上帧一致——过期即未命中");
    }

    // ── 矩阵 #6：续约形态——持有者以当前地址为期望换新值 + 续 TTL，token 换新（地址即版本）──

    [Fact]
    public async Task LeaseRenewal_CasByCurrentAddress_TokenRotates()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("renew"));

        // 获取（NX + 租约）——token = NewAddress
        var acquire = await kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(1L),
            timeToLive: TimeSpan.FromMinutes(1));
        acquire.Swapped.Should().BeTrue();

        // 他人以陈旧地址（Invalid 预期位已被占）——拒绝
        var contender = await kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(2L),
            timeToLive: TimeSpan.FromMinutes(1));
        contender.Swapped.Should().BeFalse("锁已被持有——NX 预期不成立");

        // 持有者续约：以当前地址为期望换新值 + 续 TTL——token 换新
        var renew = await kv.CompareAndSwapAsync(1, acquire.NewAddress, BitConverter.GetBytes(3L),
            timeToLive: TimeSpan.FromMinutes(1));
        renew.Swapped.Should().BeTrue("持有者以 token 为期望——续约成功");
        renew.NewAddress.Should().NotBe(acquire.NewAddress, "地址即版本——续约即 token 换新");
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(3L), "新值可见");

        // 旧 token 已失效（版本落后）——持旧 token 的释放/续约确定性拒绝
        var stale = await kv.CompareAndSwapAsync(1, acquire.NewAddress, BitConverter.GetBytes(4L),
            timeToLive: TimeSpan.FromMinutes(1));
        stale.Swapped.Should().BeFalse("陈旧 token fencing 拒绝");
    }

    // ── 矩阵 #7：默认回归——timeToLive=null 路径盘上帧 expiry == 0（逐字节同既有行为）──

    [Fact]
    public async Task CasDefault_NullTtl_FrameExpiryZero()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("default"));

        var ok = await kv.CompareAndSwapAsync(1, null, BitConverter.GetBytes(9L));
        ok.Swapped.Should().BeTrue();

        var expiry = await StoredExpiryTicksAsync(kv, ok.NewAddress);
        expiry.Should().Be(0, "timeToLive=null = 无过期——与既有 CAS 产物逐字节同帧");
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(9L));
    }
}
