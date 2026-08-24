namespace TC.Tier.Contracts.Structures;

/// <summary>
/// 三段式传输读写公共契约（任何结构、任何后端实现）。
/// </summary>
public interface ICommonReaderWriter : IDisposable
{
    /// <summary>
    /// 传输上限（单次 Write/Read 的最大字节数）。
    /// </summary>
    int MaxTransferBytes { get; }
    /// <summary>
    /// 完成会话（写尾/读尾）——Dispose 而未完成 = Abort（本次内容对读侧不可见）。
    /// </summary>
    /// <param name="isSuccess">指示会话是否成功完成。</param>
    void Complete(bool isSuccess=true);
}