namespace TC.Tier.Contracts.Meta;

/// <summary>
/// Meta 传输契约——策略与存储介质之间的唯一通道：写一个完整 meta block / 读回最后一条。
/// <para>★ 实现决定介质与放置策略（单槽覆盖 / 追加到自身存储流后倒扫 / 远程服务……），
///   策略不关心；块的格式与 CRC 完全由策略负责（[Header][Payload][Footer Crc32C]），
///   传输只搬字节——实现不需要理解块内容，也不承担完整性校验。</para>
/// <para>契约：</para>
/// <list type="bullet">
/// <item><description><b>读写统一 Span/Memory 视图，零中间拷贝</b>——<c>WriteBlock</c> 收
///   <see cref="ReadOnlySpan{T}"/>（byte），<c>ReadLastBlock</c> 回 <see cref="ReadOnlySpan{T}"/>；</description></item>
/// <item><description><b>空 = 无数据</b>——<c>ReadLastBlock</c> 返回 <see cref="ReadOnlySpan{T}.IsEmpty"/>
///   （异步版返回空 <see cref="ReadOnlyMemory{T}"/>）即"从未写入"，不用 null；</description></item>
/// <item><description><b>视图生命周期</b>——返回的 Span/Memory 有效至本传输的下一次调用
///   （实现以字段持有底层缓冲，不转移所有权）；调用方需要留存必须立即拷贝；</description></item>
/// <item><description><c>WriteBlock</c> 写入的块即成为本传输的"最后一条"（覆盖或追加由实现决定，
///   语义上 last-write-wins）；同步/异步成员语义对等。</description></item>
/// </list>
/// </summary>
public interface IMetaTransport
{
    /// <summary>写完整 meta block（同步，Span 零分配）。</summary>
    /// <param name="block">完整 meta block（[Header][Payload][Footer Crc32C]）</param>
    void WriteBlock(ReadOnlySpan<byte> block);

    /// <summary>写完整 meta block（异步对等版）。</summary>
    /// <param name="block">完整 meta block（[Header][Payload][Footer Crc32C]）</param>
    /// <param name="ct">取消令牌（可选）。</param>
    ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct);

    /// <summary>读回最后一条 meta block（<see cref="ReadOnlySpan{T}.IsEmpty"/> = 无）。</summary>
    /// <returns>完整 meta block（[Header][Payload][Footer Crc32C]）</returns>
    ReadOnlySpan<byte> ReadLastBlock();

    /// <summary>读回最后一条 meta block（异步对等版；空 Memory = 无）。</summary>
    /// <param name="ct">取消令牌（可选）。</param>
    /// <returns>完整 meta block（[Header][Payload][Footer Crc32C]）</returns>
    ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct);
}
