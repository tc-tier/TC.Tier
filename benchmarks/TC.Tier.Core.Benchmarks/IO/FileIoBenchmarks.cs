using BenchmarkDotNet.Attributes;
using Microsoft.Win32.SafeHandles;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Mem;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Core.Benchmarks.IO;

/// <summary>
/// Core/IO 性能压测（§11 交付物）——池热路径 / 抽象税 / CopyRange / 向量 IO / 映射读 / mem 直址 / Append 原子预留。
/// <para>★ mem 槽索引寻址 vs 旧 segId 索引的对比基准挂 Runtime.Benchmarks（Core.Benchmarks 不引 Runtime）。</para>
/// </summary>
[MemoryDiagnoser]
public class FileIoBenchmarks
{
    private const int IoSize = 64 * 1024;
    private DiskFileSystem? _diskFs;
    private string _dir = null!;
    private IFileHandle _diskHandle = null!;
    private IFileHandle _diskReadHandle = null!;
    private IFileHandle _memHandle = null!;
    private FileHandlePool _pool = null!;
    private MemoryFileSystem _memFs = null!;
    private MemoryFileSystem _appendFs = null!;
    private IFileHandle _appendHandle = null!;   // Sparse 独立卷（Append 无限增长——周期复位）
    private SafeFileHandle _rawHandle = null!;
    private byte[] _buffer = null!;
    private IMappedSection _section = null!;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[IoSize];
        Random.Shared.NextBytes(_buffer);

        // 磁盘
        _dir = Path.Combine(Path.GetTempPath(), $"core-io-bench-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _diskFs = DiskFileSystem.OpenOrCreate(_dir);
        _diskFs.EnsureRoot();
        _diskHandle = _diskFs.Open("bench.data", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            PreallocateSize = 8 * IoSize,
        });
        _diskHandle.Write(0, _buffer);
        _diskReadHandle = _diskFs.Open("bench.data", new FileOpenOptions
        {
            Access = AccessMode.Read,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
        });
        _rawHandle = File.OpenHandle(Path.Combine(_dir, "bench.data"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _section = _diskReadHandle.Map(0, IoSize, AccessMode.Read);

        // 内存（Reserved——内存引擎类负载的直址模型）
        _memFs = MemoryFileSystem.New(new MemoryFileSystemOptions
        {
            Allocation = MemoryAllocationMode.Reserved,
            QuotaBytes = 64 * 1024 * 1024,
        });
        _memHandle = _memFs.Open("bench", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
            PreallocateSize = 8 * IoSize,
        });
        _memHandle.Write(0, _buffer);

        // 池
        _pool = new FileHandlePool(_memFs);
        _ = _pool.Acquire("pooled", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
        });

