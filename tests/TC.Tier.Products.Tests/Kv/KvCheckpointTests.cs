using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W5 契约测试——检查点 + 快速恢复（tierkv-design.md §2.3）：CheckpointAsync 三拍编排
/// （Ring 全量落盘 → 索引帧落盘 → 版本图入 opaque 随 2PC Prepare 原子落盘）+
/// 恢复 = 索引帧物化（O(索引)）+ 增量重放 (W, Tail]（非全量重放——
/// <see cref="TierKv{TKey, TValue}.IndexMainStorageAppliedLastRecovery"/> 断言）+
/// 帧缺失 fail-safe 回退全量重放 + 版本图代际续接。
/// <para>★ mem 介质跨实例恢复（同卷同名重开——deleteOnClose=false）。</para>
/// </summary>
public class KvCheckpointTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-w5-" + suffix);

    [Fact]
    public async Task CheckpointThenReopen_FastRecovery_IncrementalReplayNotFull()
    {
        using var vol = new TestVolume();
        var opts = Opts("fast");

        // 实例 1：写 N 条 → 检查点 → 再写 M 条（检查点之后的增量）
        await using (var kv1 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            using var s = kv1.CreateSession();
            for (long k = 1; k <= 200; k++)
                await s.PutFormattedAsync(k, k * 10);
            var cp = await kv1.CheckpointAsync();
            cp.Version.Should().BeGreaterThan(0, "检查点消费代际版本");
            for (long k = 201; k <= 250; k++)
                await s.PutFormattedAsync(k, k * 10);
        }

        // 重开：帧物化（O(索引)）+ 增量重放 (W, Tail]——非全量重放
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.IndexMainStorageAppliedLastRecovery.Should().BeTrue(
            "有有效检查点帧——恢复走物化+增量重放路径（非全量重放）");

        // 数据完整（检查点前 + 增量后）
        using var s2 = kv2.CreateSession();
        (await s2.TryGetFormattedAsync(1)).Value.Should().Be(10L);
        (await s2.TryGetFormattedAsync(200)).Value.Should().Be(2000L, "检查点前数据经帧物化恢复");
        (await s2.TryGetFormattedAsync(201)).Value.Should().Be(2010L, "检查点后数据经增量重放恢复");
        (await s2.TryGetFormattedAsync(250)).Value.Should().Be(2500L);
        kv2.Count.Should().Be(250);
    }

    [Fact]
    public async Task NoCheckpoint_FailSafeFullReplay_SameData()
    {
        using var vol = new TestVolume();
        var opts = Opts("failsafe");

        // 实例 1：写数据不检查点（帧未生成或 W 过旧）——走 Prepare 提交（随批提交的 opaque/水位落盘）
        await using (var kv1 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            using var s = kv1.CreateSession();
            s.BeginAtomicBatch();
            for (long k = 1; k <= 30; k++)
                await s.PutFormattedAsync(k, k);
            await s.CommitBatchAsync();
        }

        // 重开：无有效帧 → fail-safe 全量重放（同一条路），数据等价
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.IndexMainStorageAppliedLastRecovery.Should().BeFalse(
            "无检查点帧——fail-safe 全量重放");
        using var s2 = kv2.CreateSession();
        (await s2.TryGetFormattedAsync(30)).Value.Should().Be(30L, "全量重放数据等价");
        kv2.Count.Should().Be(30);
    }

    [Fact]
    public async Task Checkpoint_VersionLabel_MonotonicAcrossRecovery()
    {
        using var vol = new TestVolume();
        var opts = Opts("version");

        long checkpointVersion;
        await using (var kv1 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            using var s1 = kv1.CreateSession();
            checkpointVersion = (await kv1.CheckpointAsync()).Version;
            // 检查点之后继续分配版本
            using var s2 = kv1.CreateSession();
            s2.SessionVersion.Should().BeGreaterThan(checkpointVersion);
        }

        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        using var sNew = kv2.CreateSession();
        sNew.SessionVersion.Should().BeGreaterThan(checkpointVersion,
            "崩溃恢复到检查点版本之后——代际标签单调续接");
    }

    [Fact]
    public async Task Checkpoint_BTreeFamily_FastRecoveryWorks()
    {
        using var vol = new TestVolume();
        var opts = Opts("btree").WithIndexKind(KvIndexKind.BTree);

        await using (var kv1 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            using var s = kv1.CreateSession();
            for (long k = 1; k <= 50; k++)
                await s.PutFormattedAsync(k, k * 3);
            await kv1.CheckpointAsync();
            for (long k = 51; k <= 60; k++)
                await s.PutFormattedAsync(k, k * 3);
        }

        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.IndexMainStorageAppliedLastRecovery.Should().BeTrue("比较族（BTree）帧物化路径");
        kv2.Count.Should().Be(60, "物化 + 增量重放数据完整");
        using var s2 = kv2.CreateSession();
        (await s2.TryGetFormattedAsync(60)).Value.Should().Be(180L);
    }

    [Fact]
    public async Task Checkpoint_MultipleCycles_LatestWins()
    {
        using var vol = new TestVolume();
        var opts = Opts("cycle");

        await using (var kv1 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            using var s = kv1.CreateSession();
            await s.PutFormattedAsync(1, 1L);
            await kv1.CheckpointAsync();
            await s.PutFormattedAsync(2, 2L);
            await kv1.CheckpointAsync();   // 二次检查点——版本链轮替，最新帧生效
            await s.PutFormattedAsync(3, 3L);
        }

        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        kv2.IndexMainStorageAppliedLastRecovery.Should().BeTrue();
        kv2.Count.Should().Be(3, "二次检查点后数据完整（物化+增量）");
        using var s2 = kv2.CreateSession();
        (await s2.TryGetFormattedAsync(3)).Value.Should().Be(3L);
    }
}
