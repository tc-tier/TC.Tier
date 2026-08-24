namespace TC.Tier.Runtime.Storage.Checkpoint;

/// <summary>
/// 扫盘切面 Checkpoint——只读，按 segId 游标顺序遍历段文件系统，O(1) 内存。
/// <para>★ 只读切面：Writer = NoopWriter（空操作），Reader = StreamingSegmentReader（流式扫盘）。</para>
/// <para>★ O(1) 内存：不持有段集合，仅 _cursor 游标 + _maxSegId 上界。
///   顺序遍历 [minSegId, maxSegId]，Fs.Exists 跳空洞，存在即读段元组比较取值吐段元组。</para>
/// <para>★ maxSegId 通过二分 Fs.Exists 探测（O(log n) 次 stat），不枚举目录。</para>
/// <para>★ 段元组比较取值：每段经 FileExtra（ReadSegmentTuple）读精确 growthLimit/maxOffset/state；
///   元组无/损坏回退到文件大小（fileSize 权威）。</para>
/// <para>★ 不计算水位——ReadFooter 返回 null，两水位由 LoadAddressTable 循环后从段表算。</para>
/// </summary>
/// <param name="storage">存储引擎（定位段文件 + 读段元组）。</param>
/// <param name="logger">可选日志。</param>
/// <param name="onProgress">可选进度回调（扫盘阶段上报 RecoveryPhase.Recovering）。</param>
internal sealed partial class ScanCheckpoint(
    StorageEngine storage,
    ILogger? logger = null,
    Action<RecoveryProgress>? onProgress = null) : ICheckpoint
{
    /// <summary>存储引擎（供 Reader 定位段文件 + 读段元组 FileExtra）。</summary>
    private readonly StorageEngine _storage = storage;
    /// <summary>日志。</summary>
    private readonly ILogger? _logger = logger;
    /// <summary>进度回调（扫盘上报）。</summary>
    private readonly Action<RecoveryProgress>? _onProgress = onProgress;

    /// <summary>写适配器——扫盘只读，Writer 为空操作（不抛异常）。</summary>
    public IAddressTableWriter Writer { get; } = new NoopWriter();

    private StreamingSegmentReader? _reader;

    /// <summary>读适配器——流式扫盘，单例（保持游标状态，多次访问同一实例）。</summary>
    public IAddressTableReader Reader => _reader ??= new StreamingSegmentReader(this);

    public bool HasSnapshot => false;

    public void Dispose()
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>进度上报辅助（Reader 内部调）。</summary>
    private void RaiseProgress(int percent, string? detail = null)
        => _onProgress?.Invoke(new RecoveryProgress { Phase = RecoveryPhase.Recovering, Percent = percent, Detail = detail });
}
