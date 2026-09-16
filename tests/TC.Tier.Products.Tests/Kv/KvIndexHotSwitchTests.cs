using FluentAssertions;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W-Hot 契约测试——运行中主索引热切换（D9 四步：开窗→追平→发布→释放）：
/// 切换后数据完整（重放+双写无丢）、服务不中断（继续读写删）、旧索引释放、
/// 并发写窗不丢（双写窗内写入全部落新索引）、重启回落 Options 装配（fail-safe 重放）、
/// 非法工厂状态不脏、Watch 零联动（Ring 真源不受索引切换影响）。
/// </summary>
public class KvIndexHotSwitchTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-hot-" + suffix);

    /// <summary>BTree 切换目标工厂（Runtime 公开封闭形态——字节序比较器内置）。</summary>
    private static Func<TierKvOptions, RingBase<long>, IIndex<long>> BTreeFactory(TestVolume vol)
        => (o, ring) => new ByteOrderBTreeIndex<long>(vol.Fs, TierKvAssembly.BTreeSettings(vol.Fs, o), keyResolver: ring);

    [Fact]
    public async Task Switch_HashToBTree_DataIntact_ServiceContinues()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("basic"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        for (long i = 1; i <= 20; i++)
            await s.PutFormattedAsync(i, i * 100);

        var oldIndex = kv.IndexCurrent;
        oldIndex.Should().BeAssignableTo<ProbingIndexBase<long>>("起始装配为 Hash 探测族（封闭形态派生肢）");

        var result = await kv.SwitchIndexAsync(BTreeFactory(vol));

        result.SwitchStartAddress.IsValid.Should().BeTrue("切换起点 = 双写窗开启时 Ring 尾");
        kv.IndexCurrent.Should().NotBeSameAs(oldIndex, "发布相原子换绑索引引用");
        kv.IndexCurrent.Should().BeAssignableTo<SortedIndexBase<long>>("新索引为比较族（BTree）");

        // 切换前数据全部可读（重放追平验证）
        kv.Count.Should().Be(20);
        for (long i = 1; i <= 20; i++)
            (await kv.TryGetFormattedAsync(i)).Found.Should().BeTrue($"key {i} 经新索引可读");

        // 服务不中断：切换后继续读写删（新索引承载）
        await s.PutFormattedAsync(21, 2100);
        await s.DeleteAsync(5);
        (await kv.TryGetFormattedAsync(21)).Value.Should().Be(2100);
        (await kv.TryGetFormattedAsync(5)).Found.Should().BeFalse("删除经新索引生效");
        kv.Count.Should().Be(20);
    }

    [Fact]
    public async Task Switch_OldIndexReleased()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("release"));
        using (var s = kv.CreateSession(KvSessionConditions.None))
            await s.PutFormattedAsync(1, 1);

        var oldIndex = kv.IndexCurrent;
        await kv.SwitchIndexAsync(BTreeFactory(vol));

        kv.IndexCurrent.Should().NotBeSameAs(oldIndex);

        // 旧索引已释放：经引擎交互点触发 disposed 状态闸门（探测族持久化帧触发口走引擎写）
        var oldProbing = (ProbingIndexBase<long>)oldIndex;
        var act = () => oldProbing.CheckpointFrame();
        act.Should().Throw<Exception>("旧索引在发布后经排水 Dispose——引擎交互被状态闸门拒绝");
    }

    [Fact]
    public async Task Switch_WindowConcurrentWrites_NoLoss()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("window"));
        using var setup = kv.CreateSession(KvSessionConditions.None);
        for (long i = 1; i <= 50; i++)
            await setup.PutFormattedAsync(i, i);
        await setup.CompletePendingAsync();

        // 切换窗内 4 写者并发写（双写窗覆盖验证——全部写入必须落新索引）
        const int writers = 4, perWriter = 50;
        var writeTask = Task.Run(async () =>
        {
            var tasks = Enumerable.Range(0, writers).Select(async w =>
            {
                using var ws = kv.CreateSession(KvSessionConditions.None);
                for (long i = 1; i <= perWriter; i++)
                    await ws.PutFormattedAsync(1000 + w * perWriter + i, w * perWriter + i,
                        KvCommitPolicy.FireAndForget);
                await ws.CompletePendingAsync();
            });
            await Task.WhenAll(tasks);
        });

        await kv.SwitchIndexAsync(BTreeFactory(vol));
        await writeTask;

        // 对账探针：Ring 真源 vs 索引计数
        var ringKeys = new List<long>();
        await foreach (var (k, a, t) in kv.Ring.ScanAsync(kv.Ring.BeginAddress, kv.Ring.TailAddress))
            if (!t) ringKeys.Add(k);
        // 全部数据经新索引可读（切换前 50 + 窗内 200）
        kv.Count.Should().Be(250, "切换窗内并发写入全部落新索引——无丢");
        for (long i = 1; i <= 50; i++)
            (await kv.TryGetFormattedAsync(i)).Found.Should().BeTrue();
        for (long key = 1001; key <= 1000 + writers * perWriter; key++)
            (await kv.TryGetFormattedAsync(key)).Found.Should().BeTrue($"窗内写入 key {key} 不丢");
    }

    [Fact]
    public async Task Switch_ReopenAfterSwitch_FailSafeReplay_DataIntact()
    {
        using var vol = new TestVolume();
        var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("reopen"));
        using (var s = kv.CreateSession(KvSessionConditions.None))
        {
            for (long i = 1; i <= 10; i++)
                await s.PutFormattedAsync(i, i * 10);
        }

        await kv.SwitchIndexAsync(BTreeFactory(vol));   // 切 BTree（运行时视图）
        await kv.CheckpointAsync();                      // 检查点落 BTree 帧
        await kv.DisposeAsync();

        // 重开 = Options 装配 Hash（热切换不持久化）——BTree 帧族不匹配 → fail-safe 全量重放，数据等价
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("reopen"));
        kv2.Count.Should().Be(10, "重启回落原族装配，数据经重放完整重建");
        for (long i = 1; i <= 10; i++)
            (await kv2.TryGetFormattedAsync(i)).Value.Should().Be(i * 10);
    }

    [Fact]
    public async Task Switch_InvalidFactory_StateClean()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("invalid"));
        using var s = kv.CreateSession(KvSessionConditions.None);
        await s.PutFormattedAsync(1, 1);

        Console.WriteLine($"[probe] put1 done, state={kv.SessionEpvs.CurrentState()}");
        var act = async () => await kv.SwitchIndexAsync((o, ring) => null!);
        await act.Should().ThrowAsync<ArgumentException>();
        Console.WriteLine($"[probe] switch threw, state={kv.SessionEpvs.CurrentState()}");

        // 状态不脏：原索引服务正常（双写窗未开启/已关、数据可读写）
        kv.Count.Should().Be(1);
        (await kv.TryGetFormattedAsync(1)).Found.Should().BeTrue();
        Console.WriteLine($"[probe] before put2, state={kv.SessionEpvs.CurrentState()}");
        await s.PutFormattedAsync(2, 2);
        Console.WriteLine($"[probe] put2 done");
        (await kv.TryGetFormattedAsync(2)).Found.Should().BeTrue();
    }

    [Fact]
    public async Task Switch_DuringWatch_EventsContinue()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("watch"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        // Watch 订阅（先启动收集注册）→ 写 2 → 切换 → 写 2——事件连续（Watch 走 Ring 真源，零联动）
        var watch = CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: 4);

        await s.PutFormattedAsync(1, 1);
        await s.PutFormattedAsync(2, 2);
        await kv.SwitchIndexAsync(BTreeFactory(vol));
        await s.PutFormattedAsync(3, 3);
        await s.PutFormattedAsync(4, 4);

        var events = await watch;
        events.Select(e => e.Key).Should().Equal([1L, 2L, 3L, 4L], "索引切换不影响 Watch 事件流");
    }

    private static async Task<List<KvWatchEvent<long>>> CollectAsync(
        IAsyncEnumerable<KvWatchEvent<long>> source, int count, int timeoutMs = 10_000)
    {
        var list = new List<KvWatchEvent<long>>();
        using var cts = new CancellationTokenSource(timeoutMs);
        await foreach (var ev in source.WithCancellation(cts.Token))
        {
            list.Add(ev);
            if (list.Count >= count) break;
        }
        return list;
    }

    [Fact]
    public async Task Switch_WindowConcurrentWrites_NoLoss_Stress()
    {
        // ★ 竞态窗口回归门（多轮放大）：追平游标（分配尾）越过「已分配未完成」在途槽——
        //   该写者后续换绑落在被丢弃的旧索引即永久丢失。修复=追平推进前等在途排空
        //   （Ring.WaitForNoInFlightWrites）。单轮编排是概率触发，10 轮交替切换提高覆盖。
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("stress"));

        const int rounds = 10, writers = 4, perWriter = 50;
        var nextBase = 1000L;
        for (var round = 0; round < rounds; round++)
        {
            var roundBase = nextBase;
            var writerAddrs = new System.Collections.Concurrent.ConcurrentDictionary<long, LogicalAddress>();
            var writeTask = Task.Run(async () =>
            {
                var tasks = Enumerable.Range(0, writers).Select(async w =>
                {
                    using var ws = kv.CreateSession(KvSessionConditions.None);
                    for (long i = 1; i <= perWriter; i++)
                    {
                        var k = roundBase + w * perWriter + i;
                        var addr = await ws.PutFormattedAsync(k, k, KvCommitPolicy.FireAndForget);
                        writerAddrs[k] = addr;
                    }
                    await ws.CompletePendingAsync();
                });
                await Task.WhenAll(tasks);
            });

            await kv.SwitchIndexAsync(BTreeFactory(vol));   // 每轮新 BTree 实例（同族异实例——切换协议全链每轮走全）
            await writeTask;

            var expectedTotal = roundBase - 1000L + writers * perWriter;   // 前轮累计 + 本轮 200
            // 对账探针：Ring 真源 vs 索引——丢失时输出丢失 key 及其 Ring 地址（定位越过窗口）
            var ringKeyAddrs = new Dictionary<long, LogicalAddress>();
            await foreach (var (k, a, t) in kv.Ring.ScanAsync(kv.Ring.BeginAddress, kv.Ring.TailAddress))
                if (!t) ringKeyAddrs[k] = a;
            var idxKeys = new HashSet<long>();
            foreach (var k in ringKeyAddrs.Keys)
                if (kv.TryGetFormatted(k, out _)) idxKeys.Add(k);
            if (idxKeys.Count != expectedTotal)
            {
                var missing = ringKeyAddrs.Keys.Where(k => !idxKeys.Contains(k)).OrderBy(k => k).ToList();
                var winfo = string.Join(",", missing.Select(k =>
                    writerAddrs.TryGetValue(k, out var wa) ? $"{k}@ring={ringKeyAddrs[k]}@writer={wa}" : $"{k}@NO-WRITER-REC"));
                Console.WriteLine($"[diag] round={round} roundBase={roundBase} count={kv.Count} " +
                    $"ring={ringKeyAddrs.Count} idx={idxKeys.Count} missing=[{winfo}]");
            }
            kv.Count.Should().Be(expectedTotal,
                $"第 {round} 轮窗内写入全部落新索引——无丢（在途排空判据）；Ring 真源非墓碑 {ringKeyAddrs.Count} 条");
            for (var w = 0; w < writers; w++)
                for (long i = 1; i <= perWriter; i++)
                    (await kv.TryGetFormattedAsync(roundBase + w * perWriter + i)).Found.Should()
                        .BeTrue($"第 {round} 轮 key {roundBase + w * perWriter + i} 不丢");
            nextBase += writers * perWriter;
        }
    }
}
