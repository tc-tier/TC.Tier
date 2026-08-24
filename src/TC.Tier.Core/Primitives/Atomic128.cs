using System.Runtime.CompilerServices;
using TC.Tier.Core.NativeInterop;
using NativeInt128 = TC.Tier.Core.NativeInterop.Int128;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 标准 128 位 CAS 单槽封装——16B 对齐背板 + 能力探测降级 + 裸读不撕裂。
/// <para>★ 消除各处（<c>TailWatermarkSlot</c>/<c>IndexBase</c>/<c>AsyncPriorityQueue</c>）重写的
///   128 CAS 样板：16B 对齐分配 + <c>Unsafe.As&lt;T,Int128&gt;</c> reinterpret +
///   <see cref="NativeAtomic128.CompareExchange"/> + 能力探测 + lock 降级。</para>
/// <para>★ 统一对齐保证：托管数组（<c>T[]</c>）只保证 8B 对齐，<b>无 16B 对齐保证</b>——
///   <c>lock cmpxchg16b</c> 要求 16B 对齐，未对齐会 #GP（硬件异常）。本封装用
///   <see cref="AlignedMemoryManager"/>（64B 对齐 ⊃ 16B，且独占缓存行避 false sharing）做背板，
///   从根上消除"碰巧 GC 分配对齐"的定时炸弹。</para>
/// <para>★ 标准用法（参考 <c>TailWatermarkSlot</c>）：CAS 循环 = <see cref="Read"/> 裸读当前 →
///   业务条件检查 → 算新值（含 ABA version）→ <see cref="TryCompareExchange"/> →
///   失败 <c>SpinWait.SpinOnce()</c> 重试。</para>
/// <para>★ 能力探测 + 降级：native 128-bit CAS 不可用时（生产永不触发）退化到 <c>lock</c>，
///   行为与 native 路径完全一致（位精确）。测试钩子 <c>_casEnabledForTesting</c> 反射改写触发降级分支
///   （沿用 <c>TailWatermarkSlot</c> 旧约定）。</para>
/// </summary>
/// <typeparam name="T">载荷类型——必须是 <b>16 字节 blittable struct</b>（构造期校验，不符抛
///   <see cref="ArgumentException"/>）。典型：<c>LogicalAddress</c>(SegId+Extension+Offset=16B)。</typeparam>
public sealed class Atomic128<T> : IDisposable where T : struct
{
    /// <summary>★ 64B 对齐背板——native CAS 路径的 16B 槽位（64B 对齐 ⊃ 16B，独占缓存行避 false sharing）。</summary>
    private readonly AlignedMemoryManager _mem;

    /// <summary>★ CAS 降级专用锁 + 背板值——native 128-bit CAS 不可用时用（生产永不触发）。</summary>
    private readonly object _fallbackLock = new();
    private T _fallbackValue;

    /// <summary>★ native 128-bit CAS 能力（全局静态探测，失败降级 lock）。</summary>
    private static readonly bool NativeCasAvailable = ProbeNativeCas();

    /// <summary>★ 测试钩子：static field（非 const），测试通过反射改写触发 lock 降级分支。
    /// 字段名 <c>_casEnabledForTesting</c> 沿用 <c>TailWatermarkSlot</c> 旧约定。</summary>
    internal static bool _casEnabledForTesting = true;

    /// <summary>是否走 native 128-bit CAS（false 时 Read/TryCompareExchange/Store 退化到 lock）。</summary>
    internal static bool CasEnabled => _casEnabledForTesting && NativeCasAvailable;

    /// <summary>构造（初值 default）。校验 T 为 16B blittable，不符抛 <see cref="ArgumentException"/>。</summary>
    public Atomic128()
    {
        // ★ 泛型约束无法表达 sizeof==16 / unmanaged，故构造期校验（Unsafe.SizeOf 是常数 intrinsic，开销可忽略）。
        //   先查 blittable 再查 size——含引用的 struct 任何 size 都先拒。
        if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            throw new ArgumentException(
                $"Atomic128<T> 要求 blittable（无引用字段），{typeof(T).Name} 含引用", nameof(T));
        if (Unsafe.SizeOf<T>() != 16)
            throw new ArgumentException(
                $"Atomic128<T> 要求 sizeof(T)==16，实际 {Unsafe.SizeOf<T>()}（{typeof(T).Name}）", nameof(T));

        _mem = new AlignedMemoryManager(AlignmentConst.Alignment64B, AlignmentConst.Alignment64B, zeroed: true);
    }

    /// <summary>构造（指定初值，零拷贝写入背板）。</summary>
    public Atomic128(T initial) : this() => Store(initial);

    /// <summary>探测 native 128-bit CAS 能力（probe CAS 自检，失败静默降级）。</summary>
    private static bool ProbeNativeCas()
    {
        try
        {
            NativeInt128 probe = new(0, 0);
            return NativeAtomic128.CompareExchange(ref probe, probe, probe);
        }
        catch { return false; }  // CAS 能力是全局属性（静态探测）——静默降级
    }

