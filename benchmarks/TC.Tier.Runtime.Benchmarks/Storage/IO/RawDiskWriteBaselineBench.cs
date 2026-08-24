using System.Buffers;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// ★ 磁盘裸写基线——.NET RandomAccess.Write 直接测磁盘物理写极限。
/// <para>这是 Log 性能对照的基线（同盘同数据量），回答"Log 榨干了磁盘百分之多少"。</para>
/// <para>维度：块大小 × {Buffered, DIO 无缓冲} × 数据量</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks/ -- --filter "*RawDiskWrite*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class RawDiskWriteBaselineBench : IDisposable
{
    private string _filePath = null!;
    private SafeFileHandle _handle = null!;
    private byte[] _buffer = null!;

    /// <summary>块大小：64K / 256K / 1M / 4M（对齐 Log 页大小）</summary>
    [Params(65536, 262144, 1048576, 4194304)]
    public int BlockSize { get; set; }

    /// <summary>IO 模式：0=Buffered(page cache), 1=DIO 无缓冲</summary>
    [Params(0, 1)]
    public int IoModel { get; set; }

    /// <summary>数据量：64MB</summary>
    private const long TotalBytes = 64L * 1024 * 1024;

    private FileOptions Opts => IoModel == 1
        ? FileOptions.Asynchronous | FileOptions.WriteThrough  // DIO + WT（每写穿透落盘）
        : FileOptions.Asynchronous;                              // Buffered（page cache）

    [IterationSetup]
    public void IterationSetup()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "raw-write-baseline-" + Guid.NewGuid().ToString("N") + ".dat");
        _handle = File.OpenHandle(_filePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None, Opts);
        _buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        new Random(42).NextBytes(_buffer);
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _handle?.Dispose();
        ArrayPool<byte>.Shared.Return(_buffer);
        try { File.Delete(_filePath); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>裸顺序写（RandomAccess.Write，零中间层）。</summary>
    [Benchmark(Description = "RawWrite")]
    public long RawWrite()
    {
        long written = 0;
        var buf = _buffer.AsSpan(0, BlockSize);
        while (written < TotalBytes)
        {
            int len = (int)Math.Min(BlockSize, TotalBytes - written);
            RandomAccess.Write(_handle, buf.Slice(0, len), written);
            written += len;
        }
        return written;
    }
}
