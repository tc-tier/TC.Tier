using System.Runtime.InteropServices;

namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// zstd 原生编解码（RM-13——TCA1 流帧压缩 codec）。
/// <para>★ 运行时探测：候选库名逐个 <c>NativeLibrary.TryLoad</c> 取句柄；缺失环境 <see cref="IsAvailable"/>=false
///   （ImageOptions.Validate 对 Zstd 请求显式拒绝——诚实降级，不静默回退）。</para>
/// <para>★ 调用经 <c>NativeLibrary.GetExport</c> 函数指针（探测与使用同一句柄——无 DllImport 名解析歧义：
///   Linux 运行库常只有 libzstd.so.1 而无 libzstd.so 符号链接）。逐帧独立（坏一帧不毁全卷）。</para>
/// </summary>
internal static unsafe class ZstdCodec
{
    private static readonly IntPtr _lib = LoadLib();

    /// <summary>本机 zstd 可用性（探测一次缓存）。</summary>
    public static bool IsAvailable => _lib != IntPtr.Zero;

    private static IntPtr LoadLib()
    {
        foreach (var name in new[]
                 {
                     "libzstd.so.1", "libzstd.so",   // Linux（运行库常见仅 .so.1）
                     "libzstd.dylib",                 // macOS
                     "zstd.dll", "libzstd.dll",       // Windows
                 })
            if (NativeLibrary.TryLoad(name, out var handle))
                return handle;
        return IntPtr.Zero;
    }

    private static unsafe delegate* unmanaged<nuint, nuint> CompressBound()
        => (delegate* unmanaged<nuint, nuint>)NativeLibrary.GetExport(_lib, "ZSTD_compressBound");

    private static delegate* unmanaged<void*, nuint, void*, nuint, int, nuint> CompressFn()
        => (delegate* unmanaged<void*, nuint, void*, nuint, int, nuint>)NativeLibrary.GetExport(_lib, "ZSTD_compress");

    private static delegate* unmanaged<void*, nuint, void*, nuint, nuint> DecompressFn()
        => (delegate* unmanaged<void*, nuint, void*, nuint, nuint>)NativeLibrary.GetExport(_lib, "ZSTD_decompress");

    private static delegate* unmanaged<nuint, uint> IsErrorFn()
        => (delegate* unmanaged<nuint, uint>)NativeLibrary.GetExport(_lib, "ZSTD_isError");

    /// <summary>压缩（level 3 = 默认档——帧级小块高 level 无收益）。失败抛 IOException。</summary>
    public static byte[] CompressFrame(ReadOnlySpan<byte> raw)
    {
        var bound = (int)CompressBound()((nuint)raw.Length);
        var dst = new byte[bound];
        fixed (byte* dp = dst)
        fixed (byte* sp = raw)
        {
            var written = CompressFn()(dp, (nuint)bound, sp, (nuint)raw.Length, 3);
            if (IsErrorFn()(written) != 0)
                throw new IOException($"zstd compress failed: code={written}");
            return dst.AsSpan(0, (int)written).ToArray();
        }
    }

    /// <summary>解压（rawLen 为调用方已知的原始长度——帧头契约）。失败抛 IOException。</summary>
    public static byte[] DecompressFrame(ReadOnlySpan<byte> stored, int rawLen)
    {
        var dst = new byte[rawLen];
        fixed (byte* dp = dst)
        fixed (byte* sp = stored)
        {
            var written = DecompressFn()(dp, (nuint)rawLen, sp, (nuint)stored.Length);
            if (IsErrorFn()(written) != 0 || written != (nuint)rawLen)
                throw new IOException($"zstd decompress failed: code={written} expect={rawLen}");
            return dst;
        }
    }
}
