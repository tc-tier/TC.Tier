namespace TC.Tier.Contracts.Storage;

/// <summary>
/// 魔术字方向性定位结果（<see cref="MagicLocation"/>）——引擎 <c>MagicLocator.Locate</c> 的返回：
/// 命中方向的<b>精确逻辑地址</b> + 其所在扫描页起点。
/// <para>★ 两步定位协议：引擎侧粗锚点（Linear 逐页方向扫 / Monotone 页级二分 + 页内方向扫）；
///   上层使用者从锚点起结合自身格式<b>精确查找</b>记录边界（magic 只提名候选，结构/CRC 才是裁决）。</para>
/// <para>★ 直接给 <see cref="LogicalAddress"/>（上层工作语言）——不是距 MinAddress 的距离，
///   也没有段号/段偏移拆分（上层无法直接消费）。</para>
/// </summary>
/// <param name="Found">是否命中。★ 显式区分"未找到"与"命中在 seg#0@0x0"（Empty 是合法地址，不能作哨兵）。</param>
/// <param name="MagicAddress">最后一个 magic 命中的精确逻辑地址（未命中时 = <see cref="LogicalAddress.Invalid"/>）。</param>
/// <param name="PageAddress">命中所在扫描页起点的逻辑地址——上层精确扫描的工作锚点（未命中时 = Invalid）。</param>
public readonly record struct MagicLocation(bool Found, LogicalAddress MagicAddress, LogicalAddress PageAddress)
{
    /// <summary>未命中——地址字段 = <see cref="LogicalAddress.Invalid"/>（-1 哨兵：IsValid=false，
    /// 忽略 Found 直接读地址会响亮地错，而非静默指向合法的 seg#0@0x0 = Empty）。</summary>
    public static readonly MagicLocation NotFound = new(false, LogicalAddress.Invalid, LogicalAddress.Invalid);
}