    /// <summary>
    /// ★ 裸读当前值——16B 对齐保证单次读不撕裂（cache coherence 最终一致 + CAS 兜底）。
    /// <para>⚠️ 跨线程可见性靠 cache coherence 最终一致；读到旧值时 CAS 会失败重试（调用方负责循环）。
    ///   不用 <c>Volatile.Read</c>（不支持 struct 泛型）；<c>MemoryBarrier</c> 对热路径开销过大。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T Read()
    {
        if (CasEnabled) return _mem.IsDisposed ? default : _mem.GetRefUnsafe<T>(0);
        lock (_fallbackLock) { return _fallbackValue; }
    }

    /// <summary>
    /// ★ 128 位 CAS（<b>位精确</b>比较，含 ABA version 等所有位）——native 优先，降级 lock。
    /// <para>成功 = location==expected，写入 <paramref name="value"/> 返回 true；
    ///   失败 = location!=expected，值不变返回 false。</para>
    /// <para>⚠️ <b>位精确</b>：与 <c>LogicalAddress.Equals</c>（只比 SegId+Offset）不同——CAS 含
    ///   Extension(version) 全部 16 字节。调用方做 ABA 防护时须在 expected/newValue 里携带正确 version。</para>
    /// <para>★ 失败<b>不</b>回写观察值（与 <see cref="NativeAtomic128.CompareExchange"/> 公开语义一致）——
    ///   调用方在 CAS 循环里自己 <see cref="Read"/> 重读。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryCompareExchange(T expected, T value)
    {
        if (CasEnabled)
        {
            if (_mem.IsDisposed) return false;
            ref var loc = ref _mem.GetRefUnsafe<T>(0);
            ref var loc128 = ref Unsafe.As<T, NativeInt128>(ref loc);
            return NativeAtomic128.CompareExchange(ref loc128,
                Unsafe.As<T, NativeInt128>(ref expected),
                Unsafe.As<T, NativeInt128>(ref value));
        }
        lock (_fallbackLock)
        {
            if (_mem.IsDisposed) return false;
            // 位精确比较（含 version 全部位）——与 native CAS 语义一致
            ref var cur = ref _fallbackValue;
            if (Unsafe.As<T, NativeInt128>(ref cur).Lo != Unsafe.As<T, NativeInt128>(ref expected).Lo) return false;
            if (Unsafe.As<T, NativeInt128>(ref cur).Hi != Unsafe.As<T, NativeInt128>(ref expected).Hi) return false;
            _fallbackValue = value;
            return true;
        }
    }

    /// <summary>
    /// ★ <b>快路径</b> CAS——跳过 <see cref="CasEnabled"/>/IsDisposed 检查，直接 native 128-bit CAS。
    /// <para>⚠️ 调用方须保证：(1) native 128-bit CAS 可用（<see cref="CasEnabled"/> 为 true）；
    ///   (2) 未 <see cref="Dispose"/>。违反 → 未定义行为（disposed 后 AV / native 不可用抛异常）。</para>
    /// <para>★ 用于热路径榨性能——省 <see cref="TryCompareExchange"/> 的 ~3.6ns 运行时分支开销
    ///   （CasEnabled 读 + Isdisposed Volatile.Read）。TailWatermarkSlot 等已知"disposed 顺序保证 +
    ///   native CAS 一定可用"的场景用本方法。</para>
    /// <para>位精确比较、失败不回写——语义同 <see cref="TryCompareExchange"/>（仅少两个前置检查）。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryCompareExchangeUnsafe(T expected, T value)
    {
        ref var loc = ref _mem.GetRefUnsafe<T>(0);
        ref var loc128 = ref Unsafe.As<T, NativeInt128>(ref loc);
        return NativeAtomic128.CompareExchange(ref loc128,
            Unsafe.As<T, NativeInt128>(ref expected),
            Unsafe.As<T, NativeInt128>(ref value));
    }

    /// <summary>
    /// ★ <b>快路径</b> 裸读——跳过 <see cref="CasEnabled"/>/IsDisposed 检查，直接读背板。
    /// <para>⚠️ 调用方须保证未 <see cref="Dispose"/>（disposed 后 AV）。16B 对齐保证单次读不撕裂。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T ReadUnsafe() => _mem.GetRefUnsafe<T>(0);

    /// <summary>
    /// ★ 装配期裸写（启动期单线程，无并发写者）。
    /// <para>⚠️ 运行期并发写<b>必须</b>走 <see cref="TryCompareExchange"/>（CAS），不要用本方法。</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Store(T value)
    {
        if (CasEnabled)
        {
            if (_mem.IsDisposed) return;
            _mem.GetRefUnsafe<T>(0) = value;
        }
        else
        {
            lock (_fallbackLock) { _fallbackValue = value; }
        }
    }

    /// <summary>背板是否已释放。调用方据此对 disposed 后的 <see cref="Read"/> 返回值做语义调整
    /// （如 <c>LogicalAddress</c> 场景 disposed 后应返回 <c>Invalid</c> 而非 <c>default(Empty)</c>）。</summary>
    public bool IsDisposed => _mem.IsDisposed;

    /// <summary>释放对齐背板（非托管内存，必须 Dispose——无 finalizer 兜底）。</summary>
    public void Dispose() => _mem.Dispose();
}
