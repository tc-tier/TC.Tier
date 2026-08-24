namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// Ring 快照写入器——上层 push 数据填回 Ring 页池（快照导入用）。
/// <para>★ 页级流式：caller 每次 Write 推送一块数据，Ring 逐段填入页池，支持 GB/TB。</para>
/// <para>★ 填完所有数据后须调 Complete 推进 TailAddress 到 end。</para>
/// <para>★ 对齐 WAL 的 SnapshotWrite 范式（push 模式，WriteAsync + CompleteAsync）。</para>
/// <para>参见 base.md §2.10。</para>
/// </summary>
public interface IRingSnapshotWriter : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// 同步：增量写入数据块（caller push，Ring 填入页池）。
    /// </summary>
    /// <param name="buffer">caller 提供的源数据。</param>
    void Write(ReadOnlySpan<byte> buffer);

    /// <summary>
    /// 异步：增量写入数据块。
    /// </summary>
    /// <param name="buffer">caller 提供的源数据。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default);

    /// <summary>
    /// 完成写入（推进 TailAddress 到 end，标记快照导入结束）。
    /// <para>填完后必须调——否则 TailAddress 不会推进。</para>
    /// </summary>
    void Complete();

    /// <summary>异步完成写入。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask CompleteAsync(CancellationToken ct = default);
}
