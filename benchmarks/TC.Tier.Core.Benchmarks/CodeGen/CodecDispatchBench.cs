using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace TC.Tier.Core.Benchmarks.CodeGen;

/// <summary>
/// 验证接口分发（interface dispatch）vs static 直接调用 vs 手写 BinaryPrimitives 的真实开销。
/// 关键问题：.NET 8 JIT 对 sealed 类型实现的接口是否做去虚化（devirtualization）？
/// 如果去虚化生效，接口分发 ≈ static，接口注入无性能代价。
///
/// 形态模拟 Log 的 ILogCodec（sealed Codec 实现 interface，通过 interface 字段调用）。
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class CodecDispatchBench
{
    // 模拟源生成 codec 的 static Read/Write
    private static readonly byte[] _scratch = new byte[22];

#pragma warning disable CA1859   // ★ 接口 vs 具体派发对比正是本基准的命题——改具体类型即失义
    private readonly ICodec _viaInterface; // 接口字段（模拟 LogCodec）
#pragma warning restore CA1859
    private readonly ConcreteCodec _concrete; // 具体类型字段

    public CodecDispatchBench()
    {
        _viaInterface = new ConcreteCodec();
        _concrete = new ConcreteCodec();
    }

    [Benchmark(Baseline = true, Description = "Interface dispatch")]
    public int ViaInterface() => _viaInterface.Write(_scratch, 42, 0);

    [Benchmark(Description = "Concrete (static-like)")]
    public int ViaConcrete() => _concrete.Write(_scratch, 42, 0);

    [Benchmark(Description = "Direct static")]
    public int ViaStatic() => StaticCodec.Write(_scratch, 42, 0);

    [Benchmark(Description = "Hand BinaryPrimitives")]
    public int ViaHandWritten()
    {
        BinaryPrimitives.WriteInt32LittleEndian(_scratch.AsSpan(0), 42);
        return 42;
    }

    internal interface ICodec { int Write(Span<byte> dest, int payloadLen, int paddingLen); }

    internal sealed class ConcreteCodec : ICodec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Write(Span<byte> dest, int payloadLen, int paddingLen)
        {
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(0), payloadLen);
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(4), paddingLen);
            return payloadLen + paddingLen;
        }
    }

    internal static class StaticCodec
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Write(Span<byte> dest, int payloadLen, int paddingLen)
        {
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(0), payloadLen);
            BinaryPrimitives.WriteInt32LittleEndian(dest.Slice(4), paddingLen);
            return payloadLen + paddingLen;
        }
    }
}
