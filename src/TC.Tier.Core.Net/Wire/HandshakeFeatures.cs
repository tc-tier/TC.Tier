using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 握手特性位 v1（spec-12 §3.3——bit4-7 保留，发方必须清零；交集规则=双方都开才生效）。
/// <para>★ 升级矩阵（二期-H2）：Keepalive/UdpEndpoint/Security/Trace = v1 已定义位清单——
/// 新特性按 bit4 起顺序申请，ReservedMask 收窄同步本表。</para>
/// </summary>
[ConstantRegistry]
public static partial class HandshakeFeatures
{
    /// <summary>无特性。</summary>
    public const byte None = 0x00;

    /// <summary>bit0：Keepalive（10s 周期 + 3 次未答断连——NAT/QUIC 场景）。</summary>
    public const byte Keepalive = 0x01;

    /// <summary>bit1：UDP 端点通告（TCP 连接上告知本端 UDP 监听地址——p2p 用）。</summary>
    public const byte UdpEndpoint = 0x02;

    /// <summary>
    /// bit2：安全档通告（KeyPair/mTLS——spec-12 §3.4）。<b>通告位不是协商位</b>
    /// （安全档由本地配置驱动 fail-closed，明文 Init/Ack 无完整性保护，协商驱动=降级攻击面）。
    /// </summary>
    public const byte Security = 0x04;

    /// <summary>
    /// bit3：trace 上下文传播（二期-I4——请求载荷带 SpanContextCodec 前缀；
    /// 交集协商门控——混版滚动升级下旧对端载荷零变化）。
    /// </summary>
    public const byte Trace = 0x08;

    /// <summary>保留位掩码（bit4-7——发送方必须清零）。</summary>
    public const byte ReservedMask = 0xF0;
}
