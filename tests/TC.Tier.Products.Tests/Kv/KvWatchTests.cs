using FluentAssertions;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// W9 契约测试——Watch 变更流（F5，etcd Watch 对齐）：可见性点发布（Put/Delete 事件按提交序）、
/// 前缀过滤、原子批 Confirm 后逐条事件、地址游标续传（无丢无重）、跨重开历史补扫、
/// 慢订阅者断连（有界通道不反压写路径）、续传越回收线 fail-fast、检查点尾作游标、并发写对账。
/// <para>★ CollectAsync 启动即完成订阅注册（async 同步段执行到实时等待点），先注册后写 = 时序确定；
/// 收集以 count 驱动 + 超时 fail-fast（Watch 流无自然终点，禁止无界等待）。</para>
/// <para>★ long 键 LE 布局（W7 同款）：byte0 = 最低字节——前缀 0x10 家族 = [10,0,…]。</para>
/// </summary>
public class KvWatchTests
{
    private static readonly long K1 = 0x11;
    private static readonly long K2 = 0x12;
    private static readonly long K3 = 0x13;
    private static readonly long K20 = 0x20;   // 前缀 0x10 家族之外

    private static TierKvOptions Opts(string suffix, int? watchCapacity = null)
    {
        var o = TierKvOptions.Default.WithKvName("tier-kv-w9-" + suffix);
        return watchCapacity is { } cap ? o.WithWatchChannelCapacity(cap) : o;
    }

    /// <summary>收集恰好 count 个事件（订阅注册在启动时同步完成；超时 fail-fast）。</summary>
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
    public async Task LiveEvents_PutDelete_OrderedByCommit()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("live"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        var watch = CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: 3);

        await s.PutFormattedAsync(K1, 1);
        await s.PutFormattedAsync(K2, 2);
        await s.DeleteAsync(K1);

