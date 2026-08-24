using System.Globalization;
using System.Runtime.CompilerServices;
using static System.Char;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 通用工具类。提供字节大小解析、2 的幂运算、哈希计算、单调更新、blittable 判定等通用工具方法。
/// </summary>
public static class Utility
{
    /// <summary>
    /// 哈希计算的乘法因子（Knuth 乘法哈希变种使用的质数）。
    /// </summary>
    private const long HashMultiplier = 40343;

    /// <summary>
    /// 读缓存地址标志位（bit 47）。逻辑地址此位置 1 表示属于读缓存区。
    /// </summary>
    private const long ReadCacheBitMask = 1L << 47;

    /// <summary>
    /// 将字符串表示的大小解析为字节数。
    /// <para>支持后缀：k/K/KB（千字节）、m/M/MB（兆字节）、g/G/GB（吉字节）、t/T/TB（太字节）、p/P/PB（拍字节）。</para>
    /// <para>示例："4k"→4096，"8MB"→8388608，"12g"→12884901888。</para>
    /// </summary>
    /// <param name="value">大小字符串（数字 + 可选后缀）。</param>
    /// <returns>解析后的字节数。</returns>
    public static long ParseSize(string value)
    {
        char[] suffix = ['k', 'm', 'g', 't', 'p'];
        long result = 0;
        foreach (var c in value)
        {
            if (IsDigit(c))
            {
                result = result * 10 + (byte)c - '0';
            }
            else
            {
                for (var i = 0; i < suffix.Length; i++)
                {
                    if (ToLower(c) != suffix[i]) continue;
                    result *= (long)Math.Pow(1024, i + 1);
                    return result;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// 计算小于等于指定值的最大 2 的幂的位数（log2）。
    /// <para>若输入不是 2 的幂，记录警告并取下舍入值。例如 5000 → 12（4096）。</para>
    /// </summary>
    /// <param name="v">输入值。</param>
    /// <param name="logger">可选日志器，输入非 2 的幂时记录警告。</param>
    /// <returns>下舍入后 2 的幂的 log2 值。</returns>
    internal static int NumBitsPreviousPowerOf2(long v, ILogger? logger = null)
    {
        var adjustedSize = PreviousPowerOf2(v);
        if (v != adjustedSize)
            logger?.LogError($"警告：使用下舍入值 {adjustedSize} 替代指定值 {v}");
        return (int)Math.Log(adjustedSize, 2);
    }

    /// <summary>
    /// 计算小于等于指定值的最大 2 的幂。
    /// <para>例如 5000 → 4096，8192 → 8192，1 → 1。</para>
    /// </summary>
    /// <param name="v">输入值。</param>
    /// <returns>小于等于 v 的最大 2 的幂。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long PreviousPowerOf2(long v)
    {
        v |= v >> 1;
        v |= v >> 2;
        v |= v >> 4;
        v |= v >> 8;
        v |= v >> 16;
        v |= v >> 32;
        return v - (v >> 1);
    }

    /// <summary>
    /// 将字节数格式化为易读字符串（带 K/M/G/T/P 后缀）。
    /// <para>例如 4096 → "4KB"，1048576 → "1MB"。</para>
    /// </summary>
    /// <param name="value">字节数。</param>
    /// <returns>带后缀的易读字符串。</returns>
    internal static string PrettySize(long value)
    {
        char[] suffix = ['K', 'M', 'G', 'T', 'P'];
        double v = value;
        var exp = 0;
        while (v - Math.Floor(v) > 0)
        {
            if (exp >= 18)
                break;
            exp += 3;
            v *= 1024;
            v = Math.Round(v, 12);
        }

        while (Math.Floor(v).ToString(CultureInfo.InvariantCulture).Length > 3)
        {
            if (exp <= -18)
                break;
            exp -= 3;
            v /= 1024;
            v = Math.Round(v, 12);
        }

        return exp switch
        {
            > 0 => v.ToString(CultureInfo.InvariantCulture) + suffix[exp / 3 - 1] + "B",
            < 0 => v.ToString(CultureInfo.InvariantCulture) + suffix[-exp / 3 - 1] + "B",
            _ => v.ToString(CultureInfo.InvariantCulture) + "B"
        };
    }

    /// <summary>
    /// 判定地址是否属于读缓存区（read cache）。
    /// </summary>
    /// <param name="address">逻辑地址。</param>
    /// <returns>true 表示读缓存地址；false 表示主存地址。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsReadCache(long address) => (address & ReadCacheBitMask) != 0;

    /// <summary>
    /// 去除读缓存标志位，获取绝对逻辑地址。
    /// </summary>
    /// <param name="address">逻辑地址（可能含读缓存标志位）。</param>
    /// <returns>不含读缓存标志位的绝对地址。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long AbsoluteAddress(long address) => address & ~ReadCacheBitMask;

    /// <summary>
    /// 判定类型 T 是否为 blittable（可直接内存布局序列化，无 GC 引用）。
    /// <para>blittable 类型在 P/Invoke 和非托管内存中无需 marshalling 转换。</para>
    /// </summary>
    /// <typeparam name="T">待判定类型。</typeparam>
    /// <returns>true 表示 blittable；false 表示含引用类型。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsBlittable<T>()
    {
        return !RuntimeHelpers.IsReferenceOrContainsReferences<T>();
    }

    /// <summary>
    /// 逐字节比较两个内存区域是否相等。
    /// </summary>
    /// <param name="src">源内存指针。</param>
    /// <param name="dst">目标内存指针。</param>
    /// <param name="length">比较字节数。</param>
    /// <returns>true 表示完全相同；false 表示存在差异。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe bool IsEqual(byte* src, byte* dst, int length)
    {
        for (var i = 0; i < length; i++)
        {
            if (*(src + i) != *(dst + i))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 逐字节复制内存区域。
    /// </summary>
    /// <param name="src">源内存指针。</param>
    /// <param name="dest">目标内存指针。</param>
    /// <param name="numBytes">复制字节数。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe void Copy(byte* src, byte* dest, int numBytes)
    {
        for (var i = 0; i < numBytes; i++)
        {
            *(dest + i) = *(src + i);
        }
    }

    /// <summary>
    /// 计算 64 位整数的哈希值（高质量散列，用于 hash index）。
    /// </summary>
    /// <param name="input">输入值。</param>
    /// <returns>64 位哈希值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long GetHashCode(long input)
    {
        var localRand = input;
        long localRandHash = 8;

        localRandHash = HashMultiplier * localRandHash + ((localRand) & 0xFFFF);
        localRandHash = HashMultiplier * localRandHash + ((localRand >> 16) & 0xFFFF);
        localRandHash = HashMultiplier * localRandHash + ((localRand >> 32) & 0xFFFF);
        localRandHash = HashMultiplier * localRandHash + (localRand >> 48);
        localRandHash = HashMultiplier * localRandHash;

        return (long)Rotr64((ulong)localRandHash, 45);
    }

    /// <summary>
    /// 计算字节数组的 64 位哈希值（用于 hash index 的变长 key）。
    /// </summary>
    /// <param name="pbString">字节数组指针。</param>
    /// <param name="len">字节数。</param>
    /// <returns>64 位哈希值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe long HashBytes(byte* pbString, int len)
    {
        var pwString = (char*)pbString;
        var cbBuf = len / 2;
        var hashState = (ulong)len;

        for (var i = 0; i < cbBuf; i++, pwString++)
            hashState = HashMultiplier * hashState + *pwString;

        if ((len & 1) <= 0) return (long)Rotr64(HashMultiplier * hashState, 4);
        var pC = (byte*)pwString;
        hashState = HashMultiplier * hashState + *pC;

        return (long)Rotr64(HashMultiplier * hashState, 4);
    }

    /// <summary>
    /// 计算内存区域所有字节的异或值（XOR 校验）。
    /// <para>按 8 字块展开循环加速，处理剩余尾部字节。</para>
    /// </summary>
    /// <param name="src">内存指针。</param>
    /// <param name="length">字节数。</param>
    /// <returns>64 位异或结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static unsafe ulong XorBytes(byte* src, int length)
    {
        ulong result = 0;
        var curr = src;
        var end = src + length;
        while (curr + 4 * sizeof(ulong) <= end)
        {
            result ^= *(ulong*)curr;
            result ^= *(1 + (ulong*)curr);
            result ^= *(2 + (ulong*)curr);
            result ^= *(3 + (ulong*)curr);
            curr += 4 * sizeof(ulong);
        }

        while (curr + sizeof(ulong) <= end)
        {
            result ^= *(ulong*)curr;
            curr += sizeof(ulong);
        }

        while (curr + 1 <= end)
        {
            result ^= *curr;
            curr++;
        }

        return result;
    }

    /// <summary>
    /// 64 位循环右移。
    /// </summary>
    /// <param name="x">输入值。</param>
    /// <param name="n">右移位数。</param>
    /// <returns>循环右移结果。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Rotr64(ulong x, int n)
    {
        return (((x) >> n) | ((x) << (64 - n)));
    }

    /// <summary>
    /// 判定指定值是否为 2 的幂。
    /// <para>1 是 2 的幂（2^0），0 和负数不是。</para>
    /// </summary>
    /// <param name="x">输入值。</param>
    /// <returns>true 表示 2 的幂；false 表示非 2 的幂。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsPowerOfTwo(long x)
    {
        return (x > 0) && ((x & (x - 1)) == 0);
    }

    /// <summary>
    /// De Bruijn 序列位查表（用于快速 log2 计算）。
    /// </summary>
    private static readonly int[] MultiplyDeBruijnBitPosition2 =
    [
        0, 1, 28, 2, 29, 14, 24, 3, 30, 22, 20, 15, 25, 17, 4, 8,
        31, 27, 13, 23, 21, 19, 16, 7, 26, 12, 18, 6, 11, 5, 10, 9
    ];

    /// <summary>
    /// 计算 32 位 2 的幂的 log2 值（De Bruijn 快速算法）。
    /// <para>输入必须是 2 的幂，否则结果无意义。</para>
    /// </summary>
    /// <param name="x">输入值（须为 2 的幂）。</param>
    /// <returns>log2 值。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetLogBase2(int x)
    {
        return MultiplyDeBruijnBitPosition2[(uint)(x * 0x077CB531U) >> 27];
    }

    /// <summary>
    /// 计算 64 位值的 log2（最高有效位的位置）。
    /// </summary>
    /// <param name="value">输入值。</param>
    /// <returns>log2 值；0 的 log2 返回 0。</returns>
    public static int GetLogBase2(ulong value)
    {
        int i;
        for (i = -1; value != 0; i++)
            value >>= 1;

        return (i == -1) ? 0 : i;
    }

    /// <summary>
    /// 判定值是否在 32 位范围内（小于 2^32）。
    /// </summary>
    /// <param name="x">输入值。</param>
    /// <returns>true 表示可安全转为 32 位；false 表示超出范围。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Is32Bit(long x)
    {
        return ((ulong)x < 4294967295ul);
    }

    /// <summary>
    /// 单调递增更新（long 版）。仅当新值大于当前值时 CAS 更新。
    /// <para>用于水位指针推进，拒绝回退。</para>
    /// </summary>
    /// <param name="variable">待更新的变量（ref）。</param>
    /// <param name="newValue">新值（必须大于当前值才更新）。</param>
    /// <param name="oldValue">输出更新前的旧值。</param>
    /// <returns>true 表示更新成功（新值 > 旧值）；false 表示未更新（新值 <c>&lt;=</c> 旧值）。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MonotonicUpdate(ref long variable, long newValue, out long oldValue)
    {
        do
        {
            oldValue = variable;
            if (oldValue >= newValue) return false;
        } while (Interlocked.CompareExchange(ref variable, newValue, oldValue) != oldValue);

        return true;
    }

    /// <summary>
    /// 单调递增更新（int 版）。仅当新值大于当前值时 CAS 更新。
    /// <para>用于水位指针推进，拒绝回退。</para>
    /// </summary>
    /// <param name="variable">待更新的变量（ref）。</param>
    /// <param name="newValue">新值（必须大于当前值才更新）。</param>
    /// <param name="oldValue">输出更新前的旧值。</param>
    /// <returns>true 表示更新成功（新值 > 旧值）；false 表示未更新（新值 <c>&lt;=</c> 旧值）。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool MonotonicUpdate(ref int variable, int newValue, out int oldValue)
    {
        do
        {
            oldValue = variable;
            if (oldValue >= newValue) return false;
        } while (Interlocked.CompareExchange(ref variable, newValue, oldValue) != oldValue);

        return true;
    }

    /// <summary>
    /// 为 Task 附加取消支持。
    /// <para><see cref="CancellationToken"/> 取消时不中止内部 Task，但让调用方"解锁"并响应取消。用于避免阻塞在卡死的 IO 上。</para>
    /// </summary>
    /// <param name="task">原始 Task。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <typeparam name="T">Task 返回类型。</typeparam>
    /// <returns>支持取消的 Task。</returns>
    public static Task<T> WithCancellationAsync<T>(this Task<T> task,
        CancellationToken cancellationToken = default) =>
        task.WithCancellationAsync(false, cancellationToken);
    /// <summary>
    /// 为 Task 附加取消支持。
    /// <para><see cref="CancellationToken"/> 取消时不中止内部 Task，但让调用方"解锁"并响应取消。用于避免阻塞在卡死的 IO 上。</para>
    /// </summary>
    /// <typeparam name="T">Task 返回类型。</typeparam>
    /// <param name="task">原始 Task。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="useSynchronizationContext">是否使用同步上下文。</param>
    /// <returns>支持取消的 Task。</returns>
    public static Task<T> WithCancellationAsync<T>(this Task<T> task, bool useSynchronizationContext,
        CancellationToken cancellationToken = default)
    {
        if (!cancellationToken.CanBeCanceled || task.IsCompleted)
        {
            return task;
        }
        return cancellationToken.IsCancellationRequested ? Task.FromCanceled<T>(cancellationToken) : SlowWithCancellationAsync(task, useSynchronizationContext, cancellationToken);
    }

    /// <summary>
    /// WithCancellationAsync 的慢路径实现（token 可取消且 Task 未完成时走此路径）。
    /// </summary>
    private static async Task<T> SlowWithCancellationAsync<T>(Task<T> task, bool useSynchronizationContext,
        CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(s => ((TaskCompletionSource<bool>?)s)?.TrySetResult(true), tcs,
                         useSynchronizationContext))
        {
            if (task != await Task.WhenAny(task, tcs.Task))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        // 确保内部 Task 的异常被解包暴露给调用方
        return await task;
    }

    /// <summary>
    /// 将哈希值转为字符串表示（处理负哈希的符号显示）。
    /// </summary>
    /// <param name="hash">哈希值。</param>
    /// <returns>哈希的字符串表示。</returns>
    internal static string GetHashString(long hash)
    {
        var hashSign = hash < 0 ? "-" : string.Empty;
        var absHash = hash >= 0 ? hash : -hash;
        return $"{hashSign}{absHash}";
    }

    /// <summary>
    /// 将可空哈希值转为字符串表示。null 返回 "null"。
    /// </summary>
    /// <param name="hash">可空哈希值。</param>
    /// <returns>哈希的字符串表示，或 "null"。</returns>
    internal static string GetHashString(long? hash) => hash.HasValue ? GetHashString(hash.Value) : "null";
}