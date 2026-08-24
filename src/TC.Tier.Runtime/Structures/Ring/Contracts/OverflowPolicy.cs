namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// 溢出策略（WiscKey 式 KV 分离，配置驱动）。
/// <para>参见 base.md §2.4/§2.8。</para>
/// </summary>
public enum OverflowPolicy
{
    /// <summary>★ 默认：Value 内联主 log record（record=[Header][Key][Value字节]）。对齐 Log 单设备。</summary>
    Disabled,
    /// <summary>Value > MinOverflowSize 时分离到溢出设备（record=[Header][Key][AddressInfo]，Value 在溢出设备）。</summary>
    Enabled,
}
