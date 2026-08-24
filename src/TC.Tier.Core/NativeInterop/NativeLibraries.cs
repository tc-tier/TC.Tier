namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 原生库名常量。统一消除散落各处的库名字符串硬编码
/// <para>所有 [LibraryImport] 调用必须引用本类常量，禁止内联字符串字面量。</para>
/// </summary>
internal static class NativeLibraries
{
    /// <summary>Windows kernel32.dll（文件 IO / IOCP / 线程 / NUMA / 内存锁）。</summary>
    public const string Kernel32 = "kernel32.dll";

    /// <summary>Windows advapi32.dll（权限 / 令牌）。</summary>
    public const string Advapi32 = "advapi32.dll";

    /// <summary>Unix libc（open / fcntl / mlock / munlock）。</summary>
    public const string Libc = "libc";
}