        var events = await watch;
        events.Should().HaveCount(3);
        events[0].Key.Should().Be(K1);
        events[0].Kind.Should().Be(KvWatchEventKind.Put);
        events[1].Key.Should().Be(K2);
        events[1].Kind.Should().Be(KvWatchEventKind.Put);
        events[2].Key.Should().Be(K1);
        events[2].Kind.Should().Be(KvWatchEventKind.Delete);
        events.Select(e => e.Address).Should().BeInAscendingOrder("事件地址 = Ring 追加序，全局单调");
    }

    [Fact]
    public async Task PrefixFilter_ByteOrder()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("prefix"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        // 订阅 1 字节前缀 0x10（W7 家族形态：0x10/0x110 = [10,*]；0x20 家族外不入订阅）
        const long F1 = 0x10;     // [10,00,…]
        const long F2 = 0x110;    // [10,01,…]
        var watch = CollectAsync(kv.WatchAsync(LogicalAddress.Empty, 0x10, prefixByteLength: 1), count: 2);

        await s.PutFormattedAsync(F1, 1);
        await s.PutFormattedAsync(K20, 2);
        await s.PutFormattedAsync(F2, 3);

        var events = await watch;
        events.Select(e => e.Key).Should().Equal([F1, F2], "前缀 0x10 家族按序产出，0x20 家族被过滤");
    }

    [Fact]
    public async Task AtomicBatch_EventsAfterConfirm_TombstoneDelete()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("batch"));
        using var s = kv.CreateSession(KvSessionConditions.None);
        await s.PutFormattedAsync(K3, 30);   // 批内将删除的既有 key（写入发生在订阅前 = 补扫历史）

        var watch = CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: 4);

        s.BeginAtomicBatch();
        await s.PutFormattedAsync(K1, 1);
        await s.PutFormattedAsync(K2, 2);
        await s.DeleteAsync(K3);
        await s.CommitBatchAsync();

        var events = await watch;
        events.Should().HaveCount(4, "K3 初始写 = 订阅前历史（补扫产出）+ 批 = 提交点后逐条事件");
        events.Select(e => (e.Key, e.Kind)).Should().Equal(
            new (long Key, KvWatchEventKind Kind)[]
            {
                (K3, KvWatchEventKind.Put),      // 历史补扫
                (K1, KvWatchEventKind.Put),      // 批内序 = 发布序
                (K2, KvWatchEventKind.Put),
                (K3, KvWatchEventKind.Delete),   // 墓碑 = Delete 事件
            }, "严格序 = 发布序");
        events.Select(e => e.Address).Should().BeInAscendingOrder("墓碑地址 = 墓碑记录地址，全局单调");
    }

    [Fact]
    public async Task Resume_FromLastAddress_NoLossNoDup()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("resume"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        // 1. 首轮订阅：写 3 → 收 3（记录游标）
        await s.PutFormattedAsync(K1, 1);
        await s.PutFormattedAsync(K2, 2);
        await s.PutFormattedAsync(K3, 3);
        var first = await CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: 3);
        var cursor = first.Max(e => e.Address);

        // 2. 断开期间写 3
        await s.PutFormattedAsync(K1, 11);
        await s.DeleteAsync(K2);
        await s.PutFormattedAsync(K20, 20);

        // 3. 续传 (cursor, ...]——历史补扫补齐断开窗口，无丢无重
        var resumed = await CollectAsync(kv.WatchAsync(cursor), count: 3);
        resumed.Select(e => (e.Key, e.Kind)).Should().Equal(
            new (long Key, KvWatchEventKind Kind)[]
            {
                (K1, KvWatchEventKind.Put),
                (K2, KvWatchEventKind.Delete),
                (K20, KvWatchEventKind.Put),
            }, "续传 = 真源历史补扫 + 实时直通无缝衔接，严格序 = 发布序");
        resumed.All(e => e.Address > cursor).Should().BeTrue("续传开区间 (cursor, ...]");
    }

    [Fact]
    public async Task Resume_AfterRestart_HistoryRescanned()
    {
        using var vol = new TestVolume();
        var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("restart"));
        using (var s = kv.CreateSession(KvSessionConditions.None))
        {
            await s.PutFormattedAsync(K1, 1);
            await s.PutFormattedAsync(K2, 2);
            await s.CompletePendingAsync();
        }
        // 规范事件游标 = 已见最大事件地址（先收一轮确认游标形态；裸 TailAddress 是「下一写入位」，
        // 恢复后首条记录地址与之重合——(from, ...] 开区间语义下不作游标）
        var seen = await CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: 2);
        var cursorBeforeRestart = seen.Max(e => e.Address);
        await kv.DisposeAsync();

        // 重开（同卷）——恢复后 Ring 尾 = 悬干裁决后的干净提交边界
        await using var kv2 = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("restart"));

        var history = await CollectAsync(kv2.WatchAsync(LogicalAddress.Empty), count: 2);
        history.Select(e => (e.Key, e.Kind)).Should().Equal(
            new (long Key, KvWatchEventKind Kind)[]
            {
                (K1, KvWatchEventKind.Put),
                (K2, KvWatchEventKind.Put),
            }, "恢复后从头订阅 = Ring 真源历史补扫（悬干记录已被 Abort 截断，无假事件）");

        using (var s2 = kv2.CreateSession(KvSessionConditions.None))
            await s2.PutFormattedAsync(K1, 111);

        var resumed = await CollectAsync(kv2.WatchAsync(cursorBeforeRestart), count: 1);
        resumed.Should().ContainSingle("重开后从检查点前事件游标续传只收新写");
        resumed[0].Key.Should().Be(K1);
        resumed[0].Address.Should().BeGreaterThan(cursorBeforeRestart);
    }

    [Fact]
    public void WatchHub_BoundedChannel_SlowSubscriberDisconnected()
    {
        // 单线程零竞态：无消费者灌容量+1 条 → 第 3 条 TryWrite 失败 = 断连（通道完结 + 注销）
        var hub = new KvWatchHub<long>(capacity: 2, publishedStart: LogicalAddress.Empty);
        var (reader, _) = hub.Subscribe(0, 0);

        hub.Publish(1, KvWatchEventKind.Put, new LogicalAddress(0, 10));
        hub.Publish(2, KvWatchEventKind.Put, new LogicalAddress(0, 20));
        reader.Completion.IsCompleted.Should().BeFalse("容量内——订阅保持");

        hub.Publish(3, KvWatchEventKind.Put, new LogicalAddress(0, 30));

        // BoundedChannel 完成语义：断连（Writer 完成）后缓冲按序交付，排空才完成——
        // 读出恰为前 2 条、第 3 条被弃（写满即断连，写路径不被反压）
        reader.TryRead(out var ev1).Should().BeTrue();
        ev1.Address.Should().Be(new LogicalAddress(0, 10));
        reader.TryRead(out var ev2).Should().BeTrue();
        ev2.Address.Should().Be(new LogicalAddress(0, 20));
        reader.TryRead(out _).Should().BeFalse("容量 2：第 3 条写入失败被弃");
        reader.Completion.IsCompleted.Should().BeTrue("缓冲排空后流终结（断连信号）");
        hub.PublishedWatermark.Should().Be(new LogicalAddress(0, 30), "断连不回退发布水位");
    }

    [Fact]
    public void WatchHub_WatermarkDedup_SkipsRepublishedAddress()
    {
        // 水位去重：地址 ≤ 已发布水位 = 已被历史补扫覆盖 → 跳过（续传无重）
        var hub = new KvWatchHub<long>(capacity: 8, publishedStart: LogicalAddress.Empty);
        var (reader, watermark) = hub.Subscribe(0, 0);

        hub.Publish(1, KvWatchEventKind.Put, new LogicalAddress(0, 10));
        hub.PublishedWatermark.Should().Be(new LogicalAddress(0, 10));

        hub.Publish(1, KvWatchEventKind.Put, new LogicalAddress(0, 5));    // 旧地址重发（交错窗口）——skip
        reader.TryRead(out var ev).Should().BeTrue();
        ev.Address.Should().Be(new LogicalAddress(0, 10), "仅水位之上的地址入通道");
        reader.TryRead(out _).Should().BeFalse("水位之下地址不重复入通道");
        watermark.Should().Be(LogicalAddress.Empty, "注册快照 = 注册时水位");
    }

    [Fact]
    public async Task Resume_BeforeRecycleLine_FailFast()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("recycled"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        // 同 key 覆写 3 次；先取首事件地址（回收前——回收后补扫区间清空收不到）
        // ★ 显式 Committed：回收安全下界只推已持久化前缀（默认内存档不落盘——回收线不动）
        await s.PutFormattedAsync(K1, 1, policy: KvCommitPolicy.Committed);
        await s.PutFormattedAsync(K1, 2, policy: KvCommitPolicy.Committed);
        await s.PutFormattedAsync(K1, 3, policy: KvCommitPolicy.Committed);
        var first = await CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: 1);
        var staleCursor = first[0].Address;

        await kv.ReclaimAsync();   // 存活 = 第 3 条，前两条被截断
        staleCursor.Should().BeLessThan(kv.BeginAddress, "首条记录已被回收取代");

        var act = async () =>
        {
            await foreach (var _ in kv.WatchAsync(staleCursor)) break;
        };
        await act.Should().ThrowAsync<InvalidOperationException>("续传点已越出回收线——历史事件已被回收，fail-fast（etcd compacted 同义）");

        // 合法游标不受影响：Empty = 从头（自回收线起）
        var act2 = async () =>
        {
            await foreach (var _ in kv.WatchAsync(LogicalAddress.Empty)) break;
        };
        await act2.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CheckpointTail_AsWatchCursor()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("ckpt"));
        using var s = kv.CreateSession(KvSessionConditions.None);

        await s.PutFormattedAsync(K1, 1);
        await s.PutFormattedAsync(K2, 2);
        var cp = await kv.CheckpointAsync();
        cp.WatchCursor.Should().BeGreaterThan(LogicalAddress.Empty, "检查点游标 = 时点最后事件地址");

        await s.PutFormattedAsync(K3, 3);

        var events = await CollectAsync(kv.WatchAsync(cp.WatchCursor), count: 1);
        events.Should().ContainSingle("检查点 WatchCursor = 规范续传游标（时点已发布水位）");
        events[0].Key.Should().Be(K3);
        events[0].Address.Should().BeGreaterThan(cp.WatchCursor);
    }

    [Fact]
    public async Task ConcurrentWriters_NoDupNoLoss_SubscribersAgree()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("concurrent"));

        const int writers = 4;
        const int perWriter = 25;
        const int total = writers * perWriter;

        // key 编码（LE 布局）：byte0=0x10 恒同（家族），byte1=w（写者），byte2+=i（序号）——
        // 1 字节前缀 0x10 = 全家族，2 字节前缀 [10,00] = w=0 批次
        static long Key(int w, int i) => 0x10 | ((long)w << 8) | ((long)i << 16);

        var watchAll = CollectAsync(kv.WatchAsync(LogicalAddress.Empty), count: total);
        var watchHalf = CollectAsync(
            kv.WatchAsync(LogicalAddress.Empty, 0x10, prefixByteLength: 2), count: perWriter);

        var tasks = Enumerable.Range(0, writers).Select(async w =>
        {
            using var ws = kv.CreateSession(KvSessionConditions.None);
            for (var i = 0; i < perWriter; i++)
                await ws.PutFormattedAsync(Key(w, i), w * 1000 + i);
        });
        await Task.WhenAll(tasks);

        var all = await watchAll;
        var half = await watchHalf;

        all.Select(e => e.Address).Should().OnlyHaveUniqueItems("水位去重：补扫与实时通道地址严格不相交");
        all.Should().HaveCount(total, "全部写入无一丢失（并发写交错下的发布闭环）");
        all.Select(e => e.Key).Should().BeEquivalentTo(
            Enumerable.Range(0, writers).SelectMany(w => Enumerable.Range(0, perWriter)
                .Select(i => Key(w, i))));
        half.Should().HaveCount(perWriter, "前缀 [10,00] 只收 w=0 批次");
        half.Select(e => e.Kind).Should().OnlyContain(k => k == KvWatchEventKind.Put);
        half.Select(e => e.Key).Should().OnlyContain(k => (k & 0xFF00) == 0, "byte1=0 即 w=0 批次");
    }
}
