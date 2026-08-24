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
        foreach (var (id, addr) in book)
        {
            var expected = MakePattern(recordSize, (byte)(id & 0xFF));
            var dst = new byte[recordSize];
            var n = dev.Read(addr, dst);
            n.Should().Be(recordSize, $"记录 {id} @{addr} 读全");
            dst.AsSpan().SequenceEqual(expected).Should().BeTrue($"记录 {id} @{addr} 内容一致");
        }

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

            var pattern = MakePattern(totalLen, 0x5A);
            dev.Append(pattern);

            var torn = new ConcurrentQueue<string>();
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
