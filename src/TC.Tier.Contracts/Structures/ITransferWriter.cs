namespace TC.Tier.Contracts.Structures;

/// <summary>
/// 三段式传输读写契约（任何结构、任何后端实现）。
/// </summary>
public interface ITransferWriter : ICommonReaderWriter
{
    /// <summary>
    /// 写头（相位信号：协调器开始预准备写入空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="header">要写入的头部字节的源缓冲区。</param>
    void WriteHeader(ReadOnlySpan<byte> header);

    /// <summary>
    /// 写载荷（相位信号：协调器开始预准备写入空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="chunk">要写入的载荷字节的源缓冲区。</param>
    void WritePayload(ReadOnlySpan<byte> chunk);

    /// <summary>
    /// 写尾（相位信号：传输结束）。载荷格式归消费方。
    /// </summary>
    /// <param name="footer">要写入的尾部字节的源缓冲区。</param>
    void WriteFooter(ReadOnlySpan<byte> footer);
}