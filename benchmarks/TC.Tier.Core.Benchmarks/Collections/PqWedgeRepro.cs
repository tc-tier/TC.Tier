using System.Diagnostics;

namespace TC.Tier.Core.Benchmarks.Collections;

/// <summary>
/// AsyncPriorityQueue 楔死复现器——复刻 AsyncPriorityQueueTests.Stress_EnqueueDequeueRounds 负载
/// （N worker × 轮次：Enqueue(i, i%4)×32 + TryDequeue×32），看门狗发现挂住时**主动遍历打印链现场**：
/// head 32 层边 + level-0 主链前 64 节点（Key/序列/标记状态）+ 层间悬挂诊断。
///
/// 用法：dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks -- --pq-wedge [rounds]
/// </summary>
internal static class PqWedgeRepro
{
    public static int Run(string[] args)
    {
        var rounds = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 2000;
        Console.WriteLine($"================ PqWedgeRepro（rounds={rounds}）================");
        var q = new AsyncPriorityQueue<int>();
        const int perRound = 32;
        var done = 0;

        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(w => Task.Run(() =>
        {
            for (var rr = 0; rr < rounds / Environment.ProcessorCount; rr++)
            {
                for (var i = 0; i < perRound; i++)
                    q.Enqueue(i, priority: i % 4);
                for (var i = 0; i < perRound; i++)
                    q.TryDequeue(out _);
                Interlocked.Increment(ref done);
            }
        })).ToArray();

        // 看门狗：进度停滞 10s = 楔死 → 打印链现场
        var lastDone = 0;
        var stallSw = Stopwatch.StartNew();
        var sw = Stopwatch.StartNew();
        while (!Task.WaitAll(workers, 500))
        {
            if (done != lastDone) { lastDone = done; stallSw.Restart(); continue; }
            if (stallSw.ElapsedMilliseconds > 10_000)
            {
                Console.WriteLine($"\n★ 楔死现场（{sw.Elapsed.TotalSeconds:F0}s，进度 {done}/{rounds} 轮停滞 10s）");
                DumpStructure(q);
                return 2;
            }
        }
        sw.Stop();
        Console.WriteLine($"✓ 未复现：{rounds} 轮 {sw.Elapsed.TotalMilliseconds:F0}ms 完成");
        q.ValidateInvariants();
        Console.WriteLine("✓ 链校验通过");
        return 0;
    }

    /// <summary>遍历打印：head 各层边 + level-0 主链 + 层间悬挂诊断（层边指向的 key < 层0 队头 key = 悬挂僵尸）。</summary>
    private static void DumpStructure(AsyncPriorityQueue<int> q)
    {
        var headField = typeof(AsyncPriorityQueue<int>).GetField("_head", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var head = headField.GetValue(q)!;
        var headType = head.GetType();
        var fwd = (Array?)headType.GetField("Forward", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(head);
        var nodeType = typeof(AsyncPriorityQueue<int>).Assembly.GetType("TC.Tier.Core.Collections.AsyncPriorityQueue`1+Node")!.MakeGenericType(typeof(int));
        var markerType = typeof(AsyncPriorityQueue<int>).Assembly.GetType("TC.Tier.Core.Collections.AsyncPriorityQueue`1+Marker")!.MakeGenericType(typeof(int));
        const System.Reflection.BindingFlags F2 = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

        object? F(object o, int i) => ((Array?)nodeType.GetField("Forward", F2)!.GetValue(o))!.GetValue(i);
        long Key(object o) => (long)nodeType.GetField("Key", F2)!.GetValue(o)!;
        long Seq(object o) => (long)nodeType.GetField("Sequence", F2)!.GetValue(o)!;
        object? MarkerNext(object m) => markerType.GetField("Next", F2)!.GetValue(m);
        string Desc(object? o) => o is null ? "null"
            : nodeType.IsInstanceOfType(o) ? $"Node(P{Key(o) >> 48}/{Seq(o)})"
            : $"Marker(→{(MarkerNext(o) is null ? "null" : nodeType.IsInstanceOfType(MarkerNext(o)) ? $"Node(P{Key(MarkerNext(o)!) >> 48}/{Seq(MarkerNext(o)!)})" : "Marker?")})";

        Console.WriteLine("── head 各层边 ──");
        for (var i = 31; i >= 0; i--)
        {
            var e = fwd!.GetValue(i);
            if (e is not null) Console.WriteLine($"  head.F[{i,2}] = {Desc(e)}");
        }

        Console.WriteLine("── level-0 主链（最多 64 步）──");
        var curr = fwd!.GetValue(0)!;   // 反射字段必非空（哨兵初始化后前置填充）
        var steps = 0;
        var markedCount = 0;
        var nodeCount = 0;
        while (curr is not null && steps++ < 64)
        {
            if (markerType.IsInstanceOfType(curr)) { Console.WriteLine($"  #{steps,-3} {Desc(curr)}"); curr = MarkerNext(curr); continue; }
            var f0 = F(curr, 0);
            var isMarked = markerType.IsInstanceOfType(f0);
            if (isMarked) markedCount++; else nodeCount++;
            var f1 = curr.Equals(fwd.GetValue(0)) ? F(curr, 1) : null;   // 只打队头的 F1（诊断重点）
            Console.WriteLine($"  #{steps,-3} Node(P{Key(curr) >> 48}/{Seq(curr)}) F0={Desc(f0)}" +
                              (isMarked ? "  ← 已删未摘" : "") +
                              (f1 is not null ? $"  F1={Desc(f1)}" : ""));
            curr = f0;
        }

        // 层间悬挂：层 i ≥1 的 head 边目标 key 若 < 层 0 队头 key，则该层入口指向"应已出队"的僵尸
        var l0 = fwd.GetValue(0);
        if (nodeType.IsInstanceOfType(l0))
        {
            var l0Key = Key(l0!);
            Console.WriteLine($"── 层间悬挂诊断（层0 队头 key=P{l0Key >> 48}/{l0Key & 0xFFFFFFFFFFFF}）──");
            for (var i = 31; i >= 1; i--)
            {
                var e = fwd.GetValue(i);
                object? target = e;
                while (target is not null && markerType.IsInstanceOfType(target)) target = MarkerNext(target);
                if (target is not null && Key(target) < l0Key)
                {
                    Console.WriteLine($"  ✗ 层 {i} 入口指向 P{Key(target) >> 48}/{Seq(target)}——key 小于层0队头（悬挂僵尸）");
                    for (var j = 0; j <= 2; j++)
                        Console.WriteLine($"     僵尸.F[{j}] = {Desc(F(target, j))}");
                }
            }
        }
        Console.WriteLine($"（已删未摘 {markedCount} ｜ 活节点 {nodeCount} ｜ 主链 64 步内）");
    }
}
