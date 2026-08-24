using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Tests;

/// <summary>
/// 测试宿主线程池预热——消除满套并行下的 <c>Task.Run</c> 起跑延迟（池注入节流）。
/// <para>★ 同 TC.Tier.Core.Tests.TestHostThreadPool（三连假红收口，两套件统一宿主环境）：
///   xUnit 并行 collection 打满池后注入节流使 Task.Run 起跑延迟秒级，固定毫秒 Wait 必假红；
///   SetMinThreads(64,64) 去节流，起跑回 µs 级。本套件虽无固定 Wait 形态，但 IO 型并发测试
///   的 Task.Run actor 同受池排队影响（ConcurrentReadWrite 满套 flaky 的疑似因素之一——
///   未单独归因，不下结论）。</para>
/// <para>★ 适用边界：测试原语/引擎语义，不测池调度。生产代码禁止模仿。</para>
/// </summary>
internal static class TestHostThreadPool
{
    [ModuleInitializer]
    internal static void Configure() => ThreadPool.SetMinThreads(64, 64);
}
