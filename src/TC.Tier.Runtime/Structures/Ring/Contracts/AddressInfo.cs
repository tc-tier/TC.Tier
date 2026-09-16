using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Structures.Ring.Contracts;

/// <summary>
/// AddressInfo——溢出指针（LogicalAddress + Size）。
/// <para>★ 新模型：溢出地址是 LogicalAddress（16B，大小无关），不再用 long 位压缩。</para>
/// <para>对齐 base.md §2.9——溢出指针指向独立溢出引擎地址空间内的 Value。</para>
/// <para>★ 旧版 8B 位压缩（Address 42bit + Size 21bit + Multiplier 1bit）在 LogicalAddress 模型下不适用：
///   LogicalAddress 是 16B 结构（SegId+Extension+Offset），无法压进 42bit。改为显式 24B 结构。</para>
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct AddressInfo
{
    [FieldOffset(0)]  public LogicalAddress Address;
    [FieldOffset(16)] public long Size;

    /// <summary>构造溢出指针（写溢出值后随 record 内联持久化）。</summary>
    /// <param name="address">溢出值在溢出引擎地址空间内的逻辑地址。</param>
    /// <param name="size">溢出值长度（字节）。</param>
    /// <returns>填好 Address/Size 的 24B 溢出指针。</returns>
    public static AddressInfo WriteInfo(LogicalAddress address, long size)
    {
        var ai = new AddressInfo
        {
            Address = address,
            Size = size
        };
        return ai;
    }
}
