namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 引擎 N&gt;2 消费者复现轮——定位「workerConsumers&gt;2 高并发死锁/协调问题」。
/// <para>★ 负载与 <see cref="EngineN2ConcurrentStressTests"/> 同款，仅消费者数上调——同款断言，
///   挂起/双建/meta 失真即复现。</para>
/// </summary>
[Collection("LargeScaleIO")]
public sealed class EngineN4ConcurrentStressTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();
    private const string DeviceName = "test";
    private const long Growth = 4 * 1024;
    private readonly int _consumers;

    public EngineN4ConcurrentStressTests() => _consumers = 4;

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


    /// <summary>N=4 多写者并发 Append——不挂起 + single-flight + 满段 meta 全 Full。</summary>
    [Fact]
    public async Task N4Writers_AllFullSegments_MetaRecordsFullState()
    {
        var vol = NewVol();
        int tailSegId;
        var options = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false);
        options = options.WithOptimization(options.Optimization with { WorkerConsumers = _consumers });
        using var builder = options.Builder(vol.Fs, logger: TestConsoleLogger.Instance);
        using (var dev = builder.Start())
        {
            dev.WaitForReady();

            const int threads = 6;
            const int perThread = 400;
            var payload = new byte[512];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    dev.Append(payload);
                }
            })).ToArray();

            var all = Task.WhenAll(tasks);
            var timeout = Task.Delay(TimeSpan.FromSeconds(60));
            (await Task.WhenAny(all, timeout)).Should().Be(all, $"N={_consumers} 并发 Append 不应挂起");
            await all;   // ★ 写者异常必须浮出（faulted 也算"完成"——卡 Empty 段会以 SegmentCreationException 形式现形）

            tailSegId = dev.AllocatedTail.SegId;
            var duplicates = builder.Engine.PhysicalBuildLog.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            duplicates.Should().BeEmpty($"N={_consumers} 同一 segId 的物理构建必须恰好一次");

            await WaitForAllFullMetaOnDiskAsync(vol.Fs, tailSegId);
        }

        var verifierFs = vol.Fs;
        for (var segId = 0; segId < tailSegId; segId++)
        {
            var ok = ReadTupleOnDisk(verifierFs, segId, out var gl, out var mo, out var st);
            ok.Should().BeTrue($"seg{segId} 元组应存在（建段必写）");
            st.Should().Be(StableState.Full, $"seg{segId} 满段 meta 必须是 Full");
            mo.Should().Be(Growth, $"seg{segId} maxOffset 应定格到段大小");
            gl.Should().Be(Growth, $"seg{segId} growthLimit");
        }
    }

    /// <summary>旁路读段元组（FileExtra 直读——红轮签名断言用，不经引擎缓存）。</summary>
    private static bool ReadTupleOnDisk(IFileSystem fs, int segId, out long growthLimit, out long maxOffset, out StableState state)
    {
        growthLimit = maxOffset = 0;
        state = default;
        try
        {
            var extra = fs.Stat($"{DeviceName}/{DeviceName}.{segId}").FileExtra;
            if (extra.IsEmpty) return false;
            if (SegmentTupleCodec.Decode(extra.Span) is not { } t) return false;
            growthLimit = t.GrowthLimit;
            maxOffset = t.MaxOffset;
            state = t.State;
            return true;
        }
        catch (FileIOException)
        {
            return false;
        }
    }

    /// <summary>N=4 混合负载：持续 Append + ReclaimHead 截断并发——不挂起。</summary>
    [Fact]
    public async Task N4MixedAppendReclaim_NoHang()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false);
        options = options.WithOptimization(options.Optimization with { WorkerConsumers = _consumers });
        using var builder = options.Builder(vol.Fs, logger: TestConsoleLogger.Instance);
        using (var dev = builder.Start())
        {
            dev.WaitForReady();

            var payload = new byte[512];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i + 3) & 0xFF);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            long appended = 0;

            var writer = Task.Run(() =>
            {
                for (var i = 0; i < 3000; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    dev.Append(payload);
                    Interlocked.Increment(ref appended);
                }
            });

            var truncator = Task.Run(() =>
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    if (Volatile.Read(ref appended) > 50 && dev.AllocatedTail.SegId > 3)
                    {
                        try { dev.ReclaimHead(new LogicalAddress(dev.AllocatedTail.SegId - 2, 0)); }
                        catch { /* 截断失败可接受——关注挂起/双建 */ }
                        Thread.Sleep(5);
                    }
                    if (Volatile.Read(ref appended) >= 3000) break;
                }
            });

            var all = Task.WhenAll(writer, truncator);
            var timeout = Task.Delay(TimeSpan.FromSeconds(60));
            (await Task.WhenAny(all, timeout)).Should().Be(all, $"N={_consumers} Append×ReclaimHead 混合负载不应挂起");
            await all;   // ★ 写者异常必须浮出（faulted 也算"完成"——不与"不挂起"断言互相掩盖）

            var duplicates = builder.Engine.PhysicalBuildLog.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            duplicates.Should().BeEmpty($"N={_consumers} 混合负载下同一 segId 的物理构建必须恰好一次");
        }
    }

    /// <summary>等全部满段（0..tailSegId-1）的 Full meta 落盘。</summary>
    private static async Task WaitForAllFullMetaOnDiskAsync(IFileSystem fs, int tailSegId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var allFull = true;
            for (var segId = 0; segId < tailSegId; segId++)
            {
                if (!ReadTupleOnDisk(fs, segId, out _, out var mo, out var st)
                    || st != StableState.Full || mo != Growth)
                {
                    allFull = false;
                    break;
                }
            }
            if (allFull) return;
            await Task.Delay(100);
        }
    }
}
