using System.Collections.Concurrent;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Tests.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.ProbingIndex;

/// <summary>
/// HashIndex 并发契约测试（W0 收口——审计 #173/#237/#268/#240/#274 + Insert×GrowIndex 换代竞态）。
/// <para>★ 契约面：</para>
/// <list type="bullet">
/// <item>并发 Insert×强制增长（多轮换代）不丢条目——换代竞态（落位废弃代）在修复前必丢；</item>
/// <item>并发同 key Upsert 收敛单条目（废 Tentative 两阶段后无二次 CAS 丢失/双 entry）；</item>
/// <item>并发 Upsert×Delete 收敛（键要么缺席要么值∈upsert 集，无幽灵/无假删）；</item>
/// <item>恢复还原溢出池 bump 指针（#411）——恢复后再分配溢出桶不别名覆盖已恢复链条。</item>
/// </list>
/// <para>★ 注册时序契约：并发下判等闭环读回他写者刚落位条目，要求 resolver 注册先于 Insert
/// （真实 Ring 同构——record 先写日志后插索引）。</para>
/// </summary>
public class HashIndexConcurrencyTests
{
    /// <summary>线程安全 resolver（并发 Insert 判等闭环回调 TryGetKey——字典版有竞态）。</summary>
    internal sealed class ConcurrentKeyResolver<TKey> : IKeyResolver<TKey>
        where TKey : unmanaged, IEquatable<TKey>
    {
        private readonly ConcurrentDictionary<LogicalAddress, TKey> _map = new();
        private readonly ConcurrentQueue<(LogicalAddress Addr, TKey Key)> _ordered = new();

        public void Put(LogicalAddress addr, TKey key)
        {
            _map[addr] = key;
            _ordered.Enqueue((addr, key));
        }

        public LogicalAddress FlushedWatermark { get; set; } = LogicalAddress.Empty;

        public bool TryGetKey(LogicalAddress addr, out TKey key) => _map.TryGetValue(addr, out key!);

        public LogicalAddress GetFlushedWatermark() => FlushedWatermark;

        public async IAsyncEnumerable<(TKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(
            LogicalAddress begin, LogicalAddress end,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var (addr, key) in _ordered)
            {
                if (addr >= begin && addr < end)
                    yield return (key, addr, false);
            }
        }

        public IAsyncEnumerable<(TKey Key, LogicalAddress Address, bool IsTombstone)> ScanAsync(CancellationToken ct = default)
            => ScanAsync(LogicalAddress.Empty, new LogicalAddress(int.MaxValue, long.MaxValue), ct);
    }

    static (HashIndex<long> Index, ConcurrentKeyResolver<long> Resolver) CreateIndex(
        TestVolume vol, int hashTableCapacity = 1 << 20, int overflowPoolCapacity = 1 << 18)
    {
        var settings = TestProbingIndexSettingsFactory.On(vol, "hash-conc", hashTableCapacity, overflowPoolCapacity);
        var resolver = new ConcurrentKeyResolver<long>();
        var hx = TestProbingIndexSettingsFactory.NewHash<long>(vol, settings, resolver);
        return (hx, resolver);
    }

    // ══ 契约 1：并发 Insert × 多轮换代不丢条目 ══

