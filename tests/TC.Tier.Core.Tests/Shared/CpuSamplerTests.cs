using TC.Tier.Core.Metrics;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Shared;
using Xunit;

namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// CpuSampler 契约测试——CPU 采样 worker（BackgroundWorkerLoop 公共池模式使用方）。
/// 覆盖：生命周期（Start/Stop/Dispose 干净，不挂）、暴露属性的不变量（有限值、非负、
/// ThrottleFactor 在 [0,1] 区间语义）。
/// ⚠️ 系数插值（EMA/lowCutoff/highCutoff → ThrottleFactor 映射）依赖真实 CPU 采样输入，
/// 无可测性缝（无注入点），标注待重构后补契约测试（见 docs/unit-test-coverage.md 🟡）。
/// </summary>
public class CpuSamplerTests
{
    [Fact]
    public void Ctor_Defaults_AreSane()
    {
        using var sampler = new CpuSampler();
        sampler.CpuUtilization.Should().BeInRange(0, 1.0, "CPU 利用率归一化 [0,1]");
        sampler.ThrottleFactor.Should().BeInRange(0, 1.0, "限流系数归一化 [0,1]");
    }

    [Fact]
    public void StartStopDispose_Lifecycle_Clean()
    {
        var sampler = new CpuSampler(sampleInterval: TimeSpan.FromMilliseconds(20));
        sampler.Start();

        // 短暂运行后 CPU 属性仍为有限值（采样循环不崩）
        Assert.True(SpinWait.SpinUntil(() => !double.IsNaN(sampler.CpuUtilization), 2000));
        Thread.Sleep(100);

        sampler.Stop();
        sampler.WaitForExit();
        sampler.Dispose();   // 不挂即通过（WaitForExit 默认 5s 超时仅 WARN）
    }

    [Fact]
    public async Task StartAsyncDispose_Clean()
    {
        var sampler = new CpuSampler(sampleInterval: TimeSpan.FromMilliseconds(20));
        sampler.Start();
        await Task.Delay(100);
        await sampler.DisposeAsync();
    }

    [Fact]
    public void RunsOn_PublicPool_ByDefault()
    {
        // CpuSampler 是低频周期 worker——契约：不注入调度器（公共池模式）
        using var sampler = new CpuSampler();
        sampler.ConsumerCount.Should().Be(1, "采样器单消费者");
    }

    // ════════════════════════════════════════════════════════════
    //  数学契约（可测性缝：ApplyEma / MapThrottleFactor 纯函数）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Ema_FirstSample_TakesRawDirectly()
    {
        CpuSampler.ApplyEma(hasPrev: false, prev: 0.0, raw: 0.8, alpha: 0.5).Should().Be(0.8, "首样本直接取 raw（标志位初始化，非 prev==0 哨兵）");
    }

    [Fact]
    public void Ema_IdleToLoad_TransitionIsSmooth_NotJump()
    {
        // ★ 回归（治"不平稳过渡"）：空闲（ema=0，hasPrev=true）→ 高载 0.8，α=0.5
        //   旧实现 prev==0 哨兵会直接跳 0.8（无平滑）；正确 EMA 应为半步 0.4
        CpuSampler.ApplyEma(hasPrev: true, prev: 0.0, raw: 0.8, alpha: 0.5).Should().Be(0.4, "空闲→高载必须半步过渡，不许跳变");
    }

    [Fact]
    public void Ema_ConvergesMonotonically_ToConstantInput()
    {
        var ema = 0.0;
        for (var i = 0; i < 100; i++)
        {
            var next = CpuSampler.ApplyEma(true, ema, 0.9, 0.5);
            next.Should().BeGreaterThanOrEqualTo(ema, "常量输入下 EMA 单调不减（收敛至双精度 ε 后增量归零）");
            if (i < 10)
                next.Should().BeGreaterThan(ema, "远离目标时必须严格上升（真平滑）");
            next.Should().BeLessThanOrEqualTo(0.9, "不超过目标值");
            ema = next;
        }
        ema.Should().BeApproximately(0.9, 0.001, "常量输入下收敛到目标");
    }

