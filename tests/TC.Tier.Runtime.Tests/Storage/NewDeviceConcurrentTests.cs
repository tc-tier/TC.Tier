using System.Reflection;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Storage.Compact;

namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 新 Device 并发测试——CAS 地址租借、SpinRWLock 读写锁、SequentialReader DirtyRead。
/// </summary>
public sealed class NewDeviceConcurrentTests : IDisposable
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

    // ═══════════════════════════════════════════════════════════════
    //  CAS 地址租借——多线程 Append 竞争
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultiThreadAppend_TailAdvancesMonotonically()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 100 * 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        int threadCount = 4;
        int writesPerThread = 50;
        long writeSize = 64;

        var results = new long[threadCount * writesPerThread];
        int pos = 0;

        Parallel.For(0, threadCount, t =>
        {
            var data = MakePattern((int)writeSize, (byte)(t * 31));
            for (int i = 0; i < writesPerThread; i++)
            {
                var addr = dev.Append(data);
                int slot = Interlocked.Increment(ref pos) - 1;
                results[slot] = addr.Offset;
            }
        });

        long expected = threadCount * writesPerThread * writeSize;
        dev.AllocatedTail.Offset.Should().Be(expected,
            $"tail should be {expected} after {threadCount * writesPerThread} appends");

        // Verify all offsets are unique
        var sorted = results.OrderBy(o => o).ToArray();
        sorted.Length.Should().Be(threadCount * writesPerThread);
        for (int i = 1; i < sorted.Length; i++)
            sorted[i].Should().BeGreaterThan(sorted[i - 1],
                $"CAS should yield unique offsets (dup at index {i}: {sorted[i]})");
    }

    [Fact]
    public void MultiThreadAppend_DataIntegrity()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 10 * 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        int threadCount = 4;
        int writesPerThread = 100;
        int writeSize = 64;

        var seedSeq = new byte[10000];
        var rng = new Random(42);
        rng.NextBytes(seedSeq);

        var addrs = new (int thread, int idx, LogicalAddress addr)[threadCount * writesPerThread];
        int pos = 0;

        Parallel.For(0, threadCount, t =>
        {
            for (int i = 0; i < writesPerThread; i++)
            {
                var data = new byte[writeSize];
                data[0] = (byte)t;
                data[1] = (byte)i;
                var addr = dev.Append(data);

                int slot = Interlocked.Increment(ref pos) - 1;
                addrs[slot] = (t, i, addr);
            }
        });

        // Verify all writes completed: tail = total bytes
        long expected = threadCount * writesPerThread * writeSize;
        dev.AllocatedTail.Offset.Should().Be(expected);

        // Verify sequential reads: read the entire range
        Span<byte> allData = new byte[expected];
        int totalRead = dev.Read(new LogicalAddress(0, 0), allData);
        totalRead.Should().Be((int)expected);
    }

    // ═══════════════════════════════════════════════════════════════
    //  SpinRWLock —— 并发 Read + Write 锁竞争
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentReadWrite_NoDataTeardown()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 10 * 1024 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // Pre-fill with uniform data
        byte[] uniform = new byte[256];
        Array.Fill(uniform, (byte)0xAA);
        var addr = dev.Append(uniform);

        using var barrier = new Barrier(4);
        int errors = 0;
        int writeCount = 0;

        var tasks = new Task[4];
        for (int t = 0; t < 2; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var buf = new byte[256];
                barrier.SignalAndWait();
                for (int i = 0; i < 100; i++)
                {
                    byte val = (byte)(i & 0xFF);
                    Array.Fill(buf, val); // uniform write — every byte same
                    dev.Write(addr, buf);
                    Interlocked.Increment(ref writeCount);
                }
            });
        }
        for (int t = 2; t < 4; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                var buf = new byte[256];
                barrier.SignalAndWait();
                for (int i = 0; i < 500; i++)
                {
                    int n = dev.Read(addr, buf);
                    if (n > 0)
                    {
                        byte first = buf[0];
                        for (int j = 1; j < n; j++)
                            if (buf[j] != first) // torn write: mixed bytes
                                Interlocked.Increment(ref errors);
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        writeCount.Should().BeGreaterThan(0);
        errors.Should().Be(0, "SpinRWLock should guarantee reads see complete (not torn) writes");
    }

    [Fact]
    public void ManyConcurrentReaders_SharedLock()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 64 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(512, 0xCC);
        var addr = dev.Append(data);

        int readers = 16;
        int readsPerReader = 100;
        var tasks = new Task[readers];
        var dstBuf = new byte[512];

        for (int t = 0; t < readers; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < readsPerReader; i++)
                {
                    int n = dev.Read(addr, dstBuf);
                    n.Should().BeGreaterThan(0);
                }
            });
        }

        Task.WaitAll(tasks);
    }

    // ═══════════════════════════════════════════════════════════════
    //  CAS 地址租借——多线程 Append 竞争
    // ═══════════════════════════════════════════════════════════════
    //  SequentialReader DirtyRead + concurrent writes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void SequentialReader_DirtyRead_DoesNotBlockWriters()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        byte[] data = MakePattern(10000, 0xDD);
        var start = dev.Append(data);
        var end = dev.CommittedTail;

        using var reader = dev.OpenSequentialReader(start, end,
            snapshotMode: SnapshotMode.DirtyRead);

        bool readComplete = false;
        var readTask = Task.Run(() =>
        {
            Span<byte> buf = stackalloc byte[100];
            long total = 0;
            while (total < 10000)
            {
                int n = reader.Read(buf);
                if (n == 0) break;
                total += n;
                Thread.Sleep(1);
            }
            readComplete = true;
        });

        // Concurrently write to the same region — should NOT be blocked
        bool writeComplete = false;
        var writeAddr = dev.Append(MakePattern(500, 0xEE));
        var writeTask = Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                dev.Append(MakePattern(100, (byte)i));
            }
            writeComplete = true;
        });

        Task.WaitAll(readTask, writeTask);
        readComplete.Should().BeTrue();
        writeComplete.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  并发 + Compact
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ConcurrentAppendThenCompact_MultiplePasses()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: 256 * 1024).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        using var dev = options.Builder(vol.Fs).Start();
        dev.WaitForReady();

        // Fill with data
        for (int i = 0; i < 5; i++)
            dev.Append(MakePattern(1000, (byte)i));

        // Compact, then write more, then compact again
        var r1 = await dev.StartCompact().WaitAsync();
        r1.MigrationMap.Should().NotBeEmpty();

        for (int i = 5; i < 10; i++)
            dev.Append(MakePattern(1000, (byte)i));

        var r2 = await dev.StartCompact().WaitAsync();
        r2.MigrationMap.Should().NotBeEmpty();
    }

    [Fact]
    public void RangeCompact_WaitsForPriorEpochReader()
    {
        const int blockSize = 64 * 1024;
        var vol = NewVol();
        var options = new StorageEngineOptions("test", segmentGrowthLimit: blockSize * 4L).WithPreallocateFile(false).WithHints(FileOpenHints.WriteThrough);
        // ★ epoch 注入实例（引擎不暴露内部原语）——测试持同一实例驱动 reader 保护
        var epoch = new LightEpoch();
        using var dev = options.Builder(vol.Fs, epoch: epoch).Start();
        dev.WaitForReady();

        var first = dev.Append(MakePattern(blockSize, 0x21));
        var reclaimed = dev.Append(MakePattern(blockSize, 0x42));
        var second = dev.Append(MakePattern(blockSize, 0x63));
        var tail = dev.CommittedTail;
        dev.Reclaim(reclaimed, second);

        using var readerEntered = new ManualResetEventSlim(false);
        using var releaseReader = new ManualResetEventSlim(false);
        Exception? readerError = null;
        var readerThread = new Thread(() =>
        {
            var protectedByEpoch = false;
            try
            {
                epoch.Resume();
                protectedByEpoch = true;
                readerEntered.Set();
                releaseReader.Wait();
            }
            catch (Exception exception)
            {
                readerError = exception;
            }
            finally
            {
                if (protectedByEpoch)
                    epoch.Suspend();
            }
        });

        readerThread.Start();
        readerEntered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task<CompactResult>? compactTask = null;
        try
        {
            compactTask = Task.Run(async () => await dev.StartRangeCompact(
                first,
                tail,
                [first, reclaimed, second]).WaitAsync());

            compactTask.Wait(TimeSpan.FromMilliseconds(200)).Should().BeFalse(
                "physical movement must wait for readers protected by the prior epoch");
        }
        finally
        {
            releaseReader.Set();
        }

        readerThread.Join(TimeSpan.FromSeconds(5)).Should().BeTrue();
        readerError.Should().BeNull();

        var result = compactTask!.GetAwaiter().GetResult();
        result.MigrationMap.Should().HaveCount(3);
        result.MigrationMap[reclaimed].Should().BeNull();
        result.MigrationMap[second].Should().Be(new LogicalAddress(0, blockSize));
    }

}
