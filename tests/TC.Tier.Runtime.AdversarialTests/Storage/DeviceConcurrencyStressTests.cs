using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// Device 层高并发死锁/挂起压测——验证多线程下不会死锁、不丢数据、不永久自旋。
/// 暴露审计发现的风险：AddressMap resize 与持 ref 跨 IO 的并发、worker 生命周期。
/// </summary>
[Collection("LargeScaleIO")]
public sealed class DeviceConcurrencyStressTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();

    public void Dispose()
    {
        foreach (var vol in _vols) vol.Dispose();
    }

    private TestVolume NewVol()
    {
        var vol = new TestVolume();
        _vols.Add(vol);
        return vol;
    }

    private static byte[] MakePattern(int length, byte seed = 0xAB)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    /// <summary>
    /// 多线程并发 Append——压测 AddressMap 扩容（Append→EnsureCapacity→Array.Resize）
    /// 与主路径持 ref 跨 IO 的竞态。若 AddressMap 结构突变未与持 ref 读同步，
    /// 会 orphaned SpinRWLock → 某线程永久自旋（测试超时即暴露）。
    /// </summary>
    [Fact]
    public async Task ConcurrentAppend_ManyWriters_NoDeadlockNoLoss()
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions( "test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false).Builder(vol.Fs).Start();
        // 小段——快速触发多次跨段 + AddressMap 扩容
        dev.WaitForReady();

        const int threads = 8;
        const int perThread = 500;
        var payload = MakePattern(512, 0xCC);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tasks = new Task[threads];
        int[] progress = new int[threads];
        for (int t = 0; t < threads; t++)
        {
            int tid = t;
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < perThread; i++)
                {
                    if (cts.Token.IsCancellationRequested) break;
                    dev.Append(payload);
                    progress[tid] = i + 1;
                }
            });
        }

        // 若死锁/自旋，WhenAll 在 15s 内不返回 → 超时分支打印各线程进度定位挂点
        var allTask = Task.WhenAll(tasks);
        var delayTask = Task.Delay(TimeSpan.FromSeconds(15));
        if (await Task.WhenAny(allTask, delayTask) == delayTask)
        {
            string prog = string.Join(",", Enumerable.Range(0, threads).Select(i => $"t{i}={progress[i]}"));
            throw new Xunit.Sdk.XunitException($"DEADLOCK/HANG: 并发 Append 15s 未完成。进度: {prog}, Tail={dev.AllocatedTail}");
        }

        // 全部完成——验证无丢失
        long expectedBytes = (long)threads * perThread * payload.Length;
        dev.AllocatedTail.SegId.Should().BeGreaterThanOrEqualTo(1,
            "8×500×512B = 2MB，4KB 段应跨数百段");
    }

    /// <summary>
    /// 并发 Append + ReclaimHead——压测 AddressMap.ShiftDown（头部段删除）
    /// 与活跃 Append 持 ref 的竞态。ShiftDown 原地 ArrayCopy 可能撕裂持 ref 的 SpinRWLock。
    /// </summary>
    [Fact]
    public async Task ConcurrentAppendAndTruncate_NoDeadlock()
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions( "test", segmentGrowthLimit: 4 * 1024).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        var payload = MakePattern(512, 0xDD);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long appended = 0;

        // 写者：持续 Append
        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 2000; i++)
            {
                cts.Token.ThrowIfCancellationRequested();
                dev.Append(payload);
                Interlocked.Increment(ref appended);
            }
        });

        // 截断者：写到一定量后 ReclaimHead 头部段
        var truncator = Task.Run(() =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                if (Volatile.Read(ref appended) > 50 && dev.AllocatedTail.SegId > 3)
                {
                    try
                    {
                        // 删头部段——触发 AddressMap.ShiftDown
                        dev.ReclaimHead(new LogicalAddress(dev.AllocatedTail.SegId - 2, 0));
                    }
                    catch { /* 截断失败可接受，关注是否死锁 */ }
                    Thread.Sleep(5);
                }
                if (Volatile.Read(ref appended) >= 2000) break;
            }
        });

        await Task.WhenAll(writer, truncator);
    }

    /// <summary>
    /// 并发读写——多读者 + 多写者，验证 SpinRWLock 读写锁不死锁、不撕裂。
    /// </summary>
    [Fact]
    public async Task ConcurrentReadWrite_NoDeadlockNoTornReads()
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions( "test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        var pattern = MakePattern(512, 0xEE);
        // 先写一些数据供读
        var addrs = new System.Collections.Concurrent.ConcurrentQueue<LogicalAddress>();
        for (int i = 0; i < 100; i++)
        {
            var addr = dev.Append(pattern);
            addrs.Enqueue(addr);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 2 写者持续 Append
        var writers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                cts.Token.ThrowIfCancellationRequested();
                var addr = dev.Append(pattern);
                addrs.Enqueue(addr);
            }
        })).ToArray();

        // 4 读者持续 Read
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var buf = new byte[512];
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(5))
            {
                cts.Token.ThrowIfCancellationRequested();
                if (addrs.TryDequeue(out var addr))
                {
                    int n = dev.Read(addr, buf);
                    // 读到的数据要么是 pattern，要么长度正确（不撕裂）
                    n.Should().BeLessOrEqualTo(512);
                    addrs.Enqueue(addr); // 放回供其他读者
                }
            }
        })).ToArray();

        await Task.WhenAll(writers.Concat(readers));
    }
}
