using System.Buffers;
using Microsoft.Win32.SafeHandles;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using System.IO.MemoryMappedFiles;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// 零拷贝 RandomAccess.Read vs MemoryMappedFile 读性能对照——验证引擎选择 pread 是否正确。
/// <para>★ 测试目的：mmap 读路径常被误认为比 pread 快（省一次用户态拷贝）。
///   本基准实测对比，用数据钉死结论（对齐 lease 模型基准的方法论）。</para>
/// <para>★ 维度：读方式 × 块大小 × {顺序读, 随机读}</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks/ -- --filter "*RandomAccessVsMmap*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5)]
public class RandomAccessVsMmapBench : IDisposable
{
    private string _filePath = null!;
    private SafeFileHandle _handle;
    private MemoryMappedFile _mmf = null!;
    private MemoryMappedViewAccessor _mmapAccessor = null!;
    private long _fileSize;
    private int[] _randomOffsets = null!;
    private byte[] _readBuffer = null!;

    /// <summary>块大小：4K / 64K</summary>
    [Params(4096, 65536)]
    public int BlockSize { get; set; }

    private const long TotalBytes = 256L * 1024 * 1024;  // 256MB 数据文件

    [GlobalSetup]
    public void Setup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), $"mmap-vs-pread-{Guid.NewGuid():N}.dat");
        _fileSize = TotalBytes;

        // 预写 256MB 数据（用 RandomAccess.Write 一次写满）
        _handle = File.OpenHandle(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            FileOptions.None, (long)_fileSize);
        var writeBuf = new byte[64 * 1024];
        new Random(42).NextBytes(writeBuf);
        for (long off = 0; off < _fileSize; off += writeBuf.Length)
        {
            int len = (int)Math.Min(writeBuf.Length, _fileSize - off);
            RandomAccess.Write(_handle, writeBuf.AsSpan(0, len), off);
        }
        RandomAccess.FlushToDisk(_handle);
        _handle.Dispose();

        // 重新用只读句柄打开（公平：都从同一文件读）
        _handle = File.OpenHandle(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous);

        // mmap 映射整个文件
        var fs = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        _mmapAccessor = _mmf.CreateViewAccessor(0, _fileSize, MemoryMappedFileAccess.Read);

        _readBuffer = ArrayPool<byte>.Shared.Rent(BlockSize);

        // 预生成随机偏移（随机读用）
        int blockCount = (int)(_fileSize / BlockSize);
        _randomOffsets = new int[Math.Min(blockCount, 10_000)];
        var rng = new Random(123);
        for (int i = 0; i < _randomOffsets.Length; i++)
            _randomOffsets[i] = rng.Next(0, blockCount) * BlockSize;
    }

    [GlobalCleanup]
    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_readBuffer);
        _mmapAccessor?.Dispose();
        _mmf?.Dispose();
        _handle.Dispose();
        try { File.Delete(_filePath); } catch { }
        GC.SuppressFinalize(this);
    }

    // ═══════════════════════════════════════════════════════════════
    // 顺序读（256MB 全扫）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>RandomAccess.Read 顺序读（当前引擎读路径，零拷贝 pread）。</summary>
    [Benchmark(Description = "RandomAccess.Read 顺序", Baseline = true)]
    public long SequentialRead_RandomAccess()
    {
        long sum = 0;
        var buf = _readBuffer.AsSpan(0, BlockSize);
        for (long off = 0; off < _fileSize; off += BlockSize)
        {
            int got = RandomAccess.Read(_handle, buf, off);
            sum += got;
        }
        return sum;
    }

    /// <summary>MemoryMappedFile 顺序读（CreateViewAccessor + ReadArray）。</summary>
    [Benchmark(Description = "mmap 顺序 (ViewAccessor)")]
    public long SequentialRead_Mmap_ViewAccessor()
    {
        long sum = 0;
        var buf = _readBuffer;
        for (long off = 0; off < _fileSize; off += BlockSize)
        {
            _mmapAccessor.ReadArray(off, buf, 0, BlockSize);
            sum += BlockSize;
        }
        return sum;
    }

    /// <summary>MemoryMappedFile 顺序读（指针直读，最快 mmap 路径）。</summary>
    [Benchmark(Description = "mmap 顺序 (unsafe ptr)")]
    public unsafe long SequentialRead_Mmap_Ptr()
    {
        long sum = 0;
        byte* basePtr = null;
        _mmapAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            var buf = _readBuffer;
            for (long off = 0; off < _fileSize; off += BlockSize)
            {
                // 模拟"直接在映射区处理"——读一个字节代表消费数据
                sum += basePtr[off];
            }
        }
        finally
        {
            _mmapAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        return sum;
    }

    // ═══════════════════════════════════════════════════════════════
    // 随机读（10000 次）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>RandomAccess.Read 随机读（10000 次随机偏移）。</summary>
    [Benchmark(Description = "RandomAccess.Read 随机")]
    public long RandomRead_RandomAccess()
    {
        long sum = 0;
        var buf = _readBuffer.AsSpan(0, BlockSize);
        foreach (int off in _randomOffsets)
        {
            RandomAccess.Read(_handle, buf, off);
            sum += BlockSize;
        }
        return sum;
    }

    /// <summary>MemoryMappedFile 随机读（10000 次随机偏移，ViewAccessor）。</summary>
    [Benchmark(Description = "mmap 随机 (ViewAccessor)")]
    public long RandomRead_Mmap_ViewAccessor()
    {
        long sum = 0;
        var buf = _readBuffer;
        foreach (int off in _randomOffsets)
        {
            _mmapAccessor.ReadArray(off, buf, 0, BlockSize);
            sum += BlockSize;
        }
        return sum;
    }

    /// <summary>MemoryMappedFile 随机读（指针直读，最快 mmap 路径）。</summary>
    [Benchmark(Description = "mmap 随机 (unsafe ptr)")]
    public unsafe long RandomRead_Mmap_Ptr()
    {
        long sum = 0;
        byte* basePtr = null;
        _mmapAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        try
        {
            foreach (int off in _randomOffsets)
            {
                sum += basePtr[off];
            }
        }
        finally
        {
            _mmapAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
        }
        return sum;
    }
}
