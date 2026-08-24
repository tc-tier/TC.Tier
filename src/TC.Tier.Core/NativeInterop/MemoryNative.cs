using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 内核原生互操作封装。提供内存锁定、解锁等原生 API 封装。
/// <para>★ <see langword="internal"/>——Core.IO/Primitives 的实现底座，编译期封堵外部直调
///   （外部用 <c>AlignedMemoryManager(lockPhysicalMemory: true)</c>）。</para>
/// </summary>
internal static unsafe class MemoryNative
{
    /// <summary>
    /// 锁定内存（禁止 swap）。Windows 走 VirtualLock，Unix 走 mlock。
    /// </summary>
    /// <param name="address">内存地址</param>
    /// <param name="size">内存大小</param>
    /// <returns>true 成功；false 失败（调用方查 Marshal.GetLastPInvokeError 取错误码）。</returns>
    public static bool LockMemory(void* address, nuint size)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Kernel32.VirtualLock(address, size);
        return LibC.MLock(address, size) == 0;
    }

    /// <summary>
    /// 解锁内存（允许 swap）。Windows 走 VirtualUnlock，Unix 走 munlock。
    /// </summary>
    /// <param name="address">内存地址</param>
    /// <param name="size">内存大小</param>
    /// <returns>true 成功；false 失败（调用方查 Marshal.GetLastPInvokeError 取错误码）。</returns>
    public static bool UnlockMemory(void* address, nuint size)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Kernel32.VirtualUnlock(address, size);
        return LibC.MunLock(address, size) == 0;
    }
}