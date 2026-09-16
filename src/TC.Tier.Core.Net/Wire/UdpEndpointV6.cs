using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// UDP 端点通告载荷·IPv6 形态（20B）。
/// <code>
/// [0]     Tag      0x01（[ValidEquals] 锁死）
/// [1]     Family   2 = IPv6（[ValidEquals] 锁死）
/// [2..4)  Port     UDP 端口（小端）
/// [4..20) Address  16B 不透明字节串（<see cref="Opaque16"/>——原序直拷）
/// </code>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 20)]
public readonly struct UdpEndpointV6
{
    /// <summary>通告 Tag（规范值 <see cref="NegotiateCodec.TagUdpEndpoint"/>）。</summary>
    [FieldOffset(0), ValidEquals(NegotiateCodec.TagUdpEndpoint)] public readonly byte Tag;

    /// <summary>地址族（规范值 <see cref="NegotiateCodec.FamilyIPv6"/>）。</summary>
    [FieldOffset(1), ValidEquals(NegotiateCodec.FamilyIPv6)] public readonly byte Family;

    /// <summary>UDP 端口（小端）。</summary>
    [FieldOffset(2)] public readonly ushort Port;

    /// <summary>IPv6 地址（16B 不透明字节串）。</summary>
    [FieldOffset(4)] public readonly Opaque16 Address;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="tag">通告 Tag。</param>
    /// <param name="family">地址族。</param>
    /// <param name="port">UDP 端口。</param>
    /// <param name="address">IPv6 地址。</param>
    public UdpEndpointV6(byte tag, byte family, ushort port, Opaque16 address)
    {
        Tag = tag;
        Family = family;
        Port = port;
        Address = address;
    }
}