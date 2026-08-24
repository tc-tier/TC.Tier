namespace TC.Tier.Runtime.Tests.Storage;

/// <summary>
/// 引擎 N=2 专轮压测——§XI/§XII 修复（single-flight build-gate + EngineMeta 写面串行化 +
/// VII-7 终局兜底 flush + VII-8 索引扩容发布顺序）后的复验线。
/// <para>★ 断言核心是 红轮签名「Full meta 未更新（读到 stale 创建值）」：N=2 双消费者
///   并发建段/段满后，每个满段的 meta 必须是 state=Full + maxOffset 定格——读到创建值即复发。</para>
/// <para>★ reopen 完整性：重开恢复尾段必须与 live 尾段一致（VII-8 空洞曾使重开截断、数据不可达）。</para>
/// <para>★ logger 全程注入（TestConsoleLogger）——异常被吞分支在测试输出直接现形。</para>
/// </summary>
[Collection("LargeScaleIO")]
public sealed class EngineN2ConcurrentStressTests : IDisposable
{
    private readonly List<TestVolume> _vols = new();
    private const string DeviceName = "test";
    private const long Growth = 4 * 1024;

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

    /// <summary>
    /// N=2 多写者并发 Append——不挂起 + 同一 segId 恰好一次物理构建 + 满段 meta 全 Full（红轮签名）+
    /// Dispose 后 reopen 恢复完整尾段。
    /// </summary>
    [Fact]
    public async Task N2Writers_AllFullSegments_MetaRecordsFullState_ReopenIntact()
    {
        var vol = NewVol();
        int tailSegId;
        var options = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false);
        options = options.WithOptimization(options.Optimization with { WorkerConsumers = 2 });
        using var builder = options.Builder(vol.Fs, logger: TestConsoleLogger.Instance);
        using (var dev = builder.Start())
        {
            dev.WaitForReady();

            const int threads = 6;
            const int perThread = 400;
            var payload = new byte[512];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var tasks = Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < perThread; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    dev.Append(payload);
                }
            })).ToArray();

            var all = Task.WhenAll(tasks);
            var timeout = Task.Delay(TimeSpan.FromSeconds(30));
            (await Task.WhenAny(all, timeout)).Should().Be(all, "N=2 并发 Append 不应挂起");
            await all;   // ★ 写者异常必须浮出（faulted 也算"完成"——卡 Empty 段会以 SegmentCreationException 形式现形）

            // 静默：等全部满段（0..tail-1）的 Full meta 落盘（worker Full 任务排空 + 引擎 flusher 刷盘）
            tailSegId = dev.AllocatedTail.SegId;
            tailSegId.Should().BeGreaterThan(10, "6×400×512B ≈ 1.2MB，4KB 段应建数百段");

            var duplicates = builder.Engine.PhysicalBuildLog.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            duplicates.Should().BeEmpty("同一 segId 的物理构建必须恰好一次（build-gate single-flight）");

            await WaitForAllFullMetaOnDiskAsync(vol.Fs, tailSegId);
        }

        // 红轮签名断言：Dispose 后新读取器逐段读 ADS——满段必须是 Full 终态，读到创建值 = 复发
        var verifierFs = vol.Fs;
        for (var segId = 0; segId < tailSegId; segId++)
        {
            var ok = ReadTupleOnDisk(verifierFs, segId, out var gl, out var mo, out var st);
            ok.Should().BeTrue($"seg{segId} 元组应存在（建段必写）");
            st.Should().Be(StableState.Full,
                $"seg{segId} 满段 meta 必须是 Full——读到创建值即红轮签名复发（stale 创建值 = {StableState.Ready}）");
            mo.Should().Be(Growth, $"seg{segId} maxOffset 应定格到段大小");
            gl.Should().Be(Growth, $"seg{segId} growthLimit");
        }

        // reopen 完整性：重开恢复尾段必须追平 live（VII-8 空洞曾使重开截断在首个空洞、数据不可达）。
        // 容差 1 段（防御）：区间统一后尾停驻 (seg,limit) 不再虚高成 (tail,0x0)，此容差保留为宽限。
        using var reopened = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false).Builder(vol.Fs).Start();
        reopened.WaitForReady();
        reopened.AllocatedTail.SegId.Should().BeGreaterThanOrEqualTo(tailSegId - 1,
            $"reopen 恢复尾段必须追平 live 尾段（live={tailSegId}，容差 1 段零数据边界）——大幅截断即 VII-8 复发（数据不可达）");
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

    /// <summary>
    /// N=2 混合负载：持续 Append + ReclaimHead 截断并发——不挂起 + 建段 single-flight 维持。
    /// </summary>
    [Fact]
    public async Task N2MixedAppendReclaim_NoHangNoDoubleBuild()
    {
        var vol = NewVol();
        var options = new StorageEngineOptions(DeviceName, segmentGrowthLimit: Growth).WithPreallocateFile(false);
        options = options.WithOptimization(options.Optimization with { WorkerConsumers = 2 });
        using var builder = options.Builder(vol.Fs, logger: TestConsoleLogger.Instance);
        using (var dev = builder.Start())
        {
            dev.WaitForReady();

            var payload = new byte[512];
            for (var i = 0; i < payload.Length; i++) payload[i] = (byte)((i + 3) & 0xFF);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
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
            var timeout = Task.Delay(TimeSpan.FromSeconds(40));
            (await Task.WhenAny(all, timeout)).Should().Be(all, "N=2 Append×ReclaimHead 混合负载不应挂起");
            await all;   // ★ 写者异常必须浮出（faulted 也算"完成"——不与"不挂起"断言互相掩盖）

            // VII-9 已修（过期任务守卫）：混合负载建段也必须 single-flight——恢复硬断言
            await WaitForBuildLogStableAsync(builder.Engine);
            var duplicates = builder.Engine.PhysicalBuildLog.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            duplicates.Should().BeEmpty(
                $"混合负载下同一 segId 的物理构建必须恰好一次（过期任务守卫回归——重复即已删段复活风险复发）");
        }
    }

    /// <summary>等全部满段（0..tailSegId-1）的 Full meta 落盘（旁路读取器直读 ADS，缓存不命中即读盘）。</summary>
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
        // 超时不在此抛——断言阶段给出逐段精确失败
    }

    /// <summary>等待建段日志计数连续两次采样稳定。</summary>
    private static async Task WaitForBuildLogStableAsync(StorageEngine dev)
    {
        var prev = -1;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            var cur = dev.PhysicalBuildLog.Count;
            if (cur == prev) return;
            prev = cur;
        }
    }
}
