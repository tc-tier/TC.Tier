using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// TierKv 调度器配置透传契约（#486）：TierKvOptions.WorkerScheduler 经 TierKvAssembly.EngineOptions
/// 映射到引擎选项——同实例双引擎（-data/-index）同用该配置；缺省 null = 引擎全默认。
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
    }

    [Fact]
    public void WithWorkerScheduler_FullOptionsFlowsThrough()
    {
        using var vol = new TestVolume();
        var scheduler = new TC.Tier.Core.Execution.IsolatedSchedulerOptions
        {
            Name = "kv-test-worker",
            ThreadCount = 3,
        };
        var engine = TierKvAssembly.EngineOptions(
            vol.Fs, TierKvOptions.Default.WithKvName("knob-full").WithWorkerScheduler(scheduler), "-data");

        engine.WorkerScheduler.Should().BeSameAs(scheduler, "完整旋钮注入按引用透传");
    }
}
