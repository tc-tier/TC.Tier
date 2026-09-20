namespace TC.Tier.CodeGen;

/// <summary>
/// [BinaryLayout] 字节序（#498 扩展——字节流协议族如 DNS RFC 1035 为网络字节序）。
/// <para>★ 缺省 <see cref="LittleEndian"/>（本仓既有铁律——unified-binary-layout.md §1.2）；
/// <see cref="BigEndian"/> 面向网络字节序协议，生成物逐字段替换为 BinaryPrimitives 大端调用。</para>
/// </summary>
public enum LayoutEndianness
{
    /// <summary>小端（缺省——既有全部布局）。</summary>
    LittleEndian = 0,

    /// <summary>大端（网络字节序——DNS 及后续大端线协议）。</summary>
    BigEndian = 1,
}
