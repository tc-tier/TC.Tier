namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 顺序读句柄——游标 + 读/跳分离的流式接口，自动跨段。
/// </summary>
public interface ISequentialReader : IDisposable
{
    /// <summary>当前读游标（下一次读取的起始地址）。</summary>
    LogicalAddress Position { get; }

    /// <summary>读取窗口起点。</summary>
    LogicalAddress Start { get; }

    /// <summary>读取窗口终点（读到/越过返回 0 = EOF）。</summary>
    LogicalAddress End { get; }

    /// <summary>正序 / 倒序。</summary>
    ReadDirection Direction { get; }

    /// <summary>快照模式。</summary>
    SnapshotMode SnapshotMode { get; }

    /// <summary>
    /// 从 <see cref="Position"/> 读取 destination.Length 字节，读后 Position 自动推进。
    /// <para>跨段自动；越界返回实际所读字节数（可能 0 = EOF）。</para>
    /// </summary>
    int Read(Span<byte> destination);

    /// <summary>
    /// 异步从 <see cref="Position"/> 读取。
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct);

    /// <summary>
    /// 相对移动游标 length 字节，不读数据。
    /// <para>正序前进 / 倒序后退。</para>
    /// </summary>
    void Skip(long length);

    /// <summary>
    /// 绝对地址跳转——把游标定位到任意地址（方向不变）。
    /// </summary>
    /// <param name="target">目标地址。</param>
    /// <exception cref="PartitionInvalidException">target 指向已删除的段文件（硬错误）。</exception>
    void Seek(LogicalAddress target);
}
