namespace TC.Tier.Contracts.Meta;

/// <summary>
/// Meta 策略统一接口——结构化元数据的持久化契约（独立能力，不限使用方）。
/// <para>★ 统一布局：<b>[Header 纯规范][Payload 结构化水位 + opaque 扩展][Footer Crc32C 4B]</b>。
/// Header 只放规范字段（Magic/Version/Flags/PayloadLength/PaddingLength），水位一律走 Payload；
/// opaque 是写在结构化水位之后的原始扩展字节，长度由 Header.PayloadLength 倒推。</para>
/// <para>★ 契约（所有实现必须一致）：</para>
/// <list type="bullet">
/// <item><description><b>未 Load 的读返回空</b>——ReadHeader/ReadMetaPayload 返回 null、
/// ReadPayload 返回 Empty（Commit 成功后视为已 Load，同实例可读）；</description></item>
/// <item><description><b>重新 Load 全量重置缓存</b>——上一轮的 header/payload/opaque 不残留；</description></item>
/// <item><description><b>Load 返回 false 的三种情况等价</b>：空/无数据/校验失败（Magic、版本、CRC、长度界），
/// 不区分原因；</description></item>
/// <item><description><b>Dispose 幂等</b>，重复调用不抛。</description></item>
/// </list>
/// </summary>
/// <typeparam name="THeader">纯规范 header 结构体（12B 规范字段）。</typeparam>
/// <typeparam name="TPayload">结构化水位 payload 结构体（各使用方独有水位字段）。</typeparam>
public interface IMetaPolicy<THeader, TPayload> : IDisposable, IAsyncDisposable
    where THeader : struct
    where TPayload : struct
{
    /// <summary>Payload 区总容量（结构化首部 + opaque 扩展）。</summary>
    int PayloadSize { get; }

    /// <summary>从存储加载 meta（false = 空/无数据/损坏）。</summary>
    /// <returns>是否成功加载（true = 成功，false = 空/无数据/损坏）。</returns>
    bool Load();

    /// <summary>异步加载（对等同步版）。</summary>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <returns>是否成功加载（true = 成功，false = 空/无数据/损坏）。</returns>
    ValueTask<bool> LoadAsync(CancellationToken ct);

    /// <summary>提交（缓冲 → 算 Crc → 写存储 + Flush）。</summary>
    void Commit();

    /// <summary>异步提交（对等同步版）。</summary>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <returns>提交完成的 <see cref="ValueTask"/>。</returns>
    ValueTask CommitAsync(CancellationToken ct);

    // === Header 读写（纯规范字段）===

    /// <summary>写规范 header（Magic/Version/Flags 由策略保证正确；水位不在此）。</summary>
    /// <param name="header">纯规范 header 结构体。</param>
    void WriteHeader(THeader header);

    /// <summary>读规范 header（未 Load 返回 null）。</summary>
    /// <returns>纯规范 header 结构体或 null。</returns>
    THeader? ReadHeader();

    // === Payload 读写（结构化水位）===

    /// <summary>写结构化水位 payload。</summary>
    /// <param name="payload">结构化水位 payload 结构体。</param>
    void WritePayload(in TPayload payload);

    /// <summary>读结构化水位 payload（未 Load 返回 null）。</summary>
    /// <returns>结构化水位 payload 结构体或 null。</returns>
    TPayload? ReadMetaPayload();

    // === Opaque 读写（raw 扩展区，写在结构化 payload 之后）===

    /// <summary>写 opaque 扩展（raw bytes，写入结构化 payload 之后；不抹掉结构化首部）。</summary>
    /// <param name="opaque">Opaque 扩展字节。</param>
    void WritePayload(ReadOnlySpan<byte> opaque);

    /// <summary>读 opaque 扩展（raw bytes；未 Load 返回 Empty）。</summary>
    /// <returns>Opaque 扩展字节。</returns>
    ReadOnlySpan<byte> ReadPayload();
}