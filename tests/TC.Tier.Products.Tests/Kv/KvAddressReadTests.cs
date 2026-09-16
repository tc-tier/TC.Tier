using System.Buffers.Binary;
using FluentAssertions;
using TC.Tier.Products.Kv;
using TC.Tier.Runtime.Structures.Ring.Contracts;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// A.2c 契约测试——TryGetAt 地址直读（免索引，tierkv-ttl-cas-addressread-design.md §3/§4）：
/// 三形态等价性 / 守卫矩阵（回收/墓碑/过期/伪造全 false 不抛）/ 溢出跟随 / 会话局部视图不可见。
/// </summary>
public class KvAddressReadTests
{
    private static TierKvOptions Opts(string suffix, bool range = true)
        => TierKvOptions.Default.WithKvName("tier-kv-a2c-" + suffix).WithRangeIndex(range);

    [Fact]
    public async Task TryGetAt_ThreeFaces_EquivalentToIndexRead()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("faces"));
        var addr = await kv.PutFormattedAsync(1, 42, policy: KvCommitPolicy.Committed);
        var expected = BitConverter.GetBytes(42L);

        var spanBuf = new byte[sizeof(long)];
        kv.TryGetAt(addr, spanBuf).Should().BeTrue("同步快路径命中");
        spanBuf.Should().BeEquivalentTo(expected);

        var (foundAsync, valueAsync) = await kv.TryGetBytesAtAsync(addr);
        foundAsync.Should().BeTrue();
        valueAsync.Should().BeEquivalentTo(expected, "异步字节面同值");

        Memory<byte> memBuf = new byte[sizeof(long)];
        (await kv.TryGetAt(addr, memBuf)).Should().BeTrue("Memory 面命中");
        memBuf.ToArray().Should().BeEquivalentTo(expected);

        // 索引读等价性：同 key 两路同值
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(expected);
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(42L);
    }

    [Fact]
    public async Task TryGetAt_Guards_AllFalse_NoThrow()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("guards"));

        // 伪造地址（未写入即查）：越尾 / 无效哨兵 / 段外
        var fabricated = new LogicalAddress(kv.TailAddress.SegId, kv.TailAddress.Offset + 4096);
        kv.TryGetAt(fabricated, stackalloc byte[8]).Should().BeFalse("越尾 = 尚未写入的位");
        (await kv.TryGetBytesAtAsync(fabricated)).Found.Should().BeFalse();
        (await kv.TryGetAt(fabricated, new Memory<byte>(new byte[8]))).Should().BeFalse();
        kv.TryGetAt(LogicalAddress.Invalid, stackalloc byte[8]).Should().BeFalse("Invalid 哨兵");
        kv.TryGetAt(new LogicalAddress(9999, 0), stackalloc byte[8]).Should().BeFalse("段外伪造");
        (await kv.TryGetBytesAtAsync(new LogicalAddress(9999, 0))).Found.Should().BeFalse("不抛契约");

        // 墓碑：墓碑记录自身地址直读 false（地址读免索引——被删 key 的旧值地址在回收前
        // 仍物理持原帧，是日志物理事实；守卫语义 = 帧在地址处是墓碑 → 无值）
        var addr = await kv.PutFormattedAsync(1, 1, policy: KvCommitPolicy.Committed);
        var tombAddr = kv.TailAddress;   // append-only 单线程——下一写位即墓碑起点
        await kv.DeleteAsync(1);
        kv.TryGetAt(tombAddr, stackalloc byte[8]).Should().BeFalse("墓碑记录无值帧——直读未命中");
        (await kv.TryGetBytesAtAsync(tombAddr)).Found.Should().BeFalse();

        // 过期：直读不豁免惰性读删
        var addrExp = await kv.PutFormattedAsync(2, 2, policy: KvCommitPolicy.Committed,
            timeToLive: TimeSpan.Zero);
        (await kv.TryGetBytesAtAsync(addrExp)).Found.Should().BeFalse("过期 = 不存在语义");

        // 已回收：地址越出回收线 false（数据事实，不抛）
        await kv.PutFormattedAsync(3, 3, policy: KvCommitPolicy.Committed);
        await kv.ReclaimAsync();   // 回收 1/2 及 3 之前的前缀
        kv.Ring.BeginAddress.IsValid.Should().BeTrue();
        (await kv.TryGetBytesAtAsync(addr)).Found.Should().BeFalse("回收线以下地址不可达");
    }

    [Fact]
    public async Task TryGetAt_ShortDestination_ThrowsArgument()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("short"));
        var addr = await kv.PutFormattedAsync(1, 42, policy: KvCommitPolicy.Committed);

        var act = () => { var b = new byte[4]; return Task.FromResult(kv.TryGetAt(addr, b)); };
        await act.Should().ThrowAsync<ArgumentException>("destination 不足 = 调用契约错（区别于数据事实 false）");
        var actAsync = () => kv.TryGetAt(addr, new Memory<byte>(new byte[4])).AsTask();
        await actAsync.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task TryGetAt_OverflowValue_FollowsOverflowEngine()
    {
        using var vol = new TestVolume();
        var opts = TierKvOptions.Default.WithKvName("tier-kv-a2c-ovf")
            .WithRingOverflowPolicy(OverflowPolicy.Enabled, minOverflowSize: 64);
        await using var kv = await TierKvOfLongByteArray.CreateAsync(vol.Fs, opts);

        var payload = new byte[1024];
        BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(0, 8), 0x54495452);
        payload[1023] = 0xAB;   // 尾部哨兵
        var addr = await kv.PutFormattedAsync(1, payload, policy: KvCommitPolicy.Committed);

        var (found, value) = await kv.TryGetBytesAtAsync(addr);
        found.Should().BeTrue("溢出跟随——大值经溢出引擎读回");
        value.Should().NotBeNull();
        value!.Length.Should().Be(1024);
        value[0..8].Should().BeEquivalentTo(payload[0..8]);
        value[1023].Should().Be(0xAB, "尾部字节完整");
        (await kv.TryGetAt(addr, new Memory<byte>(new byte[1024]))).Should().BeTrue("Memory 面同样跟随溢出");
    }

    [Fact]
    public async Task TryGetAt_GlobalView_SessionStagedWrite_Invisible()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("isolation"));
        using var s = kv.CreateSession();
        var addr = await s.PutFormattedAsync(1, 10, policy: KvCommitPolicy.Committed);

        // 批内暂存覆写（未提交）——会话局部视图可见，全局地址视图不可见
        s.BeginAtomicBatch();
        await s.PutFormattedAsync(1, 99);   // 暂存——零落环
        (await s.TryGetFormattedAsync(1)).Value.Should().Be(99, "会话批内自见暂存值");

        var (found, value) = await kv.TryGetBytesAtAsync(addr);
        found.Should().BeTrue();
        value.Should().BeEquivalentTo(BitConverter.GetBytes(10L),
            "TryGetAt = 全局已提交视图——会话未提交写不可见（语义钉）");
        s.AbortBatch();
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(10, "批回滚零应用");
    }
}
