using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using TC.Tier.Core.NativeInterop;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;
using AlignedMemoryManager = TC.Tier.Core.Primitives.AlignedMemoryManager;

namespace TC.Tier.Core.Benchmarks.NativeInterop;

/// <summary>
/// ★ 水位线内存布局对比基准——回答"三个水位线应共享一个 48B 对齐 slot，还是用三个独立 16B slot"。
/// <para>硬件只支持 16B CAS，48B 无法一次 CAS。两种布局在单线程下都是 3 次独立 16B CAS，指令数相同。
/// 真正差异在<b>多线程并发下的 cache line 局部性</b>：48B 三水位共一条 64B cache line → MESI 互相 invalidate；
/// 3×16B 各自独立 cache line → 互不干扰。</para>
/// <para>★ 测量口径对齐 atomic-access-perf-report.md §四（AtomicContentionBench）：</para>
/// <para>• 每线程 OpsPerThread=200_000（同报告），不放大稀释单次延迟。</para>
/// <para>• <b>缓存 ref</b>——吸报告 §2.3 教训：ref 每次重算会让 aligned 反比 heap 慢。循环外取 ref。</para>
/// <para>• 不在热循环里分配数组/捕获——Setup 预解析三个 ref 传给线程。</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks/ -- --filter '*WatermarkLayout*'</para>
/// </summary>
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser(false)]
public class WatermarkLayoutBench : IDisposable
{
    [Params(1, 2, 4, 8)]
    public int Threads { get; set; }

    private const int OpsPerThread = 200_000;

    // ── 布局 A：48B 单 slot，三水位共一条 cache line ──
    private AlignedMemoryManager? _packedMem;

    // ── 布局 B：三个独立 16B slot，各自独立 cache line ──
    private AlignedMemoryManager? _solo0, _solo1, _solo2;

    // 预解析的裸指针——Setup 里算一次，循环里直接用（吸 §2.3 教训，不重复求值 property/ref）
    private IntPtr _p0, _p1, _p2, _s0, _s1, _s2;

    [GlobalSetup]
    public unsafe void Setup()
    {
        // 48B 装三个水位，对齐 16B（offset 0/16/32 均 16B 对齐，命中 CAS 快路径）
        _packedMem = new AlignedMemoryManager(48, 16, zeroed: true);
        var b = (byte*)_packedMem.Ptr;
        _p0 = (IntPtr)b;
        _p1 = (IntPtr)(b + 16);
        _p2 = (IntPtr)(b + 32);

        // 三个独立 16B slot（各自 AlignedAlloc，落点不同 cache line）
        _solo0 = new AlignedMemoryManager(16, 16, zeroed: true);
        _solo1 = new AlignedMemoryManager(16, 16, zeroed: true);
        _solo2 = new AlignedMemoryManager(16, 16, zeroed: true);
        _s0 = (IntPtr)_solo0.Ptr;
        _s1 = (IntPtr)_solo1.Ptr;
        _s2 = (IntPtr)_solo2.Ptr;
    }

    /// <summary>max-CAS 推进——ref 在循环外取一次，循环内只 CAS（对齐 §2.3 缓存 ref 教训）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void CasAdvanceLoop(IntPtr target, int ops)
    {
        // ★ 关键：ref 在循环外取一次，不在 do-while 内重算（§2.3 教训）
        ref var loc = ref Unsafe.AsRef<NativeInt128>(target.ToPointer());
        for (int i = 0; i < ops; i++)
        {
            NativeInt128 cur;
            do { cur = loc; }
            while (!NativeAtomic128.CompareExchange(ref loc, cur, new NativeInt128(cur.Lo + 1, cur.Hi)));
        }
    }

    /// <summary>布局 A：48B 单 slot——三水位共一条 cache line，按线程分散推进。</summary>
    [Benchmark(Description = "48B 单slot (共享cache line)")]
    public unsafe ulong PackedLayout()
    {
        Unsafe.AsRef<NativeInt128>(_p0.ToPointer()) = new NativeInt128(0, 0);
        Unsafe.AsRef<NativeInt128>(_p1.ToPointer()) = new NativeInt128(0, 0);
        Unsafe.AsRef<NativeInt128>(_p2.ToPointer()) = new NativeInt128(0, 0);

        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            var slot = t % 3;
            var target = slot == 0 ? _p0 : slot == 1 ? _p1 : _p2;
            tasks[t] = Task.Run(() => CasAdvanceLoop(target, OpsPerThread));
        }
        Task.WaitAll(tasks);
        return Unsafe.AsRef<NativeInt128>(_p0.ToPointer()).Lo;
    }

    /// <summary>布局 B：3×16B 独立 slot——三水位各自独立 cache line。</summary>
    [Benchmark(Description = "3×16B 独立slot (独立cache line)")]
    public unsafe ulong SoloLayout()
    {
        Unsafe.AsRef<NativeInt128>(_s0.ToPointer()) = new NativeInt128(0, 0);
        Unsafe.AsRef<NativeInt128>(_s1.ToPointer()) = new NativeInt128(0, 0);
        Unsafe.AsRef<NativeInt128>(_s2.ToPointer()) = new NativeInt128(0, 0);

        var tasks = new Task[Threads];
        for (int t = 0; t < Threads; t++)
        {
            var slot = t % 3;
            var target = slot == 0 ? _s0 : slot == 1 ? _s1 : _s2;
            tasks[t] = Task.Run(() => CasAdvanceLoop(target, OpsPerThread));
        }
        Task.WaitAll(tasks);
        return Unsafe.AsRef<NativeInt128>(_s0.ToPointer()).Lo;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _packedMem?.Dispose();
        _solo0?.Dispose();
        _solo1?.Dispose();
        _solo2?.Dispose();
    }

    public void Dispose()
    {
        _packedMem?.Dispose();
        _solo0?.Dispose();
        _solo1?.Dispose();
        _solo2?.Dispose();
        GC.SuppressFinalize(this);
    }
}
