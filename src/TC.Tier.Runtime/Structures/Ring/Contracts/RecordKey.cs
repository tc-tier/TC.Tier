namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// RingBase 记录Key结构（Key + ValueLength + Flags + Address）。
/// </summary>
/// <param name="key">Key 值</param>
/// <param name="valueLength">Value 长度</param>
/// <param name="flags">Flags 值</param>
/// <param name="address">逻辑地址</param>
/// <typeparam name="TKey">Key 类型</typeparam>
public readonly struct RecordKey<TKey>(TKey key, int valueLength, ushort flags, LogicalAddress address)
{
    /// <summary>
    /// Key
    /// </summary>
    public TKey Key { get; } = key;

    /// <summary>
    /// Value 长度
    /// </summary>
    public int ValueLength { get; } = valueLength;

    /// <summary>
    /// 逻辑地址
    /// </summary>
    public LogicalAddress Address { get; } = address;

    /// <summary>Value 是否溢出到溢出设备（payload 含 AddressInfo，非内联 Value）。</summary>
    public bool IsOverflow => (flags & RecordFlags.FLAG_VALUE_OVERFLOW) != 0;

    /// <summary>是否墓碑记录（删除标记）。</summary>
    public bool IsTombstone => (flags & RecordFlags.FLAG_RINGRECORD_TOMBSTONE) != 0;
}