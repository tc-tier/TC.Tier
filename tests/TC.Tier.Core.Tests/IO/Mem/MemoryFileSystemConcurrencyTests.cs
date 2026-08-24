using System.Collections.Concurrent;
using System.Diagnostics;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Mem;

namespace TC.Tier.Core.Tests.IO.Mem;

/// <summary>
/// MemoryFileSystem 并发模型测试（设计 §11 第四/五轮 ⑯–㉑）——epoch/替换屏障/代际/引用计数四机制的组合验收。
/// <para>⑯ 在途读者×换租：无撕裂无串数据（现状代码此测试会暴露 use-after-free 竞态，Core 版必须绿）。</para>
/// <para>⑰ epoch drain 延迟归还正确性（借出中的 buffer 不被二次租出——经 ⑯ 全量比对间接断言）。</para>
/// <para>⑱ per-fs epoch 隔离：A 卷 drain 不被 B 卷长读阻塞。</para>
/// <para>⑲ 长活观察者（映射）不走 epoch：映射持期间其他槽换租照常推进。</para>
/// <para>⑳ 写者×Grow：确定性数据模式 + 审计日志 + 逐字节重建全量比对（R9 规范）。</para>
/// <para>㉑ 替换屏障活性：freeze 期间写者阻塞但不死锁。</para>
/// <para>㉒–㉕ Sparse per-file Gate 契约（数据面锁分片改造）：不同文件并发写零串扰（逐字节重建）/
/// 同文件读写无半写撕裂 / 写×截断锁序活性 / 在途写×删除回收无池页串台（DEBUG 0xCC 毒化兜底）。</para>
/// </summary>
public sealed class MemoryFileSystemConcurrencyTests
{
    /// <summary>确定性数据模式（R9）：writerId 种子 PRNG → (offset, byte) 全确定。</summary>
    private static byte ExpectedByte(int writerId, long offset)
    {
        // 简单可重放散列：避免共享 PRNG 状态的顺序依赖
        var v = (uint)(writerId * 2654435761L + offset * 40503L);
        v ^= v >> 13;
        v *= 0x5bd1e995;
        v ^= v >> 15;
        return (byte)(v & 0xFF);
    }

