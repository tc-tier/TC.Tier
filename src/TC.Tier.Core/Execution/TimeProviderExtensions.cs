namespace TC.Tier.Core.Execution;

/// <summary>
/// <see cref="TimeProvider"/> 扩展——时钟缝（故障注入面补全设计 件一）消费侧统一供给源。
/// <para>★ <see cref="GetMsTimestamp"/>：毫秒单调戳——语义与存量 <c>Environment.TickCount64</c> 域逐字节对齐
///   （System 快路径直读 TickCount64；假钟按 <see cref="TimeProvider.TimestampFrequency"/> 换算），
///   落点改造零算术变更（deadline 字段全部保持 ms 域）。</para>
/// <para>★ <see cref="Delay(TimeProvider, TimeSpan, CancellationToken)"/>：provider 可注入的任务延迟——
///   假钟下由测试快进驱动（零真实睡等），System 下直通 <see cref="Task.Delay(TimeSpan, CancellationToken)"/>
///   （缺省零变化）。</para>
/// </summary>
public static class TimeProviderExtensions
{
    /// <summary>毫秒单调戳（与 <c>Environment.TickCount64</c> 同域——停走/倒流免疫语义由各 provider 自身保证；
    /// 墙钟跳变不影响单调钟）。</summary>
    /// <param name="timeProvider">时钟供给源。</param>
    /// <returns>毫秒单调时间戳（任意两个读数可减，差 = 实际流逝毫秒）。</returns>
    public static long GetMsTimestamp(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        // ★ System 快路径——存量域直读（换源不停走语义与现状逐字节一致）
        if (ReferenceEquals(timeProvider, TimeProvider.System)) return Environment.TickCount64;
        return timeProvider.GetTimestamp() * 1000 / timeProvider.TimestampFrequency;
    }

    /// <summary>provider 可注入的任务延迟（<see cref="Task.Delay(int)"/> 的时钟缝替身）。</summary>
    /// <param name="timeProvider">时钟供给源。</param>
    /// <param name="millisecondsDelay">延迟毫秒数（负值 = 立即完成，对齐 Task.Delay 语义）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>延迟到期（或取消）后完成的任务。</returns>
    public static Task Delay(this TimeProvider timeProvider, int millisecondsDelay,
        CancellationToken cancellationToken = default)
        => timeProvider.Delay(TimeSpan.FromMilliseconds(millisecondsDelay), cancellationToken);

    /// <summary>provider 可注入的任务延迟（<see cref="Task.Delay(TimeSpan, CancellationToken)"/> 的时钟缝替身）。
    /// <para>★ 假钟形态：经 <see cref="TimeProvider.CreateTimer"/> 注册定时器，测试快进（Advance）确定性触发——
    ///   零真实睡等；<see cref="TimeProvider.System"/> 直通 Task.Delay（缺省零变化）。</para></summary>
    /// <param name="timeProvider">时钟供给源。</param>
    /// <param name="delay">延迟时长（≤ 0 = 立即完成；无限 = 永不完成，对齐 Task.Delay 语义）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>延迟到期（或取消）后完成的任务。</returns>
    public static async Task Delay(this TimeProvider timeProvider, TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (delay == Timeout.InfiniteTimeSpan)
        {
            // Task.Delay(-1) 语义：永不完成（仅取消可解）
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (delay <= TimeSpan.Zero)
        {
            if (cancellationToken.IsCancellationRequested)
                await Task.FromCanceled(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (ReferenceEquals(timeProvider, TimeProvider.System))
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion, delay, Timeout.InfiniteTimeSpan);
        try
        {
            if (cancellationToken.CanBeCanceled)
            {
                var registration = cancellationToken.Register(
                    static state =>
                    {
                        var (tcs, token) = ((TaskCompletionSource, CancellationToken))state!;
                        tcs.TrySetCanceled(token);
                    }, (completion, cancellationToken));
                try
                {
                    await completion.Task.ConfigureAwait(false);
                }
                finally
                {
                    await registration.DisposeAsync().ConfigureAwait(false);
                }
            }
            else
            {
                await completion.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            await timer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
