using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Disk;
using TC.Tier.Core.IO.Raw;

namespace TC.Tier.Core.Benchmarks.IO;

/// <summary>
/// Raw 介质专项基准（RM-16——DiskVsRawProbe prototype 的工程化收编，§12.4 验收正式跑法）。
/// <para>★ 方法学（RM-38 修正）：定量工作/op——每 op 固定工作量，Mean = 每 op 成本（低=优）、Ratio 可比、
///   Allocated/op 有意义。此前自旋窗式（while elapsed &lt; 400ms）使 Mean 恒等于窗长、Ratio 失义——已废弃。
///   吞吐 = 每 op 字节量 / Mean（离线换算）。</para>
/// <para>★ 维度 × 对照 Disk：追加（两档 + 每 op 4MB）/ fsync 追加 / 随机读 4K×512（自管页缓存命中）/
///   顺序读 256MB 全扫（直达/缓冲两档 + Disk NoBuffering）/ Open / Stat / 枚举 1000。</para>
/// <para>★ 追加维纪律（RM-38 修复）：[IterationSetup] 删文件——否则 64MB 预分配 × 迭代数打满卷
///   （DiskFull 基准缺陷，HEAD 即无法跑完 append 维）。IterationSetup 计入迭代开销外的 BDN 机制，不污染 op 计时。</para>
/// <para>★ 运行：<c>dotnet run -c Release -- --filter *RawIo*</c>。</para>
/// <para>★ 基线报告：docs/raw-medium-issue-ledger.md RM-01/17/28/38 数字族。</para>
/// </summary>
[MemoryDiagnoser]
public class RawIoBenchmarks
{
    private const int AppendOps = 64;               // 每 op 追加 64 次 × 64KB = 4MB
    private const int IoSize = 64 * 1024;
    private const long SeqFileSize = 256L << 20;    // 顺序读维文件（RM-28 格）
    private const int SeqBufSize = 4 << 20;

