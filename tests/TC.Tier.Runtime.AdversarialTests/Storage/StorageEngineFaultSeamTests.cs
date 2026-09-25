using System.Diagnostics;
using TC.Tier.Core.IO.Testing;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 引擎缝（IStorageEngineFaultInjector）契约测试——故障注入面补全设计 件二：
/// FailOn 语义码透传 / DelayOn 前置延迟 / HangOn 有界等待 / 三状态窗（恢复窗/Compact 活动窗/节流饱和窗）/
/// 与 fs 脸的组合叠加。
/// <para>★ 与既有 StorageEngineFaultInjectionTests（fs 脸经引擎的透传语义）互补——本组打引擎常设面本体。</para>
/// </summary>
public sealed class StorageEngineFaultSeamTests : StorageEngineTestBase
{
    /// <summary>seam 引擎（WithFaults 显式开启；spinMs 缩短段表自旋窗；sampleMs 武装 CPU 采样——
    /// 限流路径的 hermetic 前提）。返回（公开 op 面, 注入面——注入器类型在引擎白盒上，经 builder.Engine 取）。</summary>
    private static (IStorageEngine Dev, IStorageEngineFaultInjector? Faults) NewSeamEngine(
        FaultInjectingFileSystem fi, string name, long? spinMs = null, int? sampleMs = null)
    {
        var options = new StorageEngineOptions(name, segmentGrowthLimit: 4096)
            .WithPreallocateFile(false)
            .WithFaults();
        if (spinMs is { } ms)
            options = options.WithOptimization(options.Optimization with { SpinMilliseconds = ms });
        if (sampleMs is { } sms)
            options = options.WithOptimization(options.Optimization with
            {
                SampleInterval = TimeSpan.FromMilliseconds(sms),
                ThrottleSpinMilliseconds = options.Optimization.SpinMilliseconds,
            });
        var builder = options.Builder(fi);
        var dev = builder.Start();
        dev.WaitForReady();
        return (dev, builder.Engine.Faults);
    }

    // ═══════════════ FailOn（op 级失败）═══════════════

    [Fact]
    public void FailOn_SemanticCodePreserved()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-f1");
        using var _devScope = dev;
        faults!.FailOn("Append", IOError.DiskFull);

