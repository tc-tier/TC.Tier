// 自研原语 vs 官方 System.IO.Hashing 对照微基准（验收：生成一致 + 无性能差异）。
// ★ 官方 8.0.0 无 Crc32C 类型——CRC32C 仅报告自研绝对吞吐（SSE4.2 指令路径）。
// 别名绕开 TC.Tier.Core.Primitives 内部移植类与官方同名类的可见性歧义。

using System.IO.Hashing;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Core.Primitives;
using OfficialXxHash64 = System.IO.Hashing.XxHash64;
using OfficialXxHash128 = System.IO.Hashing.XxHash128;
using OfficialCrc64 = System.IO.Hashing.Crc64;
using OfficialCrc32 = System.IO.Hashing.Crc32;

namespace TC.Tier.Core.Benchmarks.Utilities;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class HashPrimitiveOursVsOfficialBench
{
    private byte[] _data = null!;

    [Params(8, 64, 4096, 65536)]
    public int Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _data = new byte[Size];
        new Random(42).NextBytes(_data);
    }

    [Benchmark(Description = "XxHash64 Ours")]
    public ulong XxHash64_Ours() => UnifiedXxHash.Hash64(_data);

    [Benchmark(Description = "XxHash64 Official")]
    public ulong XxHash64_Official() => OfficialXxHash64.HashToUInt64(_data);

    [Benchmark(Description = "XxHash128 Ours")]
    public int XxHash128_Ours()
    {
        Span<byte> dest = stackalloc byte[16];
        UnifiedXxHash.Hash128(_data, dest);
        return dest[0];
    }

    [Benchmark(Description = "XxHash128 Official")]
    public int XxHash128_Official()
    {
        Span<byte> dest = stackalloc byte[16];
        OfficialXxHash128.Hash(_data, dest);
        return dest[0];
    }

    [Benchmark(Description = "Crc64 Ours")]
    public ulong Crc64_Ours() => UnifiedCrc.ComputeCrc64(_data);

    [Benchmark(Description = "Crc64 Official")]
    public ulong Crc64_Official() => OfficialCrc64.HashToUInt64(_data);

    [Benchmark(Description = "Crc32(IEEE) Ours")]
    public uint Crc32Ieee_Ours() => UnifiedCrc.ComputeCrc32(_data);

    [Benchmark(Description = "Crc32(IEEE) Official")]
    public uint Crc32Ieee_Official() => OfficialCrc32.HashToUInt32(_data);

    [Benchmark(Description = "Crc32C Ours（官方无此类型）")]
    public uint Crc32C_Ours() => UnifiedCrc.ComputeCrc32C(_data);
}
