using System.Runtime.InteropServices;
using TC.Tier.Contracts.Structures;
using TC.Tier.Runtime.Structures.ProbingIndex;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// ★★ 回归守卫（W6.1 已修复：flush 失效冷页缓存）——Ring flush×冷读互溶：多记录同页 + 逐写 FlushUntil + 冷读
/// （GetKeyAsync/Find resolver 回读）→ 冷读返回陈旧/错位内容（实测签名：全部冷读统一返回 key=3
/// ——0xA8 处记录——含 0xE0..0x230 各地址；index.Find(1)=Empty；桶原始槽位全程完好——
/// 索引零污染，腐坏发生在冷读供给侧）。取证要点：
/// ① 裸 Ring（无 index）同序列全绿 → 需要 index 挂载才触发（resolver 回读/双引擎共存）；
/// ② meta Disabled/Managed、sync/async flush、PersistenceKind Builtin/None、读写交错、写次数——
///    全部不影响（均排除）；③ 重开实例读引擎文件=正确（盘上副本无损）——腐坏仅存于进程内
///    冷读供给；④ 疑点收敛：LoadColdPage 冷页缓存/共享 PinnedBufferPool 缓冲别名
///    （index dump 缓冲 × ring 冷页缓存）。修复入口=Runtime Ring 冷读供给侧。
/// </summary>
public class RingFlushColdReadReproTests
{
    [Fact]
    public async Task PerWriteFlush_ThenColdRead_KeysSurvive()
    {
        using var vol = new TestVolume();
        var settings = TestRingSettingsFactory.On(vol, "flush-cold-read-repro",
            metaKind: MetaPolicyKind.Managed, deleteOnClose: false);
        using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

        var noPersist = Environment.GetEnvironmentVariable("REPRO_INDEX_NO_PERSIST") == "1";
        var indexSettings = new HashIndexSettings(
            new StorageEngineOptions("flush-cold-read-repro-index", 1L << 24,
                enableSegmentation: true, preallocateFile: false, deleteOnClose: false))
        {
            HashTableCapacity = 1 << 10,
            OverflowPoolCapacity = 1 << 8,
            PersistenceKind = noPersist
                ? TC.Tier.Runtime.Structures.ProbingIndex.ProbingIndexPersistenceKind.None
                : TC.Tier.Runtime.Structures.ProbingIndex.ProbingIndexPersistenceKind.Builtin,
        };
        using var index = new HashIndex<long>(vol.Fs, indexSettings, keyResolver: ring);
        index.Initialize();
        index.WaitForReady();

        var addrs = new List<(long Key, long Value, LogicalAddress Addr)>();
        var useSyncFlush = Environment.GetEnvironmentVariable("REPRO_SYNC_FLUSH") == "1";
        async Task Put(long key, long value)
        {
            var addr = await ring.WriteAsync(key, BitConverter.GetBytes(value));
            index.Insert(key, addr, ring.BeginAddress);
            if (useSyncFlush) ring.FlushUntil(addr);   // ★ Committed 档=每写一刷（同步形态）
            else await ring.FlushUntilAsync(addr);     // ★ Committed 档=每写一刷（异步形态）
            addrs.Add((key, value, addr));
        }

        foreach (var (k, v) in new[] { (1L, 10L), (2L, 20L), (3L, 30L) })
            await Put(k, v);
        for (long v = 11; v <= 15; v++) await Put(1, v);
        for (long v = 21; v <= 23; v++) await Put(2, v);

        // ① 全地址冷读回验（缺陷主面：GetKeyAsync 冷页缓存供给）——每条记录逐地址回读 key
        var bad = new System.Text.StringBuilder();
        foreach (var (k, v, addr) in addrs)
        {
            var record = await ring.GetKeyAsync(addr);
            if (record.IsTombstone || !record.Key.Equals(k))
                bad.AppendLine($"ring.GetKeyAsync({addr.Offset:X}) expect key={k} got key={record.Key} tomb={record.IsTombstone}");
        }

        // ② 索引 latest 指针：Find(key) == 该 key 最后一次写入地址
        foreach (var key in new long[] { 1, 2, 3 })
        {
            var last = addrs.Where(a => a.Key == key).Last().Addr;
            var find = index.Find(key);
            if (!find.Equals(last))
                bad.AppendLine($"index.Find({key})={find.Offset:X} expect latest={last.Offset:X}");
        }

        bad.Length.Should().Be(0, $"逐写 flush 后冷读 key 必须逐条保真 (syncFlush={useSyncFlush} noPersist={noPersist})" + System.Environment.NewLine + bad.ToString());
    }
}
