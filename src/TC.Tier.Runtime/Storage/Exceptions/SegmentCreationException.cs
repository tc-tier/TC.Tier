namespace TC.Tier.Runtime.Storage.Exceptions;

/// <summary>
/// 段创建失败异常。
/// </summary>
public class SegmentCreationException : StorageEngineException
{
    /// <summary>失败的段号。</summary>
    public int SegId { get; }

    /// <summary>
    ///初始化一个 <see cref="SegmentCreationException"/> 类的新实例。
    /// </summary>
    /// <param name="message">错误描述。</param>
    /// <param name="segId">失败的段号。</param>
    /// <param name="innerException">内部异常（可空）。</param>
    public SegmentCreationException(string message, int segId, Exception? innerException = null)
        : base(message, null, innerException)
    {
        SegId = segId;
    }
}