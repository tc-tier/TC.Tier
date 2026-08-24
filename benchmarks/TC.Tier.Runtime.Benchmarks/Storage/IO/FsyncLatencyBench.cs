using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// ★ fsync 单次延迟独立基准 — Log 根因分析的关键缺失分母。
/// <para>★ 现有所有 bench 都把 Write + Flush 混在一起测，无法回答"单次 fsync 多少 μs"。
///   log-perf.md 声称"每次 fsync 10ms"但无任何 benchmark 支撑，且被实测数据反证（~0.8ms）。
///   本 bench 独立测 <c>RandomAccess.FlushToDisk</c>（即 fsync(2)）的单次延迟。</para>
/// <para>★ 维度：</para>
/// <para> - HandleKind：Buffered（page cache→disk fsync）/ DIO（设备 cache→NAND fsync）/ WriteThrough（Flush no-op 对照）</para>
/// <para> - DirtyBytes：fsync 前写入量梯度（4K/64K/256K/1M/4M）—— fsync 延迟是否随脏数据量增长</para>
/// <para>★ 关键区分：Buffered fsync 刷 OS page cache（脏页可能很多）；DIO fsync 刷设备内部 cache（数据已绕 OS cache）。</para>
/// <para>运行：dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks/ -- --filter "*FsyncLatency*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 5)]
public partial class FsyncLatencyBench : IDisposable
{
    private string _dir = null!;
    private SafeFileHandle _bufHandle = null!;    // Buffered handle
    private SafeFileHandle _dioHandle = null!;    // O_DIRECT handle (via P/Invoke open)
    private SafeFileHandle _wtHandle = null!;     // WriteThrough handle
    private AlignedMemoryManager _buf = null!;
    private int _fdDio = -1;

    /// <summary>0=Buffered fsync, 1=DIO fsync, 2=WriteThrough(Flush no-op 对照)</summary>
    [Params(0, 1, 2)]
    public int HandleKind { get; set; }

    /// <summary>fsync 前的写入量（字节）。测 fsync 延迟是否随脏数据增长。</summary>
    [Params(4096, 65536, 262144, 1048576, 4194304)]
    public int DirtyBytes { get; set; }

    private const int Iterations = 200;   // 每次 [Benchmark] 调 200 次 fsync 取均值
    private long _fileSize;

    private static string Root()
    {
        var r = Environment.GetEnvironmentVariable("BM_DIOM_DIR");
        return string.IsNullOrEmpty(r) ? Path.GetTempPath() : r;
    }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Root(), $"bm-fsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _buf = new AlignedMemoryManager(4194304, 4096);
        _buf.GetSpan().Fill(0x5A);
        _fileSize = (long)Iterations * 4194304 * 2;  // 足够大，防文件末尾

        // Buffered handle
        _bufHandle = File.OpenHandle(Path.Combine(_dir, "buf.dat"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
            FileOptions.Asynchronous, preallocationSize: _fileSize);

        // WriteThrough handle（FileOptions.WriteThrough）
        _wtHandle = File.OpenHandle(Path.Combine(_dir, "wt.dat"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
            FileOptions.Asynchronous | FileOptions.WriteThrough, preallocationSize: _fileSize);

        // DIO handle via native open(O_DIRECT)（Linux 独有路径，复用 Device 层的 P/Invoke）
        _fdDio = OpenODirect(Path.Combine(_dir, "dio.dat"));
        _dioHandle = _fdDio >= 0
            ? SafeFileHandleAdapter(_fdDio)
            : File.OpenHandle(Path.Combine(_dir, "dio.dat"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite,
                FileOptions.Asynchronous, preallocationSize: _fileSize);
    }

    [GlobalCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _bufHandle?.Dispose();
        _wtHandle?.Dispose();
        _dioHandle?.Dispose();
        _buf?.Dispose();
        try { if (_dir != null) Directory.Delete(_dir, true); } catch { }
    }

    /// <summary>测单次 fsync（FlushToDisk）延迟：先写 DirtyBytes → 再 fsync，循环 Iterations 次。</summary>
    [Benchmark(Description = "Fsync.Latency")]
    public void FsyncLatency()
    {
        SafeFileHandle h = HandleKind switch { 0 => _bufHandle, 1 => _dioHandle, 2 => _wtHandle, _ => _bufHandle };
        var span = _buf.GetSpan().Slice(0, DirtyBytes);
        long offset = 0;
        for (int i = 0; i < Iterations; i++)
        {
            // 写 DirtyBytes 到文件（制造脏数据）
            RandomAccess.Write(h, span, offset);
            offset += 4194304;   // 每次跳 4M，避免覆盖同一区域（防 DIO 对齐 + 反映追加语义）
            // ★ 关键测量点：单次 FlushToDisk = fsync(2)
            RandomAccess.FlushToDisk(h);
        }
    }

    // === Linux O_DIRECT P/Invoke（复用 Device 层语义，独立 open 避免依赖 internal 类型）===

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenDirect(string pathname, int flags, int mode);

    private const int O_RDWR = 0x0002;
    private const int O_CREAT = 0x0040;
    private const int O_DIRECT = 0x4000;

    private static int OpenODirect(string path)
    {
        if (!OperatingSystem.IsLinux()) return -1;
        try
        {
            int fd = OpenDirect(path, O_RDWR | O_CREAT | O_DIRECT, 0x1A4);
            if (fd < 0) return -1;
            // 预分配
            if (Interop.ftruncate(fd, (long)Iterations * 4194304 * 2) != 0) { /* best-effort */ }
            return fd;
        }
        catch { return -1; }
    }

    private static SafeFileHandle SafeFileHandleAdapter(int fd)
        => new((IntPtr)fd, ownsHandle: true);
}

internal static partial class Interop
{
    [LibraryImport("libc", EntryPoint = "ftruncate", SetLastError = true)]
    internal static partial int ftruncate(int fd, long length);
}
