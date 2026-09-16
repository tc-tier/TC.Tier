// AsyncPump 微基准（组件 A/B 判例——优化前基线与优化后同轮对照）：
// ① 跨线程完成严格串行往返（raft 实况热路径：外部线程逐个完成 TCS → Post → 泵线程执行续体）；
// ② 域内推进（await 已完成任务——同步快路径控制组）；
// ③ 窗口化流水线（64 在途——吞吐路径：work 逐窗 WhenAll，外部线程逐个完成）。
// 计时器与 pump.Run 同线程（完成线程只做 TrySetResult——其循环无 await，调度可靠）。
// 口径：Release、预热后测量、零测量期 IO。
using System.Diagnostics;
using TC.Tier.Core.Execution;

var n = int.TryParse(Environment.GetEnvironmentVariable("PUMP_PROBE_N"), out var nn) && nn > 0 ? nn : 100_000;

Console.WriteLine($"=== AsyncPump 微基准（N={n}）===");
Console.WriteLine($"环境：{Environment.ProcessorCount} 逻辑核，.NET {Environment.Version}，{DateTime.Now:HH:mm:ss.fff}");

var pump = new AsyncPump("probe");

// ── ② 域内推进（同步快路径控制组）──
{
    pump.Run(async () =>
    {
        for (var i = 0; i < 10_000; i++)
            await Task.CompletedTask;
    });
    var sw = Stopwatch.StartNew();
    pump.Run(async () =>
    {
        for (var i = 0; i < n; i++)
            await Task.CompletedTask;
    });
    sw.Stop();
    Console.WriteLine($"② 域内推进（await Task.CompletedTask ×{n}）：{n / sw.Elapsed.TotalSeconds:F0} ops/s（{sw.Elapsed.TotalMilliseconds:F1}ms）");
}

// ── ① 跨线程完成严格串行往返（work 逐个 await 槽位；外部线程逐个完成；计时在 pump 线程）──
{
    var slots = new TaskCompletionSource<bool>[n];
    for (var i = 0; i < n; i++) slots[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    // 预热 1000
    var warmSlots = new TaskCompletionSource<bool>[1_000];
    for (var i = 0; i < 1_000; i++) warmSlots[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var warmTask = Task.Run(() =>
    {
        var w = Stopwatch.StartNew();
        pump.Run(async () =>
        {
            for (var i = 0; i < 1_000; i++)
                await warmSlots[i].Task;
        });
        w.Stop();
        return w.Elapsed;
    });
    for (var i = 0; i < 1_000; i++) warmSlots[i].TrySetResult(true);
    _ = await warmTask;

    var timed = Task.Run(() =>
    {
        var w = Stopwatch.StartNew();
        pump.Run(async () =>
        {
            for (var i = 0; i < n; i++)
                await slots[i].Task;
        });
        w.Stop();
        return w.Elapsed;
    });
    for (var i = 0; i < n; i++)
        slots[i].TrySetResult(true);
    var elapsed = await timed;
    Console.WriteLine($"① 跨线程严格串行往返：{n / elapsed.TotalSeconds:F0} ops/s（{elapsed.TotalMilliseconds:F1}ms）");
}

// ── ③ 窗口化流水线（64 在途——吞吐路径；计时在 pump 线程）──
{
    const int Window = 64;
    var slots = new TaskCompletionSource<bool>[n];
    for (var i = 0; i < n; i++) slots[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    // 预热两窗
    var warm = new TaskCompletionSource<bool>[2 * Window];
    for (var i = 0; i < warm.Length; i++) warm[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var warmTask = Task.Run(() =>
    {
        pump.Run(async () =>
        {
            for (var i = 0; i < warm.Length; i += Window)
            {
                var tasks = new Task[Window];
                for (var k = 0; k < Window; k++)
                    tasks[k] = warm[i + k].Task;
                await Task.WhenAll(tasks);
            }
        });
    });
    for (var i = 0; i < warm.Length; i++) warm[i].TrySetResult(true);
    await warmTask;

    var timed = Task.Run(() =>
    {
        var w = Stopwatch.StartNew();
        pump.Run(async () =>
        {
            for (var i = 0; i < n; i += Window)
            {
                var count = Math.Min(Window, n - i);
                var tasks = new Task[count];
                for (var k = 0; k < count; k++)
                    tasks[k] = slots[i + k].Task;
                await Task.WhenAll(tasks);
            }
        });
        w.Stop();
        return w.Elapsed;
    });
    for (var i = 0; i < n; i++)
        slots[i].TrySetResult(true);
    var elapsed = await timed;
    Console.WriteLine($"③ 窗口流水线（{Window} 在途）：{n / elapsed.TotalSeconds:F0} ops/s（{elapsed.TotalMilliseconds:F1}ms）");
}

Console.WriteLine("=== 完成 ===");
