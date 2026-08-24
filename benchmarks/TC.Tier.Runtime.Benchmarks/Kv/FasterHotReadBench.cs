using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FASTER.core;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Runtime.Structures.ProbingIndex;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ 点查对 FASTER 同形对照（用户问：为何进不了 100ns、与 FASTER 差多少）：
/// FASTER 2.6.5 纯内存 hlog（NullDevice）100k×(8B key + 64B struct value) 会话热随机 Read
/// vs TC.Tier 同形组合（RingOfLong + HashIndex.Find + Ring.GetValue）——同进程同轮。
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks -- --filter "*FasterHotRead*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 5, iterationCount: 12)]
public class FasterHotReadBench : IDisposable
{
    private const int PrefillCount = 100_000;
    private const int ValueSize = 64;

    // ── FASTER 侧 ──
    private FasterKV<long, LongData>? _faster;
    private ClientSession<long, LongData, LongData, LongData, Empty, IFunctions<long, LongData, LongData, LongData, Empty>>? _session;

    // ── TC.Tier 侧（与 KvCompositionBench.PointRead 同形）──
    private IFileSystem _fs = null!;
    private RingOfLong _ring = null!;
    private HashIndex<long> _index = null!;
    private byte[] _readBuf = null!;
    private long[] _keys = null!;

    private long _cursor;

    [GlobalSetup]
    public void Setup()
    {
        var value = new LongData();
        var rng = new Random(42);

        // FASTER：内存 hlog，100k Upsert 后热读
        _faster = new FasterKV<long, LongData>(
            1L << 20,
            new FASTER.core.LogSettings
            {
                LogDevice = new NullDevice(),
                MemorySizeBits = 22,      // 4M×64B 槽远超 100k 条——全内存
                PageSizeBits = 21,
            });
        _session = _faster.NewSession(new SimpleFunctions<long, LongData>());
        for (long k = 0; k < PrefillCount; k++)
            _session.Upsert(k, value);
        _session.Refresh();

        // TC.Tier：同形组合
        _fs = TierFs.New("memory:");
        _ring = RingOfLong.Create(new BlittableRingSettings(new StorageEngineOptions("cmp-ring", 64L << 20,
            enableSegmentation: true, preallocateFile: true, deleteOnClose: false))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,
        }, _fs);
        _index = new HashIndex<long>(_fs,
            new HashIndexSettings(new StorageEngineOptions("cmp-hash", 1L << 24, true, true, true)), null, _ring);
        _index.Initialize();
        _index.WaitForReady();

        var payload = new byte[ValueSize];
        rng.NextBytes(payload);
        for (long k = 0; k < PrefillCount; k++)
        {
            var addr = _ring.Write(k, payload);
            _index.Insert(k, addr, LogicalAddress.Empty);
        }

        _keys = Enumerable.Range(0, PrefillCount).Select(i => (long)i)
            .OrderBy(_ => Random.Shared.Next()).ToArray();
        _readBuf = new byte[ValueSize];
    }

    [Benchmark(Baseline = true, Description = "FASTER.Read(session,100k hot)")]
    public long FasterHotRead()
    {
        var key = _keys[_cursor++ % PrefillCount];
        var (_, output) = _session!.Read(key);
        return output.V0;
    }

    [Benchmark(Description = "TierKv.PointRead(find+getvalue,100k hot)")]
    public long TierKvPointRead()
    {
        var key = _keys[_cursor++ % PrefillCount];
        var addr = _index.Find(key);
        return addr == LogicalAddress.Empty ? -1 : _ring.GetValue(addr, _readBuf);
    }

    /// <summary>★ 终态组合形态：scope 持 epoch（index+ring 双 scope）+ 零拷贝值交付（GetValueSpan）。
    /// 基准体内每次迭代 enter/exit——批量场景摊得更薄，此处为保守口径。</summary>
    [Benchmark(Description = "TierKv.ScopedZeroCopy(scope+span,100k hot)")]
    public long TierKvScopedZeroCopy()
    {
        var key = _keys[_cursor++ % PrefillCount];
        using var ringScope = _ring.EnterReadScope();
        using var indexScope = _index.EnterScope();
        var addr = indexScope.Find(key);
        if (addr == LogicalAddress.Empty) return -1;
        return _ring.GetValueSpan(addr).Length;
    }

    /// <summary>★ scope 的正用形态：一次进出摊薄 epoch（256 查/invocation——scope 成本 /256 ≈ 0），
    /// 零拷贝交付。这是组合层缓存地址前的"发现+首读"批口径。</summary>
    [Benchmark(Description = "TierKv.ScopedZeroCopyBatch(256/inv)", OperationsPerInvoke = 256)]
    public long TierKvScopedZeroCopyBatch()
    {
        long total = 0;
        using var ringScope = _ring.EnterReadScope();
        using var indexScope = _index.EnterScope();
        for (int i = 0; i < 256; i++)
        {
            var key = _keys[_cursor++ % PrefillCount];
            var addr = indexScope.Find(key);
            if (addr != LogicalAddress.Empty)
                total += _ring.GetValueSpan(addr).Length;
        }
        return total;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _faster?.Dispose();
        (_index as IDisposable)?.Dispose();
        _ring?.Dispose();
        _fs?.Dispose();
        GC.SuppressFinalize(this);   // ★ CA1816：派生类型引 finalizer 时不重复 Dispose
    }

    /// <summary>64B blittable 值（8×long）——零分配读输出。</summary>
#pragma warning disable CS0649   // ★ 字段经 MemoryMarshal 整体读写（blittable DTO）——逐字段赋值即失义
    private struct LongData
    {
        public long V0, V1, V2, V3, V4, V5, V6, V7;
    }
#pragma warning restore CS0649
}
