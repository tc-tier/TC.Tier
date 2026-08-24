namespace TC.Tier.Contracts.Structures;

/// <summary>
/// record 扫描游标（对齐 <see cref="IStructureScanCursor"/>，产出 record 的 key/value Span）。
/// </summary>
public interface IRecordScanCursor : IDisposable, IAsyncDisposable
{
    /// <summary>扫描方向。</summary>
    ReadDirection Direction { get; }

    /// <summary>推进到下一条 record。到末尾返回 false。</summary>
    bool MoveNext();

    /// <summary>异步推进到下一条 record。</summary>
    ValueTask<bool> MoveNextAsync(CancellationToken cancellationToken = default);

    /// <summary>当前 record 的 key 字节（零拷贝 Span，仅在下一次 MoveNext 前有效）。</summary>
    ReadOnlySpan<byte> CurrentKey { get; }

    /// <summary>当前 record 的 value 字节（零拷贝 Span，仅在下一次 MoveNext 前有效）。</summary>
    ReadOnlySpan<byte> CurrentValue { get; }

    /// <summary>当前 record 的逻辑地址。</summary>
    LogicalAddress CurrentAddress { get; }
}