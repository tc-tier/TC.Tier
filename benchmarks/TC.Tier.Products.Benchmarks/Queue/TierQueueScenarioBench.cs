using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using TC.Tier.Products.Queue;
using TC.Tier.Runtime.Benchmarks.Storage;

namespace TC.Tier.Products.Benchmarks.Queue;

/// <summary>
/// TierQueue 端到端场景矩阵：即时 FIFO、批量写、多生产者、延迟入队、幂等判重与消费确认。
/// <para>运行：<c>dotnet run -c Release --project benchmarks/TC.Tier.Products.Benchmarks --
/// --filter "*TierQueueScenario*"</c>。通过 <c>TC_BENCH_FS_SPEC</c> 可指定 virtual/S3；本基准的
/// <see cref="Medium"/> 参数固定覆盖 memory 与隔离的 local 临时卷。</para>
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80, warmupCount: 3, iterationCount: 5, invocationCount: 256)]
public class TierQueueScenarioBench : IDisposable
{
    private const int SteadyBacklog = 256;
    private BenchVolume? _volume;
    private TierQueue? _queue;
    private byte[] _payload = null!;
    private ReadOnlyMemory<byte>[] _batch = null!;
    private int _sequence;

    [Params("memory:", "local")]
    public string Medium { get; set; } = null!;

    [Params(64, 4096)]
    public int PayloadBytes { get; set; }

    [GlobalSetup(Targets = [nameof(Enqueue), nameof(EnqueueBatch16), nameof(ConcurrentEnqueue)])]
    public void SetupImmediate() => Setup();

    [GlobalSetup(Target = nameof(DelayedEnqueue))]
    public void SetupDelayed() => Setup(delay: true);

    [GlobalSetup(Target = nameof(IdempotentDuplicate))]
    public void SetupIdempotency() => Setup(idempotency: true);

    private void Setup(bool delay = false, bool idempotency = false)
    {
        _volume = new BenchVolume(Medium);
        _payload = GC.AllocateUninitializedArray<byte>(PayloadBytes);
        _batch = Enumerable.Range(0, 16).Select(_ => (ReadOnlyMemory<byte>)_payload).ToArray();
        _queue = new TierQueueBuilder(_volume.Fs, new TierQueueOptions
        {
            QueueName = $"queue-bench-{Guid.NewGuid():N}",
            PageSize = 64 << 10,
            // The append-only scenarios retain their default group's backlog.  256 MB
            // covers all fixed-count BDN samples without allocating the 16 GB runtime default.
            MemorySize = 256L << 20,
            DeadLetter = null,
            Delayed = delay ? new DelayedOptions() : null,
            Idempotency = idempotency ? new IdempotencyOptions() : null,
        }).StartAsync().GetAwaiter().GetResult();
    }

    [Benchmark(Description = "即时入队")]
    public ValueTask<EnqueueResult> Enqueue()
        => _queue!.EnqueueAsync(_payload, default);

    [Benchmark(Description = "批量入队 16")]
    public ValueTask<IReadOnlyList<EnqueueResult>> EnqueueBatch16()
        => _queue!.EnqueueBatchAsync(_batch, default);

    [Benchmark(Description = "多生产者入队 4x4")]
    public void ConcurrentEnqueue()
    {
        Parallel.For(0, 4, _ =>
        {
            for (var i = 0; i < 4; i++)
                _queue!.EnqueueAsync(_payload, default).AsTask().GetAwaiter().GetResult();
        });
    }

    [Benchmark(Description = "延迟入队")]
    public ValueTask<EnqueueResult> DelayedEnqueue()
        => _queue!.EnqueueAsync(
            new EnqueueOptions { DueTime = DateTime.UtcNow.AddMinutes(1).Ticks }, _payload, default);

    [Benchmark(Description = "幂等重复入队")]
    public ValueTask<EnqueueResult> IdempotentDuplicate()
        => _queue!.EnqueueAsync(
            new EnqueueOptions { ProducerId = 1, Seq = 1 }, _payload, default);

    [Benchmark(Description = "端到端取出确认")]
    public async ValueTask<int> DequeueAckSteadyState()
    {
        var delivery = await _queue!.DequeueAsync(1, default);
        if (delivery.Count == 0)
            throw new InvalidOperationException("Steady-state queue unexpectedly empty.");

        await _queue.AckAsync([delivery[0].Address], default);
        await _queue.EnqueueAsync(_payload, default);
        return delivery[0].Payload.Length;
    }

    [GlobalSetup(Target = nameof(DequeueAckSteadyState))]
    public void SetupDequeueAckSteadyState()
    {
        Setup();
        for (var i = 0; i < SteadyBacklog; i++)
            _queue!.EnqueueAsync(_payload, default).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        if (_queue is not null)
            await _queue.DisposeAsync();
        _volume?.Dispose();
        _queue = null;
        _volume = null;
    }


    /// <summary>BDN 基准生命周期收口（GlobalCleanup 阶段自动调用；字段释放随 GlobalCleanup 域）。</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
