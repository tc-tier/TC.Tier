using System.Threading.Tasks;
using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W3 契约测试：版本模型（D1 session-version——分配器全局单调/会话版本有序/高水位随 2PC Prepare
/// 持久化+恢复续接）+ Read/Upsert/Delete 全 API 面（kv 级+会话级×字节面+类型化面）。
/// <para>★ mem 介质跨实例恢复（TestVolume 同卷同名重开——deleteOnClose=false）。</para>
/// </summary>
public class KvVersionAndApiTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-w3-" + suffix);

    // ═══ 版本分配器（全局单调）═══

    [Fact]
    public void Allocator_Monotonic_UnderConcurrency()
    {
        var allocator = new KvVersionAllocator();
        const int total = 10_000;
        var versions = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, total, _ => versions.Add(allocator.NextVersion()));

        versions.Should().OnlyHaveUniqueItems("并发分配零重复");
        allocator.HighWater.Should().Be(total, "高水位 = 分配总数");
        versions.Should().Contain(v => v >= 1 && v <= total);
    }

    [Fact]
    public void Allocator_Restore_OnlyRaises()
    {
        var allocator = new KvVersionAllocator();
        allocator.NextVersion();
        allocator.NextVersion();
        allocator.HighWater.Should().Be(2);

        allocator.Restore(100);
        allocator.HighWater.Should().Be(100, "恢复续接历史");
        allocator.Restore(50);
        allocator.HighWater.Should().Be(100, "向下恢复被忽略——版本单调不回退");
        allocator.NextVersion().Should().Be(101);
    }

    // ═══ session-version（D1——会话创建取单调区间）═══

    [Fact]
    public async Task SessionVersions_OrderedByCreation()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("sessver"));

        using var s1 = kv.CreateSession();
        using var s2 = kv.CreateSession(KvSessionConditions.None);
        using var s3 = kv.CreateSession(KvSessionConditions.Serializable);

        s1.SessionVersion.Should().BeGreaterThan(0);
        s2.SessionVersion.Should().BeGreaterThan(s1.SessionVersion, "后创建会话版本恒大");
        s3.SessionVersion.Should().BeGreaterThan(s2.SessionVersion);
        kv.Versions.HighWater.Should().Be(s3.SessionVersion);
    }

    // ═══ 恢复高水位（opaque 通道随 2PC Prepare 原子落盘 → 重开续接）═══

    [Fact]
    public async Task HighWater_PersistedViaBatchCommit_RestoredAcrossReopen()
    {
        using var vol = new TestVolume();
        var opts = Opts("persist");

        // 实例 1：批提交（Prepare 落盘 opaque——高水位持久化点）→ 关闭
        var persistedHighWater = await CommitAndReturnHighWaterAsync(vol.Fs, opts);

        // 重开同卷同名：分配器必须从持久化高水位续接（新会话版本 > 历史）
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        using var sNew = kv2.CreateSession();
        sNew.SessionVersion.Should().BeGreaterThan(persistedHighWater,
            "恢复高水位后续接分配——版本单调不回退");
    }

    private static async Task<long> CommitAndReturnHighWaterAsync(TC.Tier.Core.IO.IFileSystem fs,
        TierKvOptions opts)
    {
        await using var kv = await TierKvOfLongLong.CreateAsync(fs, opts);
        using var s = kv.CreateSession();
        s.BeginAtomicBatch();
        await s.PutFormattedAsync(1, 1L);
        await s.PutFormattedAsync(2, 2L);
        await s.CommitBatchAsync();   // Prepare 落盘 opaque（高水位持久化点）

        var hw = kv.Versions.HighWater;
        hw.Should().BeGreaterThan(0);
        return hw;
    }

    [Fact]
    public async Task FreshVolume_VersionsStartAtOne()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("fresh"));
        using var s = kv.CreateSession();
        s.SessionVersion.Should().Be(1, "无历史 opaque——首版本从 1 起");
    }

    [Fact]
    public async Task Reopen_WithoutValidOpaque_ContinuesFromZero()
    {
        using var vol = new TestVolume();
        var opts = Opts("noopaque");

        // 第一实例：只创建会话（不批提交——高水位未持久化），写走非批路径
        await using (var kv1 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts))
        {
            using var s = kv1.CreateSession();
            await s.PutFormattedAsync(1, 1L);   // Committed 即刷数据但 opaque 未走 Prepare
        }

        // 重开：无有效持久化高水位 → 从 0 续（版本单调不回退仍成立——从 1 起分配）
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, opts);
        using var sNew = kv2.CreateSession();
        sNew.SessionVersion.Should().BeGreaterThan(0);
    }

    // ═══ Read/Upsert/Delete 全 API 面（kv 级+会话级×字节面+类型化面）═══

    [Fact]
    public async Task ApiSurface_ReadUpsertDelete_AllFacesConsistent()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("api"));
        using var s = kv.CreateSession();

        // Upsert（Put=Upsert 同义——kv 级字节面/类型化面）
        await kv.PutFormattedAsync(1, 10L);
        var buf = new byte[8];
        (await kv.TryGetAsync(1, buf)).Should().BeTrue();
        BitConverter.ToInt64(buf).Should().Be(10L);
        (await kv.TryGetBytesAsync(1)).Should().BeEquivalentTo(BitConverter.GetBytes(10L));
        (await kv.TryGetFormattedAsync(1)).Value.Should().Be(10L);

        // 会话级同面
        await s.PutFormattedAsync(2, 20L);
        (await s.TryGetFormattedAsync(2)).Value.Should().Be(20L);
        var sbuf = new byte[8];
        (await s.TryGetBytesAsync(2)).Found.Should().BeTrue();

        // Delete：kv 级与会话级语义一致
        (await kv.DeleteAsync(1)).Should().BeTrue();
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeFalse();
        (await s.DeleteAsync(2)).Should().BeTrue();
        (await s.TryGetFormattedAsync(2)).Found.Should().BeFalse();

        // 删除不存在 = false
        (await kv.DeleteAsync(99)).Should().BeFalse();
        (await s.DeleteAsync(99)).Should().BeFalse();
    }
}
