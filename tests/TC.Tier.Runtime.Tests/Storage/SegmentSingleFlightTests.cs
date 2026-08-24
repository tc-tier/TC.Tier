namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 物理建段 single-flight 压测——N=2 消费者 + 预备池补建并发：同一 segId 的
/// <c>CreateSegmentPhysical</c> 必须恰好执行一次。
/// <para>★ 2026-08-16 引擎 N≥2 审计：池补建（PreCreateSegmentPhysical）与正式建段任务
///   （EnsureSegmentPhysicalAsync）之间存在双建窗口——容量双重计数、句柄缓存覆盖泄漏
///   （Windows 上阻文件删除）、重复等值 meta 写。修复 = build-gate single-flight
///   （TryConsumeOrClaimPhysicalBuild 原子取用或声明 + 在途等待）。断言依据
///   <see cref="TC.Tier.Runtime.Storage.StorageEngineBuilder.Engine"/> 的 PhysicalBuildLog（每次物理构建记一笔 segId）。</para>
/// </summary>
[Collection("LargeScaleIO")]
public sealed class SegmentSingleFlightTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    [Fact]
    public async Task N2Consumers_ManySegments_EachBuiltExactlyOnce()
    {
        var vol = NewVol();
        IStorageEngine dev;
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false);
        options = options.WithOptimization(options.Optimization with { WorkerConsumers = 2 });
        using var builder = options.Builder(vol.Fs);
        using (dev = builder.Start())
        {
            dev.WaitForReady();

            const int threads = 8;
            const int perThread = 600;
            var payload = new byte[512];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    dev.Append(payload);
                }
            })).ToArray();

            // 若死锁/挂起，30s 内 WhenAll 不返回 → 超时失败
            var all = Task.WhenAll(tasks);
            var timeout = Task.Delay(TimeSpan.FromSeconds(30));
            (await Task.WhenAny(all, timeout)).Should().Be(all, "N=2 并发 Append 不应挂起");

            await WaitForBuildLogStableAsync(builder.Engine);

            var log = builder.Engine.PhysicalBuildLog;
            log.Count.Should().BeGreaterThan(10, "8×600×512B ≈ 2.4MB，4KB 段应建数百段");
            var duplicates = log.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            duplicates.Should().BeEmpty(
                "同一 segId 的物理构建必须恰好一次（build-gate single-flight）——重复即双建窗口未关死");
        }

        // reopen 完整性——N=2 全程后 meta 状态可恢复
        using var reopened = options.Builder(vol.Fs).Start();
        reopened.WaitForReady();
        reopened.AllocatedTail.SegId.Should().BeGreaterThanOrEqualTo(1, "重开应恢复全部段");
    }

    /// <summary>等待建段日志计数连续两次采样稳定（Full/Create/Background 任务排空）。</summary>
    private static async Task WaitForBuildLogStableAsync(StorageEngine dev)
    {
        var prev = -1;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            var cur = dev.PhysicalBuildLog.Count;
            if (cur == prev) return;
            prev = cur;
        }
    }
}
