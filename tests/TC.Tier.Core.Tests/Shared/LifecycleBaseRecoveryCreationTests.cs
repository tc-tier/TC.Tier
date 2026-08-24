using FluentAssertions;
using TC.Tier.Core.Shared;

namespace TC.Tier.Core.Tests.Shared;

/// <summary>
/// LifecycleBase.Recovery 创建语义契约测试——钉住"getter 纯读 + Initialize 单一创建点"（用户裁定：
/// 懒创建风险不该上层承担，基类直接原子）。
/// <para>旧实现 <c>_recovery ??= CreateRecovery()</c> 的两宗罪（已废除）：
/// ① 非原子——并发读双实例竞态；② 副作用——Initialize 前任何 IsReady 观测读都偷跑工厂
/// （Dispose 前查一下也凭空建出 Recovery）。</para>
/// </summary>
public class LifecycleBaseRecoveryCreationTests
{
    private readonly struct TestHints { }

    /// <summary>可计数的恢复——记录创建/运行次数。</summary>
    private sealed class CountingRecovery : RecoveryBase<TestHints>
    {
        public static int Constructions;

        public CountingRecovery() => Interlocked.Increment(ref Constructions);

        protected override ValueTask OnRecoveryCoreAsync(TestHints hints, CancellationToken ct)
            => ValueTask.CompletedTask;
    }

    /// <summary>工厂可计数的宿主——CreateRecovery 记录调用次数。</summary>
    private sealed class CountingHost : LifecycleBase<TestHints>
    {
        public static int FactoryCalls;

        public CountingHost() : base()
        {
        }

        protected override IRecovery<TestHints>? CreateRecovery()
        {
            Interlocked.Increment(ref FactoryCalls);
            return new CountingRecovery();
        }
    }

    [Fact]
    public void Getter_IsPureRead_NoFactorySideEffect()
    {
        var host = new CountingHost();
        // Initialize 前的观测读（IsReady/RecoveryState）不得偷跑工厂——零副作用
        for (int i = 0; i < 10; i++)
        {
            _ = host.IsReady;
            _ = host.RecoveryState;
        }

        CountingHost.FactoryCalls.Should().Be(0, "Initialize 前的观测读不得触发 CreateRecovery（getter 纯读）");
    }

    [Fact]
    public void Initialize_CreatesRecovery_ExactlyOnce()
    {
        var host = new CountingHost();
        _ = host.IsReady; // 先观测（旧实现此处就偷跑了）
        host.Initialize();
        host.WaitForReady();

        CountingHost.FactoryCalls.Should().Be(1, "Initialize 的 CAS 闸门内单一创建点——恰好一次");
        CountingRecovery.Constructions.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void ConcurrentObservation_BeforeInitialize_ZeroFactoryCalls()
    {
        var host = new CountingHost();
        using var barrier = new Barrier(8);
        var tasks = Enumerable.Range(0, 8).Select(_t => Task.Run(() =>
        {
            barrier.SignalAndWait(TimeSpan.FromSeconds(5));
            for (int i = 0; i < 100; i++)
            {
                _ = host.IsReady;
                _ = host.RecoveryState;
            }
        })).ToArray();

        Task.WaitAll(tasks);
        CountingHost.FactoryCalls.Should().Be(0, "并发观测读零副作用——原子性由基类承担，不是调用方纪律");
    }

    [Fact]
    public void NotInitialized_Dispose_DoesNotCreateRecovery()
    {
        var before = CountingHost.FactoryCalls;
        var host = new CountingHost();
        host.Dispose(); // 未 Initialize 就 Dispose——不得凭空创建 Recovery（旧实现会）

        CountingHost.FactoryCalls.Should().Be(before, "Dispose 路径纯读——不触发工厂");
    }
}
