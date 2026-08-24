namespace TC.Tier.Runtime.Structures.Snapshot.Contracts;

/// <summary>
/// 写会话（双 buffer flush 流水线）——GB/TB 流式写核心。
/// 双 buffer：A 满启动异步 flush(A) 立即切 B；B 满 await flush(A)（通常已完成）再 flush(B)。
/// CPU 序列化与磁盘 IO 并行，消除单 buffer 串行停顿。
/// </summary>
public interface ISnapshotWriteSession : IDisposable, IAsyncDisposable
{
    /// <summary>当前 buffer 剩余可用字节。</summary>
    int FreeBytes { get; }

    /// <summary>每次 flush 完成触发：(flushedAddress, logicalBytes, alignedBytes)。</summary>
    event Action<LogicalAddress, int, int>? OnFlushed;

    /// <summary>高性能异步写入：自动管理 buffer swap + pipeline flush。快速路径零分配同步返回。</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>同步写入：buffer 满时同步 swap+flush。单线程契约。</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>微写入（header/footer 等固定小数据）：不触发 flush；调用方先 FlushIfFull 确保空间。</summary>
    void WriteSmall(ReadOnlySpan<byte> data);

    /// <summary>空间不足 needed 时 swap + flush（异步）。</summary>
    ValueTask FlushIfFullAsync(int needed, CancellationToken ct = default);

    /// <summary>空间不足 needed 时同步 swap + flush。</summary>
    void FlushIfFull(int needed);

    /// <summary>最终 flush：await pipeline + flush 剩余。幂等。</summary>
    ValueTask FlushAsync(CancellationToken ct = default);

    /// <summary>同步最终 flush。幂等。</summary>
    void Flush();
}

/// <summary>
/// 读会话（双 buffer 异步预读）——GB/TB 流式读核心。
/// 物理/逻辑偏移分离：底层 DIO 要求 offset/length 扇区对齐，上层需要紧凑逻辑流——
/// 对外暴露逻辑视图，内部对齐预读并剔除 padding。
/// </summary>
public interface ISnapshotReadSession : IAsyncDisposable
{
    /// <summary>逻辑起点。</summary>
    LogicalAddress LogicalStart { get; }

    /// <summary>逻辑终点。</summary>
    LogicalAddress LogicalEnd { get; }

    /// <summary>物理终点。</summary>
    LogicalAddress PhysicalEnd { get; }

    /// <summary>填充式字节流交付（剔除 padding，只交付逻辑区间内字节）。返回 0 = 逻辑 EOF。</summary>
    ValueTask<int> ReadAsync(Memory<byte> dest, CancellationToken ct = default);
}
