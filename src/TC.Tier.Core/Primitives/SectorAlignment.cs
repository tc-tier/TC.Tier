using System.Numerics;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 提供对齐操作的扩展方法，用于将值向上或向下对齐到指定的倍数。默认对齐到 4K 或 2G。
/// <para>★ <c>alignment</c> 必须是 2 的幂且为正——底层位运算 <c>(value + mask) &amp; ~mask</c>
///   仅对 2 幂成立：传 0 会静默返回 0、传非 2 幂/负数会静默产生错位对齐值，极难排查
///   （IO 对齐错误会引发 O_DIRECT 失败、数据错位等连锁问题）。</para>
/// <para>★ 入口用 <see cref="BitOperations.IsPow2(int)"/> 校验（单条 CPU intrinsic，纳秒级，分支预测下几乎零开销）+
///   <see cref="ThrowHelper"/>（<c>[DoesNotReturn]</c> + <c>NoInlining</c>）隔离冷 throw 路径，
///   热路径内联不受影响。</para>
/// </summary>
public static class SectorAlignment
{
    /// <summary>校验 alignment 为正且 2 的幂。<see cref="Validate(int)"/> 标 AggressiveInlining →
    /// <see cref="BitOperations.IsPow2(int)"/> 检查内联进调用方；throw 路径经 ThrowHelper 隔离，不阻碍调用方内联。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Validate(int alignment)
    {
        if (!BitOperations.IsPow2(alignment))
            ThrowHelper.ThrowArgumentOutOfRange(nameof(alignment), "alignment 必须是 2 的幂且为正");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Validate(long alignment)
    {
        if (!BitOperations.IsPow2(alignment))
            ThrowHelper.ThrowArgumentOutOfRange(nameof(alignment), "alignment 必须是 2 的幂且为正");
    }

    /// <summary>
    /// 将 value 向上对齐到 alignment 的倍数。默认对齐到 4K。
    /// </summary>
    /// <param name="value">要对齐的值。</param>
    /// <param name="alignment">对齐的边界（必须是 2 的幂且为正）。</param>
    /// <returns>对齐后的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AlignUp(this long value, int alignment = AlignmentConst.Alignment4K)
    {
        Validate(alignment);
        var mask = alignment - 1L;
        return (value + mask) & ~mask;
    }

    /// <summary>
    /// 将 value 向下对齐到 alignment 的倍数。默认对齐到 4K。
    /// </summary>
    /// <param name="value">要对齐的值。</param>
    /// <param name="alignment">对齐的边界（必须是 2 的幂且为正）。</param>
    /// <returns>对齐后的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AlignDown(this long value, int alignment = AlignmentConst.Alignment4K)
    {
        Validate(alignment);
        var mask = alignment - 1L;
        return value & ~mask;
    }


    /// <summary>
    /// 将 value 向上对齐到 alignment 的倍数。默认对齐到 2G。
    /// </summary>
    /// <param name="value">要对齐的值。</param>
    /// <param name="alignment">对齐的边界（必须是 2 的幂且为正）。</param>
    /// <returns>对齐后的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AlignUp(this long value, long alignment = AlignmentConst.Alignment2G)
    {
        Validate(alignment);
        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }
    /// <summary>
    /// 将 value 向下对齐到 alignment 的倍数。默认对齐到 2G。
    /// </summary>
    /// <param name="value">要对齐的值。</param>
    /// <param name="alignment">对齐的边界（必须是 2 的幂且为正）。</param>
    /// <returns>对齐后的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long AlignDown(this long value, long alignment = AlignmentConst.Alignment2G)
    {
        Validate(alignment);
        var mask = alignment - 1;
        return value & ~mask;
    }


    /// <summary>
    /// 将 value 向上对齐到 alignment 的倍数。默认对齐到 4K。
    /// </summary>
    /// <param name="value">要对齐的值。</param>
    /// <param name="alignment">对齐的边界（必须是 2 的幂且为正）。</param>
    /// <returns>对齐后的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignUp(this int value, int alignment = AlignmentConst.Alignment4K)
    {
        Validate(alignment);
        var mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    /// <summary>
    /// 将 value 向下对齐到 alignment 的倍数。默认对齐到 4K。
    /// </summary>
    /// <param name="value">要对齐的值。</param>
    /// <param name="alignment">对齐的边界（必须是 2 的幂且为正）。</param>
    /// <returns>对齐后的值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int AlignDown(this int value, int alignment = AlignmentConst.Alignment4K)
    {
        Validate(alignment);
        var mask = alignment - 1;
        return value & ~mask;
    }

}
