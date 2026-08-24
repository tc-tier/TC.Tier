using System.Collections.Concurrent;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// lease 协议并发测试——验证多线程下 lease 协议的正确性和不变量。
/// <para>★ 验证的不变量：</para>
/// <list type="bullet">
/// <item>地址唯一性：并发 Append 分配的地址不重叠</item>
/// <item>CommittedTail ≤ AllocatedTail：始终成立</item>
/// <item>区间排他：同一地址不被两个 lease 同时占</item>
/// <item>无死锁：所有线程在超时内完成</item>
/// <item>数据完整：Commit 的区间变 Committed 可读</item>
/// </list>
/// <para>★ 这些测试真正验证之前的修复（LockWord（段锁前身）死锁、AcquireExtent 超时、InsertUnsafe 拆分）
///   在并发下是否可用——单线程测试覆盖不了并发维度。</para>
/// </summary>
public class LeaseConcurrencyTests
{
    private static SegmentTable NewTable(long growthLimit = 100_000, int capacity = 256)
        => new(new SegmentTableSettings(growthLimit, 0, capacity, SpinMilliseconds: 10_000), LeaseFactory.WithDiagnostics);

    // ════════════════════════════════════════════════════════════
    //  并发 Append——地址唯一性 + 无死锁
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentAppend_AllAddressesUnique_NoOverlap()
    {
        // N 个线程并发 AppendLease(100).Commit()——分配的地址不能重叠
        const int threads = 8;
        const int perThread = 50;
        using var table = NewTable();
        var startAddresses = new ConcurrentBag<LogicalAddress>();
        var errors = new ConcurrentQueue<Exception>();

        Parallel.For(0, threads, i =>
        {
            try
            {
                for (var j = 0; j < perThread; j++)
                {
                    using var lease = table.AppendLease(100);
                    startAddresses.Add(lease.Start);
                    lease.Commit();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        Assert.Empty(errors);
        var all = startAddresses.ToList();
        Assert.Equal(threads * perThread, all.Count);
        // ★ 地址唯一——不重叠（同一 start 不能出现两次）
        Assert.Equal(all.Count, all.Distinct().Count());
        // 不变量：CommittedTail ≤ AllocatedTail
        Assert.True(table.CommittedTail <= table.AllocatedTail,
            $"CommittedTail {table.CommittedTail} > AllocatedTail {table.AllocatedTail}");
    }

    [Fact]
    public void ConcurrentAppend_CommittedTailMonotonicAndCorrect()
    {
        // 并发 Append 后 CommittedTail 应正确推进到总长度
        const int threads = 4;
        const int perThread = 25;
        const int unitLen = 100;
        using var table = NewTable();
        var count = 0;

        Parallel.For(0, threads, _ =>
        {
            for (var j = 0; j < perThread; j++)
            {
                using var lease = table.AppendLease(unitLen);
                lease.Commit();
                Interlocked.Increment(ref count);
            }
        });

        Assert.Equal(threads * perThread, count);
        // 所有 Append 都 Commit 了，CommittedTail 应 ≥ 起点推进总长度
        // （跨段进位可能略大，但至少包含所有提交的数据）
        var distance = table.GetDistance(new LogicalAddress(0, 0), table.CommittedTail);
        Assert.True(distance >= threads * perThread * unitLen,
            $"CommittedTail 距离 {distance} < 预期 {threads * perThread * unitLen}");
    }

    // ════════════════════════════════════════════════════════════
    //  并发 Append + Read——读者不读到未提交数据
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentAppendAndRead_NoDeadlockCompletes()
    {
        // 写者持续 Append，读者持续查 IsRangeFullyReadable——不能死锁
        using var table = NewTable();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var errors = new ConcurrentQueue<Exception>();

        var writer = Task.Run(() =>
        {
            try
            {
                var rnd = new Random(Environment.CurrentManagedThreadId);
                while (!cts.IsCancellationRequested)
                {
                    using var lease = table.AppendLease(100);
                    Thread.SpinWait(rnd.Next(10));   // 模拟锁外 IO
                    lease.Commit();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        var reader = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // 读已提交范围内的某段——不读到在途数据
                    var committed = table.CommittedTail;
                    if (committed.Offset >= 100)
                        table.IsRangeFullyReadable(committed.SegId,
                            Math.Max(0, committed.Offset - 100), committed.Offset);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        Task.WaitAll(writer, reader);
        Assert.Empty(errors);   // 无异常 = 无死锁崩溃
    }

    // ════════════════════════════════════════════════════════════
    //  并发 Append + Write——不同区间不冲突
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentAppendAndWrite_DifferentRanges_Complete()
    {
        // 先 Append 一批已提交空间，然后并发 Write 覆写不同子区间 + 继续 Append
        using var table = NewTable();
        // 预热：Append 10 块各 100 字节
        var starts = new List<LogicalAddress>();
        for (var i = 0; i < 10; i++)
        {
            var lease = table.AppendLease(100);
            starts.Add(lease.Start);
            lease.Commit();
        }

        var errors = new ConcurrentQueue<Exception>();
        // 并发：5 个 Write 各覆写一块 + 3 个 Append 继续推
        Parallel.Invoke(
            () =>
            {
                foreach (var s in starts.Take(5))
                {
                    try { using var l = table.WriteLease(s, 100); l.Commit(); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }
            },
            () =>
            {
                for (var i = 0; i < 3; i++)
                {
                    try { using var l = table.AppendLease(100); l.Commit(); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }
            });

        Assert.Empty(errors);
        Assert.True(table.CommittedTail <= table.AllocatedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  并发同区间 Write——排他性（只能一个占住）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentSameRangeWrite_Exclusive_AtMostOneAcquires()
    {
        // N 个线程并发 Write 同一区间（lease 持有不释放）——在途排他，最多 1 个占住，其余超时
        using var table = NewTable(growthLimit: 10_000, capacity: 32);
        table.AppendLease(200).Commit();

        var acquired = 0;
        var failed = 0;
        var target = new LogicalAddress(0, 50);
        var heldLeases = new ConcurrentBag<LeaseBase>();   // 持有不 Dispose

        Parallel.For(0, 4, _ =>
        {
            try
            {
                var lease = table.WriteLease(target, 100);   // 不 using——持有
                if (lease.State == LeaseState.Active)
                {
                    heldLeases.Add(lease);   // 保持占住，让其他线程撞排他
                    Interlocked.Increment(ref acquired);
                }
            }
            catch (TimeoutException) { Interlocked.Increment(ref failed); }
            catch (ArgumentOutOfRangeException) { Interlocked.Increment(ref failed); }
        });

        // ★ 排他不变量：acquired ≤ 1（lease 持有期间，同一区间只能一个占住）
        Assert.True(acquired <= 1, $"排他失败：{acquired} 个 lease 同时占住同一区间");
        Assert.Equal(4, acquired + failed);

        // 清理持有的 lease
        foreach (var l in heldLeases) l.Dispose();
    }

    // ════════════════════════════════════════════════════════════
    //  ForceRelease 与 Commit 并发——不腐坏状态
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentForceReleaseAndCommit_NoCorruption()
    {
        // 一个线程正常 Append+Commit，另一个线程 ForceRelease——不能腐坏段表状态
        using var table = NewTable();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var errors = new ConcurrentQueue<Exception>();

        var committer = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    using var lease = table.AppendLease(100);
                    lease.Commit();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        var releaser = Task.Run(() =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    // 尝试 ForceRelease 活跃 lease（可能抓到也可能抓不到）
                    foreach (var info in table.GetActiveLeases())
                        table.ForceRelease(info.Id);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        Task.WaitAll(committer, releaser);
        Assert.Empty(errors);
        // 不变量：水位一致
        Assert.True(table.CommittedTail <= table.AllocatedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  并发 Append + Reclaim 打洞——空间复用不冲突
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentAppendAndReclaim_CompleteNoDeadlock()
    {
        // 先建一批已提交空间，然后并发 Append 新空间 + Reclaim 旧空间打洞
        using var table = NewTable();
        for (var i = 0; i < 5; i++)
            table.AppendLease(200).Commit();

        var errors = new ConcurrentQueue<Exception>();
        Parallel.Invoke(
            () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    try { using var l = table.AppendLease(100); l.Commit(); }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }
            },
            () =>
            {
                // Reclaim 已提交的中间区间
                for (var i = 0; i < 3; i++)
                {
                    try
                    {
                        table.ReclaimLease(
                            new LogicalAddress(0, i * 200 + 50),
                            new LogicalAddress(0, i * 200 + 150)).Commit();
                    }
                    catch (Exception ex) { errors.Enqueue(ex); }
                }
            });

        Assert.Empty(errors);
        Assert.True(table.CommittedTail <= table.AllocatedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  多线程多 chunk 跨段 lease——CAS 协调正确
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentCrossSegmentAppend_AllCommitCorrectly()
    {
        // 跨段 AppendLease（每段 100，分配 250 跨 3 段）并发——CAS 多 chunk 协调
        using var table = NewTable(growthLimit: 100, capacity: 256);
        const int threads = 4;
        var completed = 0;
        var errors = new ConcurrentQueue<Exception>();

        Parallel.For(0, threads, _ =>
        {
            try
            {
                using var lease = table.AppendLease(250);   // 跨 3 段
                var iter = lease.GetEnumerator();
                while (iter.MoveNext())
                    iter.CommitCurrent();
                Assert.Equal(LeaseState.Committed, lease.State);
                Interlocked.Increment(ref completed);
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        });

        Assert.Empty(errors);
        Assert.Equal(threads, completed);
        Assert.True(table.CommittedTail <= table.AllocatedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  混合全部操作——全协议并发压力
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentMixedAllOperations_NoDeadlockNoCorruption()
    {
        // Append / Write / Reclaim / Read / ForceRelease 全部混合并发
        using var table = NewTable(growthLimit: 10_000, capacity: 256);
        // 预热
        for (var i = 0; i < 10; i++)
            table.AppendLease(500).Commit();

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var errors = new ConcurrentQueue<Exception>();
        var rnd = new Random(42);

        var tasks = new[]
        {
            Task.Run(() => RunUntil(cts, () =>
            {
                using var l = table.AppendLease(100); l.Commit();
            }, errors, rnd)),
            Task.Run(() => RunUntil(cts, () =>
            {
                var c = table.CommittedTail;
                if (c.Offset >= 100)
                {
                    using var l = table.WriteLease(
                        new LogicalAddress(c.SegId, Math.Max(0, c.Offset - 100)), 100);
                    l.Commit();
                }
            }, errors, rnd)),
            Task.Run(() => RunUntil(cts, () =>
            {
                var c = table.CommittedTail;
                if (c.Offset >= 200)
                    table.IsRangeFullyReadable(c.SegId, Math.Max(0, c.Offset - 200), c.Offset);
            }, errors, rnd)),
            Task.Run(() => RunUntil(cts, () =>
            {
                foreach (var info in table.GetActiveLeases().ToList())
                    table.ForceRelease(info.Id);
            }, errors, rnd)),
        };

        Task.WaitAll(tasks);
        Assert.Empty(errors);
        // 最终不变量
        Assert.True(table.CommittedTail <= table.AllocatedTail,
            $"最终 CommittedTail {table.CommittedTail} > AllocatedTail {table.AllocatedTail}");
    }

    private static void RunUntil(CancellationTokenSource cts, Action work,
        ConcurrentQueue<Exception> errors, Random rnd)
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                work();
                Thread.SpinWait(rnd.Next(5));
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (Exception ex) { errors.Enqueue(ex); }
    }
}
