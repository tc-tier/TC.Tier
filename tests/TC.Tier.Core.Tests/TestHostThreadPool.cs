using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Tests;

/// <summary>
/// 测试宿主线程池预热——消除满套并行下的 <c>Task.Run</c> 起跑延迟（池注入节流）。
/// <para>★ 根因（三连假红后收口）：xUnit 并行 collection 打满线程池后，池的注入节流
///   （约 1 线程/ms + 饥饿检测延迟）使新排队的 Task.Run <b>起跑</b>延迟可达秒级——固定毫秒
///   <c>Wait(N)</c> 的隐含假设是"任务及时起跑"，满套下必假红。本会话三例：LockWordTests
///   Shared_OverlappingHolders / ChaseCompaction 发布竞态 / AcquireExclusive_MemRealLock
///   （均为隔离复跑绿、满套红）。SetMinThreads 去掉注入节流——排队即建线程（上限 64），
///   起跑回到 µs 级，一次性消灭这一整类，覆盖现存 38 处与未来新增。</para>
/// <para>★ 适用边界：本套件测原语语义（锁/队列/桥），不测线程池调度本身——IsolatedTaskScheduler
///   用私有线程池无关。生产代码禁止模仿（掩盖真实饥饿是生产 bug）。</para>
/// <para>★ 与 SpinUntil 轮询加固双保险：SetMinThreads 抗<b>起跑慢</b>，SpinUntil 抗<b>完成慢</b>——
///   满套下对并行负载敏感的等待仍优先写轮询（见 SpinRWLockTests 同型注释）。</para>
/// </summary>
internal static class TestHostThreadPool
{
    [ModuleInitializer]
    internal static void Configure() => ThreadPool.SetMinThreads(64, 64);
}
