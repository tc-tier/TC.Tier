namespace TC.Tier.Runtime.Tests;

/// <summary>
/// 测试条件等待——让出式轮询（对照 <c>SpinWait.SpinUntil</c>）。
/// <para>★ #419：SpinUntil 忙等在 2vCPU runner 上与被等待方争抢 CPU——恢复续体跑在线程池，
///   并行套件的自旋测试烧满核后续体饿死，IsReady 120s 不可达（本地多核恒不复现）。
///   Sleep 步进让出调度，被等待方得以推进。</para>
/// </summary>
internal static class TestWait
{
    /// <summary>轮询条件直至成立或超时（让出式——Thread.Sleep 步进）。</summary>
    /// <param name="condition">等待条件。</param>
    /// <param name="timeoutMs">超时毫秒。</param>
    /// <returns>成立 true；超时 false。</returns>
    internal static bool Until(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline) return false;
            Thread.Sleep(10);
        }
        return true;
    }
}
