using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// UDP 端点通告载荷·IPv4 形态（8B——spec-12 §4.5 特性 bit1）。
/// <code>
/// [0]   Tag      0x01（[ValidEquals] 锁死——validate 补全）
/// [1]   Family   1 = IPv4（[ValidEquals] 锁死）
/// [2..4) Port    UDP 端口（小端）
/// [4..8) Address 4B 不透明字节串（<see cref="Opaque4"/>——原序直拷）
/// </code>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 8)]
public readonly struct UdpEndpointV4
{
    /// <summary>通告 Tag（规范值 <see cref="NegotiateCodec.TagUdpEndpoint"/>）。</summary>
    [FieldOffset(0), ValidEquals(NegotiateCodec.TagUdpEndpoint)] public readonly byte Tag;

    /// <summary>地址族（规范值 <see cref="NegotiateCodec.FamilyIPv4"/>）。</summary>
    [FieldOffset(1), ValidEquals(NegotiateCodec.FamilyIPv4)] public readonly byte Family;

    /// <summary>UDP 端口（小端）。</summary>
    [FieldOffset(2)] public readonly ushort Port;

    /// <summary>IPv4 地址（4B 不透明字节串）。</summary>
    [FieldOffset(4)] public readonly Opaque4 Address;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="tag">通告 Tag。</param>
    /// <param name="family">地址族。</param>
    /// <param name="port">UDP 端口。</param>
    /// <param name="address">IPv4 地址。</param>
    public UdpEndpointV4(byte tag, byte family, ushort port, Opaque4 address)
    {
        Tag = tag;
        Family = family;
        Port = port;
        Address = address;
    }
}