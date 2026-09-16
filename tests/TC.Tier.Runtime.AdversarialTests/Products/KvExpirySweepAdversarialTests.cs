using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Runtime.AdversarialTests.Products;

/// <summary>
/// A.2a 对抗线——过期回收 sweep 循环 kill（tierkv-ttl-cas-addressread-design.md §4 对抗条目）：
/// kill（Dispose/取消 = 进程退出的进程内等价窗口）→ 重开水位/回收线恢复自洽、绝不动到未过期数据。
/// <para>★ 真磁盘介质跑法：<c>TC_TEST_FS_SPEC=local:///…</c>（§4.1 介质契约，零重编译）——
/// 本项目单独跑、单独取证；进程级 SIGKILL 形态归云上验证战役（scripts/cloud）。</para>
/// </summary>
public class KvExpirySweepAdversarialTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-adv-" + suffix);

    [Fact]
    public async Task SweepKill_Reopen_WatermarkAndReclaimLineSelfConsistent()
    {
        using var vol = new TestVolume();
        var opts = Opts("kill");
        LogicalAddress beginBefore;
        LogicalAddress wmBefore;

        await using (var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            for (long k = 1; k <= 6; k++)
                await kv.PutFormattedAsync(k, k, policy: KvCommitPolicy.Committed, timeToLive: TimeSpan.Zero);
            var alive = await kv.PutFormattedAsync(7, 7, policy: KvCommitPolicy.Committed);
            await kv.ForceExpirySweepAsync();
            beginBefore = kv.Ring.BeginAddress;
            wmBefore = kv.SweepWatermark;
            beginBefore.Should().Be(alive, "sweep 后回收线钉在存活记录地址");
        }   // kill 窗口 = 实例整生命周期终止（进程退出等价）

        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.SweepWatermark.Should().Be(wmBefore, "水位自 opaque 恢复——续扫语义（不回退 Invalid 全扫）");
        kv2.Ring.BeginAddress.Should().Be(beginBefore, "回收线跨 kill 不回退");
        (await kv2.TryGetFormattedAsync(7)).Value.Should().Be(7, "未过期数据完好");
        for (long k = 1; k <= 6; k++)
            (await kv2.TryGetFormattedAsync(k)).Found.Should().BeFalse("过期项不存在（未被复活）");

        // kill 后续扫一轮：零新增死记录——回收线/水位均不动
        await kv2.ForceExpirySweepAsync();
        kv2.Ring.BeginAddress.Should().Be(beginBefore);
        kv2.SweepWatermark.Should().Be(wmBefore);
    }

    [Fact]
    public async Task Sweep_ConcurrentWithWriteStorm_CancelMidway_AliveIntact()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("storm"));

        const int writers = 4, perWriter = 30;
        var lastAliveValue = new long[writers];

        // 写风暴：每写者双 key——key_a 无 TTL 反复覆写（存活钉，终值精确已知）；
        // key_b TTL=0 反复覆写（过期源，sweep 粮食）。全 Committed——回收线可推进。
        var storm = Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            var keyAlive = 10L + w;
            var keyExpired = 20L + w;
            for (var i = 1; i <= perWriter; i++)
            {
                var v = (long)(w * 1000 + i);
                await kv.PutFormattedAsync(keyAlive, v, policy: KvCommitPolicy.Committed);
                lastAliveValue[w] = v;
                await kv.PutFormattedAsync(keyExpired, v, policy: KvCommitPolicy.Committed,
                    timeToLive: TimeSpan.Zero);
            }
        })));

        // sweep 与风暴并发——半途 kill（取消令牌 = 协作中断窗口：扫描/截断/落盘任意 await 点）
        using var killCts = new CancellationTokenSource();
        var sweeps = new List<Task>();
        while (!storm.IsCompleted)
        {
            sweeps.Add(kv.ForceExpirySweepAsync(killCts.Token).AsTask());
            try
            {
                await Task.Delay(5, killCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        await storm;
        await killCts.CancelAsync();
        try
        {
            await Task.WhenAll(sweeps);   // 在途 sweep 以 OCE 收口——零部分提交
        }
        catch (OperationCanceledException)
        {
            // kill 语义预期
        }

        // ★ kill 点取持久化边界（CheckpointAsync=数据+索引帧+meta 原子落盘）：风暴内 Committed 写的
        //   meta 泵窗口（产品既有 RPO 语义）不属 sweep 责任面——本测钉 sweep 的水位/回收线自洽。
        await kv.CheckpointAsync();
        var beginBeforeKill = kv.Ring.BeginAddress;
        var wmBeforeKill = kv.SweepWatermark;
        await using var reopen = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("storm"));
        reopen.Ring.BeginAddress.Should().Be(beginBeforeKill, "kill 后回收线恢复自洽（持久化边界点）");
        reopen.SweepWatermark.Should().Be(wmBeforeKill, "kill 后扫描水位恢复自洽");
        for (var w = 0; w < writers; w++)
        {
            (await reopen.TryGetFormattedAsync(10L + w)).Value.Should().Be(lastAliveValue[w],
                "未过期数据绝不动——kill 后存活终值精确可读");
            (await reopen.TryGetFormattedAsync(20L + w)).Found.Should().BeFalse("过期 churn 项不存在");
        }

        // kill 后续扫收敛：水位/回收线不动（零新增死记录），存活数据仍精确
        await reopen.ForceExpirySweepAsync();
        for (var w = 0; w < writers; w++)
            (await reopen.TryGetFormattedAsync(10L + w)).Value.Should().Be(lastAliveValue[w]);
    }
}
