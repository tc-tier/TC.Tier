using System.Diagnostics;
using Xunit.Abstractions;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 追赶式持续整理——使用方仿真测试（A8 契约钉死）。
/// <para>★ 仿真结构层（使用方）的确定行为：记录簿（recordId→地址）+ append / Reclaim 打洞删数据 /
///   cursor 追赶 RangeCompact + MigrationMap 应用重指 + 字节级校验。结构层落地前把 IO 引擎的
///   追赶契约在引擎层钉死——绝不等高层使用时踩坑回头调引擎。</para>
/// <para>★ 钉死的契约：① 每轮整理后全部存活数据在新地址逐字节可读；② cursor 模型——已压实前缀
///   永不再迁（地址稳定，WA 1.0×）；③ 整理后 [0, cursor) 无空洞（GetHoleRatio≈0）；④ 存活记录
///   保持逻辑序（顺序读拿到的就是连续地址上的原序数据）；⑤ 被迁移的老地址读 sparse 零；⑥
///   整理期间并发 Append 不受影响且数据完好；⑦ A7 Broken 空洞落在整理窗口内的行为确定。</para>
/// </summary>
[Collection("LargeScaleIO")]
public sealed class ChaseCompactionSimulationTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();
    private const string DeviceName = "chase";
    private const long Growth = 4 * 1024;
    private const int RecordSize = 512;        // 8 条/段
    /// <summary>每轮整理窗口上界（字节）——追赶轮次工作量有界化（mem 写者吞吐失衡修复）。</summary>
    private const int ChaseWindowBytes = 16 * 1024;
    private static readonly TimeSpan CompactTimeout = TimeSpan.FromSeconds(60);
    private readonly ITestOutputHelper _output;
    public ChaseCompactionSimulationTests(ITestOutputHelper output) => _output = output;

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

    /// <summary>记录 payload 只由 recordId 决定（迁移后仍可校验）：头 4B=recordId LE，余下确定性伪随机。</summary>
    private static byte[] PayloadOf(long recordId)
    {
        var buf = new byte[RecordSize];
        BitConverter.TryWriteBytes(buf.AsSpan(0, 4), (int)recordId);
        for (var i = 4; i < RecordSize; i++)
            buf[i] = (byte)((recordId * 131 + i * 7) & 0xFF);
        return buf;
    }

    /// <summary>使用方记录簿——recordId → 当前地址（迁移应用点）。</summary>
    private sealed class RecordBook
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, LogicalAddress> _map = new();
        public int Count { get { lock (_gate) return _map.Count; } }

        public void Put(long recordId, LogicalAddress addr)
        {
            lock (_gate) _map[recordId] = addr;
        }

        public void Remove(long recordId, out LogicalAddress addr)
        {
            lock (_gate) _map.Remove(recordId, out addr);
        }

        public List<(long Id, LogicalAddress Addr)> Snapshot()
        {
            lock (_gate) return _map.Select(kv => (kv.Key, kv.Value)).ToList();
        }

        public void ApplyMigration(IReadOnlyDictionary<LogicalAddress, LogicalAddress?> migration)
        {
            lock (_gate)
            {
                foreach (var key in _map.Keys.ToList())
                {
                    if (migration.TryGetValue(_map[key], out var to) && to.HasValue)
                        _map[key] = to.Value;
                }
            }
        }
    }

    private static byte[] ReadExact(IStorageEngine dev, LogicalAddress addr)
    {
        var buf = new byte[RecordSize];
        var n = dev.Read(addr, buf);
        n.Should().Be(RecordSize, $"addr={addr} 应完整可读（部分读=数据丢失）");
        return buf;
    }

    private static void VerifyAll(IStorageEngine dev, RecordBook book)
    {
        foreach (var (id, addr) in book.Snapshot())
            ReadExact(dev, addr).Should().Equal(PayloadOf(id), $"record#{id} @{addr} 迁移后逐字节一致");
    }

    // ════════════════════════════════════════════════════════════

    /// <summary>契约①②③④⑤：多轮 append→打洞删→追赶整理——数据完好、前缀稳定、空洞闭合、逻辑序保持、老地址读零。</summary>
    [Fact]
    public async Task ChaseRounds_DataIntact_SettledPrefixStable_HolesClosed()
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions( DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false).Builder(vol.Fs, logger: TestConsoleLogger.Instance).Start();
        dev.WaitForReady();

        var book = new RecordBook();
        var nextId = 0L;
        var cursor = new LogicalAddress(0, 0);        // 使用方 cursor——由 NewHighWaterMark 推进
        var migratedAtLeastOnce = false;
        Dictionary<long, LogicalAddress>? settledAfterRound2 = null;

        for (var round = 1; round <= 6; round++)
        {
            // ① 追加一批
            for (var i = 0; i < 32; i++)
            {
                var addr = dev.Append(PayloadOf(nextId));
                book.Put(nextId, addr);
                nextId++;
            }

            // ② 删掉约 1/3 存活记录（中间打洞——使用方删数据的引擎映像）
            foreach (var (id, addr) in book.Snapshot().Where(kv => kv.Id % 3 == round % 3).ToList())
            {
                book.Remove(id, out var hole);
                dev.Reclaim(hole, dev.CalculationAddress(hole, RecordSize));
            }

            // ③ 追赶整理：RangeCompact(cursor, CommittedTail, 存活地址簿) → 应用迁移 → 推进 cursor
            if (round >= 2)
            {
                var live = book.Snapshot().Select(kv => kv.Addr).ToList();
                var liveRecords = book.Snapshot().Select(kv => (kv.Addr, (long)RecordSize)).ToList();
                var result = await dev.StartRangeCompact(cursor, dev.CommittedTail, liveRecords).WaitAsync();
                result.NewHighWaterMark.Should().BeGreaterThan(cursor, "整理后稠密前缀必须推进");
                if (result.MigrationMap.Values.Any(v => v.HasValue)) migratedAtLeastOnce = true;
                book.ApplyMigration(result.MigrationMap);
                cursor = result.NewHighWaterMark;

                // 契约①：每轮整理后立刻字节级全量校验
                VerifyAll(dev, book);

                if (round == 2)
                    settledAfterRound2 = book.Snapshot().ToDictionary(kv => kv.Id, kv => kv.Addr);
            }
        }

        // 契约②（WA 1.0× 的可观测面）：round 2 压实定居的地址，后续轮次永不再变
        migratedAtLeastOnce.Should().BeTrue("场景必须真实触发过迁移（否则测试无效）");
        var final = book.Snapshot().ToDictionary(kv => kv.Id, kv => kv.Addr);
        foreach (var (id, addr) in settledAfterRound2!)
        {
            if (!final.TryGetValue(id, out _)) continue;   // 后续轮删除的除外
            final[id].Should().Be(addr, $"record#{id} 定居地址必须稳定（已压实前缀永不再迁——cursor 模型）");
        }

        // 契约③：最终 [起点, cursor) 稠密无空洞（GetHoleRatio≈0）
        var holeRatio = dev.GetHoleRatio(new LogicalAddress(0, 0), cursor);
        holeRatio.Should().BeLessThan(0.05, $"追赶整理后稠密前缀不应有空洞（实测 {holeRatio:P1}）");

        // 契约④：存活记录保持逻辑序——按地址排序 == 按 recordId（追加序）排序
        var byAddr = book.Snapshot().OrderBy(kv => kv.Addr).Select(kv => kv.Id).ToList();
        byAddr.Should().BeInAscendingOrder("存活记录的逻辑序必须保持（顺序读契约）");

        // 契约⑤：被迁移的老地址读 sparse 零（确定行为——使用方判旧地址失效的依据）
        // ★ 模型修正：cursor 只前进的纯追赶不重整已定居前缀——定居后打的洞留在身后，
        //   使用方策略 = 追赶 + 周期性重置（GetHoleRatio(settled) 超阈值时 cursor 归零重整）。
        var settled = settledAfterRound2.Where(kv => final.ContainsKey(kv.Key)).ToList();
        var movedProbe = settled.FirstOrDefault(kv => final[kv.Key] != kv.Value);
        if (movedProbe.Key > 0 || movedProbe.Value > default(LogicalAddress))
        {
            var old = ReadExact(dev, movedProbe.Value);
            old.Should().OnlyContain(b => b == 0, "被迁移的老地址必须读 sparse 零（非垃圾、非异常）");
        }

        // ★ 周期性重置（模型契约）：定居区的洞（后续轮删除打的）由 cursor 归零重整关闭
        var resetLive = book.Snapshot().Select(kv => kv.Addr).ToList();
        var resetRecords = book.Snapshot().Select(kv => (kv.Addr, (long)RecordSize)).ToList();
        var reset = await dev.StartRangeCompact(new LogicalAddress(0, 0), dev.CommittedTail, resetRecords).WaitAsync();
        book.ApplyMigration(reset.MigrationMap);
        VerifyAll(dev, book);
        dev.GetHoleRatio(new LogicalAddress(0, 0), reset.NewHighWaterMark)
            .Should().BeLessThan(0.05, "重置重整后全程稠密（定居区遗留洞被关闭）");
    }

    /// <summary>契约④强化：整理后顺序读 [起点, cursor) 逐 512B 步进 == 存活记录按追加序的精确拼接。</summary>
    [Fact]
    public async Task AfterChase_SequentialRead_ReturnsExactOrderedConcatenation()
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions( DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false).Builder(vol.Fs, logger: TestConsoleLogger.Instance).Start();
        dev.WaitForReady();

        var book = new RecordBook();
        var nextId = 0L;
        var cursor = new LogicalAddress(0, 0);

        for (var round = 1; round <= 4; round++)
        {
            for (var i = 0; i < 24; i++)
            {
                var addr = dev.Append(PayloadOf(nextId));
                book.Put(nextId, addr);
                nextId++;
            }
            foreach (var (id, _) in book.Snapshot().Where(kv => kv.Id % 4 == round % 4).ToList())
            {
                book.Remove(id, out var hole);
                dev.Reclaim(hole, dev.CalculationAddress(hole, RecordSize));
            }
            if (round >= 2)
            {
                var live = book.Snapshot().Select(kv => kv.Addr).ToList();
                var liveRecords = book.Snapshot().Select(kv => (kv.Addr, (long)RecordSize)).ToList();
                var result = await dev.StartRangeCompact(cursor, dev.CommittedTail, liveRecords).WaitAsync();
                book.ApplyMigration(result.MigrationMap);
                cursor = result.NewHighWaterMark;
            }
        }

        // ★ cursor 重置重整（模型契约）：末轮删除在定居区打的洞由归零重整关闭——
        //   之后 [起点, NewHighWaterMark) 才是"存活数据按逻辑序的精确拼接"
        var reset = await dev.StartRangeCompact(new LogicalAddress(0, 0), dev.CommittedTail,
            book.Snapshot().Select(kv => (kv.Addr, (long)RecordSize)).ToList()).WaitAsync();
        book.ApplyMigration(reset.MigrationMap);

        // 期望：存活记录按地址升序（= 追加序）的 payload 拼接，从 (0,0) 起连续排布
        var ordered = book.Snapshot().OrderBy(kv => kv.Addr).ToList();
        var expected = new List<byte>();
        foreach (var (id, _) in ordered)
            expected.AddRange(PayloadOf(id));

        var actual = new byte[expected.Count];
        var pos = new LogicalAddress(0, 0);
        var read = 0;
        while (read < actual.Length)
        {
            var n = dev.Read(pos, actual.AsSpan(read, RecordSize));
            n.Should().Be(RecordSize, $"顺序读 @{pos} 应完整");
            read += n;
            pos = dev.CalculationAddress(pos, RecordSize);
        }

        VerifyAll(dev, book);   // 簿内全部地址逐字节可读（迁移应用正确性的独立复核）
        actual.Should().Equal(expected.ToArray(), "重置重整后稠密前缀顺序读 = 存活数据按逻辑序的精确拼接（无洞、无乱序）");
    }

    /// <summary>契约⑥：整理进行时并发 Append 不中断、不丢、不坏——使用方模型的并发交织面。</summary>
    [Fact]
    public async Task AppendDuringChaseCompaction_NeverInterrupted_DataIntact()
    {
        var vol = NewVol();
        using var dev = new StorageEngineOptions( DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false).Builder(vol.Fs, logger: TestConsoleLogger.Instance).Start();
        dev.WaitForReady();

        var book = new RecordBook();
        var nextId = 0L;

        // 预铺一批，给整理足够窗口
        for (var i = 0; i < 160; i++)
        {
            var addr = dev.Append(PayloadOf(nextId));
            book.Put(nextId, addr);
            nextId++;
        }
        foreach (var (id, _) in book.Snapshot().Where(kv => kv.Id % 3 == 0).ToList())
        {
            book.Remove(id, out var hole);
            dev.Reclaim(hole, dev.CalculationAddress(hole, RecordSize));
        }

        var stop = 0;
        var writerFailures = new List<Exception>();
        var writer = Task.Run(() =>
        {
            var id = 3000L;
            // ★ 写者预算有界（mem 吞吐失衡修复）：无界写者 + 每轮 VerifyAll 全簿校验 = O(轮×簿) 膨胀。
            //   10 万条（~50MB）足以与 3 轮整理充分交织。
            const long budget = 100_000;
            while (Volatile.Read(ref stop) == 0 && id < 3000L + budget)
            {
                try
                {
                    var addr = dev.Append(PayloadOf(id));
                    book.Put(id, addr);
                    id++;
                }
                catch (Exception ex)
                {
                    // ★ 使用方视角：整理期间追加失败 = 引擎违约（句柄释放竞态等）——记录并让测试红
                    lock (writerFailures) writerFailures.Add(ex);
                    break;
                }
            }
        });

        var cursor = new LogicalAddress(0, 0);
        // ★ 只统计真正执行整理的轮次（2026-08-20）：发布竞态窗口（CommittedTail 已推进、book.Put 未落）
        //   下 bookEnd == cursor——LockWord 读优先下整理排他被读者拖慢从未打中；SpinRWLock 写偏向让
        //   排他准时落地、轮次变快后窗口暴露。空区间是调用方视角合法状态（引擎 from&lt;to 前置检查正确），
        //   跳过不计轮——但必须等写者推进再整理（continue 空转会让 3 轮秒过，写者攒不够 160 条）。
        var doneRounds = 0;
        var deadline = Stopwatch.StartNew();
        while (doneRounds < 3)
        {
            // ★ 写者异常先浮出——若写者已死（引擎违约），先报写者真因，不报下游 from==to 伪症状
            lock (writerFailures) writerFailures.Should().BeEmpty(
                "整理期间的并发 Append 不得失败（引擎违约——需回引擎修，不在使用方兜底）");
            if (dev.CommittedTail <= cursor
                || dev.CalculationAddress(book.Snapshot().Max(kv => kv.Addr), RecordSize) <= cursor)
            {
                deadline.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60),
                    "写者持续追加下 bookEnd 应推进到 cursor 之上——整理轮次不应无限空转");
                Thread.Sleep(10);   // 发布竞态窗口——等写者 book.Put 落地再快照
                continue;
            }
            // ★ 调用方纪律（§XVIII 契约）：to 不得超过记录簿快照覆盖点——快照后完成、窗口内的
            //   Append 会因"未申报=洞"被丢；窗口尾（bookEnd..CommittedTail）留给物理保守保留。
            var snapshot = book.Snapshot();
            snapshot.Should().NotBeEmpty();
            var bookEnd = dev.CalculationAddress(snapshot.Max(kv => kv.Addr), RecordSize);
            // ★ 追赶窗口有界（2026-08-20 mem 介质化修复）：mem 写者吞吐远高于整理（零 syscall 追加），
            //   无界窗口下每轮整理范围随写者无限膨胀——3 轮永不收敛（活锁螺旋，~2 核持续燃烧）。
            //   每轮只整理 [cursor, min(bookEnd, cursor+ChaseWindowBytes))，窗口内 live 记录全量申报——
            //   并发交织契约不变（写者照常无界追加），轮次工作量有界可收敛。
            var windowEnd = dev.CalculationAddress(cursor, ChaseWindowBytes);
            if (bookEnd < windowEnd) windowEnd = bookEnd;
            var liveRecords = snapshot
                .Where(kv => kv.Addr >= cursor && kv.Addr < windowEnd)
                .Select(kv => (kv.Addr, (long)RecordSize)).ToList();
            var result = await dev.StartRangeCompact(cursor, windowEnd, liveRecords).WaitAsync();
            book.ApplyMigration(result.MigrationMap);
            cursor = result.NewHighWaterMark;
            VerifyAll(dev, book);   // 每轮整理后：含并发写者刚写的数据全部字节级可读
            doneRounds++;
        }

        Volatile.Write(ref stop, 1);
        await writer;
        writerFailures.Should().BeEmpty("整理期间的并发 Append 不得失败（引擎违约——需回引擎修，不在使用方兜底）");
        book.Count.Should().BeGreaterThan(160, "并发写者的数据必须全部在簿");
        VerifyAll(dev, book);
    }

    /// <summary>契约⑦：A7 Broken 空洞落在整理窗口内——行为必须确定（当前引擎行为探针，见断言）。</summary>
    [Fact]
    public async Task BrokenHoleInsideChaseWindow_BehaviorIsDeterministic()
    {
        var vol = NewVol();
        var seg2RelPath = $"{DeviceName}/{DeviceName}.2";   // ★ 注入用相对路径
        vol.Fs.CreateDirectory(seg2RelPath);   // 注入：seg2 建段必失败

        using var builder = new StorageEngineOptions( DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false).Builder(vol.Fs, logger: TestConsoleLogger.Instance);
        using var dev = builder.Start();
        builder.Engine.SuppressSegmentPoolForLifecycle();
        dev.WaitForReady();

        var book = new RecordBook();
        var nextId = 0L;
        var failures = 0;
        for (var i = 0; i < 48; i++)           // 48×512B=24KB ≈ 6 段，必然跨过 seg2
        {
            try
            {
                var addr = dev.Append(PayloadOf(nextId));
                book.Put(nextId, addr);
                nextId++;
            }
            catch (SegmentCreationException) { failures++; }
        }
        failures.Should().BeLessThanOrEqualTo(1, "A7：跨 Broken 段恰好一次快失败");
        book.Snapshot().Should().OnlyContain(kv => kv.Addr.SegId != 2, "地址永不落 Broken 段");
        vol.Fs.DeleteDirectory(seg2RelPath);   // 清注入——模拟失败清理已删文件的终态

        // 追赶整理窗口 [0, CommittedTail) 含 Broken seg2（表内存在、无文件、零数据）——行为探针：
        // 当前引擎不识别 Broken 段，将在源句柄打开处抛 IO 异常。使用方契约 = 按 Broken 分片整理
        // （窗口不得跨 Broken）——此断言钉住现状，engine fail-fast 改进另案。
        var live = book.Snapshot().Select(kv => kv.Addr).ToList();
        Func<Task> act = async () => await dev.StartRangeCompact(new LogicalAddress(0, 0), dev.CommittedTail, live).WaitAsync();
        await act.Should().ThrowAsync<Exception>("窗口含 Broken 段时引擎必须给出确定失败（当前为源句柄 IO 异常）");

        // 使用方正确姿势：按连续段区间分片——[0, seg2) 与 (seg2, tail) 各自整理，全部数据完好
        var cursor = new LogicalAddress(0, 0);
        var seg2Start = new LogicalAddress(2, 0);
        var r1 = await dev.StartRangeCompact(cursor, seg2Start, book.Snapshot()
            .Where(kv => kv.Addr < seg2Start)
            .Select(kv => (kv.Addr, (long)RecordSize)).ToList()).WaitAsync();
        book.ApplyMigration(r1.MigrationMap);

        var seg3Start = new LogicalAddress(3, 0);
        var r2 = await dev.StartRangeCompact(seg3Start, dev.CommittedTail, book.Snapshot()
            .Where(kv => kv.Addr >= seg3Start)
            .Select(kv => (kv.Addr, (long)RecordSize)).ToList()).WaitAsync();
        book.ApplyMigration(r2.MigrationMap);

        VerifyAll(dev, book);
    }
}
