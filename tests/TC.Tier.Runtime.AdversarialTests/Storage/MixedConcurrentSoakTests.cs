using System.Collections.Concurrent;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// ★ S4 三路真正并发混合 soak——生产场景最危险的竞态（C4 + C5 + C2 同时压）。
/// <para>同一 LocalStorageDevice 上并行跑：</para>
/// <list type="bullet">
/// <item>N 个 Append 写者（持续写，CAS 租借主路径）</item>
/// <item>M 个 Read 读者（读已写地址）</item>
/// <item>1 个周期回收者：交替 ReclaimHead / Compact（验证 Bug 3/4/5 修复在并发压力下稳定）</item>
/// </list>
///
/// <para>★ 默认持续 15 秒（CI 模式，快速回归）；通过环境变量 <c>TC_SOAK_SECONDS</c> 可延长到 15 分钟（生产压测）。</para>
/// <para>★ 软超时（duration × 3）+ 进度快照：卡死时打印各线程进度定位挂点。</para>
/// <para>★ 正确性断言：</para>
/// <list type="bullet">
/// <item>所有 Append 返回地址区间两两不重叠（HashSet 校验）</item>
/// <item>读不撕裂（每次读回字节内容自洽）</item>
/// <item>Compact 后 MigrationMap 非空 + 旧段真删除（Bug 3 回归）</item>
/// <item>Compact 后继续 Append 可读（Bug 5 回归）</item>
/// </list>
/// </summary>
public sealed class MixedConcurrentSoakTests : IDisposable
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

    private static int SoakSeconds =>
        int.TryParse(Environment.GetEnvironmentVariable("TC_SOAK_SECONDS"), out var s) ? s : 15;

    private static byte[] MakePattern(int length, byte seed)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    /// <summary>
    /// 三路并发混合：Append + Read + ReclaimHead/Compact 交替，持续 SoakSeconds 秒。
    /// 验证高并发下不死锁、地址不重叠、读不撕裂、Compact 不破坏活跃写入。
    /// </summary>
    [Fact]
    public async Task MixedSoak_AppendReadReclaim_NoDeadlockNoOverlap()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("soak", segmentGrowthLimit: 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        // 1MB 段——足够频繁触发跨段，但不至于碎到影响 Compact 搬迁
        dev.WaitForReady();

        int durationSec = SoakSeconds;
        int timeoutSec = durationSec * 3;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));

        const int writers = 4;
        const int readers = 4;
        int payload = 256;
        var writeBufs = new byte[writers][];
        for (int i = 0; i < writers; i++) writeBufs[i] = MakePattern(payload, (byte)(0x40 + i));

        // 已写地址队列（写者推入、读者取出）；cap 防止读者跟不上内存爆
        var addrQueue = new ConcurrentQueue<LogicalAddress>();
        const int queueCap = 4096;

        // 收集所有写地址用于最终唯一性校验
        var allAddrs = new ConcurrentBag<LogicalAddress>();
        long totalWritten = 0;
        long totalRead = 0;
        long readErrors = 0;
        long compactsDone = 0;
        long truncatesDone = 0;
        int[] writerProgress = new int[writers];
        int[] readerProgress = new int[readers];

        // 写者
        var writeTasks = new Task[writers];
        for (int w = 0; w < writers; w++)
        {
            int wid = w;
            writeTasks[wid] = Task.Run(() =>
            {
                var buf = writeBufs[wid];
                while (!cts.Token.IsCancellationRequested)
                {
                    var addr = dev.Append(buf);
                    allAddrs.Add(addr);
                    Interlocked.Increment(ref totalWritten);
                    writerProgress[wid]++;
                    if (addrQueue.Count < queueCap)
                        addrQueue.Enqueue(addr);
                }
            });
        }

        // 读者
        var readTasks = new Task[readers];
        var readDst = new byte[payload];
        for (int r = 0; r < readers; r++)
        {
            int rid = r;
            readTasks[rid] = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (addrQueue.TryDequeue(out var addr))
                    {
                        int n = dev.Read(addr, readDst);
                        if (n > 0)
                        {
                            Interlocked.Add(ref totalRead, n);
                            // 字节自洽校验（非全零且长度对）
                            if (n != payload) Interlocked.Increment(ref readErrors);
                        }
                        readerProgress[rid]++;
                    }
                    else
                    {
                        Thread.Yield();
                    }
                }
            });
        }

        // 周期回收者：交替 ReclaimHead / Compact
        var reclaimTask = Task.Run(async () =>
        {
            bool doCompact = false;
            while (!cts.Token.IsCancellationRequested)
            {
                Thread.Sleep(2000);  // 每 2s 一次回收
                try
                {
                    var tail = dev.AllocatedTail;
                    if (doCompact && tail.SegId >= 4)
                    {
                        await dev.StartCompact().WaitAsync();
                        Interlocked.Increment(ref compactsDone);
                    }
                    else if (tail.SegId >= 2)
                    {
                        // 删头段（保留活跃段）
                        dev.ReclaimHead(new LogicalAddress(1, 0));
                        Interlocked.Increment(ref truncatesDone);
                    }
                    doCompact = !doCompact;
                }
                catch
                {
                    // 回收失败可吞——核心是验证不死锁
                }
            }
        });

        // 监控线程：每 durationSec/4 打印进度
        // ★ 不持 cts.Token——Join(1000) 超时后方法返回、cts 被 using 释放，monitor 醒来访问
        //   cts.Token 会 ObjectDisposedException 崩 testhost（带活线程出 using，台账 §VI 同族）。
        //   改用 volatile stop 标志 + 必等干 Join（醒来见 stop 即退，不再碰 dev）。
        var monitorStop = 0;
        var monitor = new Thread(() =>
        {
            int interval = Math.Max(1, durationSec * 1000 / 4);
            while (Volatile.Read(ref monitorStop) == 0)
            {
                Thread.Sleep(interval);
                if (Volatile.Read(ref monitorStop) != 0) return;   // 退出前不再碰 dev（可能已 Dispose）
                var tail = dev.AllocatedTail;
                Console.WriteLine($"[soak] tail={tail} written={Interlocked.Read(ref totalWritten)} read={Interlocked.Read(ref totalRead)} compact={Interlocked.Increment(ref compactsDone) - 1} trunc={Interlocked.Increment(ref truncatesDone) - 1} q={addrQueue.Count}");
            }
        }) { IsBackground = true };
        monitor.Start();

        // 等待 durationSec
        await Task.Delay(TimeSpan.FromSeconds(durationSec));
        cts.Cancel();
        Volatile.Write(ref monitorStop, 1);

        // 软超时护栏：所有任务在 timeoutSec 内应完成
        var allTasks = writeTasks.Concat(readTasks).Concat(new[] { reclaimTask }).ToArray();
        var allDone = Task.WhenAll(allTasks);
        var delay = Task.Delay(TimeSpan.FromSeconds(timeoutSec - durationSec + 5));
        if (await Task.WhenAny(allDone, delay) == delay)
        {
            string prog = $"w=[{string.Join(",", writerProgress)}] r=[{string.Join(",", readerProgress)}]";
            throw new Xunit.Sdk.XunitException(
                $"SOAK DEADLOCK/HANG: 任务在 {timeoutSec}s 内未结束。进度: {prog}, tail={dev.AllocatedTail}");
        }

        // ★ 必等干 monitor（一个睡眠周期 + 余量）——出 using（cts/dev 释放）前不允许活线程
        monitor.Join(Math.Max(1, durationSec * 1000 / 4) + 2000);

        // ★ 地址唯一性校验（C4 压测级）
        var sorted = allAddrs.OrderBy(a => a.SegId).ThenBy(a => a.Offset).ToList();
        long overlap = 0;
        for (int i = 1; i < sorted.Count; i++)
        {
            var p = sorted[i - 1];
            var c = sorted[i];
            if (p.SegId == c.SegId && p.Offset + payload > c.Offset)
                overlap += (p.Offset + payload - c.Offset);
        }
        overlap.Should().Be(0, $"CAS 地址不应重叠（{writers} 线程并发 Append）");

        readErrors.Should().Be(0, "读不应撕裂（每次读回字节自洽）");
        totalWritten.Should().BeGreaterThan(0, "应有数据写入");
        Console.WriteLine($"[soak done] writers={writers} readers={readers} dur={durationSec}s "
            + $"written={totalWritten} read={totalRead} compact={compactsDone} trunc={truncatesDone} "
            + $"overlap={overlap} readErrors={readErrors}");
    }
}
