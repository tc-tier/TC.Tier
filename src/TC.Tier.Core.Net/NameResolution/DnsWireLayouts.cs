using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.NameResolution;

/// <summary>
/// DNS 报文头（RFC 1035 §4.1.1——12B 大端）。布局声明一次（FieldOffset），读写全为
/// <see cref="DnsMessageHeaderCodec"/> 生成物——零手写偏移（generators.md 反模式 1）。
/// <para>★ <see cref="Flags"/> 是位域打包（QR/Opcode/AA/TC/RD/RA/Z/AD/CD/RCODE）——位面语义归
/// <see cref="DnsHeaderFlags"/> 常量族，布局面只承载原始 16 位。</para>
/// </summary>
[BinaryLayout(Endianness = LayoutEndianness.BigEndian, Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 12)]
public struct DnsMessageHeader
{
    /// <summary>事务 ID（响应与请求对账）。</summary>
    [FieldOffset(0)] public ushort Id;

    /// <summary>标志位域（原始 16 位——位语义见 <see cref="DnsHeaderFlags"/>）。</summary>
    [FieldOffset(2)] public ushort Flags;

    /// <summary>问题节数。</summary>
    [FieldOffset(4)] public ushort QuestionCount;

    /// <summary>应答节数。</summary>
    [FieldOffset(6)] public ushort AnswerCount;

    /// <summary>权威节数。</summary>
    [FieldOffset(8)] public ushort AuthorityCount;

    /// <summary>附加节数。</summary>
    [FieldOffset(10)] public ushort AdditionalCount;
}

/// <summary>
/// 资源记录固定前缀（RFC 1035 §4.1.3——10B 大端；RR 与 OPT 伪记录共用形态）。
/// <para>★ OPT（RFC 6891）：Class 字段承载 UDP 载荷声明、Ttl 字段承载扩展段（DO 占最高位）、
/// NAME 恒为根——名字不入本布局（记录名走压缩指针游标，见 <see cref="DnsWire"/>）。</para>
/// </summary>
[BinaryLayout(Endianness = LayoutEndianness.BigEndian, Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 10)]
public struct DnsRecordPrefix
{
    /// <summary>记录类型（<see cref="DnsRecordType"/>；OPT = 41）。</summary>
    [FieldOffset(0)] public ushort Type;

    /// <summary>类（RR = IN 1；OPT = UDP 载荷声明）。</summary>
    [FieldOffset(2)] public ushort Class;

    /// <summary>TTL（OPT = 扩展 RCode/版本/DO 位段）。</summary>
    [FieldOffset(4)] public uint Ttl;

    /// <summary>RDATA 长度。</summary>
    [FieldOffset(8)] public ushort RdLength;
}

/// <summary>
/// DNS 头标志位语义（原始 16 位的位面拆解——布局面见 <see cref="DnsMessageHeader"/>）。
/// </summary>
internal static class DnsHeaderFlags
{
    /// <summary>QR：0=查询 1=应答。</summary>
    public const ushort QueryResponse = 0x8000;

    /// <summary>TC：应答不完整（stub → TCP 重试）。</summary>
    public const ushort Truncated = 0x0200;

    /// <summary>RD：期望递归（查询恒置）。</summary>
    public const ushort RecursionDesired = 0x0100;

    /// <summary>RA：服务器支持递归。</summary>
    public const ushort RecursionAvailable = 0x0080;

    /// <summary>AD：递归层已验证（security-aware 外显——采信归消费方）。</summary>
    public const ushort AuthenticatedData = 0x0020;

    /// <summary>CD：递归层未验证检查（原样外显）。</summary>
    public const ushort CheckingDisabled = 0x0010;

    /// <summary>RCODE 位掩码（低 4 位——EDNS 扩展码 v1 不消费）。</summary>
    public const ushort ResponseCodeMask = 0x000F;
}
