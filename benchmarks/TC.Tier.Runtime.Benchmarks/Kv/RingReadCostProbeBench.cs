using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.IO;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Benchmarks.Kv;

/// <summary>
/// ★ 读内核成本解剖探针：同环同记录上分解
/// GetFields（仅 header 解码）/ GetValueSpan（零拷贝全链）/ GetValue（拷贝交付）与裸 CRC32C(112B)。
/// <para>read-protection-tiering W3 量具（留存）——CRC 校验优化的 A/B 基准锚点。
/// 2026-09-11 基线（i7-10510U）：GetValueSpan 旧内核 12.5ns → 自愈初版 72.8ns → 内联段循环 +
/// 轻量门优化后 44.6ns（税 +32ns）；裸 CRC32C(112B) 单次调用口径 30ns（内联后 ~13ns）。</para>
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 10)]
public class RingReadCostProbeBench : IDisposable
{
    private IFileSystem _fs = null!;
    private BlittableRing<long> _ring = null!;
    private readonly byte[] _buf = new byte[64];
    private readonly byte[] _crcSrc = new byte[112];
    private LogicalAddress _addr;

    [GlobalSetup]
    public void Setup()
    {
        _fs = TierFs.New("memory:");
        _ring = new BlittableRing<long>(new BlittableRingSettings(
            new StorageEngineOptions("probe-ring", 64L << 20, true, true, true))
        {
            PageSize = 8192,
            MemorySize = 32L << 20,
        }, _fs);
        _ring.Initialize();
        _ring.WaitForReady();
        var payload = new byte[64];
        Random.Shared.NextBytes(payload);
        _addr = _ring.Write(42L, payload);
        Random.Shared.NextBytes(_crcSrc);
    }

    [Benchmark]
    public int GetFieldsOnly() => _ring.GetFields(_addr).PayloadLength == 0 ? 1 : 0;

    [Benchmark]
    public int GetValueSpanLen() => _ring.GetValueSpan(_addr).Length;

    [Benchmark]
    public int GetValueCopy() => _ring.GetValue(_addr, _buf);

    [Benchmark]
    public uint CrcOnly() => UnifiedCrc.ComputeCrc32C(_crcSrc);

    [GlobalCleanup]
    public void Cleanup()
    {
        _ring.Dispose();
        _fs.Dispose();
    }

    /// <summary>BDN 基准生命周期收口（GlobalCleanup 阶段自动调用；字段释放随 GlobalCleanup 域）。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
