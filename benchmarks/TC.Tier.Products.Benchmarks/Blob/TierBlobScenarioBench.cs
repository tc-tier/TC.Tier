using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Products.Blob;
using TC.Tier.Contracts.Storage;
using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Blob;

/// <summary>
/// TierBlob 端到端场景矩阵：整对象单发写（Put）、句柄直达读（Get）、流式会话写读、删除与回收。
/// <para>运行：<c>dotnet run -c Release --project benchmarks/TC.Tier.Products.Benchmarks --
/// --filter "*TierBlobScenario*"</c>。通过 <c>TC_BENCH_FS_SPEC</c> 可指定 virtual/S3；本基准的
/// <see cref="Medium"/> 参数固定覆盖 memory 与隔离的 local 临时卷。</para>
/// <para>基线口径：归档第一版 blob-perf 旧基线为 V1 结构（四 Blob 并列），V2 = StreamSnapshot 单形态
/// 重写——数字不直接可比（帧格式/会话/生命周期全换代），列于此仅作量级对照。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5, invocationCount: 256)]
public class TierBlobScenarioBench : IDisposable
{
    private BenchVolume? _volume;
    private TierBlob? _blob;
    private byte[] _payload = null!;
    private byte[] _streamChunk = null!;
    private LogicalAddress _handle;

    [Params("memory:", "local")]
    public string Medium { get; set; } = null!;

    [Params(4096, 65536)]
    public int PayloadBytes { get; set; }

    [GlobalSetup(Targets = [nameof(Put), nameof(PutGetRoundtrip), nameof(Get), nameof(Delete)])]
    public void SetupPutGet()
    {
        Setup();
        _handle = _blob!.PutAsync(_payload, default).AsTask().GetAwaiter().GetResult().ObjectId;
    }

    [GlobalSetup(Target = nameof(StreamingSessionWrite))]
    public void SetupStreaming() => Setup();

    private void Setup()
    {
        _volume = new BenchVolume(Medium);
        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        _streamChunk = GC.AllocateUninitializedArray<byte>(64 << 10);
        _blob = new TierBlobBuilder(_volume.Fs, new TierBlobOptions
        {
            BlobName = $"blob-bench-{Guid.NewGuid():N}",
            SessionBufferSize = 128 << 10,
        }).StartAsync().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _blob?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _volume?.Dispose();
    }

    [Benchmark(Description = "整对象单发写")]
    public ValueTask<BlobPutResult> Put()
        => _blob!.PutAsync(_payload, default);

    [Benchmark(Description = "写读往返")]
    public async ValueTask<BlobPutResult> PutGetRoundtrip()
    {
        var put = await _blob!.PutAsync(_payload, default);
        var dst = new byte[PayloadBytes];
        await _blob.GetAsync(put.ObjectId, dst, default);
        return put;
    }

    [Benchmark(Description = "句柄直达读")]
    public async ValueTask<int> Get()
    {
        var dst = new byte[PayloadBytes];
        return await _blob!.GetAsync(_handle, dst, default);
    }

    [Benchmark(Description = "流式会话写（64KB×4）")]
    public async ValueTask<BlobPutResult> StreamingSessionWrite()
    {
        await using var session = _blob!.OpenWrite();
        for (var i = 0; i < 4; i++)
            await session.WriteAsync(_streamChunk, default);
        return await session.CompleteAsync(default);
    }

    [Benchmark(Description = "删除（表墓碑）")]
    public ValueTask Delete()
        => _blob!.DeleteAsync(_handle, default);

    /// <summary>BDN 基准生命周期收口（GlobalCleanup 阶段自动调用；字段释放随 GlobalCleanup 域）。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
