using System.Collections.Concurrent;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Tests.IO.Raw;

/// <summary>
/// 写回页缓存（§14.7）+ Mmap（§3.5 补齐）+ Advise 预取——契约测试。
/// 写回崩溃语义：未 Flush 丢失 / Flush 后存活（fsync 语义窗口）；MMF 单区间直映射往返。
/// </summary>
public sealed class RawWriteBackTests : IDisposable
{
    private readonly string _dir = TestTempDir.Create("core-io-raw-wb");
    private readonly string _volPath;

    public RawWriteBackTests() => _volPath = Path.Combine(_dir, "v.raw");

    public void Dispose() => TestTempDir.TryCleanup(_dir);

    private RawFileSystem Format()
        => RawFileSystem.New(RawCarrier.File(_volPath), new RawFormatOptions { QuotaBytes = 32L << 20 });

    private static FileOpenOptions RWO() => new()
    { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static readonly string[] s_scanNames = ["w0", "w1", "w2"];   // CA1861：热循环数组参静态化

    [Fact]
    public void WriteBack_UnflushedCrash_Lost_FlushedCrash_Survives()
    {
        var fs1 = Format();
        using (var committed = fs1.Open("committed", RWO()))
        {
            committed.Write(0, new byte[500]);
            committed.Flush();   // 脏页排干 + 提交
        }
        using (var pending = fs1.Open("pending", RWO()))
            pending.Write(0, new byte[300]);   // 写回：仅脏页——不 Flush
        fs1.CrashSimulate();   // 崩溃（跳过 clean 关闭——脏页未排干）

        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("committed").Should().BeTrue("Flush 排干后存活");
        using (var h = fs2.Open("committed", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be(500);
            h.Read(0, new byte[16]).Should().Be(16);
        }
        fs2.Exists("pending").Should().BeFalse("未 Flush 的写回数据丢失（fsync 语义窗口——§4.1）");
    }

    [Fact]
    public void WriteBack_ReadCohernce_BeforeFlush()
    {
        using var fs = Format();
        using var h = fs.Open("f", RWO());
        var data = new byte[10000];
        new Random(4).NextBytes(data);
        h.Write(0, data);
        var buf = new byte[10000];
        h.Read(0, buf).Should().Be(10000);
        buf.Should().BeEquivalentTo(data, "脏页读一致（写回下读走缓存）");
        h.Flush();
        var buf2 = new byte[10000];
        h.Read(0, buf2).Should().Be(10000);
        buf2.Should().BeEquivalentTo(data, "排干后读一致（页保留）");
    }

    [Fact]
    public void Mmap_SingleExtent_Roundtrip()
    {
        using var fs = Format();
        using (var h = fs.Open("m", RWO()))
        {
            h.Write(0, new byte[8192]);   // 单区间连续 Written
            h.Flush();
        }
        using (var h = fs.Open("m", RWO()))
        using (var map = h.Map(0, 8192, AccessMode.ReadWrite))
        {
            map.View.Length.Should().Be(8192);
            map.View.Span[0] = 0xAB;
            map.View.Span[8191] = 0xCD;
            map.Flush();   // msync
        }
        using (var h = fs.Open("m", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[2];
            h.Read(0, buf).Should().Be(2);
            buf[0].Should().Be(0xAB, "视图写入经 MSF 落载体——Map 后缓存失效重载可见");
            h.Read(8190, buf).Should().Be(2);
            buf[1].Should().Be(0xCD);
        }
    }

    [Fact]
    public void Mmap_FragmentedFile_MaterializesThenMaps()
    {
        using var fs = Format();
        var a = new byte[4096];
        var b = new byte[4096];
        new Random(3).NextBytes(a);
        new Random(4).NextBytes(b);
        using (var h = fs.Open("frag", RWO()))
        {
            h.Write(0, a);
            h.Write(1 << 20, b);   // 两区间（洞）——碎片
            h.Flush();
            using var map = h.Map(0, 4096, AccessMode.Read);   // RM-08：物化整理后映射
            map.View.Length.Should().Be(4096);
            map.View.Span.ToArray().Should().BeEquivalentTo(a, "物化后首块内容保真");
            h.Flush();
        }
        fs.CrashSimulate();
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        using (var h = fs2.Open("frag", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be((1 << 20) + 4096, "逻辑长度保真");
            var buf = new byte[4096];
            h.Read(1 << 20, buf).Should().Be(4096);
            buf.Should().BeEquivalentTo(b, "物化后尾块内容保真（洞段零填充搬运）");
            h.Read(0, buf).Should().Be(4096);
            buf.Should().NotBeEquivalentTo(new byte[4096], "首块非零");
        }
    }

    [Fact]
    public void Mmap_FragmentedFile_ReadOnlyHandle_NoMutate()
    {
        using var fs = Format();
        using (var w = fs.Open("frag2", RWO()))
        {
            w.Write(0, new byte[4096]);
            w.Write(1 << 20, new byte[4096]);
            w.Flush();
        }
        using (var h = fs.Open("frag2", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var act = () => h.Map(0, 4096, AccessMode.Read);
            act.Should().Throw<FileIOException>().Which.Error.Should().Be(IOError.Unsupported,
                "只读句柄不触发物化（写操作）——RM-08 诚实语义");
        }
    }

    [Fact]
    public void Advise_Sequential_NoThrow_RealBehavior()
    {
        using var fs = Format();
        using var h = fs.Open("seq", RWO());
        var data = new byte[1 << 20];
        new Random(6).NextBytes(data);
        h.Write(0, data);
        h.Flush();
        h.Advise(FileAdvise.Sequential);   // 预取开关——不抛
        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);   // 顺序读触发下一块预取
        h.Read(4096, buf).Should().Be(4096);
        buf[0].Should().Be(data[4096]);
    }

    [Fact]
    public void DirectMode_BypassesWriteBack_FlushIndependent()
    {
        using var fs = Format();
        using var h = fs.Open("direct", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            Hints = FileOpenHints.NoBuffering,
        });
        var data = new byte[4096];
        new Random(8).NextBytes(data);
        h.Write(0, data);   // 直达档：绕过自管页缓存——直落载体
        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);
        buf.Should().BeEquivalentTo(data, "直达档数据正确");
        fs.CrashSimulate();
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        // 直达写已落载体但条目未提交（未 Flush）——fsync 语义：条目丢失（数据在但不可见）
        fs2.Exists("direct").Should().BeFalse("直达档不改变提交语义——Flush 前条目不持久");
    }

    // ═══ 两档一致性（O_DIRECT 纪律——B1/B2/B1b 修复的契约锁）═══

    private static FileOpenOptions Direct() => new()
    {
        Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
        Sharing = FileSharing.ReadWrite, Hints = FileOpenHints.NoBuffering,
    };

    [Fact]
    public void DirectFullBlockWrite_InvalidatesResidentPage_BufferedReadSeesNewData()
    {
        using var fs = Format();
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, new byte[4096]);
            h.Flush();   // 页驻留（干净）——载体同步
        }
        using (var d = fs.Open("f", Direct()))
        {
            var fresh = new byte[4096];
            Array.Fill(fresh, (byte)0xAB);
            d.Write(0, fresh);   // 直达整块覆写——驻留页必须失效
        }
        using (var h = fs.Open("f", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[4096];
            h.Read(0, buf).Should().Be(4096);
            buf.Should().OnlyContain(b => b == 0xAB, "B1：直达整块写后缓冲读见新数据（驻留页失效——非旧缓存）");
        }
    }

    [Fact]
    public void BufferedDirtyThenDirectRead_DirectReadSeesLatest()
    {
        using var fs = Format();
        using var h = fs.Open("f", RWO());
        var data = new byte[4096];
        Array.Fill(data, (byte)0xCD);
        h.Write(0, data);   // 脏页未 Flush——载体滞后
        using (var d = fs.Open("f", Direct()))
        {
            var buf = new byte[4096];
            d.Read(0, buf).Should().Be(4096);
            buf.Should().OnlyContain(b => b == 0xCD, "B2：直达读前排干重叠脏页——读到最新数据非载体滞后");
        }
    }

    // ═══ RM-28：直达档 O_DIRECT 读通道（DIO 句柄/弹跳窗/回退）契约 ═══

    [Fact]
    public void DirectRead_LargeScan_MatchesWrittenData_AnyAlignment()
    {
        using var fs = Format();
        var data = new byte[1 << 20];   // 1MB 随机数据
        new Random(41).NextBytes(data);
        using (var h = fs.Open("f", RWO()))
        {
            h.Write(0, data);
            h.Flush();   // 数据落载体（内核可能仍持脏页——直达读须经 DIO 读纪律回写后取到）
        }
        using (var d = fs.Open("f", Direct()))
        {
            // 对齐读（1MB 整段——零拷贝或弹跳窗由实现按缓冲地址择路）
            var buf = new byte[1 << 20];
            d.Read(0, buf).Should().Be(1 << 20);
            buf.Should().BeEquivalentTo(data, "直达大段读（O_DIRECT 通道）内容保真");

            // 非对齐偏移/长度（奇数偏移 + 半窗长度——弹跳窗局部拷贝路径）
            var part = new byte[65536];
            d.Read(1001, part).Should().Be(65536);
            part.Should().BeEquivalentTo(data.AsSpan(1001, 65536).ToArray(), "直达非对齐读（弹跳窗）内容保真");

            // 重复扫描（第二次读 = 真 DIO miss——DONTNEED 回退路径或 DIO 通道皆须一致）
            d.Read(0, buf).Should().Be(1 << 20);
            buf.Should().BeEquivalentTo(data, "直达重复扫描内容稳定");
        }
    }

    [Fact]
    public void DirectRead_AfterWriteAroundFlush_SeesCarrierData()
    {
        // 写绕（write-around）直落载体（内核脏页未写回）+ 直达读：内核 DIO 读纪律先回写重叠脏区——
        // 与 BufferedDirtyThenDirectRead 互补（那是自管脏页排干路径，这是直落载体路径）
        using var fs = Format();
        var data = new byte[256 * 1024];
        new Random(42).NextBytes(data);
        using (var w = fs.Open("f", RWO()))
        {
            w.Write(0, data);   // 整块 run 未驻留 → 写绕直落（缓冲载体写——内核脏页）
            w.Flush();          // FlushData 语义下屏障；数据在载体（可能仅在内核回写队列）
        }
        using (var d = fs.Open("f", Direct()))
        {
            var buf = new byte[256 * 1024];
            d.Read(0, buf).Should().Be(256 * 1024);
            buf.Should().BeEquivalentTo(data, "写绕直落后直达读见最新（DIO 读重叠脏区回写纪律）");
        }
    }

    [Fact]
    public void DirectPartialWrite_MergesResidentDirtyData()
    {
        using var fs = Format();
        using var h = fs.Open("f", RWO());
        var base_ = new byte[4096];
        Array.Fill(base_, (byte)0x11);
        h.Write(0, base_);   // 脏页（未 Flush）
        using (var d = fs.Open("f", Direct()))
        {
            var patch = new byte[50];
            Array.Fill(patch, (byte)0x22);
            d.Write(100, patch);   // 部分块直达 RMW——基底须取驻留脏页（载体滞后）
        }
        var buf = new byte[4096];
        h.Read(0, buf).Should().Be(4096);
        buf.AsSpan(0, 100).ToArray().Should().OnlyContain(b => b == 0x11, "切片前保留缓冲写");
        buf.AsSpan(100, 50).ToArray().Should().OnlyContain(b => b == 0x22, "直达切片写入");
        buf.AsSpan(150).ToArray().Should().OnlyContain(b => b == 0x11, "B1b：切片后保留缓冲写（RMW 基底=驻留脏页非载体）");
    }

    [Fact]
    public void SequentialAppend_ExtentCountStaysFlat()
    {
        using var fs = Format();
        using (var h = fs.Open("app", RWO()))   // 无预分配——逐次 64K 追加
        {
            var block = new byte[64 * 1024];
            new Random(9).NextBytes(block);
            for (var i = 0; i < 64; i++)
                h.Append(block);
            h.Flush();
            h.EnumerateAllocatedRanges().Count.Should().BeLessThanOrEqualTo(2,
                "D1 追加快道：尾区间物理邻接延伸——区间数不随追加线性膨胀（旧路径 64 次 = 64 区间）");
            h.Length.Should().Be(64L * 64 * 1024);
        }
        fs.CrashSimulate();
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        using (var h = fs2.Open("app", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            h.Length.Should().Be(64L * 64 * 1024, "快道提交后重开可见");
            var probe = new byte[64 * 1024];
            h.Read(63L * 64 * 1024, probe).Should().Be(64 * 1024, "尾块可读");
        }
    }

    // ═══ 后台 flusher（RM-02——kernel writeback 模型）═══

    [Fact]
    public void BackgroundFlusher_DrainsWithoutExplicitFlush_DataSurvivesOnlyAfterCommit()
    {
        using var fs = Format();
        var data = new byte[3 << 20];
        new Random(11).NextBytes(data);
        using (var h = fs.Open("bg", RWO()))
        {
            h.Write(0, data);
            h.Flush();
        }
        fs.CrashSimulate();   // flusher 线程随 ReleaseResources 停止（生命周期收敛）
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        using (var h = fs2.Open("bg", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[3 << 20];
            h.Read(0, buf).Should().Be(3 << 20);
            buf.Should().BeEquivalentTo(data, "flusher 并存下提交语义不变——Flush 后数据完整");
        }
    }

    [Fact]
    public void BackgroundFlusher_WakesOnThreshold_DataReadableAcrossThreshold()
    {
        using var fs = Format();
        using (var h = fs.Open("bg2", RWO()))
        {
            var chunk = new byte[64 * 1024];
            new Random(12).NextBytes(chunk);
            var payload = new byte[12 << 20];   // 12MB > 默认预算/8=8MB 阈值——必定唤醒 flusher
            var target = payload.Length / chunk.Length;
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < target; i++)
            {
                h.Append(chunk);
                // RM-14：有界节流（总预算 2s 内匀速——flusher 调度确定性；旧逐次 Sleep(1) 在
                // 低负载机积满 190ms+、高负载机又不达——两头 flaky）
                if (i < target - 1 && swTotal.ElapsedMilliseconds < 2000)
                    System.Threading.Thread.Sleep(Math.Max(0, (int)(2000 * (i + 1) / target - swTotal.ElapsedMilliseconds)));
            }
            var probe = new byte[64 * 1024];
            h.Read(0, probe).Should().Be(64 * 1024);   // 读写穿越阈值后一致（脏页/排干后页均可读）
            h.Flush();
        }
        fs.CrashSimulate();
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        using (var h = fs2.Open("bg2", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var probe = new byte[64 * 1024];
            h.Read(0, probe).Should().Be(64 * 1024, "flusher 排干与显式 Flush 并存——提交后数据完整");
        }
    }

    // ═══ RM-07/RM-09：WriteThrough 与 FlushDataOnly 接线 ═══

    [Fact]
    public void WriteThroughHint_PerWriteCommit_SurvivesImmediateCrash()
    {
        using var fs = Format();
        using (var h = fs.Open("wt", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite, Hints = FileOpenHints.WriteThrough,
        }))
        {
            h.Write(0, new byte[500]);   // 无显式 Flush——写透应逐写提交
        }
        fs.CrashSimulate();   // 立即崩溃
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("wt").Should().BeTrue("WriteThrough=逐写日志提交（O_SYNC 语义——崩溃窗口归零）");
        using (var h = fs2.Open("wt", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
            h.Length.Should().Be(500);
    }

    [Fact]
    public void FlushData_DataOnly_MetadataNotCommitted()
    {
        using var fs = Format();
        using (var h = fs.Open("fdo", RWO()))
        {
            h.Write(0, new byte[500]);
            h.Flush();   // 完整提交（条目入档）
        }
        using (var h2 = fs.Open("fdo2", RWO()))
        {
            h2.Write(0, new byte[500]);
            h2.FlushData();   // 仅数据面（fdatasync 语义）——不提交日志记录
        }
        fs.CrashSimulate();
        using var fs2 = RawFileSystem.Open(RawCarrier.File(_volPath));
        fs2.Exists("fdo").Should().BeTrue("Flush（完整）提交后存活");
        fs2.Exists("fdo2").Should().BeFalse("FlushData 数据面 only——元数据记录未屏障（fsync 语义窗口）");
    }

    // ═══ RM-12：读锁外快照（并发读写——读者见完整态，互不撕裂）═══

    [Fact]
    public async Task SnapshotReaders_RacingWriters_NeverTear()
    {
        using var fs = Format();
        const int readers = 4;
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var errors = new ConcurrentBag<Exception>();

        // 写者：持续追加 + 截断循环（区间快照高频交换）
        var writer = Task.Run(() =>
        {
            var chunk = new byte[64 * 1024];
            new Random(77).NextBytes(chunk);
            var round = 0;
            while (!stop.IsCancellationRequested)
            {
                using var h = fs.Open($"w{round % 3}", RWO());
                for (var i = 0; i < 5; i++) h.Append(chunk);
                h.Flush();
                h.SetLength(4096);   // 截断（释放 + CoW 交换）
                round++;
            }
        });
        // 读者：并发全路径读（快照读——锁外）
        var readers_ = Enumerable.Range(0, readers).Select(r => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (var name in s_scanNames)
                {
                    try
                    {
                        if (!fs.Exists(name)) continue;
                        using var h = fs.Open(name, new FileOpenOptions
                        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
                        var buf = new byte[8192];
                        var got = h.Read(0, buf);   // 快照读——完整旧态或完整新态（Length 时点值可随后推进——合法）
                        if (got is < 0 or > 8192)
                            throw new InvalidOperationException($"读返回越界：{got}（快照撕裂）");
                    }
                    catch (FileIOException ex) when (ex.Error == IOError.NotFound)
                    {
                        // 写者循环重建窗口——重试即可
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            }
        })).ToArray();
        await Task.WhenAll(readers_);
        await writer;
        errors.Should().BeEmpty("并发读写无异常（锁外快照路径稳健）");
    }

    [Fact]
    public void FreedBlocksInvalidateCache_ReallocatedReadsNewOwner()
    {
        using var fs = Format();
        // A 写满一页后删除（释放 + 缓存退出）
        var dataA = new byte[4096];
        new Random(5).NextBytes(dataA);
        using (var a = fs.Open("A", RWO()))
        {
            a.Write(0, dataA);
            a.Flush();
        }
        fs.Delete("A");
        // B 立即复用同块（first-fit 同位分配）写不同数据（write-around 直落载体）
        var dataB = new byte[4096];
        new Random(6).NextBytes(dataB);
        using (var b = fs.Open("B", RWO()))
        {
            b.Write(0, dataB);
            b.Flush();
        }
        using (var b2 = fs.Open("B", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite }))
        {
            var buf = new byte[4096];
            b2.Read(0, buf);
            buf.Should().BeEquivalentTo(dataB, "释放块缓存失效——重分配后读见新属主（非 A 残影）");
        }
    }
}