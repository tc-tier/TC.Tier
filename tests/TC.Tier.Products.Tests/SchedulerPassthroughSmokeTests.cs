using TC.Tier.Core.Execution;
using TC.Tier.Products.Queue;
using TC.Tier.Products.Tests.Blob;
using TC.Tier.Products.Tests.TimeSeries;
using TC.Tier.Products.TimeSeries;

namespace TC.Tier.Products.Tests;

/// <summary>
/// 调度器共享注入跨产品冒烟（#505 台账收口——Queue/TimeSeries/Blob 默认路径实例形态）：
/// WithWorkerScheduler（Builder/Options）注入后功能面零回归，注入实例线程数恒定。
/// <para>★ 实例→结构 Settings→全部引擎（含 meta）的深层契约由 WAL 面反射取证
/// （TierWalWorkerSchedulerTests）覆盖——三产品共用同一 Settings 管道，此处验产品装配接线。</para>
/// </summary>
public sealed class SchedulerPassthroughSmokeTests
{
    private static IsolatedTaskScheduler NewSharedScheduler() => IsolatedTaskScheduler.Create(
        new IsolatedSchedulerOptions { Name = "passthrough-shared", ThreadCount = 2 });

    [Fact]
    public async Task TierQueue_WithWorkerScheduler_FunctionalSmoke()
    {
        using var vol = new TestVolume();
        using var shared = NewSharedScheduler();
        var b = new TierQueueBuilder(vol.Fs, new TierQueueOptions
        {
            QueueName = "sched-passthrough",
            PageSize = 64 << 10,
            MemorySize = 8 << 20,
        }).WithWorkerScheduler(shared);
        await using var q = await b.StartAsync();

        var put = await q.EnqueueAsync(new EnqueueOptions(), new byte[] { 1, 2, 3 }, default);
        put.Address.Should().NotBeNull("共享调度器上本地核心功能面零回归");
        shared.ThreadCount.Should().Be(2, "注入实例线程数恒定");
    }

    [Fact]
    public async Task TierTimeSeries_WithWorkerScheduler_FunctionalSmoke()
    {
        using var vol = new TestVolume();
        using var shared = NewSharedScheduler();
        await using var s = await TierTimeSeriesTestFactory.StartAsync(vol,
            build: b => b.WithWorkerScheduler(shared));

        var baseTicks = DateTime.UtcNow.Ticks;
        var addr = await s.AppendAsync(baseTicks, TierTimeSeriesTestFactory.Val(1), default);
        addr.Address.Should().NotBeNull("共享调度器上追加面零回归");
        shared.ThreadCount.Should().Be(2, "注入实例线程数恒定");
    }

    [Fact]
    public async Task TierBlob_WithWorkerScheduler_FunctionalSmoke()
    {
        using var vol = new TestVolume();
        using var shared = NewSharedScheduler();
        await using var blob = await TierBlobTestFactory.StartAsync(vol,
            o => o with { WorkerScheduler = shared });

        var put = await blob.PutAsync(TierBlobTestFactory.MakeData(64, 0xAB));
        var dst = new byte[64];
        var n = await blob.GetAsync(put.ObjectId, dst);
        n.Should().Be(64, "共享调度器上 Put/Get 往返零回归");
        dst.Should().OnlyContain(b => b == 0xAB);
        shared.ThreadCount.Should().Be(2, "注入实例线程数恒定");
    }
}
