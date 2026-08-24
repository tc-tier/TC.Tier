using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Core.Benchmarks.NativeInterop;

/// <summary>
/// ★ 裸磁盘 I/O 基准 — 绕过 TC.Tier Device 层，直接使用 OS 文件 I/O。
/// <para>DirectIO 模式使用 NativeMemory.AlignedAlloc(4096) 保证 O_DIRECT 对齐。</para>
/// <para>对比 DeviceIoBench 可算出 Device 层开销。</para>
/// <para>运行: dotnet run -c Release --project benchmarks/TC.Tier.Core.Benchmarks/ -- --filter "*NativeIoBench*"</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3, invocationCount: 256)]
public unsafe class NativeIoBench
{
    private SafeFileHandle _fh = null!;
    private string _dir = null!;
    private string _path = null!;
    private byte[] _managedBuf = null!;
    private byte* _nativePtr;
    private int _bufByteLength;
    private int[] _randOffsets = null!;
    private int _seqIdx, _randIdx;

    [Params(true, false)]
    public bool Buffered { get; set; }

    [Params(4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576)]
    public int BlockSize { get; set; }

    private const int SizeMB = 256;
    private const int SetupBlock = 262144;
    private const long TotalBytes = (long)SizeMB * 1024 * 1024;
    private const int DioAlign = 4096;

    private static readonly FileOptions NoBuffering = (FileOptions)0x20000000;
    private const FileOptions Async = FileOptions.Asynchronous;

    private static string Root()
    {
        var r = Environment.GetEnvironmentVariable("BM_DIOM_DIR");
        return string.IsNullOrEmpty(r) ? Path.GetTempPath() : r;
    }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Root(), $"bm-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "bench.dat");

        FileOptions opts = Buffered ? Async : Async | NoBuffering;
        _fh = File.OpenHandle(_path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.ReadWrite, opts, preallocationSize: TotalBytes * 2);

        _bufByteLength = Math.Max(BlockSize, SetupBlock);

        if (Buffered)
            _managedBuf = GC.AllocateArray<byte>(_bufByteLength, pinned: true);
        else
            _nativePtr = (byte*)NativeMemory.AlignedAlloc((nuint)_bufByteLength, DioAlign);

        // 预写 256MB (SetupBlock 块, 使用独立对齐 buffer)
        int totalBlocks = (int)(TotalBytes / SetupBlock);
        var setupSpan = Buffered
            ? _managedBuf.AsSpan(0, SetupBlock)
            : new Span<byte>(_nativePtr, SetupBlock);

        for (int i = 0; i < totalBlocks; i++)
        {
            setupSpan.Fill((byte)(i & 0xFF));
            WriteSync(setupSpan, (long)i * SetupBlock);
        }

        // 随机偏移
        int maxBlocks = (int)(TotalBytes / BlockSize);
        var rng = new Random(42);
        _randOffsets = new int[Math.Min(8192, maxBlocks)];
        for (int i = 0; i < _randOffsets.Length; i++)
            _randOffsets[i] = rng.Next(0, maxBlocks);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fh?.Dispose();
        if (_nativePtr != null) NativeMemory.AlignedFree(_nativePtr);
        _nativePtr = null;
        try { File.Delete(_path); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Span<byte> Buf => Buffered
        ? _managedBuf.AsSpan(0, BlockSize)
        : new Span<byte>(_nativePtr, BlockSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void WriteSync(Span<byte> data, long offset)
    {
        RandomAccess.Write(_fh, data, offset);
    }

    // ── 顺序读 ──
    [Benchmark(Description = "SeqRead")]
    public int SeqRead()
    {
        int maxBlk = (int)(TotalBytes / BlockSize);
        int off = (_seqIdx++ % maxBlk) * BlockSize;
        return RandomAccess.Read(_fh, Buf, off);
    }

    // ── 顺序写 ──
    [Benchmark(Description = "SeqWrite")]
    public void SeqWrite()
    {
        int maxBlk = (int)(TotalBytes / BlockSize);
        int off = (_seqIdx++ % maxBlk) * BlockSize;
        Buf.Fill(0xAB);
        RandomAccess.Write(_fh, Buf, off);
    }

    // ── 随机读 ──
    [Benchmark(Description = "RandRead")]
    public int RandRead()
    {
        int blk = _randOffsets[_randIdx++ % _randOffsets.Length];
        return RandomAccess.Read(_fh, Buf, (long)blk * BlockSize);
    }

    // ── 随机写 ──
    [Benchmark(Description = "RandWrite")]
    public void RandWrite()
    {
        int blk = _randOffsets[_randIdx++ % _randOffsets.Length];
        Buf.Fill(0xCD);
        RandomAccess.Write(_fh, Buf, (long)blk * BlockSize);
    }
}