        var act = () => dev.Append(MakePattern(64, 0x11));
        act.Should().Throw<FileIOException>("op 级失败 = 引擎对上层透传语义异常")
            .Which.Error.Should().Be(IOError.DiskFull, "IOError 语义码不丢失");
    }

    [Fact]
    public void FailOn_AtCallIndex_Deterministic()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-f2");
        using var _devScope = dev;
        faults!.FailOn("Append", IOError.IOFailure, atCallIndex: 2);

        dev.Append(MakePattern(32, 0x01));   // 第 1 次放行
        var act = () => dev.Append(MakePattern(32, 0x02));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.IOFailure, "第 N 次确定性注入");
    }

    [Fact]
    public void FailOn_Reset_ClearsRules()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-f3");
        using var _devScope = dev;
        faults!.FailOn("*", IOError.DiskFull);
        faults!.Reset();

        var act = () => dev.Append(MakePattern(32, 0x03));
        act.Should().NotThrow("Reset 清除全部注入");
    }

    // ═══════════════ DelayOn（op 级慢）═══════════════

    [Fact]
    public void DelayOn_PrependLatency()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-d1");
        using var _devScope = dev;
        faults!.DelayOn("Append", TimeSpan.FromMilliseconds(80));

        var sw = Stopwatch.StartNew();
        dev.Append(MakePattern(32, 0x04));
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(70), "op 入口前置延迟生效");
    }

    // ═══════════════ HangOn（op 级挂）═══════════════

    [Fact]
    public async Task HangOn_BoundedWaitViaCancellation_ResetReleases()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-h1");
        using var _devScope = dev;
        var addr = dev.Append(MakePattern(32, 0x05));
        faults!.HangOn("ReadAsync");

        using var cts = new CancellationTokenSource(100);
        var buf = new byte[32];
        var act = () => dev.ReadAsync(addr, buf, cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>("上层有界等待 = 取消路径先行处置");

        faults!.Reset();   // 拆除放行
        (await dev.ReadAsync(addr, buf, CancellationToken.None)).Should().Be(32, "挂起点放行后操作照常完成");
    }

    // ═══════════════ 状态窗：RecoveringWindow ═══════════════

    [Fact]
    public void RecoveringWindow_ExtendsRecovery_WaitForReadyTimeoutTolerant()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var options = new StorageEngineOptions("seam-rw", 4096).WithPreallocateFile(false).WithFaults();
        var builder = options.Builder(fi);
        builder.Engine.Faults!.EnterState(EngineFaultState.RecoveringWindow);   // Start 前进入——恢复核心延后

        var startTask = Task.Run(() => builder.Start());
        try
        {
            // 等恢复任务进入调度（WaitGuard 需要恢复任务已发布——Start 先行）
            var spin = Stopwatch.StartNew();
            while (builder.Engine.RecoveryState.Phase == RecoveryPhase.NotStarted && spin.Elapsed < TimeSpan.FromSeconds(5))
                Thread.Sleep(10);
            builder.Engine.WaitForReady(300).Should().BeFalse("恢复窗存续——恢复期被延长（就绪超时容忍路径）");
        }
        finally
        {
            builder.Engine.Faults!.Reset();   // 放行恢复
        }
        startTask.Wait(10_000).Should().BeTrue("Reset 后恢复推进，Start 返回");
        builder.Engine.IsReady.Should().BeTrue();

        // 窗内存续期间 op 按"尚未完成恢复"拒绝（EnsureReady 同型异常）——Start 后独立验证
        var (dev2, faults2) = NewSeamEngine(new FaultInjectingFileSystem(TierFs.New("memory:")), "seam-rw2");
        using var _dev2Scope = dev2;
        var addr2 = dev2.Append(MakePattern(16, 0x07));
        faults2!.EnterState(EngineFaultState.RecoveringWindow);
        var act = () => dev2.Read(addr2, new byte[8]);
        act.Should().Throw<InvalidOperationException>("存续期间 op 入口按恢复期拒绝");
        faults2!.Reset();
        dev2.Read(addr2, new byte[16]).Should().Be(16, "Reset 退出状态窗后恢复服务");
    }

    // ═══════════════ 状态窗：CompactActive ═══════════════

    [Fact]
    public async Task CompactActive_WritersYield_SpinTimeoutIsTheBoundedExit()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-ca", spinMs: 100);
        using var _devScope = dev;
        dev.Append(MakePattern(64, 0x08));   // 有数据——占住区间才有冲突写者

        faults!.EnterState(EngineFaultState.CompactActive);   // 排他占住 [MinAddress, 尾段段末)

        // 冲突追加走占区间协议：自旋让位 → SpinMilliseconds 到期 TimeoutException（有界出口）
        var sw = Stopwatch.StartNew();
        var act = () => dev.AppendAsync(MakePattern(32, 0x09), CancellationToken.None).AsTask();
        await act.Should().ThrowAsync<TimeoutException>("冲突追加在占区间协议下自旋让位——有界超时出口");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(90), "让位自旋先行——不是立刻失败");

        faults!.Reset();   // 排他占住释放
        await dev.AppendAsync(MakePattern(32, 0x0A), CancellationToken.None);   // 窗退后写者获得区间——不抛即成功
    }

    // ═══════════════ 状态窗：ThrottleSaturated ═══════════════

    [Fact]
    public async Task ThrottleSaturated_AsyncPathSpinHonorsCancellation()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-ts", sampleMs: 20);
        using var _devScope = dev;
        faults!.EnterState(EngineFaultState.ThrottleSaturated);

        using var cts = new CancellationTokenSource(150);
        var act = () => dev.AppendAsync(MakePattern(32, 0x0B), cts.Token).AsTask();
        await act.Should().ThrowAsync<OperationCanceledException>("节流饱和——自旋等待路径由 ct 解围");

        faults!.Reset();
        dev.Append(MakePattern(32, 0x0C));   // 饱和窗退后放行——不抛即成功
    }

    [Fact]
    public void ThrottleSaturated_SyncPathTimeouts()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-ts2", spinMs: 100, sampleMs: 20);
        using var _devScope = dev;
        faults!.EnterState(EngineFaultState.ThrottleSaturated);

        var act = () => dev.Append(MakePattern(32, 0x0D));
        act.Should().Throw<TimeoutException>("同步路径无外部 ct——自旋至 ThrottleSpinMilliseconds（floor 采样周期后）超时直接报错");
    }

    [Fact]
    public void ThrottleSaturated_WithoutSampling_FloorGuardsSubSampleBudget()
    {
        // 限流未武装（SampleInterval=null 默认）——饱和窗仍驱动自旋路径（故障缝不依赖采样）；
        // deadline floor 至采样窗（未武装取默认 1s 窗）：100ms 级旋钮不产生亚采样周期超时
        //（等不到任何采样发布的预算观察不到 CPU 回落，必超时语义自欺——#520 形态的结构性封堵）。
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-ts3", spinMs: 100);
        using var _devScope = dev;
        faults!.EnterState(EngineFaultState.ThrottleSaturated);

        var sw = Stopwatch.StartNew();
        var act = () => dev.Append(MakePattern(32, 0x0F));
        act.Should().Throw<TimeoutException>("限流未武装但饱和窗强制饱和——同步路径仍有界超时");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900),
            "deadline floor 至采样窗——亚采样周期旋钮不得提前失败");

        faults!.Reset();
        dev.Append(MakePattern(32, 0x10));   // 饱和窗退后放行——不抛即成功
    }

    // ═══════════════ 腐败抓手（件三 CorruptRule × 引擎读路径）═══════════════

    [Fact]
    public void CorruptRule_EngineReadSurfacesMediumCorruption()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, _) = NewSeamEngine(fi, "seam-corrupt");
        using var _devScope = dev;
        var addr = dev.Append(MakePattern(64, 0x22));

        // 对抗抓手：写转发成功后翻转介质字节——读自愈/CRC 判废防线的注入面（防线本体由读保护套件覆盖）。
        // 命中第 2 次 Write：翻转文件偏移 0 的介质现存字节（= 首笔记录的首字节——腐败以介质寻址，非当前写负载）
        fi.AddCorruptRule("*", "Write", offset: 0, mask: new byte[] { 0xFF, 0x00, 0x00, 0x00 },
            failAtCallIndex: 1);
        var addr2 = dev.Append(MakePattern(64, 0x33));

        var buf = new byte[64];
        dev.Read(addr, buf);
        buf[0].Should().Be((byte)(0x22 ^ 0xFF), "引擎读观测到介质现存字节被翻转（腐败抓手生效）");
        buf[1].Should().Be(0x23, "掩码区间外不触碰（MakePattern 递增模式 seed+1）");
        var buf2 = new byte[64];
        dev.Read(addr2, buf2);
        buf2[0].Should().Be(0x33, "第二笔负载自身在规则区间外——完整无损");
        buf2[1].Should().Be(0x34);
    }

    // ═══════════════ 组合：引擎缝 + fs 脸叠加 ═══════════════

    [Fact]
    public void Combo_EngineDelay_PlusFsDiskFull_BothSeamsActive()
    {
        using var fi = new FaultInjectingFileSystem(TierFs.New("memory:"));
        var (dev, faults) = NewSeamEngine(fi, "seam-combo");
        using var _devScope = dev;

        // 两缝叠加：引擎缝前置延迟 + fs 缝 DiskFull——同一 op 两条失败边界同时命中
        faults!.DelayOn("Append", TimeSpan.FromMilliseconds(50));
        fi.AddRule("*", "Write", IOError.DiskFull, failAtCallIndex: 1);

        var sw = Stopwatch.StartNew();
        var act = () => dev.Append(MakePattern(32, 0x0E));
        act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.DiskFull, "fs 缝语义码透传");
        sw.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(40), "引擎缝延迟先行生效");
    }
}
