namespace TC.Tier.Contracts.Structures;

/// <summary>
/// 流式读写传输持久化通道——结构层通用的三段式流式读写契约（任何结构、任何后端实现）。
/// </summary>
public interface ITransferPersistence : IDisposable
{
    /// <summary>
    /// 单次 Write/Read 的默认最大字节数。
    /// </summary>
    public const int DefaultMaxTransferBytes = 128 * 1024;

    /// <summary>
    /// 打开写会话（单写者）：写头 → 写数据 × N → 写尾。false = 无法开写（双写者冲突/存储不可用）。
    /// </summary>
    /// <param name="writer">写会话；false 时为 null。</param>
    /// <param name="maxTransferBytes">本会话单次 Write 上限。</param>
    /// <returns>true = 成功打开写会话；false = 无法开写（双写者冲突/存储不可用）。</returns>
    bool TryOpenWrite(out ITransferWriter? writer, int maxTransferBytes = DefaultMaxTransferBytes);

    /// <summary>
    /// 打开读会话——读回最后一次写尾提交的像。false = 账面无完整像（三态等价）。
    /// </summary>
    /// <param name="reader">读会话；false 时为 null。</param>
    /// <param name="maxTransferBytes">本会话单次 Read 上限。</param>
    /// <returns>true = 成功打开读会话；false = 账面无完整像（三态等价）。</returns>
    bool TryOpenRead(out ITransferReader? reader, int maxTransferBytes = DefaultMaxTransferBytes);
}