    [Fact]
    public void ThrottleMap_PiecewiseLinear_EndpointsAndMidpoint()
    {
        // 端点
        CpuSampler.MapThrottleFactor(0.0, 0.7, 0.9).Should().Be(0.0, "CPU=0 → 系数 0");
        CpuSampler.MapThrottleFactor(0.7, 0.7, 0.9).Should().Be(0.0, "CPU=low → 系数 0（闭区间）");
        CpuSampler.MapThrottleFactor(0.9, 0.7, 0.9).Should().Be(1.0, "CPU=high → 系数 1（闭区间）");
        CpuSampler.MapThrottleFactor(1.0, 0.7, 0.9).Should().Be(1.0, "CPU=1 → 系数 1");
        // 中点线性
        CpuSampler.MapThrottleFactor(0.8, 0.7, 0.9).Should().BeApproximately(0.5, 1e-12, "中点 → 0.5（线性）");
        // 单调
        var prev = -1.0;
        for (var cpu = 0.0; cpu <= 1.0; cpu += 0.05)
        {
            var f = CpuSampler.MapThrottleFactor(cpu, 0.7, 0.9);
            f.Should().BeInRange(0.0, 1.0);
            f.Should().BeGreaterThanOrEqualTo(prev, "系数随 CPU 单调不减");
            prev = f;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  构造校验（fail-fast——治参数带病：反向限流/震荡/除零）
    // ════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0.0)]        // α=0：永不更新
    [InlineData(-0.1)]       // α<0：反向
    [InlineData(1.1)]        // α>1：震荡发散
    public void Ctor_RejectsInvalidAlpha(double alpha)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CpuSampler(emaAlpha: alpha));

    [Theory]
    [InlineData(0.9, 0.7)]   // low ≥ high：斜率为负 = 反向限流
    [InlineData(0.7, 0.7)]   // 相等：除零 → NaN
    [InlineData(-0.1, 0.9)]  // low < 0
    [InlineData(0.7, 1.1)]   // high > 1
    public void Ctor_RejectsInvalidCutoffs(double low, double high)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CpuSampler(throttleLowCutoff: low, throttleHighCutoff: high));

    [Fact]
    public void Ctor_RejectsNonPositiveInterval()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new CpuSampler(sampleInterval: TimeSpan.Zero));

    // ════════════════════════════════════════════════════════════
    //  Hub 折叠（治"限流判断不进视图"）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public async Task HubFold_PublishesUtilizationAndFactorGauges()
    {
        var sink = new CapturingMetricsSink();
        var hub = ObservabilityHub.Create(sink, tracer: null,
            new ObservabilityOptions { Metrics = new MetricsConfig { Enabled = true } });
        using var sampler = new CpuSampler(sampleInterval: TimeSpan.FromMilliseconds(20), hub: hub);
        sampler.Start();
        await Task.Delay(150);

        Assert.True(SpinWait.SpinUntil(
            () => sink.Gauges.Any(g => g.name == "cpu.utilization") && sink.Gauges.Any(g => g.name == "cpu.throttle.factor"),
            3000), "采样后 Hub 应收到 cpu.utilization 与 cpu.throttle.factor Gauge");
        sink.Gauges.First(g => g.name == "cpu.utilization").value.Should().BeInRange(0.0, 1.0);
        sink.Gauges.First(g => g.name == "cpu.throttle.factor").value.Should().BeInRange(0.0, 1.0);
    }

    /// <summary>捕获 Gauge 的测试 sink（同 IsolatedTaskSchedulerTests 的思路，独立小份避免跨类耦合）。</summary>
    private sealed class CapturingMetricsSink : IMetricsSink
    {
        public bool IsEnabled => true;
        public readonly System.Collections.Concurrent.ConcurrentQueue<(string name, double value)> Gauges = new();
        public void Counter(string name, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
        public void Histogram(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) { }
        public void Gauge(string name, double value, ReadOnlySpan<KeyValuePair<string, string>> tags) => Gauges.Enqueue((name, value));
    }
}
