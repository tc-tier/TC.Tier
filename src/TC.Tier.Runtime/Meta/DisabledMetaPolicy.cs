namespace TC.Tier.Runtime.Meta;

/// <summary>
/// Disabled meta 策略——no-op（不持久化 meta）。默认值。
/// <para>所有读写/提交操作都是 no-op；Load 返回 false（无 meta）。适用于无数据/空盘/外部管 hints 场景。</para>
/// </summary>
public sealed class DisabledMetaPolicy<THeader, TPayload> : IMetaPolicy<THeader, TPayload>
    where THeader : struct
    where TPayload : struct
{
    public int PayloadSize => 0;

    public bool Load() => false;

    public ValueTask<bool> LoadAsync(CancellationToken ct) => new(false);

    public void WriteHeader(THeader header) { }

    public THeader? ReadHeader() => null;

    public void WritePayload(in TPayload payload) { }

    public TPayload? ReadMetaPayload() => null;

    public void WritePayload(ReadOnlySpan<byte> opaque)
    {
        if (!opaque.IsEmpty)
            throw new ArgumentException("DisabledMetaPolicy does not support data (PayloadSize=0)");
    }

    public ReadOnlySpan<byte> ReadPayload() => ReadOnlySpan<byte>.Empty;

    public void Commit() { }

    public ValueTask CommitAsync(CancellationToken ct) => default;

    public void Dispose() { }

    public ValueTask DisposeAsync() => default;
}
