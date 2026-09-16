using System.Net;
using System.Net.Sockets;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// Negotiate 帧薄层（spec-12 §3.2 0x1F——特性协商扩展帧；版本内新特性走此帧，不改既有帧格式）。
/// 载荷 = [Tag 1B][Tag 数据]；Tag 0x01 = UDP 端点通告（§4.5 特性位 bit1）。
/// <para>★ 载荷布局 [BinaryLayout] 声明（<see cref="UdpEndpointV4"/>/<see cref="UdpEndpointV6"/>，
///   地址 = <see cref="Opaque4"/>/<see cref="Opaque16"/> 不透明字节串嵌套件；
///   [ValidEquals] 锁 Tag/Family 规范字段——Write(validate:true) 防御性补全）；本层只持
///   Tag 分发与 <see cref="IPAddress"/> 边界转换——零手写字节序、零偏移、零数组拼装。</para>
/// <para>★ 通告时序：三步握手完成（双方都收到 Final）后，协商出 bit1 的一侧各发一帧
///   （管理通道）——对端据其登记 UDP 发送端点。通告缺失 = 对端回落 TCP 承载（尽力送达）。</para>
/// </summary>
public static class NegotiateCodec
{
    /// <summary>UDP 数据报端点通告 Tag。</summary>
    public const byte TagUdpEndpoint = 0x01;

    /// <summary>IPv4 地址族。</summary>
    public const byte FamilyIPv4 = 1;

    /// <summary>IPv6 地址族。</summary>
    public const byte FamilyIPv6 = 2;

    /// <summary>编码 UDP 端点通告载荷（按地址族分发 V4/V6 布局）。</summary>
    /// <param name="endpoint">本端 UDP 数据报端点（地址族仅支持 IPv4/IPv6）。</param>
    /// <returns>线载荷（[Tag 1B][Family 1B][Port 2B][地址]——长度按地址族 8B/20B）。</returns>
    /// <exception cref="ArgumentException">地址族非 v4/v6。</exception>
    public static byte[] EncodeUdpEndpoint(IPEndPoint endpoint)
    {
        var addressBytes = endpoint.Address.GetAddressBytes();
        if (endpoint.AddressFamily == AddressFamily.InterNetwork)
        {
            var payload = new UdpEndpointV4(TagUdpEndpoint, FamilyIPv4, (ushort)endpoint.Port,
                new Opaque4(addressBytes));
            var buffer = new byte[UdpEndpointV4Codec.StructSize];
            UdpEndpointV4Codec.Write(buffer, in payload, validate: true);   // Tag/Family 防御性补全
            return buffer;
        }
        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var payload = new UdpEndpointV6(TagUdpEndpoint, FamilyIPv6, (ushort)endpoint.Port,
                new Opaque16(addressBytes));
            var buffer = new byte[UdpEndpointV6Codec.StructSize];
            UdpEndpointV6Codec.Write(buffer, in payload, validate: true);
            return buffer;
        }
        throw new ArgumentException($"不支持的地址族：{endpoint.AddressFamily}（仅 v4/v6）。", nameof(endpoint));
    }

    /// <summary>解码 Negotiate 载荷的 Tag（未知 Tag = false——向前兼容丢弃）。</summary>
    /// <param name="payload">Negotiate 载荷（≥ 1 字节）。</param>
    /// <param name="tag">解码出的 Tag（false 时为 0）。</param>
    /// <returns>true = 载荷足以读出 Tag；false = 载荷过短（畸形，丢弃）。</returns>
    public static bool TryReadTag(ReadOnlySpan<byte> payload, out byte tag)
    {
        if (payload.Length < NegotiateHeaderCodec.StructSize)
        {
            tag = 0;
            return false;
        }
        tag = NegotiateHeaderCodec.Read_Tag(payload);
        return true;
    }

    /// <summary>
    /// 解码 UDP 端点通告载荷（按载荷长度分发 V4/V6 布局）。false = 长度/Tag/地址族不符
    /// （丢弃——格式违规不致命，通告缺失只是回落 TCP 承载）。
    /// </summary>
    /// <param name="payload">Negotiate 载荷（长度须恰为 V4/V6 布局定长）。</param>
    /// <param name="endpoint">解码出的 UDP 端点（false 时为 <see cref="IPAddress.Any"/>:0 占位）。</param>
    /// <returns>true = 解码成功；false = 长度/Tag/地址族不符。</returns>
    public static bool TryReadUdpEndpoint(ReadOnlySpan<byte> payload, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Any, 0);
        if (payload.Length == UdpEndpointV4Codec.StructSize)
        {
            var p = UdpEndpointV4Codec.Read(payload);
            if (p.Tag != TagUdpEndpoint || p.Family != FamilyIPv4) return false;
            Span<byte> addressBytes = stackalloc byte[Opaque4.Size];
            p.Address.CopyTo(addressBytes);
            endpoint = new IPEndPoint(new IPAddress((ReadOnlySpan<byte>)addressBytes), p.Port);
            return true;
        }
        if (payload.Length == UdpEndpointV6Codec.StructSize)
        {
            var p = UdpEndpointV6Codec.Read(payload);
            if (p.Tag != TagUdpEndpoint || p.Family != FamilyIPv6) return false;
            Span<byte> addressBytes = stackalloc byte[Opaque16.Size];
            p.Address.CopyTo(addressBytes);
            endpoint = new IPEndPoint(new IPAddress((ReadOnlySpan<byte>)addressBytes), p.Port);
            return true;
        }
        return false;
    }
}