    private DiskFileSystem? _disk;
    private RawFileSystem? _raw;
    private string _dir = null!;
    private string _rawPath = null!;
    private byte[] _block = null!;
    private byte[] _fill = null!;
    private long[] _randomOffsets = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"tier-rawbench-{Guid.NewGuid():N}");
        _disk = DiskFileSystem.OpenOrCreate(_dir);
        _disk.EnsureRoot();
        _rawPath = Path.Combine(Path.Combine(Path.GetTempPath(), $"tier-rawbench-{Guid.NewGuid():N}.raw"));
        _raw = RawFileSystem.New(RawCarrier.File(_rawPath), new RawFormatOptions
        {
            QuotaBytes = 4L << 30,   // 追加迭代余量（IterationSetup 重置）+ 顺序读 256MB×2
            JournalReserveBytes = 8L << 20,
        });
        _block = new byte[IoSize];
        new Random(5).NextBytes(_block);
        _fill = new byte[SeqBufSize];
        new Random(33).NextBytes(_fill);
        // 随机读维：热 4MB 文件 + 固定偏移表（每 op = 512 次 4K 读）
        var rand = new Random(9);
        _randomOffsets = new long[512];
        for (var i = 0; i < _randomOffsets.Length; i++)
            _randomOffsets[i] = rand.NextInt64(0, (4 << 22) - 4096);
        foreach (var fs in new IFileSystem[] { _disk, _raw })
        using (var h = fs.Open("big", new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite }))
        {
            for (var i = 0; i < 4; i++)
                h.Write((long)i << 22, new byte[1 << 22]);
            h.Flush();
        }
        // 顺序读维文件：256MB × 两介质（填充经缓冲档 + Flush——数据在载体）
        foreach (var fs in new IFileSystem[] { _disk, _raw })
        using (var h = fs.Open("seq256", new FileOpenOptions
        { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite }))
        {
            for (long off = 0; off < SeqFileSize; off += SeqBufSize)
                h.Write(off, _fill);
            h.Flush();
        }
        // 枚举维：d0 下 1000 小文件
        foreach (var fs in new IFileSystem[] { _disk, _raw })
        {
            fs.CreateDirectory("d0");
            for (var i = 0; i < 1000; i++)
            {
                using var h = fs.Open($"d0/f{i}", new FileOpenOptions
                { Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate, Sharing = FileSharing.ReadWrite });
                h.Write(0, new byte[4096]);
                h.Flush();
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _raw?.Dispose();
        try { File.Delete(_rawPath); } catch { }
        try { File.Delete(_rawPath + ".lock"); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static FileOpenOptions AppendOpts(bool direct) => new()
    {
        Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
        Sharing = FileSharing.ReadWrite, PreallocateSize = 64L << 20,
        Hints = direct ? FileOpenHints.NoBuffering : FileOpenHints.None,
    };

    // ═══ 追加（两档 + Disk 对照——§12.4 验收主格；每 op = 4MB）═══

    [IterationSetup(Targets = new[] { nameof(Append_Disk), nameof(Append_RawBuffered), nameof(Append_RawDirect) })]
    public void AppendReset() => ResetAppendFiles();

    private void ResetAppendFiles()
    {
        try { _disk!.Delete("app-disk"); } catch { }
        try { _raw!.Delete("app-rawbuf"); } catch { }
        try { _raw!.Delete("app-rawdio"); } catch { }
    }

    [Benchmark(Baseline = true)]
    public long Append_Disk() => AppendCore(_disk!, "app-disk", direct: false);

    [Benchmark]
    public long Append_RawBuffered() => AppendCore(_raw!, "app-rawbuf", direct: false);

    [Benchmark]
    public long Append_RawDirect() => AppendCore(_raw!, "app-rawdio", direct: true);

    private long AppendCore(IFileSystem fs, string name, bool direct)
    {
        using var h = fs.Open(name, AppendOpts(direct));
        long sink = 0;
        for (var i = 0; i < AppendOps; i++)
            sink += h.Append(_block);
        return sink;
    }

    [IterationSetup(Targets = new[] { nameof(AppendFlush_Raw), nameof(AppendFlush_Disk) })]
    public void AppendFlushReset()
    {
        try { _raw!.Delete("af-raw"); } catch { }
        try { _disk!.Delete("af-disk"); } catch { }
    }

    [Benchmark]
    public long AppendFlush_Disk()   // fsync 密集对照：Disk 追加 + Flush
    {
        using var h = _disk!.Open("af-disk", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite, PreallocateSize = 64L << 20,
        });
        return h.Append(_block) + FlushCounter(h);
    }

    [Benchmark]
    public long AppendFlush_Raw()   // fsync 密集：每 op = 1 × 64KB 追加 + Flush
    {
        using var h = _raw!.Open("af-raw", new FileOpenOptions
        {
            Access = AccessMode.ReadWrite, Mode = FileOpenMode.OpenOrCreate,
            Sharing = FileSharing.ReadWrite, PreallocateSize = 64L << 20,
        });
        return h.Append(_block) + FlushCounter(h);
    }

    private static int FlushCounter(IFileHandle h)
    {
        h.Flush();
        return 0;
    }

    // ═══ 随机读（自管页缓存命中 vs Disk page cache；每 op = 512 × 4K）═══

    [Benchmark]
    public long RandomRead4K_Raw() => ReadRandom(_raw!);

    [Benchmark]
    public long RandomRead4K_Disk() => ReadRandom(_disk!);

    private long ReadRandom(IFileSystem fs)
    {
        using var h = fs.Open("big", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        var buf = new byte[4096];
        long sink = 0;
        foreach (var o in _randomOffsets)
            sink += h.Read(o, buf);
        return sink;
    }

    // ═══ 顺序读（RM-28 格：256MB 全扫 × 两档 + Disk NoBuffering；每 op = 64 × 4MB）═══

    [Benchmark]
    public unsafe long SeqRead_DiskDirect() => SeqReadCore(_disk!, direct: true);

    [Benchmark]
    public unsafe long SeqRead_RawDirect() => SeqReadCore(_raw!, direct: true);

    [Benchmark]
    public unsafe long SeqRead_RawBuffered() => SeqReadCore(_raw!, direct: false);

    private static unsafe long SeqReadCore(IFileSystem fs, bool direct)
    {
        var bufPtr = (byte*)NativeMemory.AlignedAlloc(SeqBufSize, 4096);   // Disk NoBuffering 三重对齐
        try
        {
            var buf = new Span<byte>(bufPtr, SeqBufSize);
            using var h = fs.Open("seq256", new FileOpenOptions
            {
                Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting,
                Sharing = FileSharing.ReadWrite,
                Hints = direct ? FileOpenHints.NoBuffering : FileOpenHints.None,
            });
            long sink = 0;
            for (long off = 0; off + SeqBufSize <= SeqFileSize; off += SeqBufSize)
                sink += h.Read(off, buf);
            return sink;
        }
        finally
        {
            NativeMemory.AlignedFree(bufPtr);
        }
    }

    // ═══ 元数据面（每 op = 1 次调用）═══

    [Benchmark]
    public long Stat_Raw() => _raw!.Stat("big").Length;

    [Benchmark]
    public long Stat_Disk() => _disk!.Stat("big").Length;

    [Benchmark]
    public long Open_Raw()
    {
        using var h = _raw!.Open("d0/f0", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        return h.Length;
    }

    [Benchmark]
    public long Open_Disk()
    {
        using var h = _disk!.Open("d0/f0", new FileOpenOptions
        { Access = AccessMode.Read, Mode = FileOpenMode.OpenExisting, Sharing = FileSharing.ReadWrite });
        return h.Length;
    }

    [Benchmark]
    public long Enumerate_Raw() => _raw!.EnumerateEntries("d0", "*").Count();

    [Benchmark]
    public long Enumerate_Disk() => _disk!.EnumerateEntries("d0", "*").Count();
}
