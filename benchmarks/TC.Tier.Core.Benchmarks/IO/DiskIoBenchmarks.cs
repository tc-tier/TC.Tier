using BenchmarkDotNet.Attributes;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.Collections;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Core.Benchmarks.IO;

/// <summary>
/// 磁盘专项压测——写路径抽象税 / DIO vs Buffered / CopyRange 加速比 / FlushData vs Flush / 磁盘 Append。
/// <para>★ Linux 基线报告数据见 <c>src/TC.Tier.Core/docs/io-performance.md</c>。</para>
/// </summary>
[MemoryDiagnoser]
public class DiskIoBenchmarks
{
    private const int IoSize = 64 * 1024;
    private const int CopySize = 4 * 1024 * 1024;

    private DiskFileSystem? _fs;
    private string _dir = null!;
    private IFileHandle _writeHandle = null!;
    private IFileHandle _readHandle = null!;
    private IFileHandle _dioHandle = null!;
    private IFileHandle _copySrc = null!;
    private IFileHandle _copyDst = null!;
    private SafeFileHandle _rawWriteHandle = null!;
    private PinnedBufferPool _pool = null!;
    private AlignedMemoryManager _aligned = null!;
    private byte[] _buffer = null!;
    private byte[] _copyBuf = null!;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[IoSize];
        Random.Shared.NextBytes(_buffer);
        _copyBuf = new byte[1024 * 1024];

        _dir = Path.Combine(Path.GetTempPath(), $"core-io-disk-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _fs = DiskFileSystem.OpenOrCreate(_dir);
        _fs.EnsureRoot();

        _writeHandle = _fs.Open("w.data", Opts());
        _writeHandle.Write(0, _buffer);   // 预写一块（写基准固定 offset 覆盖，文件长度稳定）
        _rawWriteHandle = File.OpenHandle(Path.Combine(_dir, "w.data"), FileMode.Open, FileAccess.Write,
            FileShare.ReadWrite);

        using (var seed = _fs.Open("r.data", Opts())) { seed.Write(0, _buffer); }
        _readHandle = _fs.Open("r.data", Opts(access: AccessMode.Read, mode: FileOpenMode.OpenExisting));

        _dioHandle = _fs.Open("dio.data", Opts(hints: FileOpenHints.NoBuffering));
        using (var seed = _fs.Open("dio.data", Opts()))
        {
            // 预分配 + 首写走缓冲句柄（DIO 句柄写需对齐 buffer——种子经缓冲路径）
            seed.Write(0, _buffer);
        }

        _copySrc = _fs.Open("src.data", Opts());
        for (var off = 0L; off < CopySize; off += _buffer.Length) _copySrc.Write(off, _buffer);
        _copyDst = _fs.Open("dst.data", Opts());

        _pool = new PinnedBufferPool();
        _aligned = _pool.RentAligned(IoSize, 4096);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _aligned.Dispose();
        _pool.Dispose();
        _copySrc.Dispose();
        _copyDst.Dispose();
        _writeHandle?.Dispose();
        _readHandle?.Dispose();
        _dioHandle?.Dispose();
        _rawWriteHandle?.Dispose();
        _fs!.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    private static FileOpenOptions Opts(AccessMode access = AccessMode.ReadWrite,
        FileOpenMode mode = FileOpenMode.OpenOrCreate, FileSharing sharing = FileSharing.ReadWrite,
        FileOpenHints hints = FileOpenHints.None, long preallocate = 0)
        => new() { Access = access, Mode = mode, Sharing = sharing, Hints = hints, PreallocateSize = preallocate };

    // ═══════════ 写路径抽象税（Core/IO vs 裸 RandomAccess）═══════════

    [Benchmark(Baseline = true, Description = "Disk 写 64K（Core/IO 抽象）")]
    public void DiskWriteViaCoreIo() => _writeHandle.Write(0, _buffer);

    [Benchmark(Description = "Disk 写 64K（裸 RandomAccess 基线）")]
    public void DiskWriteRaw() => RandomAccess.Write(_rawWriteHandle, _buffer, 0);

    // ═══════════ DIO vs Buffered（同盘同尺寸——对齐路径的诚实标定）═══════════

    [Benchmark(Description = "Disk 读 64K（Buffered 页缓存命中）")]
    public int DiskReadBuffered() => _readHandle.Read(0, _buffer);

    [Benchmark(Description = "Disk 读 64K（DIO O_DIRECT 绕页缓存）")]
    public int DiskReadDirect() => _dioHandle.Read(0, _aligned.GetSpan(0));

    [Benchmark(Description = "Disk 写 64K（DIO O_DIRECT 绕页缓存）")]
    public void DiskWriteDirect() => _dioHandle.Write(0, _aligned.GetSpan(0));

    // ═══════════ CopyRange vs 手写 Read/Write 循环（copy_file_range 加速比）═══════════

    [Benchmark(Description = "CopyRange 4M（内核 copy_file_range）")]
    public long DiskCopyRange() => _copySrc.CopyRange(_copyDst, 0, 0, CopySize);

    [Benchmark(Description = "手写 Read/Write 循环 4M（1M buffer）")]
    public void DiskCopyManual()
    {
        for (var off = 0L; off < CopySize; off += _copyBuf.Length)
        {
            _copySrc.Read(off, _copyBuf);
            _copyDst.Write(off, _copyBuf);
        }
    }

    // ═══════════ 持久化谱系（fdatasync vs fsync——Linux FlushDataOnly 位标定）═══════════

    [Benchmark(Description = "写 4K + Flush（fsync 全量）")]
    public void DiskWriteFlush()
    {
        _writeHandle.Write(4096, _buffer.AsSpan(0, 4096));
        _writeHandle.Flush();
    }

    [Benchmark(Description = "写 4K + FlushData（fdatasync）")]
    public void DiskWriteFlushData()
    {
        _writeHandle.Write(4096, _buffer.AsSpan(0, 4096));
        _writeHandle.FlushData();
    }

    // ═══════════ 磁盘 Append（原子预留热路径——Interlocked + pwrite）═══════════

    [Benchmark(Description = "Disk Append 4K（原子预留）")]
    public long DiskAppend() => _writeHandle.Append(_buffer.AsSpan(0, 4096));
}
