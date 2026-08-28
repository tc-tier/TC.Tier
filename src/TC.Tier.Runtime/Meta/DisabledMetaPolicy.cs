namespace TC.Tier.Runtime.Meta;

/// <summary>
/// Disabled meta 策略——no-op（不持久化 meta）。默认值。
/// <para>所有读写/提交操作都是 no-op；Load 返回 false（无 meta）。适用于无数据/空盘/外部管 hints 场景。</para>
/// </summary>
public sealed class DisabledMetaPolicy<THeader, TPayload> : IMetaPolicy<THeader, TPayload>
    where THeader : struct
    where TPayload : struct
{
    /// <summary>Payload 区总容量恒 0（no-op 策略——不持久化任何 meta 数据）。</summary>
    public int PayloadSize => 0;

    /// <summary>同步加载恒失败（no-op 策略无 meta 可读）。</summary>
    /// <returns>恒 false。</returns>
    public bool Load() => false;

    /// <summary>异步加载恒失败（对等同步版）。</summary>
    /// <param name="ct">取消令牌（no-op 忽略）。</param>
    /// <returns>恒 false 的任务。</returns>
    public ValueTask<bool> LoadAsync(CancellationToken ct) => new(false);

    /// <summary>写规范 header（no-op——不持久化）。</summary>
    /// <param name="header">忽略。</param>
    public void WriteHeader(THeader header) { }

    /// <summary>读规范 header（no-op——无 meta 可读）。</summary>
    /// <returns>恒 null。</returns>
    public THeader? ReadHeader() => null;

    /// <summary>写结构化水位 payload（no-op——不持久化）。</summary>
    /// <param name="payload">忽略。</param>
    public void WritePayload(in TPayload payload) { }

    /// <summary>读结构化水位 payload（no-op——无 meta 可读）。</summary>
    /// <returns>恒 null。</returns>
    public TPayload? ReadMetaPayload() => null;

    /// <summary>写 opaque 扩展（no-op 策略容量为 0——非空数据直接拒绝，fail-fast）。</summary>
    /// <param name="opaque">原始扩展字节（非空即抛）。</param>
    /// <exception cref="ArgumentException">opaque 非空（DisabledMetaPolicy 不支持数据）。</exception>
    public void WritePayload(ReadOnlySpan<byte> opaque)
    {
        if (!opaque.IsEmpty)
            throw new ArgumentException("DisabledMetaPolicy does not support data (PayloadSize=0)");
    }

    /// <summary>读 opaque 扩展（no-op——无 meta 可读）。</summary>
    /// <returns>恒为空 Span。</returns>
    public ReadOnlySpan<byte> ReadPayload() => ReadOnlySpan<byte>.Empty;

    /// <summary>提交（no-op——不持久化）。</summary>
    public void Commit() { }

    /// <summary>异步提交（no-op，对等同步版）。</summary>
    /// <param name="ct">取消令牌（no-op 忽略）。</param>
    /// <returns>已默认完成的任务。</returns>
    public ValueTask CommitAsync(CancellationToken ct) => default;

    /// <summary>释放（no-op——无资源可释放）。</summary>
    public void Dispose() { }

    /// <summary>异步释放（no-op）。</summary>
    /// <returns>已默认完成的任务。</returns>
    public ValueTask DisposeAsync() => default;
}