        // Append 基准：Sparse 独立卷（容量无硬顶）+ 周期复位（SetLength(0) 后预留盒同步复位）
        _appendFs = MemoryFileSystem.New();
        _appendHandle = _appendFs.Open("ap", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _section.Dispose();
        _appendHandle.Dispose();
        _appendFs.Dispose();
        _diskHandle.Dispose();
        _diskReadHandle?.Dispose();
        _rawHandle?.Dispose();
        _pool?.Dispose();
        _memFs?.Dispose();
        _diskFs?.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best-effort */ }
    }

    private static FileOpenOptions PooledOpts() => new()
    {
        Access = AccessMode.ReadWrite,
        Mode = FileOpenMode.OpenOrCreate,
        Sharing = FileSharing.ReadWrite,
    };

    // ═══════════ 池热路径（命中——Acquire+归还 完整往返，零分配验证）═══════════

    [Benchmark(Baseline = true, Description = "Pool.Acquire+归还 命中往返")]
    public long PoolHitRoundTrip()
    {
        var h = _pool.Acquire("pooled", PooledOpts());
        h.Dispose();   // 归还（挂载式——非关闭）
        return 0;
    }

    [Benchmark(Description = "Pool.TryAcquire 命中")]
    public bool PoolTryAcquire() => _pool.TryAcquire("pooled", PooledOpts(), out _);

    // ═══════════ 池化 vs 非池化（裸 Open/Close 往返——池价值的直接证据）═══════════

    [Benchmark(Description = "裸 fs.Open+Dispose 往返（mem）")]
    public long MemOpenCloseRoundTrip()
    {
        using var h = _memFs.Open("pooled", PooledOpts());
        return 0;
    }

    [Benchmark(Description = "裸 fs.Open+Dispose 往返（disk）")]
    public long DiskOpenCloseRoundTrip()
    {
        using var h = _diskFs!.Open("bench.data", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenExisting,
            Sharing = FileSharing.ReadWrite,
        });
        return 0;
    }

    // ═══════════ 磁盘抽象税（DiskFileHandle vs 裸 RandomAccess）═══════════

    [Benchmark(Description = "Disk 读 64K（Core/IO 抽象）")]
    public int DiskReadViaCoreIo() => _diskReadHandle.Read(0, _buffer);

    [Benchmark(Description = "Disk 读 64K（裸 RandomAccess 基线）")]
    public int DiskReadRaw() => RandomAccess.Read(_rawHandle, _buffer, 0);

    [Benchmark(Description = "mem Reserved 读 64K（直址模型）")]
    public int MemReadReserved() => _memHandle.Read(0, _buffer);

    [Benchmark(Description = "mem Reserved 写 64K（直址模型）")]
    public void MemWriteReserved() => _memHandle.Write(IoSize, _buffer);

    // ═══════════ 映射读 vs Read（Buffered）═══════════

    [Benchmark(Description = "映射视图顺序读 64K")]
    public long MappedRead()
    {
        var span = _section.View.Span;
        long acc = 0;
        for (var i = 0; i < span.Length; i += 64)
            acc += span[i];
        return acc;
    }

    // ═══════════ 向量 IO vs 逐段 ═══════════

    private readonly ReadOnlyMemory<byte>[] _vector =
    [
        new byte[16 * 1024],
        new byte[16 * 1024],
        new byte[16 * 1024],
        new byte[16 * 1024],
    ];

    [Benchmark(Description = "mem WriteVector 4×16K")]
    public void MemWriteVector() => _memHandle.WriteVector(IoSize * 2, _vector);

    [Benchmark(Description = "mem 逐段 Write 4×16K")]
    public void MemWriteSegmented()
    {
        long pos = IoSize * 2;
        foreach (var seg in _vector)
        {
            _memHandle.Write(pos, seg.Span);
            pos += seg.Length;
        }
    }

    // ═══════════ CopyRange vs 手写循环（mem 介质）═══════════

    [Benchmark(Description = "mem CopyRange 4M（用户态回退路径）")]
    public long MemCopyRange() => _memHandle.CopyRange(_memHandle, 0, 4 * IoSize, 4 * IoSize);

    // ═══════════ Append 原子预留热路径（mem）═══════════

    [Benchmark(Description = "mem Append 4K（原子预留）")]
    public long MemAppend()
    {
        var landing = _appendHandle.Append(_buffer.AsSpan(0, 4096));
        if (landing > 32 * 1024 * 1024)
            _appendHandle.SetLength(0);   // 周期复位（~8K 次一次——SetLength 权威复位预留盒，摊薄可忽略）
        return landing;
    }
}

/// <summary>池并发竞争热路径（GetOrAdd 高并发命中）。</summary>
[MemoryDiagnoser]
public class FileHandlePoolContentionBench
{
    private FileHandlePool _pool = null!;
    private MemoryFileSystem _fs = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fs = MemoryFileSystem.New();
        _pool = new FileHandlePool(_fs);
        for (var i = 0; i < 8; i++)
        {
            _ = _pool.Acquire($"f{i}", new FileOpenOptions
            {
                Access = AccessMode.ReadWrite,
                Mode = FileOpenMode.OpenOrCreate,
                Sharing = FileSharing.ReadWrite,
            });
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _pool.Dispose();
        _fs.Dispose();
    }

    [Benchmark(Description = "Pool.Acquire 8 key × 多线程命中")]
    public IFileHandle GetOrAddContended()
    {
        var h = _pool.Acquire($"f{Random.Shared.Next(8)}", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite,
            Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite,
        });
        return h;
    }
}
