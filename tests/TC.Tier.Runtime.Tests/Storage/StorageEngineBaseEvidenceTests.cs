using System.Diagnostics;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// DeviceBase 审核结论的证据测试——每个测试验证一条审核发现。
/// <para>测试名前缀 Evidence{N} 对应审核报告问题编号。</para>
/// <para>★ 这些测试「通过」= 问题存在（断言的是缺陷行为）；修复后这些测试应当反转或删除。</para>
/// </summary>
public sealed class StorageEngineBaseEvidenceTests : IDisposable
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

    private static byte[] MakePattern(int length, byte seed = 0xAB)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    // ═══════════════════════════════════════════════════════════════
    //  E1a：Append(空数据)——修复后应快速返回，不应死循环。
    //  修复点（spec §7 E1a）：ReserveAddress(length==0) 直接返回归一化 _tail，不进 CAS 循环。
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Evidence1a_EmptyAppend_ReturnsQuickly_NoLiveLock()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        int outcome = 0; // 0=挂死 1=正常返回 2=抛异常
        var t = new Thread(() =>
        {
            try
            {
                dev.Append(ReadOnlySpan<byte>.Empty);
                Interlocked.Exchange(ref outcome, 1);
            }
            catch
            {
                Interlocked.Exchange(ref outcome, 2);
            }
        })
        { IsBackground = true };
        t.Start();

        bool finished = t.Join(TimeSpan.FromSeconds(3));

        finished.Should().BeTrue("修复后 Append(空) 应快速返回，不再死循环");
        Volatile.Read(ref outcome).Should().Be(1, "修复后 Append(空) 应正常返回，不抛异常");
    }

    // ═══════════════════════════════════════════════════════════════
    //  E1b：修复后 LocalMemoryDevice 应正常跨段写（补 OnAllocatorCreated 建段回调）。
    //  修复点（spec §7 E1b）：
    //   (a) LocalMemoryDevice.OnAllocatorCreated 注册 OnSegmentPlaceholderNeeded；
    //   (b) EnsurePlaceholderSegments 缺段且回调 null → 抛 InvalidOperationException（防御性快速失败）。
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Evidence1b_MemoryDevice_AppendExactSegmentSize_Succeeds_AfterWiringCallback()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();   // ★ Initialize 返回时恢复是后台异步的（状态可能停在 Recovering），
                              //   未达 Ready 就 Append 会抛 InvalidOperationException → 必须 WaitForReady

        int outcome = 0; // 0=挂死 1=正常返回 2=抛异常
        var t = new Thread(() =>
        {
            try
            {
                dev.Append(MakePattern(4096)); // 正好填满 seg0 → 需要 seg1 → 已补回调 → 正常建段
                Interlocked.Exchange(ref outcome, 1);
            }
            catch (Exception ex)
            {
                _ = ex;
                Interlocked.Exchange(ref outcome, 2);
            }
        })
        { IsBackground = true };
        t.Start();

        bool finished = t.Join(TimeSpan.FromSeconds(3));

        finished.Should().BeTrue("修复后 LocalMemoryDevice 跨段写应正常完成，不再死循环");
        Volatile.Read(ref outcome).Should().Be(1, "修复后跨段写应成功，不抛异常");
    }

    // ═══════════════════════════════════════════════════════════════
    //  E2a：LightEpoch.BumpCurrentEpoch 在他线程持有 epoch 保护时，
    //        注册的 action【不会】在返回前执行——而 RunEpochWorker 恰在
    //        BumpCurrentEpoch 返回后立刻 NotifyCompleted（DeviceBase.Epoch.cs:66-67）。
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Evidence2a_BumpCurrentEpoch_ReturnsBeforeActionRuns_WhenAnotherThreadProtected()
    {
        var epoch = new LightEpoch();

        using var bProtected = new ManualResetEventSlim(false);
        using var bRelease = new ManualResetEventSlim(false);
        var tB = new Thread(() =>
        {
            epoch.Resume();          // 模拟 SequentialReader 持有 epoch 保护
            bProtected.Set();
            bRelease.Wait();
            epoch.Suspend();
        })
        { IsBackground = true };
        tB.Start();
        bProtected.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        // 模拟 RunEpochWorker 的调用序列
        epoch.Resume();
        try
        {
            int executed = 0;
            epoch.BumpCurrentEpoch(() => Interlocked.Exchange(ref executed, 1));

            // ★ RunEpochWorker 在这个时间点就调用 work.NotifyCompleted() 了——
            //   但 drain action（真正的 PunchHole IO）还没执行：
            Volatile.Read(ref executed).Should().Be(0,
                "BUG 证据：BumpCurrentEpoch 已返回但 action 未执行，NotifyCompleted 在此时机发出是谎报完成");

            // 释放 B 的 epoch 保护后再 drain，action 才真正执行
            bRelease.Set();
            tB.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
            epoch.ProtectAndDrain();
            Volatile.Read(ref executed).Should().Be(1, "B 退出保护后 drain 才执行 action");
        }
        finally
        {
            bRelease.Set();
            epoch.Suspend();
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  E2b 已删除：原测 StartReclaim 在 epoch drain 等待读者期间"谎报完成"的缺陷。
    //  新架构 StartReclaim 走 RunBackgroundTask，PunchHole 在后台线程直接执行，
    //  Completed 事件在物理打洞完成后才触发——原缺陷已不存在。
    // ═══════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════
    //  E3a/E3b 已删除：AppendSegment/AppendPlaceholder/ShrinkHead 已私有化，
    //  非连续 segId 在 Append* 内部抛 InvalidOperationException（修复后不再有负下标 / 静默 null 缺陷）。
    // ═══════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════
    //  E4：修复后 DisposeAsync 与 Dispose 行为一致——Initialize 之前调用都不抛。
    //  修复点（spec §7 E4）：DisposeAsyncCore 用 _allocator?.Dispose()；
    //                       Dispose/DisposeAsync 用 Interlocked.Exchange 防双线程同过。
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public async Task Evidence4_DisposeAsync_BeforeInitialize_DoesNotThrow()
    {
        var vol = NewVol();

        var dev1 = new StorageEngineOptions("sync").Builder(vol.Fs).Start();
        Action syncDispose = () => dev1.Dispose();
        syncDispose.Should().NotThrow("同步 Dispose 有 ?. 保护");

        var dev2 = new StorageEngineOptions("async").Builder(NewVol().Fs).Start();
        Func<Task> asyncDispose = async () => await dev2.DisposeAsync();
        await asyncDispose.Should().NotThrowAsync(
            "修复后异步 DisposeAsyncCore 也有 ?. 保护，与同步路径行为一致");
    }

    // ═══════════════════════════════════════════════════════════════
    //  E5：HoleRatio 账目错误——TrackSegmentCreated 已按整段计入 allocated，
    //      TrackBytesWritten 再累加 → allocated > logical → HoleRatio 为负
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Evidence5_HoleRatio_GoesNegative_AfterAppend()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        dev.Append(MakePattern(32 * 1024));

        var ratio = dev.GetHoleRatio(0);
        ratio.Should().BeInRange(0.0, 1.0,
            "FIXED: GetHoleRatio 现在查询 OS 真实物理分配，始终在 [0,1] 范围内");
    }

    // E6 已退役：引擎容量安全阀删除（容量归注入的根空间——ENOSPC 从 fs 介质来；
    // fs 容量行为由 Core.IO 契约测试覆盖，MemoryFileSystemOptions.Capacity）。
    // ═══════════════════════════════════════════════════════════════
    //  E7：SegmentLock 无写者优先——写者等待期间新读者无障碍插队（饥饿机制）
    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════
    //  E9：CommittedTail 读取 O(1)——_committedTail 是存储字段（阶段2单调推进后），
    //     段数增长时读取耗时应基本不变（不再 O(N) 线性扫描）。
    // ═══════════════════════════════════════════════════════════════
    [Fact]
    public void Evidence9_CommittedTail_ScalesLinearlyWithSegmentCount()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 4096).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] chunk = MakePattern(4096);

        for (int i = 0; i < 32; i++) dev.Append(chunk);
        double timeAt32 = MeasureCommittedTailMedian(dev, batches: 7, iterationsPerBatch: 20_000);

        for (int i = 0; i < 480; i++) dev.Append(chunk); // 共 512 段
        double timeAt512 = MeasureCommittedTailMedian(dev, batches: 7, iterationsPerBatch: 20_000);

        double ratio = timeAt512 / Math.Max(timeAt32, 0.001);
        // 修复后 _committedTail 是存储字段，读取 O(1)——段数 16 倍不应显著放大耗时
        ratio.Should().BeLessThan(3.0,
            $"修复后 CommittedTail 读取 O(1)：段数 16 倍耗时不应线性放大（实测 {timeAt32:F1}ms → {timeAt512:F1}ms，{ratio:F1} 倍）");
    }

    /// <summary>
    /// 多批量采样取中位数——整套件并行负载下单次计时受噪声支配（VII-6 实测 ratio 5.5 假红，隔离恒绿）。
    /// 中位数对个别被抢占的批量稳健。
    /// </summary>
    private static double MeasureCommittedTailMedian(IStorageEngine dev, int batches, int iterationsPerBatch)
    {
        // 预热
        for (int i = 0; i < 1000; i++) _ = dev.CommittedTail;
        var samples = new double[batches];
        for (int b = 0; b < batches; b++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterationsPerBatch; i++) _ = dev.CommittedTail;
            sw.Stop();
            samples[b] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[batches / 2];
    }

    /// <summary>
    /// 原 SegmentLockProbe 桥接 stub 已删除——SegmentLock 静态类已折叠为 Segment 实例方法
    /// （AcquireExclusive/ReleaseExclusive/AcquireShared/ReleaseShared），测试直接用 new Segment(...) 调锁方法。
    /// </summary>
}
