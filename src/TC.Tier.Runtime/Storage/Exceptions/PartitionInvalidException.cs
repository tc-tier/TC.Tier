namespace TC.Tier.Runtime.Storage.Exceptions;

/// <summary>
/// 段文件缺失（外部删除 / <see cref="IStorageEngine.ReclaimHead"/> 后访问）。
/// 硬错误——显式定位操作（Seek 等）指向无效地址时抛出。
/// </summary>
public class PartitionInvalidException : StorageEngineException
{
    /// <inheritdoc/>
    public PartitionInvalidException(string message, LogicalAddress? address = null, Exception? innerException = null)
        : base(message, address, innerException)
    {
    }
}