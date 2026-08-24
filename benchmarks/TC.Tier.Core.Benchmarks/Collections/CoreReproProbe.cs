using System.Diagnostics;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// CORE-02/03 复现验证探针：
/// ① NodeArena 并发分配（8 线程 × 高频加块——修复前实测复现 IndexOutOfRange）；
/// ② PooledValueTaskSource 取消注册泄漏（AttachCancellation → Return → 重租 → 旧 token 触发 → 伪取消）。
/// 用法：--core-repro-probe arena|pvts [rounds]
/// 返回码：0 = 无复现；3 = 复现（缺陷仍存在）
/// </summary>
internal static class CoreReproProbe
{
    public static int Run(string[] args)
    {
        var which = args.Length > 1 ? args[1] : "arena";
        return which switch
        {
            "arena" => ArenaProbe(),
            "pvts" => PvtsProbe(),
            _ => 2,
        };
    }

    /// <summary>8 线程 × 300 万次 64B 分配（64KB 块高频加块——发布窗口最大化）。</summary>
    private static unsafe int ArenaProbe()
    {
        const int threads = 8;
        const int perThread = 3_000_000;
        using var arena = new NodeArena();
        var failures = 0;
        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            var local = new Random(t * 7919);
            for (var i = 0; i < perThread; i++)
            {
                try
                {
                    _ = arena.Alloc(64 + (local.Next() & 0x1F));
                }
                catch (IndexOutOfRangeException)
                {
                    Interlocked.Increment(ref failures);
                    return;
                }
            }
        })).ToArray();
        Task.WaitAll(tasks);
        Console.WriteLine($"NodeArena {threads}×{perThread / 1000}K 分配：越界 {failures} 次" + (failures > 0 ? "——★ 复现！" : "——无复现（CORE-02 已修复）"));
        return failures > 0 ? 3 : 0;
    }

    /// <summary>AttachCancellation → 早退 Return → 重租 → 旧 token 触发 → 新等待不得被伪取消。</summary>
    private static int PvtsProbe()
    {
        var spurious = 0;
        for (var round = 0; round < 200_000; round++)
        {
            using var oldCts = new CancellationTokenSource();
            using var newCts = new CancellationTokenSource();
            var source = PooledValueTaskSource.Rent();
            source.AttachCancellation(oldCts.Token);
            PooledValueTaskSource.Return(source);   // 早退（未完成）——旧实现注册未注销

            var re = PooledValueTaskSource.Rent();
            re.AttachCancellation(newCts.Token);
            var vt = new ValueTask(re, re.Version);
            oldCts.Cancel();   // 旧 token 触发——不得影响新等待者
            if (vt.IsCompleted && !newCts.IsCancellationRequested)
            {
                // 新等待被旧 token 完成 = 伪取消
                spurious++;
                break;
            }
            if (!vt.IsCompleted)
            {
                newCts.Cancel();   // 正常取消路径收尾
                _ = vt.AsTask().ContinueWith(_ => { });
            }
        }
        Console.WriteLine($"PVTS 伪取消：{spurious} 次" + (spurious > 0 ? "——★ 复现！" : "——无复现（CORE-03 已修复）"));
        return spurious > 0 ? 3 : 0;
    }
}
