namespace TC.Tier.Contracts.Structures;
/// <summary>
/// 三段式传输读写契约（任何结构、任何后端实现）。
/// </summary>
public interface ITransferReader : ICommonReaderWriter
{
    /// <summary>
    /// 读头（相位信号：协调器开始预准备读取空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="header">要读取的头部字节的目标缓冲区。</param>
    int ReadHeader(Span<byte> header);

    /// <summary>
    /// 读载荷（相位信号：协调器开始预准备读取空间/协调同步异步落盘）。载荷格式归消费方。
    /// </summary>
    /// <param name="chunk">要读取的载荷字节的目标缓冲区。</param>
    int ReadPayload(Span<byte> chunk);

    /// <summary>
    /// 读尾（相位信号：传输结束）。载荷格式归消费方。
    /// </summary>
    /// <param name="footer">要读取的尾部字节的目标缓冲区。</param>
    int ReadFooter(Span<byte> footer);
}