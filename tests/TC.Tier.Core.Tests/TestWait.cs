namespace TC.Tier.Core.Tests;

/// <summary>
/// 异步轮询对齐 helper——替代测试中固定 <c>Task.Delay</c> 的 delay 型同步。
/// <para>★ 动机：固定 Delay 把"线程池调度延迟 &lt; N ms"当成立假设——并行测试套压公共池时
///   该假设被踩穿即假失败（2026-08-18 BackgroundWorkerLoopTests 全量套实测踩中）。等待必须与
///   被断言的条件对齐（轮询/事件），不是与假设的延迟对齐。</para>
/// </summary>
internal static class TestWait
{
    /// <summary>轮询等待条件成立（默认 10s 上限 / 10ms 间隔）。
    /// 超时返回 false——由调用方 <c>Should().BeTrue("…")</c> 给出失败语义。</summary>
    public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 10_000, int pollMs = 10)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
                return false;
            await Task.Delay(pollMs).ConfigureAwait(false);
        }
        return true;
    }
}
