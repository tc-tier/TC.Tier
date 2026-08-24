namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>批量读回调接口（key 定长 TKey 直读，无解读层）。</summary>
public interface IReadOnlyRecordHandler<in TKey>
{
    /// <summary>
    /// 批量读回调。<paramref name="flags"/> 是 <c>RecordFlags</c> 内部位（含 FLAG_VALUE_OVERFLOW 等），
    /// 调用方如需语义判断应通过位与解读（常量对程序集内可见）。
    /// </summary>
    void Handle(LogicalAddress address, TKey key, int valueLength, ushort flags);
}