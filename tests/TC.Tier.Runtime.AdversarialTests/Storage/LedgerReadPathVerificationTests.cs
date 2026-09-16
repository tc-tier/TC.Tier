using System.Collections.Concurrent;
using System.Diagnostics;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 台账 L18/L19 销案验证压测（）——读路径一致性批次 A 的对抗性复现面。
/// <list type="bullet">
/// <item>L19：Compact lease 尾段全覆盖后，贴边追加（append 起点 == 整理窗口尾 == CommittedTail）
///   与 RangeCompact 高强度交织——不失败、不丢、逐字节可读（旧实现"贴边不交叠"窗口静默丢写/换段 no-op）。</item>
/// <item>L18：异步 DirtyRead 与 Reclaim 打洞并发——单次读内打洞区间要么全旧要么全零，
///   绝不混帧（旧实现异步读无 epoch，punch 撕裂）。</item>
/// </list>
/// </summary>
public sealed class LedgerReadPathVerificationTests : IDisposable
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

    private static byte[] MakePattern(int length, byte seed)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++) buf[i] = (byte)((seed + i) & 0xFF);
        return buf;
    }

    // ═══════════════════════════════════════════════════════════════
    //  L19：贴边追加 × RangeCompact——不失败不丢（旧实现 P0 静默丢写）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>★ 全量 Compact 与尾写并行契约（2026-08-29 活跃尾段语义销案配套）：
    /// 全量 StartCompact 期间并发 append 不被阻塞（旧 lease 扩全段形态阻塞 ~118ms/条）、
    /// 不失败、数据逐字节完好（活跃尾段不 rename——原地搬移 + 源区打洞，写者句柄恒有效）。</summary>
    [Fact]
    public async Task FullCompact_ParallelAppend_NeverBlocked_NeverLost()
    {
        const long Growth = 64 * 1024;
        var vol = NewVol();
        using var dev = new StorageEngineOptions("full-parallel", segmentGrowthLimit: Growth).WithPreallocateFile(false).Builder(vol.Fs).Start();
        dev.WaitForReady();

        var buf = new byte[512];
        for (var i = 0; i < 20000; i++) dev.Append(buf);

        var book = new ConcurrentDictionary<long, LogicalAddress>();
        var failures = new List<Exception>();
        var op = dev.StartCompact();

        var sw = Stopwatch.StartNew();
        var id = 0L;
        var maxMs = 0d;
        while (!op.IsCompleted)
        {
            var t0 = Stopwatch.GetTimestamp();
            try
            {
                var payload = MakePattern(512, (byte)(++id & 0xFF));
                book[id] = dev.Append(payload);
            }
            catch (Exception ex) { failures.Add(ex); break; }
            maxMs = Math.Max(maxMs, Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) break;
        }
        await op.WaitAsync();

        failures.Should().BeEmpty($"append 失败：{string.Join("; ", failures.Select(f => f.GetType().Name + ": " + f.Message))}");
        maxMs.Should().BeLessThan(50, $"DIAG appends={id} maxAppendMs={maxMs:F2}（并行期望 < 50ms，阻塞形态 ~118ms）");
        book.Should().NotBeEmpty("compact 期间必须发生并发 append（否则契约无效）");

        // 终局全簿逐字节校验（活跃尾段原地搬移——并发写数据不得丢）
        foreach (var (rid, addr) in book)
        {
            var dst = new byte[512];
            var n = dev.Read(addr, dst);
            if (n != 512 || !dst.AsSpan().SequenceEqual(MakePattern(512, (byte)(rid & 0xFF))))
                throw new InvalidOperationException(
                    $"并发 append 记录 {rid} @{addr} 内容不一致 read={n} head={Convert.ToHexString(dst.AsSpan(0, 8))}");
        }
    }

    /// <summary>
    /// ★ L19 最小确定性回归（2026-08-29 销案配套）：写者与单窗口 Compact 并发——旧实现
    /// 清池→lease 两步之间写者开旧 inode 句柄（promote rename 后写孤儿 inode 静默丢写，
    /// 坏区 = 入口清池后写者句柄覆盖的整段尾区，8/8 复现）。修复 = 活跃尾段不 rename
    /// （原地搬移 + 源区打洞）+ lease 上界钳水位线（写者并行）。
    /// </summary>
    [Fact]
    public async Task CompactConcurrentAppend_SingleWindow_NeverLost()
    {
        const int recordSize = 512;
        var vol = NewVol();
        var options = new StorageEngineOptions("l19-min", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var book = new ConcurrentDictionary<long, LogicalAddress>();
        var failures = new ConcurrentQueue<Exception>();
        var stop = 0;
        var writer = Task.Run(() =>
        {
            long id = 0;
            while (Volatile.Read(ref stop) == 0)
            {
                try
                {
                    var i = Interlocked.Increment(ref id);
                    book[i] = dev.Append(MakePattern(recordSize, (byte)(i & 0xFF)));
                    Thread.Sleep(1);
                }
                catch (Exception ex) { failures.Enqueue(ex); break; }
            }
        });

        while (book.IsEmpty) await Task.Yield();
        var live = new List<(LogicalAddress Start, long Length)> { (book[1], recordSize) };
        await dev.StartRangeCompact(new LogicalAddress(0, 0), new LogicalAddress(0, 0x200), live).WaitAsync();

        await Task.Delay(200);
        Volatile.Write(ref stop, 1);
        await writer;

        failures.Should().BeEmpty();
        var bads = new List<string>();
        foreach (var (id, addr) in book)
        {
            var expected = MakePattern(recordSize, (byte)(id & 0xFF));
            var dst = new byte[recordSize];
            var n = dev.Read(addr, dst);
            if (n != recordSize || !dst.AsSpan().SequenceEqual(expected))
                bads.Add($"id={id} @{addr} read={n} head={Convert.ToHexString(dst.AsSpan(0, 8))} exp={Convert.ToHexString(expected.AsSpan(0, 8))}");
        }
        bads.Should().BeEmpty($"并发单窗口（{book.Count} 条）：{string.Join(" | ", bads)}");
    }

    /// <summary>
    /// 写者以 1ms/条节流持续追加（bookEnd 紧贴 CommittedTail——贴边形态高频出现），
    /// 整理者反复把窗口尾顶到 bookEnd ≈ CommittedTail；终局全簿逐字节校验 + 稠密前缀顺序读。
    /// 旧实现（lease 钳 CommittedTail）：追加起点 == lease 终点判无重叠放行 →
    /// rename 旧 inode 丢写 / 换段后 CompleteAndMerge 静默 no-op。
    /// </summary>
    [Fact]
    public async Task CompactTailEdge_AppendNeverFails_NeverLost()
    {
        const int segGrowth = 64 * 1024;      // 小段——尾段贴边窗口高频
        const int recordSize = 512;
        var vol = NewVol();
        var options = new StorageEngineOptions("l19", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        var book = new ConcurrentDictionary<long, LogicalAddress>();
        var failures = new ConcurrentQueue<Exception>();
        long nextId = 0;

        var stop = 0;
        var rounds = 0;
        var writer = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                try
                {
                    var id = Interlocked.Increment(ref nextId);
                    var addr = dev.Append(MakePattern(recordSize, (byte)(id & 0xFF)));
                    book[id] = addr;
                    Thread.Sleep(1);   // 节流：bookEnd 紧贴 CommittedTail，贴边窗口高频
                }
                catch (Exception ex)
                {
                    ex.Data["round"] = Volatile.Read(ref rounds);
                    ex.Data["committedTail"] = dev.CommittedTail.ToString();
                    ex.Data["allocatedTail"] = dev.AllocatedTail.ToString();
                    failures.Enqueue(ex);
                    break;
                }
            }
        });

        void ApplyMigration(IReadOnlyDictionary<LogicalAddress, LogicalAddress?> map,
            KeyValuePair<long, LogicalAddress>[] snapshot)
        {
            var byOld = new Dictionary<LogicalAddress, long>();
            foreach (var kv in snapshot) byOld[kv.Value] = kv.Key;
            foreach (var (old, newAddr) in map)
            {
                if (newAddr is not { } na || !byOld.TryGetValue(old, out var id)) continue;
                book[id] = na;
            }
        }

        var cursor = new LogicalAddress(0, 0);
        var deadline = Stopwatch.StartNew();
        try
        {
            while (rounds < 6 && deadline.Elapsed < TimeSpan.FromSeconds(90))
            {
                if (book.IsEmpty)
                {
                    Thread.Sleep(1);
                    continue;
                }
                var snapshot = book.ToArray();
                var bookEnd = dev.CalculationAddress(snapshot.Max(kv => kv.Value), recordSize);
                if (dev.CommittedTail <= cursor || bookEnd <= cursor)
                {
                    Thread.Sleep(1);
                    continue;
                }
                // 窗口直接顶到 bookEnd（≈ CommittedTail 的贴边形态）——旧实现竞态窗口的靶心
                var live = snapshot
                    .Where(kv => kv.Value >= cursor && kv.Value < bookEnd)
                    .Select(kv => (kv.Value, (long)recordSize))
                    .ToList();
                var result = await dev.StartRangeCompact(cursor, bookEnd, live).WaitAsync();
                ApplyMigration(result.MigrationMap, snapshot);
                cursor = result.NewHighWaterMark;
                rounds++;
            }
        }
        finally
        {
            Volatile.Write(ref stop, 1);
        }

        await writer.WaitAsync(TimeSpan.FromSeconds(30));
        failures.Should().BeEmpty("贴边追加在整理期间不得失败（引擎违约）");

        // 终局全簿校验：每条记录逐字节可读且内容正确（静默丢写检测点）
        var bad = new List<(long Id, LogicalAddress Addr, string Read, string Exp)>();
        foreach (var (id, addr) in book)
        {
            var expected = MakePattern(recordSize, (byte)(id & 0xFF));
            var dst = new byte[recordSize];
            var n = dev.Read(addr, dst);
            if (n != recordSize || !dst.AsSpan().SequenceEqual(expected))
            {
                var firstBad = Array.FindIndex(dst, b => b != expected[0]);
                bad.Add((id, addr,
                    $"read={n} firstDiff@{(firstBad < 0 ? -1 : firstBad)}={Convert.ToHexString(dst.AsSpan(0, Math.Min(16, dst.Length)))}",
                    $"exp={Convert.ToHexString(expected.AsSpan(0, 16))}"));
            }
        }
        bad.Should().BeEmpty($"全簿逐字节校验——静默丢写：{bad.Count}/{book.Count} 坏。前 10 条：\n" +
            string.Join("\n", bad.Take(10).Select(b => $"  id={b.Id} @{b.Addr} {b.Read} {b.Exp}")) +
            $"\ncommittedTail={dev.CommittedTail} allocatedTail={dev.AllocatedTail}");

        // 重置整理后稠密前缀顺序读 = 全部记录按地址序精确拼接（无洞、无乱序）
        var ordered = book.OrderBy(kv => kv.Value).ToList();
        var expectedBytes = new List<byte>();
        foreach (var (id, _) in ordered)
            expectedBytes.AddRange(MakePattern(recordSize, (byte)(id & 0xFF)));
        var finalSnapshot = book.ToArray();
        var reset = await dev.StartRangeCompact(new LogicalAddress(0, 0),
            dev.CalculationAddress(ordered[^1].Value, recordSize),
            ordered.Select(kv => (kv.Value, (long)recordSize)).ToList()).WaitAsync();
        ApplyMigration(reset.MigrationMap, finalSnapshot);

        var actual = new byte[expectedBytes.Count];
        var pos = new LogicalAddress(0, 0);
        var read = 0;
        while (read < actual.Length)
        {
            var n = dev.Read(pos, actual.AsSpan(read, recordSize));
            n.Should().Be(recordSize, $"顺序读 @{pos} 应完整");
            read += n;
            pos = dev.CalculationAddress(pos, recordSize);
        }
        actual.Should().Equal(expectedBytes.ToArray(), "重置整理后稠密前缀 = 全部存活记录精确拼接");
    }

    // ═══════════════════════════════════════════════════════════════
    //  L18：异步 DirtyRead × Reclaim 打洞——单读不混帧
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 异步读（引擎 ReadAsync 与顺序读句柄 DirtyRead 异步两路径）× 中段打洞并发：
    /// 单次读内打洞区间必须全旧或全零（旧实现：异步路径无 epoch，punch 与读交错 → 撕裂混帧）。
    /// 每轮断言：任一读完成值中，打洞区间字节要么恒等旧模式、要么恒 0——出现混帧即红。
    /// </summary>
    [Fact]
    public async Task AsyncDirtyRead_vs_PunchHole_NeverTorn()
    {
        const int segGrowth = 16 * 1024 * 1024;
        const int totalLen = 8 * 1024 * 1024;
        const int punchOff = 2 * 1024 * 1024;
        const int punchLen = 4 * 1024 * 1024;

        for (var round = 0; round < 8; round++)
        {
            var vol = NewVol();
            var options = new StorageEngineOptions("l18", segmentGrowthLimit: segGrowth).WithPreallocateFile(false);
            using var dev = options.Builder(vol.Fs).Start();
            dev.WaitForReady();

            byte[] pattern = MakePattern(totalLen, 0x5A);   // ★ 显式 byte[]（非 var）——闭包捕获经流分析后 var 推断为可空，致 CS8602
            dev.Append(pattern);

            ConcurrentQueue<string> torn = new();   // ★ 显式类型（非 var）——同 pattern：闭包捕获经流分析推断为可空，致 CS8602
            var stop = 0;

            void CheckFrame(ReadOnlySpan<byte> buf)
            {
                // 打洞区间内的字节：要么全旧模式、要么全 0——混帧 = 撕裂。
                // ★ 模式自身含合法 0 字节（0x5A+i 回绕）——按"全旧/全零"整区间判定，不按逐字节分类。
                var allOld = true;
                var allZero = true;
                for (var i = punchOff; i < punchOff + punchLen && i < buf.Length; i++)
                {
                    if (buf[i] != pattern[i]) allOld = false;
                    if (buf[i] != 0) allZero = false;
                    if (buf[i] != pattern[i] && buf[i] != 0)
                        torn.Enqueue($"非旧非零字节 @{i}: {buf[i]}");
                }
                if (!allOld && !allZero)
                    torn.Enqueue("单次读内打洞区间混帧（部分旧 + 部分零）——撕裂读实锤");
            }

            async Task ReadLoopAsync(bool useSequential)
            {
                if (useSequential)
                {
                    using var reader = dev.OpenSequentialReader(new LogicalAddress(0, 0),
                        new LogicalAddress(0, totalLen), ReadDirection.Forward, usePageCache: true,
                        SnapshotMode.DirtyRead);
                    while (Volatile.Read(ref stop) == 0)
                    {
                        var buf = new byte[512 * 1024];
                        var n = await reader.ReadAsync(buf, CancellationToken.None);
                        if (n == 0) break;
                        CheckFrame(buf.AsSpan(0, n));
                    }
                }
                else
                {
                    while (Volatile.Read(ref stop) == 0)
                    {
                        var buf = new byte[totalLen];
                        var n = await dev.ReadAsync(new LogicalAddress(0, 0), buf, CancellationToken.None);
                        if (n > 0) CheckFrame(buf.AsSpan(0, n));
                    }
                }
            }

            var readerA = Task.Run(() => ReadLoopAsync(useSequential: false));
            var readerB = Task.Run(() => ReadLoopAsync(useSequential: true));

            // 打洞者：读者跑起来后连续多次打洞（拉长总打洞窗口——撕裂概率提升），
            // 每次打洞换一个子区间（覆盖读-打-读多轮交错）。
            for (var i = 0; i < 200; i++) await Task.Yield();
            for (var punch = 0; punch < 16 && Volatile.Read(ref stop) == 0; punch++)
            {
                var off = punchOff + punch * 64 * 1024;
                dev.Reclaim(new LogicalAddress(0, off), new LogicalAddress(0, off + punchLen));
                for (var i = 0; i < 40; i++) await Task.Yield();
            }
            for (var i = 0; i < 200; i++) await Task.Yield();
            Volatile.Write(ref stop, 1);
            await Task.WhenAll(readerA, readerB);

            torn.Should().BeEmpty($"round {round}: 异步读与打洞并发不得撕裂");

            // 打洞后同步读 = 全零（打洞契约落地的静态复核）
            var verify = new byte[punchLen];
            dev.Read(new LogicalAddress(0, punchOff), verify);
            verify.SequenceEqual(new byte[punchLen]).Should().BeTrue($"round {round}: 打洞区间读零");
        }
    }
}
