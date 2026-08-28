using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 128-bit CAS — .NET 8 动态加载 / NativeAOT 静态链接。
/// <para>x86-64: lock cmpxchg16b | ARM64: ldaxp/stlxp CAS loop</para>
/// <para>.NET 8 动态加载：DllImportResolver 从 runtimes/{rid}/native/ 按平台加载。
/// NativeAOT：DirectPInvoke + 静态库（见 csproj NativeLibrary item）。</para>
/// </summary>
public static partial class NativeAtomic128
{
    static NativeAtomic128()
    {
        // ★ 动态加载解析器：LibraryImport 默认不按 RID 找 runtimes 子目录，
        //   需显式 resolver 从 runtimes/{rid}/native/ 加载对应平台的动态库。
        //   NativeAOT 编译时此 resolver 不参与（DirectPInvoke 静态链接）。
        NativeLibrary.SetDllImportResolver(typeof(NativeAtomic128).Assembly, ResolveAtomic128);
    }

    private static IntPtr ResolveAtomic128(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != "atomic128") return IntPtr.Zero;

        var asmDir = Path.GetDirectoryName(assembly.Location);
        if (string.IsNullOrEmpty(asmDir)) return IntPtr.Zero;

        // 按当前平台选文件名 + runtimes/{rid}/native/ 子目录
        string fileName, rid;
        if (OperatingSystem.IsWindows()) { fileName = "atomic128.dll"; rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64"; }
        else if (OperatingSystem.IsLinux()) { fileName = "libatomic128.so"; rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64"; }
        else if (OperatingSystem.IsMacOS()) { fileName = "libatomic128.dylib"; rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64"; }
        else return IntPtr.Zero;

        var candidate = Path.Combine(asmDir, "runtimes", rid, "native", fileName);
        if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        // fallback：assembly 目录直接放（非标准部署）
        if (NativeLibrary.TryLoad(Path.Combine(asmDir, fileName), out handle)) return handle;
        return IntPtr.Zero;
    }

    /// <summary>
    /// 128-bit CAS。location 须 16 字节对齐。
    /// <para>oldLo/oldHi 为 in/out——CAS 失败时回写内存当前值，调用方用于 CAS 循环。</para>
    /// <para><b>[SuppressGCTransition]</b>：跳过 P/Invoke 的 GC 模式切换（cooperative↔preemptive），
    /// 省 ~3-4 ns stub 开销（实测 11.8→8.3 ns）。.NET 8 起支持 [LibraryImport] 源生成器版 P/Invoke。
    /// 前提：native 调用须 &lt; 1μs、不回调托管、不阻塞。tc_cmpxchg128 满足——对齐路径
    /// cmpxchg16b ~5ns；兜底分片锁无争用 &lt;100ns。水位字段走对齐分配命中快路径，兜底极罕见。</para>
    /// <para><b>[return: UnmanagedType.U1]</b>：C99 <c>bool</c> 返回值仅 1 字节（ABI 只保证 AL 有效，
    /// EAX 高 24 位为残留垃圾）。曾误用 UnmanagedType.Bool（4 字节 Win32 BOOL）读取完整 EAX，
    /// CAS 失败路径 EDX 残留（内存当前 Hi 值）经 <c>mov eax,edx</c> 混入 EAX，失败被误判为成功，
    /// 导致并发地址租借重叠（详见 docs/issues/native-cas-concurrent-bug-diagnosis.md）。</para>
    /// </summary>
    [LibraryImport("atomic128", EntryPoint = "tc_cmpxchg128")]
    [SuppressGCTransition]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool Cmpxchg128Impl(
        ref Int128 location,
        ref ulong oldLo,
        ref ulong oldHi,
        ulong newLo,
        ulong newHi);

    /// <summary>128-bit CAS 便捷包装。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool CompareExchange(ref Int128 location, Int128 oldValue, Int128 newValue)
    {
        var ol = oldValue.Lo;
        var oh = oldValue.Hi;
        return Cmpxchg128Impl(ref location, ref ol, ref oh, newValue.Lo, newValue.Hi);
    }
}

/// <summary>128-bit 值类型（2 × ulong, 16B）。</summary>
[StructLayout(LayoutKind.Sequential, Size = 16)]
public readonly struct Int128(ulong lo, ulong hi)
{
    /// <summary>低位 64 位（x86-64 下对应 RAX 承载的半部）。</summary>
    public readonly ulong Lo = lo;

    /// <summary>高位 64 位（x86-64 下对应 RDX 承载的半部）。</summary>
    public readonly ulong Hi = hi;

    /// <summary>返回 Hi 在前、Lo 在后的 32 位大写十六进制串（各 16 位）。</summary>
    /// <returns>形如 <c>0x{Hi:X16}{Lo:X16}</c> 的十六进制字符串。</returns>
    public override string ToString() => $"0x{Hi:X16}{Lo:X16}";
}
