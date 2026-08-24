using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Microsoft.Win32.SafeHandles;

namespace TC.Tier.Runtime.Benchmarks.Storage.IO;

/// <summary>
/// 验证一个核心假设:Device 层 WriteThrough 是否拖累了 group commit 的并发页 flush。
///
/// 背景(代码事实,见调研):
///   - Allocator 的 AsyncFlushPages 对 N 页并发 fire-and-forget 调 device.WriteAsync(AllocatorBase.IO.cs:255/259/286)
///   - OnWriteComplete await device ValueTask 后才推进 FlushedUntilAddress(ValueTaskIOExtensions.cs:53-54)
///   - FlushedUntilAddress 是"已落盘边界",上层 commit 依赖它推进
///   - ManagedLocalStorageDevice 把 WriteThrough 永远开(ManagedLocalStorageDevice.cs:154)
///
/// 本 bench 直接用 File.OpenHandle 复刻 device 的打开方式,独立切换 WriteThrough,
/// 模拟 fire-and-forget 并发页写,测吞吐/延迟。这是判断"WriteThrough 是否是性能问题"的硬数据。
///
/// 运行: dotnet run -c Release --project benchmarks/TC.Tier.Runtime.Benchmarks/ -- --filter "*WriteThroughPipeline*"
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 2, iterationCount: 3)]
public class WriteThroughPipelineBench
{
    private string _dir = null!;
    private SafeFileHandle _handleWT = null!;   // WriteThrough on(= 当前 device 行为)
    private SafeFileHandle _handleNoWT = null!; // WriteThrough off(对照)
    private byte[] _page = null!;               // 一页(对齐 4K)

    // 页大小:对齐 FASTER/WAL 的 PageSize 量级(典型 4K-32K)。NO_BUFFERING 要求地址/偏移/长度对齐。
    [Params(4096, 32768, 65536)]
    public int PageSize { get; set; }

    // 并发页数:模拟 AsyncFlushPages 一次发几页(fire-and-forget fan-out)
    [Params(4, 16, 64)]
    public int Concurrency { get; set; }

    // 总写入量(固定,= PageSize * Concurrency * Batches),保证两种模式写同样字节
    private const long TotalBytes = 64L * 1024 * 1024; // 64 MB

    private static readonly FileOptions NoBuffering = (FileOptions)0x20000000;
    private const FileOptions Async = FileOptions.Asynchronous;
    private const FileOptions WT = FileOptions.WriteThrough;

    // BM_DIOM_DIR 可覆盖测试盘符(跨盘/跨 OS 实测),默认 %TEMP%
    private static string Root()
    {
        var root = Environment.GetEnvironmentVariable("BM_DIOM_DIR");
        return string.IsNullOrEmpty(root) ? Path.GetTempPath() : root;
    }

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Root(), $"bm-wt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);

        // 两个独立文件,各自用一种 flag 打开,保证对照公平
        _handleWT = OpenHandle(Path.Combine(_dir, "wt.dat"), Async | WT | NoBuffering);
        _handleNoWT = OpenHandle(Path.Combine(_dir, "nowt.dat"), Async | NoBuffering);

        // 对齐 4K 的页 buffer(GC.AllocateArray pinned 保证地址对齐;这里取 4K 对齐足够)
        _page = GC.AllocateArray<byte>(PageSize, pinned: true);
        _page.AsSpan().Fill(0xAB);
    }

    private static SafeFileHandle OpenHandle(string path, FileOptions fo)
        => File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite,
               FileShare.ReadWrite, fo, preallocationSize: TotalBytes * 2);

    [GlobalCleanup]
    public void Cleanup()
    {
        _handleWT?.Dispose();
        _handleNoWT?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── 模拟 group commit:并发 fire-and-forget 写 N 页,await 全部完成 ──
    // 这是 AsyncFlushPages 的核心模式:一次发 N 页,靠完成推进边界。

    [Benchmark(Description = "WriteThrough ON (current device)")]
    public async Task PipelineWriteThrough()
    {
        long batches = TotalBytes / ((long)PageSize * Concurrency);
        var tasks = new Task[Concurrency];
        long off = 0;
        for (long b = 0; b < batches; b++)
        {
            for (int i = 0; i < Concurrency; i++)
            {
                long o = off + (long)i * PageSize;
                tasks[i] = RandomAccess.WriteAsync(_handleWT, _page, o).AsTask();
            }
            await Task.WhenAll(tasks);
            off += (long)Concurrency * PageSize;
        }
    }

    [Benchmark(Description = "WriteThrough OFF (control)")]
    public async Task PipelineNoWriteThrough()
    {
        long batches = TotalBytes / ((long)PageSize * Concurrency);
        var tasks = new Task[Concurrency];
        long off = 0;
        for (long b = 0; b < batches; b++)
        {
            for (int i = 0; i < Concurrency; i++)
            {
                long o = off + (long)i * PageSize;
                tasks[i] = RandomAccess.WriteAsync(_handleNoWT, _page, o).AsTask();
            }
            await Task.WhenAll(tasks);
            off += (long)Concurrency * PageSize;
        }
    }
}