    [Fact]
    public void ConcurrentInsert_ForcedGrowth_NoLoss()
    {
        using var vol = new TestVolume();
        // ★ 小容量（64）强制多轮 GrowIndex（64→128→…→4096）——换代窗口与并发落位全交错
        var (idx, resolver) = CreateIndex(vol, hashTableCapacity: 64, overflowPoolCapacity: 1 << 16);
        using (idx)
        {
            const int writers = 4, perWriter = 500;
            var begin = idx.BeginAddress;

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int i = 0; i < perWriter; i++)
                {
                    var key = w * perWriter + i + 1;                 // 全局唯一 key
                    var addr = new LogicalAddress(0, key * 8);       // 唯一伪 value 地址
                    resolver.Put(addr, key);                         // ★ 注册先于 Insert（判等闭环时序契约）
                    idx.Insert(key, addr, begin);
                }
            })).ToArray();
            Task.WaitAll(tasks);

            idx.EntryCount.Should().Be(writers * perWriter, "并发插入×换代：条目计数零丢失");
            for (var key = 1L; key <= writers * perWriter; key++)
            {
                var found = idx.Find(key);
                found.Should().NotBe(LogicalAddress.Empty, $"key {key} 在多轮换代后必命中");
                found.Offset.Should().Be(key * 8, $"key {key} 的 value 地址不被串写");
            }
        }
    }

    // ══ 契约 2：并发同 key Upsert 收敛单条目 ══

    [Fact]
    public void ConcurrentUpsert_SameKey_ConvergesToSingleEntry()
    {
        using var vol = new TestVolume();
        var (idx, resolver) = CreateIndex(vol);
        using (idx)
        {
            const int writers = 4;
            const long key = 0xABCDEF;
            var begin = idx.BeginAddress;
            var values = Enumerable.Range(0, writers).Select(w => (long)(w * 1_000_000 + 7)).ToArray();

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
            {
                for (int round = 0; round < 50; round++)
                {
                    var addr = new LogicalAddress(0, values[w] + round);
                    resolver.Put(addr, key);
                    idx.Insert(key, addr, begin);
                }
            })).ToArray();
            Task.WaitAll(tasks);

            idx.EntryCount.Should().Be(1, $"同 key 并发 Upsert {writers}×50 次必须收敛单条目（双 entry=契约破）");
            var final = idx.Find(key);
            final.Should().NotBe(LogicalAddress.Empty);
            // 值域：写者 w 的地址 ∈ [w*1M+7, w*1M+7+50)——终值必须落在某写者值域内
            (((final.Offset - 7) % 1_000_000 + 1_000_000) % 1_000_000).Should().BeLessThan(50,
                $"终值地址必须 ∈ upsert 写者值集，实际 {final.Offset}");
        }
    }

    // ══ 契约 3：并发 Upsert × Delete 收敛 ══

    [Fact]
    public void ConcurrentUpsertDelete_Converges()
    {
        using var vol = new TestVolume();
        var (idx, resolver) = CreateIndex(vol, hashTableCapacity: 256, overflowPoolCapacity: 1 << 14);
        using (idx)
        {
            const int keys = 300;
            var begin = idx.BeginAddress;

            // 预插全部 key（注册先行）
            for (var k = 1L; k <= keys; k++)
            {
                var addr = new LogicalAddress(0, k * 16);
                resolver.Put(addr, k);
                idx.Insert(k, addr, begin);
            }

            // 2 删除线程 + 2 覆写线程并发
            var deleters = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
            {
                for (var k = (long)w + 1; k <= keys; k += 2)
                    idx.Delete(k);   // 返回值语义：两删者抢同 key 至多一真——幂等重试收敛
            })).ToArray();
            var upserters = Enumerable.Range(0, 2).Select(w => Task.Run(() =>
            {
                for (var k = (long)w + 1; k <= keys; k += 2)
                {
                    for (int round = 0; round < 5; round++)
                    {
                        var addr = new LogicalAddress(0, k * 16 + 100_000 + round);
                        resolver.Put(addr, k);
                        idx.Insert(k, addr, begin);
                    }
                }
            })).ToArray();
            Task.WaitAll(deleters.Concat(upserters).ToArray());

            // 终态收敛：每 key 要么缺席（删在后）要么值 ∈ upsert 值集（写在后）——无幽灵、无假值
            long live = 0;
            for (var k = 1L; k <= keys; k++)
            {
                var found = idx.Find(k);
                if (found == LogicalAddress.Empty) continue;
                live++;
                var inUpsertSet = found.Offset >= k * 16 + 100_000 && found.Offset < k * 16 + 100_000 + 5;
                inUpsertSet.Should().BeTrue($"key {k} 幸存值必须 ∈ upsert 值集，实际 {found.Offset}");
            }
            idx.EntryCount.Should().Be(live, "计数与存活条目一致");
        }
    }

    // ══ 契约 4（#411）：恢复还原溢出 bump 指针——再分配不别名覆盖 ══

    [Fact]
    public async Task Recovery_RestoresOverflowCount_NoAliasOverwrite()
    {
        using var vol = new TestVolume();
        // 16 桶×7 槽=112 主容量；400 条必触发溢出池分配
        var ringSettings = TestRingSettingsFactory.On(vol, "ofc-ring", deleteOnClose: false,
            metaKind: MetaPolicyKind.Managed);
        var hashSettings = TestProbingIndexSettingsFactory.On(vol, "ofc-hash", hashTableCapacity: 16,
            overflowPoolCapacity: 1 << 8, deleteOnClose: false,
            persistencePolicy: new ProbingIndexPersistencePolicy
            {
                Interval = TimeSpan.FromMinutes(10),
                EntryDeltaThreshold = long.MaxValue,
            });

        using (var ring = await RingOfLong.CreateAsync(ringSettings, vol.Fs))
        {
            var index = TestProbingIndexSettingsFactory.NewHash<long>(vol, hashSettings, ring);
            for (long k = 1; k <= 400; k++)
            {
                var addr = ring.Write(k, BitConverter.GetBytes(k));
                index.Insert(k, addr, LogicalAddress.Empty);
            }
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeTrue();
            index.Dispose();
        }

        // 恢复（帧物化）后继续插入——AllocateOverflow 必须从恢复的 bump 指针续分配
        using var ring2 = await RingOfLong.CreateAsync(ringSettings, vol.Fs);
        var index2 = TestProbingIndexSettingsFactory.NewHash<long>(vol, hashSettings, ring2,
            hints: new ProbingIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));
        using (index2)
        {
            index2.MainStorageAppliedLastRecovery.Should().BeTrue("帧有效——走物化路径（#411 触发前提）");

            // 恢复期既有条目（含溢出链条）先验证完好
            for (long k = 1; k <= 400; k += 13)
                index2.Find(k).Should().NotBe(LogicalAddress.Empty, $"恢复后 key {k} 必命中");

            // ★ 继续 Insert 触发 AllocateOverflow（修复前：bump 从 0 重启 → 别名覆盖已恢复链桶 → 静默丢失）
            for (long k = 401; k <= 500; k++)
            {
                var addr = ring2.Write(k, BitConverter.GetBytes(k));
                index2.Insert(k, addr, ring2.BeginAddress);
            }

            var buf = new byte[16];
            for (long k = 1; k <= 500; k++)
            {
                var found = index2.Find(k);
                found.Should().NotBe(LogicalAddress.Empty, $"key {k} 恢复后再分配溢出桶不得丢条目（#411）");
                ring2.GetValue(found, buf).Should().Be(8);
                BitConverter.ToInt64(buf).Should().Be(k);
            }
        }
    }
}