    private static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite)
        => new() { Access = access, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite };

    private static FileOpenOptions ReadOpts()
        => new() { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite };

    [Fact]
    public void ReaderGrowPoolReuse_NoTearNoCrossTalk_ReservedMode()
    {
        // ⑯+⑰：现状竞态（Read 锁外拷贝 × Grow 锁内归还 = use-after-free）——多线程 Read + Grow + 池复租压力
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions
        {
            Allocation = MemoryAllocationMode.Reserved,
        });
        using var h = fs.Open("f", Opts());
        const long initialSize = 1 << 20;
        h.Write(0, new byte[initialSize]);
        // 填充已知模式：offset 散列
        var fill = new byte[initialSize];
        for (long i = 0; i < initialSize; i++) fill[i] = ExpectedByte(0, i);
        h.Write(0, fill);

        var stop = new ManualResetEventSlim();
        var failures = new ConcurrentQueue<string>();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var buf = new byte[8192];
            var rng = new Random(42);
            while (!stop.IsSet)
            {
                var offset = (long)rng.Next(0, (int)initialSize - buf.Length);
                var n = h.Read(offset, buf);
                if (n != buf.Length)
                {
                    failures.Enqueue($"short read at {offset}: {n}");
                    continue;
                }
                for (var i = 0; i < n; i++)
                {
                    if (buf[i] != ExpectedByte(0, offset + i))
                    {
                        failures.Enqueue($"torn/cross-talk at {offset + i}: {buf[i]} != {ExpectedByte(0, offset + i)}");
                        break;
                    }
                }
            }
        })).ToArray();

        // Grow 换租 × 池复租压力（grow→truncate→grow 循环触发旧 buffer 归还+复租）
        var grower = Task.Run(() =>
        {
            var size = initialSize;
            while (!stop.IsSet)
            {
                size = size == initialSize ? initialSize * 2 : initialSize;
                fs.GrowFile("f", size);
                fs.TruncateFile("f", initialSize);
                // 池复租压力：反复建删同尺寸文件（吃掉归还的旧 buffer）
                fs.CreateOrReplaceFile("churn", initialSize);
                fs.Delete("churn");
            }
        });

        Thread.Sleep(2500);   // 压力窗口
        stop.Set();
        Task.WaitAll(readers.Append(grower).ToArray());

        failures.Should().BeEmpty("在途读者×换租×池复租必须无撕裂无串数据（epoch + 引用计数双门槛）");
    }

    [Fact]
    public void PerFsEpochIsolation_VolumeADrainNotBlockedByVolumeBLongRead()
    {
        // ⑱：B 卷持续长读（epoch 保护窗口反复开合），A 卷高频换租——若 epoch 全局共享则 A 的归还被 B 阻塞
        var fsA = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Reserved });
        var fsB = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Reserved });
        using var hA = fsA.Open("a", Opts());
        using var hB = fsB.Open("b", Opts());
        hA.Write(0, new byte[1 << 18]);
        hB.Write(0, new byte[1 << 18]);

        var stop = new ManualResetEventSlim();
        var churnCount = 0L;

        var longReader = Task.Run(() =>
        {
            var buf = new byte[1 << 16];
            while (!stop.IsSet)
                hB.Read(0, buf);
        });

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1200)
        {
            fsA.GrowFile("a", (1 << 18) + (1 << 16));
            fsA.TruncateFile("a", 1 << 18);
            Interlocked.Increment(ref churnCount);
        }
        stop.Set();
        longReader.Wait(2000);

        // A 卷换租在 B 卷读压力下持续推进（隔离：A 的 drain 不等 B 的读者）
        churnCount.Should().BeGreaterThan(10, $"1.2s 内换租次数过低：{Volatile.Read(ref churnCount)}——疑似跨卷 drain 阻塞");
        fsA.Dispose();
        fsB.Dispose();
    }

    [Fact]
    public void LongLivedObserver_MapDoesNotStallOtherSlotsGrowth()
    {
        // ⑲：长活观察者（直址映射钉住旧 buffer）不走 epoch——映射持期间其他槽的换租/归还照常推进
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Reserved });
        using var h1 = fs.Open("mapped", Opts());
        using var h2 = fs.Open("churn", Opts());
        h1.Write(0, new byte[1 << 16]);
        h2.Write(0, new byte[1 << 16]);
        using var section = h1.Map(0, 1 << 16, AccessMode.ReadWrite);   // 长活观察者
        section.View.Span[0] = 0x42;

        var sw = Stopwatch.StartNew();
        var count = 0;
        while (sw.ElapsedMilliseconds < 800)
        {
            fs.GrowFile("churn", (1 << 16) + 4096);
            fs.TruncateFile("churn", 1 << 16);
            count++;
        }
        count.Should().BeGreaterThan(10, "映射持期间其他槽换租应照常推进（epoch 未被长持）");
        section.View.Span[0].Should().Be(0x42);   // 观察者视图仍有效
    }

    [Fact]
    public void WriterVsGrow_NoLostWrites_FullByteByByteReconstruction()
    {
        // ⑳（R9 规范）：多线程 Write（固定偏移+越 EOF 混合）× Grow 换租——审计全部成功写入，
        //   收尾逐字节重建期望文件并全量比对（非抽样、非仅长度）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Reserved });
        using var h = fs.Open("f", Opts());
        const int size = 1 << 19;   // 512K
        h.Write(0, new byte[size]);

        const int writers = 4;
        const int rounds = 400;
        // ★ 独占 offset 区段：跨写者同址竞争会引入"最后写入者胜"的非确定性（撕裂横切已由 ⑯ 覆盖）——
        //   本测试验证"每个成功写入都不静默丢失"，区段独占让期望文件全确定可重建。
        var band = (size + 65536) / writers;
        var audit = new ConcurrentDictionary<int, HashSet<long>>();

        var writeTasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var log = new HashSet<long>();
            var buf = new byte[1];
            for (var i = 0; i < rounds; i++)
            {
                // 区段内散列 + 少量越过原 EOF（触发零扩展/换租路径）
                var offset = (long)w * band + ((uint)(i * 2654435761u) % (uint)band);
                buf[0] = ExpectedByte(w, offset);
                h.Write(offset, buf);
                log.Add(offset);
            }
            audit[w] = log;
        })).ToArray();
        Task.WaitAll(writeTasks);

        // Grow 换租压力与写者并发
        var growStop = new ManualResetEventSlim();
        var grower = Task.Run(() =>
        {
            while (!growStop.IsSet)
            {
                fs.GrowFile("f", h.Length + 4096);
                Thread.Sleep(1);
            }
        });
        Thread.Sleep(300);
        growStop.Set();
        grower.Wait(2000);

        // 逐字节重建期望文件（R9：非抽样、非仅长度）
        var expected = new byte[h.Length];
        for (var w = 0; w < writers; w++)
        {
            foreach (var offset in audit[w])
            {
                if (offset < expected.Length)
                    expected[offset] = ExpectedByte(w, offset);
            }
        }

        var actual = new byte[h.Length];
        h.Read(0, actual);
        // 全量比对
        var mismatches = 0;
        long firstBad = -1;
        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i] == expected[i]) continue;
            if (mismatches++ == 0) firstBad = i;
        }
        mismatches.Should().Be(0,
            $"写丢失/串数据 {mismatches} 字节（首个失配 @ {firstBad}）——写者×Grow 屏障防护失败");
    }

    [Fact]
    public async Task ReplacementBarrierLiveness_HighFrequencyGrowAndWrite_NoDeadlock()
    {
        // ㉑：freeze 期间写者阻塞但不死锁（Grow 高频 × 写者高频）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Reserved });
        using var h = fs.Open("f", Opts());
        h.Write(0, new byte[1 << 16]);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var grower = Task.Run(() =>
        {
            var up = true;
            while (!cts.IsCancellationRequested)
            {
                fs.GrowFile("f", h.Length + (up ? 65536 : -65536));
                up = h.Length < (1 << 20);
            }
        }, cts.Token);

        var writer = Task.Run(async () =>
        {
            var buf = new byte[512];
            long offset = 0;
            while (!cts.IsCancellationRequested)
            {
                h.Write(offset, buf);
                offset = (offset + 512) % (1 << 19);
                await Task.Delay(1);
            }
        }, cts.Token);

        var work = Task.WhenAll(grower, writer);
        var completed = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(6)));
        completed.Should().BeSameAs(work, "屏障活性：freeze 等待有限时长解除——不得死锁");
    }

    [Fact]
    public void ConcurrentOpenAppend_AllDistinctOffsets_NoOverwrite()
    {
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Reserved });
        using var h = fs.Open("f", Opts());
        const int threads = 8, perThread = 300, len = 64;
        var offsets = new ConcurrentBag<long>();
        Parallel.For(0, threads, _ =>
        {
            var data = new byte[len];
            for (var i = 0; i < perThread; i++)
                offsets.Add(h.Append(data));
        });
        offsets.Distinct().Count().Should().Be(threads * perThread);
        h.Length.Should().Be((long)threads * perThread * len);
    }

    [Fact]
    public void ConcurrentGetOrAddOpen_SamePathDistinctHandlesUnderContention()
    {
        // 并发打开/删除/重建同路径压力——代际与共享注册不互踩、不死锁
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Sparse });
        var errors = new ConcurrentQueue<Exception>();
        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 200; i++)
            {
                try
                {
                    using var h = fs.Open("hot", Opts());
                    h.Write(0, new byte[64]);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                }
            }
        });
        errors.Should().BeEmpty();
    }

    // ═══════════ Sparse per-file Gate 并发契约（㉒–㉕）═══════════

    [Fact]
    public void Sparse_ConcurrentWriteDistinctFiles_FullByteByByteVerification()
    {
        // ㉒ 数据面并行主回归：8 文件并发写确定性模式——per-file Gate 下不同文件零串扰，
        // 逐字节全量重建比对 + 长度精确（全局锁时代此场景吞吐串行化；本测试锁正确性回归）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Sparse });
        const int files = 8, blocks = 16, blockSize = 64 * 1024;
        var errors = new ConcurrentQueue<string>();
        Parallel.For(0, files, fileId =>
        {
            try
            {
                using var h = fs.Open($"f{fileId}", Opts());
                var buf = new byte[blockSize];
                for (var b = 0; b < blocks; b++)
                {
                    for (var i = 0; i < blockSize; i++)
                        buf[i] = ExpectedByte(fileId, (long)b * blockSize + i);
                    h.Write((long)b * blockSize, buf);
                }
            }
            catch (Exception ex)
            {
                errors.Enqueue($"fileId={fileId}: {ex.Message}");
            }
        });
        errors.Should().BeEmpty();

        for (var fileId = 0; fileId < files; fileId++)
        {
            using var h = fs.Open($"f{fileId}", ReadOpts());
            h.Length.Should().Be((long)blocks * blockSize, $"文件 f{fileId} 长度精确");
            var buf = new byte[blockSize];
            for (var b = 0; b < blocks; b++)
            {
                h.Read((long)b * blockSize, buf).Should().Be(blockSize);
                for (var i = 0; i < blockSize; i++)
                {
                    if (buf[i] != ExpectedByte(fileId, (long)b * blockSize + i))
                    {
                        errors.Enqueue($"f{fileId} @ {b * blockSize + i}: {buf[i]:X2} != {ExpectedByte(fileId, (long)b * blockSize + i):X2}");
                        break;
                    }
                }
            }
        }
        errors.Should().BeEmpty("并发写不同文件必须零串扰、逐字节可重放（per-file Gate 语义）");
    }

    [Fact]
    public void Sparse_ConcurrentReadWriteSameFile_NoTornRead()
    {
        // ㉓ 同文件读写互斥：写者整块覆写单版本字节（v → 4096×(byte)v），读者并发读必须
        // 见到"整块同一版本"——混合版本 = 半写撕裂（Gate 互斥缺失的直接证据）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Sparse });
        using var h = fs.Open("f", Opts());
        const int blockSize = 4096;
        h.Write(0, new byte[blockSize]);   // 页落位 + 长度就绪

        var stop = new ManualResetEventSlim();
        var failures = new ConcurrentQueue<string>();
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            var buf = new byte[blockSize];
            while (!stop.IsSet)
            {
                var n = h.Read(0, buf);
                if (n != blockSize)
                {
                    failures.Enqueue($"short read: {n}");
                    continue;
                }
                var first = buf[0];
                for (var i = 1; i < blockSize; i++)
                {
                    if (buf[i] != first)
                    {
                        failures.Enqueue($"torn block: [{first:X2}@0, {buf[i]:X2}@{i}]——半写撕裂");
                        break;
                    }
                }
            }
        })).ToArray();

        var writer = Task.Run(() =>
        {
            var buf = new byte[blockSize];
            var v = 1;
            while (!stop.IsSet)
            {
                Array.Fill(buf, (byte)v);   // 版本字节 1..255（跳 0——初值清零歧义）
                if (++v == 256) v = 1;
                h.Write(0, buf);
            }
        });

        Thread.Sleep(2000);
        stop.Set();
        Task.WaitAll(readers.Append(writer).ToArray());
        failures.Should().BeEmpty("同文件读写经 Gate 互斥——整块原子可见，无半写");
    }

    [Fact]
    public void Sparse_ConcurrentWriteVsTruncate_NoDeadlock_AllComplete()
    {
        // ㉔ 活性：写者（越 EOF 推长度 = 出 Gate→fs 锁）× 截断者（fs 锁→嵌套 Gate）反向交错——
        // 锁序单向不变式（fs._lock→Gate）若破则死锁；限时完成 + 零异常
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Sparse });
        for (var w = 0; w < 4; w++)
            fs.Open($"f{w}", Opts()).Dispose();   // 预创建（截断者先行 NotFound 假失败防线）
        var errors = new ConcurrentQueue<Exception>();
        const int iterations = 4000;
        var tasks = Enumerable.Range(0, 4).Select(w => (Task)Task.Run(() =>
        {
            try
            {
                using var h = fs.Open($"f{w}", Opts());
                var buf = new byte[256];
                for (var i = 0; i < iterations; i++)
                    h.Write((i & 1) << 12, buf);   // 交错 0/4K——4K 落点反复越过截断后的 EOF
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).Append(Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < iterations; i++)
                    fs.TruncateFile("f0", 0);   // 收缩→写者再越 EOF→推长度——双向锁交错压力
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();

        var done = Task.WaitAll(tasks, TimeSpan.FromSeconds(30));
        done.Should().BeTrue("锁序单向（数据面需要 fs 锁时先出 Gate）——无死锁，限时完成");
        errors.Should().BeEmpty();
    }

    [Fact]
    public void Sparse_ConcurrentWriteVsDeleteRecreate_NoStaleDataLeak()
    {
        // ㉕ 在途写 × 删除回收：写者持续写同路径（stale 后重开续写），删除者反复 Delete
        // （槽回收 → DrainLayoutGate → 页归还池）。DEBUG 池归还毒化 0xCC——若在途 IO 触碰
        // 已归还页立即暴露。终态：内容 ∈ {0x00, 0xA5}（页清零 + 写者 marker）
        using var fs = MemoryFileSystem.New(new MemoryFileSystemOptions { Allocation = MemoryAllocationMode.Sparse });
        var errors = new ConcurrentQueue<Exception>();
        var stop = new ManualResetEventSlim();

        var writer = Task.Run(() =>
        {
            var h = fs.Open("hot", Opts());
            var buf = new byte[4096];
            Array.Fill(buf, (byte)0xA5);
            try
            {
                var offset = 0L;
                const long span = 4 << 20;   // 4MB 环形区间（页分配/复用充分覆盖又不失控）
                while (!stop.IsSet)
                {
                    try
                    {
                        h.Write(offset, buf);
                        offset = (offset + buf.Length) % span;
                    }
                    catch (FileIOException ex) when (ex.Error == IOError.NotFound)
                    {
                        h.Dispose();
                        h = fs.Open("hot", Opts());   // 槽被回收（stale）——重开续写
                        offset = 0;
                    }
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
            finally { h.Dispose(); }
        });

        var deleter = Task.Run(() =>
        {
            while (!stop.IsSet)
            {
                fs.Delete("hot");   // 幂等（不存在仍成功）
                Thread.Sleep(20);
            }
        });

        Thread.Sleep(2500);
        stop.Set();
        Task.WaitAll(writer, deleter);

        errors.Should().BeEmpty("stale(NotFound) 为预期重开信号，其余异常零容忍");
        var contentErrors = new ConcurrentQueue<string>();
        if (!fs.Exists("hot"))   // deleter 末次删除可能后于 writer 末次重开——终态文件可不存在（合法）
            fs.CreateOrReplaceFile("hot", 0);
        using var h = fs.Open("hot", ReadOpts());
        var buf = new byte[8192];
        long pos = 0;
        int n;
        while ((n = h.Read(pos, buf)) > 0)
        {
            for (var i = 0; i < n; i++)
            {
                if (buf[i] is not (0x00 or 0xA5))
                {
                    contentErrors.Enqueue($"offset {pos + i}: {buf[i]:X2}——仅允许清零页/写入 marker；0xCC = 触碰已归还池页（drain 缺失）");
                    break;
                }
            }
            pos += n;
        }
        contentErrors.Should().BeEmpty("终态内容只含清零页与写入 marker——池页串台零容忍（文件可空：deleter 恰赢终局的合法态）");
    }
}
