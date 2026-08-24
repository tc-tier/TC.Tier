namespace TC.Tier.Contracts.Structures;

/// <summary>
/// 三段式异步传输读写契约（任何结构、任何后端实现）。
/// </summary>
public interface IAsyncTransferReader : ICommonReaderWriter, IAsyncDisposable
{
    /// <summary>
    /// 读头（相位信号：协调器开始预准备读取空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="header">要读取的头部字节的目标缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步读取操作的任务。</returns>
    ValueTask<int> ReadHeaderAsync(Memory<byte> header, CancellationToken ct = default);

    /// <summary>
    /// 读载荷（相位信号：协调器开始预准备读取空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="chunk">要读取的载荷字节的目标缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步读取操作的任务。</returns>
    ValueTask<int> ReadPayloadAsync(Memory<byte> chunk, CancellationToken ct = default);

    /// <summary>
    /// 读尾（相位信号：传输结束）。载荷格式归消费方。
    /// </summary>
    /// <param name="footer">要读取的尾部字节的目标缓冲区。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>表示异步读取操作的任务。</returns>
    ValueTask<int> ReadFooterAsync(Memory<byte> footer, CancellationToken ct = default);
}