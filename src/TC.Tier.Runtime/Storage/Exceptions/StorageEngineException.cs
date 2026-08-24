namespace TC.Tier.Runtime.Storage.Exceptions;

/// <summary>
/// StorageEngine 层所有异常的基类。继承 <see cref="IOException"/>——上层 catch(IOException) 自动覆盖。
/// </summary>
public class StorageEngineException : IOException
{
    /// <summary>出错的地址（可为 null；不暴露 segId，满足不变量 14）。</summary>
    public LogicalAddress? Address { get; }

    /// <summary>构造 StorageEngine 层异常。</summary>
    /// <param name="message">错误描述。</param>
    /// <param name="address">出错地址（可空）。</param>
    /// <param name="innerException">内部异常（可空）。</param>
    internal StorageEngineException(string message, LogicalAddress? address = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Address = address;
    }
}