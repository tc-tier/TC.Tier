using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 帧头（spec-12 §3.1——16B 定长、全部小端、介质无关：TCP 流帧与 UDP 数据报同一套头）。
/// <para>★ [BinaryLayout] 显式声明布局（spec-12 §10 声明即代码）——全部字段（含
///   <see cref="HeaderCrc"/>）的位置/读写/偏移常量由生成的 <c>FrameHeaderCodec</c> 产出；
///   本层零偏移知识，<see cref="FrameCodec"/> 薄层只持帧语义（CRC 计算与校验、版本与上限判定）。</para>
/// <para>★ 加字段 = 加一行 <see cref="FieldOffsetAttribute"/> 声明（TCSG001 锁 Size 与偏移和
///   一致）——手写偏移量在字段增删时整套漂移的形态不复存在。</para>
/// <code>
/// 偏移    宽度  字段
/// [0..4)  4B   PayloadLength   载荷长度（≤ 16MB）
/// [4]     1B   Kind            帧种类（<see cref="FrameKind"/>）
/// [5]     1B   ChannelId       通道标识（<see cref="ChannelIds"/>）
/// [6]     1B   ProtocolId      协议域 ID（<see cref="ProtocolIds"/>）
/// [7]     1B   Version         线协议头版本（=1；本 codec 只讲 v1）
/// [8..12) 4B   HeaderCrc       CRC32C（覆盖本字段之前的全部头字节——防半帧/错位；
///                               值依赖已写出的头前缀，构造期 0 占位、由 FrameCodec 计算补写）
/// [12..16) 4B  PayloadCrc      CRC32C（覆盖载荷）
/// </code>
/// <para>★ 头版本 1 的字段永不再解释——演进走版本协商 + 新 FrameKind（spec-12 §3.2）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 16)]
public readonly struct FrameHeader
{
    /// <summary>载荷字节数（≤ <see cref="FrameCodec.MaxPayloadLength"/>；0 合法——明文 Final 等）。</summary>
    [FieldOffset(0)] public readonly uint PayloadLength;

    /// <summary>帧种类原始字节（未知种类传输层丢弃+计数——编解码层透传）。</summary>
    [FieldOffset(4)] public readonly byte Kind;

    /// <summary>通道标识原始字节。</summary>
    [FieldOffset(5)] public readonly byte ChannelId;

    /// <summary>协议域 ID 原始字节。</summary>
    [FieldOffset(6)] public readonly byte ProtocolId;

    /// <summary>头版本（解码侧 ≠1 即拒绝）。</summary>
    [FieldOffset(7)] public readonly byte Version;

    /// <summary>
    /// 头 CRC32C（覆盖本字段之前的全部头字节 [0..<see cref="FrameHeaderCodec"/>.Offset_HeaderCrc——
    /// 防半帧/错位）。值依赖已写出的头前缀：编码构造期 0 占位，由 <see cref="FrameCodec"/> 计算后经
    /// 生成的 <c>Write_HeaderCrc</c> 补写。
    /// </summary>
    [FieldOffset(8)] public readonly uint HeaderCrc;

    /// <summary>载荷 CRC32C（线上值——<see cref="FrameCodec.VerifyPayload"/> 比对用）。</summary>
    [FieldOffset(12)] public readonly uint PayloadCrc;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="payloadLength">载荷字节数。</param>
    /// <param name="kind">帧种类原始字节。</param>
    /// <param name="channelId">通道标识原始字节。</param>
    /// <param name="protocolId">协议域 ID 原始字节。</param>
    /// <param name="version">头版本。</param>
    /// <param name="headerCrc">头 CRC32C（编码期占位 0——由 FrameCodec 计算补写）。</param>
    /// <param name="payloadCrc">载荷 CRC32C。</param>
    public FrameHeader(uint payloadLength, byte kind, byte channelId, byte protocolId, byte version, uint headerCrc, uint payloadCrc)
    {
        PayloadLength = payloadLength;
        Kind = kind;
        ChannelId = channelId;
        ProtocolId = protocolId;
        Version = version;
        HeaderCrc = headerCrc;
        PayloadCrc = payloadCrc;
    }
}
