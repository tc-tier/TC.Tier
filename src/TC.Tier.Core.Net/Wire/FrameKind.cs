using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 帧种类常量（spec-12 §3.2 v1 全量——D1-T6 [BinaryLayout]+[ConstantRegistry] 化前的手写形态）。
/// <para>★ 向前兼容规则：新增种类不 bump 头版本；接收侧未知种类 =
///   丢弃 + 计数器上报，<b>不断连</b>——编解码层对 Kind 只透传不拒绝。</para>
/// </summary>
[ConstantRegistry]
public static partial class FrameKind
{
    /// <summary>数据报通道消息（尽力送达）。</summary>
    public const byte Datagram = 0x01;

    /// <summary>流式会话建立请求。</summary>
    public const byte StreamOpen = 0x02;

    /// <summary>流式会话接受（携带反向会话号）。</summary>
    public const byte StreamAccept = 0x03;

    /// <summary>流式会话数据帧。</summary>
    public const byte StreamData = 0x04;

    /// <summary>流式会话正常收尾。</summary>
    public const byte StreamEnd = 0x05;

    /// <summary>流式会话中止（异常/背压放弃）。</summary>
    public const byte StreamReset = 0x06;

    /// <summary>流式会话窗口确认（发起端在途流控——消费一帧回一帧；控制面插队不随流帧排队）。</summary>
    public const byte StreamAck = 0x07;

    /// <summary>请求回调·请求（[CorrId 8B][payload]——spec-12 §5.2）。</summary>
    public const byte Request = 0x08;

    /// <summary>请求回调·应答（[CorrId 8B][payload]）。</summary>
    public const byte Response = 0x09;

    /// <summary>握手发起（明文头 + 安全形态负载候选）。</summary>
    public const byte HandshakeInit = 0x10;

    /// <summary>握手应答。</summary>
    public const byte HandshakeAck = 0x11;

    /// <summary>握手完成确认（明文形态=空载荷；KeyPair/mTLS 形态=密钥确认 MAC）。</summary>
    public const byte HandshakeFinal = 0x12;

    /// <summary>特性协商扩展帧（版本内新特性开关）。</summary>
    public const byte Negotiate = 0x1F;

    /// <summary>保活（默认关闭——keepalive 复用 raft 心跳；特性位开启后使用）。</summary>
    public const byte Keepalive = 0x20;

    /// <summary>传输层错误报告（协议域不可解释/版本拒绝/安全拒绝等）。</summary>
    public const byte Error = 0x7F;
}

/// <summary>
/// 通道标识常量（spec-12 §3.2 ChannelId 字段取值域）。
/// <para>握手/Negotiate/Error 帧一律走管理通道——握手完成前/后它都是唯一不依赖会话状态的通道；
///   数据报/流式帧在握手完成前到达 = Error + 断连。</para>
/// </summary>
[ConstantRegistry]
public static partial class ChannelIds
{
    /// <summary>数据报通道。</summary>
    public const byte Datagram = 0x00;

    /// <summary>流式会话号下界。</summary>
    public const byte MinStream = 0x01;

    /// <summary>流式会话号上界。</summary>
    public const byte MaxStream = 0xFE;

    /// <summary>管理通道（握手/Negotiate/Error 专属）。</summary>
    public const byte Management = 0xFF;

    /// <summary>是否流式会话通道号（1..254）。</summary>
    /// <param name="channelId">待判定的通道标识。</param>
    /// <returns>true = 属于流式会话号区间 [<see cref="MinStream"/>, <see cref="MaxStream"/>]；false = 数据报/管理通道等其他取值。</returns>
    public static bool IsStream(byte channelId) => channelId is >= MinStream and <= MaxStream;
}
