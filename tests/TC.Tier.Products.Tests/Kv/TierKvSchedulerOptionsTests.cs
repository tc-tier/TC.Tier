using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// TierKv 调度器配置透传契约（#486 双形态词汇统一）：TierKvOptions.WorkerSchedulerOptions
/// （配置形态）经 TierKvAssembly.EngineOptions 映射到引擎选项——同实例双引擎（-data/-index）同用该配置；
/// 缺省 null = 引擎全默认；WorkerScheduler（共享实例形态）经 Settings 直达结构内全部引擎（含 meta）。
/// <para>★ 线程数与实例数线性绑定是默认形态的存量成本——嵌入式/多实例/测试场景 WithSchedulerThreads(2~4)。</para>
/// </summary>
public class TierKvSchedulerOptionsTests
{
    [Fact]
    public void EngineOptions_Default_WorkerSchedulerNull()
    {
        using var vol = new TestVolume();
        var engine = TierKvAssembly.EngineOptions(vol.Fs, TierKvOptions.Default.WithKvName("knob-def"), "-data");
        engine.WorkerScheduler.Should().BeNull("缺省 = 引擎自建全默认调度器（RecommendedThreadCount）");
    }

    [Fact]
    public void WithSchedulerThreads_FlowsThroughEngineOptions()
    {
        using var vol = new TestVolume();
        var options = TierKvOptions.Default.WithKvName("knob-t2").WithSchedulerThreads(2);
        var data = TierKvAssembly.EngineOptions(vol.Fs, options, "-data");
        var index = TierKvAssembly.EngineOptions(vol.Fs, options, "-index");

        data.WorkerScheduler.Should().NotBeNull();
        data.WorkerScheduler!.ThreadCount.Should().Be(2);
        data.WorkerScheduler.Name.Should().Be("engine-worker");
        index.WorkerScheduler!.ThreadCount.Should().Be(2, "同实例双引擎同用该配置");
        options.WorkerScheduler.Should().BeNull("配置形态不影响共享实例形态");
    }

    [Fact]
    public void WithWorkerSchedulerOptions_FullOptionsFlowsThrough()
    {
        using var vol = new TestVolume();
        var scheduler = new TC.Tier.Core.Execution.IsolatedSchedulerOptions
        {
            Name = "kv-test-worker",
            ThreadCount = 3,
        };
        var engine = TierKvAssembly.EngineOptions(
            vol.Fs, TierKvOptions.Default.WithKvName("knob-full").WithWorkerSchedulerOptions(scheduler), "-data");

        engine.WorkerScheduler.Should().BeSameAs(scheduler, "完整旋钮注入按引用透传");
    }

    [Fact]
    public void WithWorkerScheduler_InstanceForm_FlowsToSettings()
    {
        using var vol = new TestVolume();
        using var shared = TC.Tier.Core.Execution.IsolatedTaskScheduler.Create(
            new TC.Tier.Core.Execution.IsolatedSchedulerOptions { Name = "kv-shared-test", ThreadCount = 2 });
        var options = TierKvOptions.Default.WithKvName("knob-instance").WithWorkerScheduler(shared);

        // 共享实例形态经 Settings 直达结构内全部引擎（含 meta/溢出——RingBase 内部继承）
        var ring = TierKvAssembly.RingSettings(vol.Fs, options);
        ring.WorkerScheduler.Should().BeSameAs(shared, "Ring Settings 持共享实例");
        var hash = TierKvAssembly.HashSettings(vol.Fs, options);
        hash.WorkerScheduler.Should().BeSameAs(shared, "Hash 索引 Settings 持共享实例");

        // 实例形态下配置形态被忽略（引擎构造先判实例）
        options.WorkerSchedulerOptions.Should().BeNull();
    }
}
