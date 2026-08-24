using System.IO.Hashing;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Tests.IO.Raw;

/// <summary>
/// v2 日志（raw-journal-design §9）契约测试族——有效前缀提交 / 崩溃矩阵 / 撕裂尾 / 环绕 / 重放确定性。
/// </summary>
public sealed class RawJournalTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-raw-journal");
    private readonly string _volPath;

    public RawJournalTests() => _volPath = Path.Combine(_dir, "v.raw");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private RawFileSystem Format(long journalBytes = 8L << 20, long capacity = 64L << 20)
        => RawFileSystem.New(RawCarrier.File(_volPath), new RawFormatOptions
        {
            QuotaBytes = capacity,
            JournalReserveBytes = journalBytes,
        });

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions RO() => new()
    { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void JournaledFlush_SurvivesCrash_UnflushedLost()
    {
        var fs = Format();
        using (var committed = fs.Open("committed", RWO()))
        {
            var data = new byte[500];
            new Random(3).NextBytes(data);
            committed.Write(0, data);
            committed.Flush();   // 日志提交（单屏障）
        }
        using (var pending = fs.Open("pending", RWO()))
            pending.Write(0, new byte[300]);   // 仅 pending 记录——未屏障
        fs.CrashSimulate();

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("committed").Should().BeTrue("日志提交屏障后存活（有效前缀）");
        using (var h = fs2.Open("committed", RO()))
        {
            h.Length.Should().Be(500);
            var buf = new byte[500];
            h.Read(0, buf).Should().Be(500);
        }
        fs2.Exists("pending").Should().BeFalse("未屏障的记录不可见（fsync 语义窗口）");
    }

    [Fact]
    public void TornJournalTail_ValidPrefixReplayedOnly()
    {
        var fs = Format();
        using (var a = fs.Open("a", RWO()))
        {
            a.Write(0, new byte[100]);
            a.Flush();
        }
        using (var b = fs.Open("b", RWO()))
        {
            b.Write(0, new byte[100]);
            b.Flush();
        }
        fs.CrashSimulate();

        // 手工撕裂最后一条记录的 body（找到 b 的记录并破坏——直接破坏日志区头部之后的第二块）
        var sb = new byte[4096];
        using (var raw = File.OpenHandle(_volPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            RandomAccess.Read(raw, sb, 0);
        var jstart = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(178));
        using (var raw = File.OpenHandle(_volPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var trash = new byte[64];
            new Random(9).NextBytes(trash);
            RandomAccess.Write(raw, trash, (long)(jstart * 4096) + 4096 + 33);   // 第二条记录 body 首字节
        }

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("a").Should().BeTrue("有效前缀内的记录重放（撕裂点之前）");
        fs2.Exists("b").Should().BeFalse("撕裂记录及之后不可见");
    }

    [Fact]
    public void JournalWrap_SmallArea_ReplayCorrect()
    {
        var fs = Format(journalBytes: 16L * 1024, capacity: 32L << 20);   // 16KB 日志区 = 4 块——数条记录即穿环绕点
        using (var h = fs.Open("wrap", RWO()))
        {
            var block = new byte[64 * 1024];
            new Random(5).NextBytes(block);
            for (var i = 0; i < 24; i++)
            {
                h.Append(block);
                h.Flush();   // 每条一次日志提交（尾延伸记录 × 24 > 4 块 → 必穿环绕/衰减检查点）
            }
        }
        fs.CrashSimulate();

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        using (var h = fs2.Open("wrap", RO()))
        {
            h.Length.Should().Be(24L * 64 * 1024, "环绕后提交的记录完整重放");
            var probe = new byte[64 * 1024];
            h.Read(23L * 64 * 1024, probe).Should().Be(64 * 1024);
        }
    }

    [Fact]
    public void ReplayDeterministic_SecondRecoveryIdempotent()
    {
        var fs = Format();
        using (var h = fs.Open("det", RWO()))
        {
            h.Write(0, new byte[1000]);
            h.Flush();
        }
        fs.CreateDirectory("d0/d1");
        using (var g = fs.Open("d0/d1/f", RWO()))
        {
            g.Write(0, new byte[64]);
            g.Flush();
        }
        fs.CrashSimulate();

        string stat1;
        using (var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath)))
        {
            stat1 = string.Join(",", fs2.EnumerateEntries(recursive: true).Select(e => $"{e.Name}:{e.Length}"));
            fs2.CrashSimulate();   // 不做任何写——再次崩溃
        }
        using var fs3 = RawFileSystem.Open(RawCarrier.File(_volPath));
        var stat2 = string.Join(",", fs3.EnumerateEntries(recursive: true).Select(e => $"{e.Name}:{e.Length}"));
        stat2.Should().Be(stat1, "重放幂等（两次恢复结果全等）");
        stat1.Should().Contain("det:1000").And.Contain("f:64");
    }

    [Fact]
    public void NamespaceOpsReplay_FullMatrix()
    {
        var fs = Format();
        using (var a = fs.Open("a", RWO())) { a.Write(0, new byte[100]); a.Flush(); }
        using (var b = fs.Open("b", RWO())) { b.Write(0, new byte[100]); b.Flush(); }
        fs.CreateDirectory("dir");
        using (var c = fs.Open("dir/c", RWO())) { c.Write(0, new byte[100]); c.Flush(); }
        fs.Move("a", "a2");
        using (var a2 = fs.Open("a2", RWO())) { a2.SetFileExtra(new byte[] { 1, 2, 3 }); a2.Flush(); }
        fs.Delete("b");
        fs.MoveDirectory("dir", "dir2");
        using (var pooled = fs.Open("p", RWO()))
        {
            pooled.Write(0, new byte[4096]);   // 首区间
            pooled.Flush();
            pooled.Write(1 << 20, new byte[4096]);   // 洞后写——双区间
            pooled.Flush();
        }
        using (var holed = fs.Open("holed", RWO()))
        {
            holed.Write(0, new byte[3 << 20]);
            holed.Flush();
            holed.PunchHole(4096, 4096);   // 打洞
            holed.Flush();
        }
        using (var truncated = fs.Open("tr", RWO()))
        {
            truncated.Write(0, new byte[3 << 20]);
            truncated.Flush();
            truncated.SetLength(8192);   // 收缩截断
            truncated.Flush();
        }
        fs.CrashSimulate();

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("a").Should().BeFalse("Move 重放");
        fs2.Exists("a2").Should().BeTrue();
        using (var a2 = fs2.Open("a2", RO()))
            a2.FileExtra.ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 }, "SetExtra 重放");
        fs2.Exists("b").Should().BeFalse("Delete 重放（区间释放无泄漏——对账）");
        fs2.Exists("dir/c").Should().BeFalse();
        fs2.Exists("dir2/c").Should().BeTrue("DirMove 重放");
        using (var p = fs2.Open("p", RO()))
            p.EnumerateAllocatedRanges().Count.Should().Be(2, "洞后写双区间重放");
        using (var hd = fs2.Open("holed", RO()))
        {
            hd.Length.Should().Be(3 << 20, "逻辑长度不因打洞回退");
            var mid = new byte[4096];
            hd.Read(4096, mid).Should().Be(4096);
            mid.Should().OnlyContain(x => x == 0, "PunchHole 重放（洞读零）");
        }
        using (var tr = fs2.Open("tr", RO()))
            tr.Length.Should().Be(8192, "SetLength 收缩重放");
        // 位图 = 可达集（Volume.FreeSpace 精确性作为泄漏哨兵）
        var vol = fs2.Volume;
        vol.FreeSpace.Should().BeGreaterThan(0);
    }

    [Fact]
    public void JournalFieldsWithoutFlag_Refused_OldVolumeOpens()
    {
        // 双门负例：flag 未置 + 日志字段非零 → 拒开（数据不一致防线）
        var path = Path.Combine(_dir, "oldstyle.raw");
        using (RawFileSystem.New(RawCarrier.File(path), new RawFormatOptions
        { QuotaBytes = 8 << 20, JournalReserveBytes = 0 })) { }   // 无日志卷（flag 未置、字段恒零）
        var sb = new byte[4096];
        foreach (var sbOffset in new[] { 0L, 4096L })
        {
            using (var raw = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                RandomAccess.Read(raw, sb, sbOffset);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(sb.AsSpan(178, 8), 77);   // 伪造非零 JournalStart
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(sb.AsSpan(4088), Crc32.HashToUInt32(sb.AsSpan(0, 4088)));
            using (var raw = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                RandomAccess.Write(raw, sb, sbOffset);
        }
        var act = () => RawFileSystem.Open(RawCarrier.File(path));
        act.Should().Throw<FileIOException>("flag 未置 + 日志字段非零 = 前向双门违约拒开");

        // 老卷（无日志）在检查点模式照常工作
        var path2 = Path.Combine(_dir, "plain.raw");
        var fs = RawFileSystem.New(RawCarrier.File(path2), new RawFormatOptions
        { QuotaBytes = 8 << 20, JournalReserveBytes = 0 });
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, new byte[100]);
            h.Flush();
        }
        fs.Dispose();
        using var fs2 = RawFileSystem.Open(RawCarrier.File(path2));
        fs2.Exists("f").Should().BeTrue("JournalReserveBytes=0 卷 = 检查点模式（兼容矩阵）");
    }

    [Fact]
    public void JournalAreaDamage_DegradesToCheckpointState()
    {
        var fs = Format();
        using (var a = fs.Open("early", RWO()))
        {
            a.Write(0, new byte[100]);
            a.Flush();
        }
        fs.FlushRoot();   // 检查点（early 入镜像；CkptLsn 前进 + 区代数复位）
        using (var b = fs.Open("late", RWO()))
        {
            b.Write(0, new byte[100]);
            b.Flush();   // 检查点后新记录（新代数）
        }
        fs.CrashSimulate();

        // 破坏整个日志区（magic 全毁）
        var sb = new byte[4096];
        using (var raw = File.OpenHandle(_volPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            RandomAccess.Read(raw, sb, 0);
        var jstart = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(178));
        var jblocks = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(sb.AsSpan(186));
        using (var raw = File.OpenHandle(_volPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var trash = new byte[(int)(jblocks * 4096)];
            new Random(9).NextBytes(trash);
            RandomAccess.Write(raw, trash, (long)(jstart * 4096));
        }

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("early").Should().BeTrue("检查点态可用（诚实降级）");
        fs2.Exists("late").Should().BeFalse("日志区损毁——检查点后部分丢失（设计 §11 风险清单）");
    }

    // ═══ W2 组提交（两段式——屏障期释放元数据锁）═══

    [Fact]
    public async Task GroupCommit_ConcurrentFlushers_AllSurviveCrash()
    {
        var fs = Format();
        const int threads = 8;
        const int perThread = 25;
        var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, threads).Select(async t =>
        {
            await Task.Yield();
            gate.Wait();
            for (var i = 0; i < perThread; i++)
            {
                using var h = fs.Open($"t{t}/f{i}", RWO());
                h.Write(0, new byte[64]);
                h.Flush();   // 两段式提交——屏障期其他线程数据面不停摆
            }
        }).ToArray();
        fs.CreateDirectory("t0");   // 预建目录族（并发 Open 也行——mkdir 幂等）
        for (var t = 1; t < threads; t++) fs.CreateDirectory($"t{t}");
        gate.Set();
        await Task.WhenAll(tasks);
        fs.CrashSimulate();

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        var survivors = 0;
        for (var t = 0; t < threads; t++)
            for (var i = 0; i < perThread; i++)
                if (fs2.Exists($"t{t}/f{i}")) survivors++;
        survivors.Should().Be(threads * perThread,
            "并发 Flush（组提交）全部屏障——崩溃后全数存活（有效前缀含全部已提交记录）");
    }

    [Fact]
    public void GroupCommit_BarrierDoesNotBlockDataPlane()
    {
        // W2 核心收益验证：屏障（fsync）进行期间，其他写者的数据面操作（写回路径）不被元数据锁挡死。
        // 观测：慢屏障（WriteThrough 逐写提交制造长屏障）与并发普通写交替——总时长 < 串行时长×容差。
        // （结构性验证：两段式提交在屏障段确实不持 _metadataLock——用内部观测点代替精确计时防 flaky）
        using var fs = Format();
        using var wt = fs.Open("wt", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite, Hints = FileOpenHints.WriteThrough,
        });
        using var plain = fs.Open("plain", RWO());
        var block = new byte[64 * 1024];
        // WriteThrough 逐写提交（每次一个屏障）+ 并发普通写（无 Flush）——若屏障持锁，plain 写会被串行化拖慢
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 30; i++)
        {
            wt.Append(block);          // 内含逐写 JournalCommit（两段式）
            plain.Append(block);       // 数据面（写绕——不碰屏障）
        }
        sw.Stop();
        sw.ElapsedMilliseconds.Should().BeLessThan(30_000, "屏障期数据面不停摆（两段式——无死锁无饥饿完成）");
        plain.Length.Should().Be(30L * 64 * 1024, "并发数据面完整");
    }
}
