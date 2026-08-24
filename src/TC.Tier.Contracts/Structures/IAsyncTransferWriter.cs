namespace TC.Tier.Contracts.Structures;
/// <summary>
/// 三段式异步传输读写契约（任何结构、任何后端实现）。
/// </summary>
public interface IAsyncTransferWriter : ICommonReaderWriter, IAsyncDisposable
{
    /// <summary>
    /// 写头（相位信号：协调器开始预准备写入空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="header">要写入的头部字节的源缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    ValueTask WriteHeaderAsync(ReadOnlyMemory<byte> header, CancellationToken ct = default);

    /// <summary>
    /// 写载荷（相位信号：协调器开始预准备写入空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="chunk">要写入的载荷字节的源缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    ValueTask WritePayloadAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default);

    /// <summary>
    /// 写尾（相位信号：传输结束）。载荷格式归消费方。
    /// </summary>
    /// <param name="footer">要写入的尾部字节的源缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步写入操作的任务。</returns>
    ValueTask WriteFooterAsync(ReadOnlyMemory<byte> footer, CancellationToken ct = default);
}