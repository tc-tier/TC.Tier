using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 帧成帧薄层（spec-12 §3.1——介质无关；布局读写由 [BinaryLayout] 生成的
/// <c>FrameHeaderCodec</c> 产出，本层只持帧语义：HeaderCrc 计算/校验、版本与长度上限判定、
/// 载荷拷贝）。
/// <para>★ 解码两段式服务流介质（TCP：读 16B → 验头 → 读 PayloadLen 字节 → 验载 → 交付）：
///   <see cref="TryReadHeader"/> → 切载荷 → <see cref="VerifyPayload"/>；数据报介质
///   （InProcess/UDP）用 <see cref="TryDecode"/> 一步到位。</para>
/// <para>★ 拒绝语义（Try 返回 false，传输层据此 Error + 断连 + 计数）：头 CRC 不符 /
///   头版本 ≠1 / 载荷长度超上限 / 载荷字节数不足 / 载荷 CRC 不符。<b>未知 Kind/ProtocolId
///   不在编解码层拒绝</b>（向前兼容——丢弃+计数是传输层策略）。</para>
/// <para>★ CRC 一律 <see cref="UnifiedCrc.ComputeCrc32C(System.ReadOnlySpan{byte})"/>（CRC32C 硬件加速）。</para>
/// </summary>
public static class FrameCodec
{
    /// <summary>帧头定长（= 帧头布局的 [StructLayout].Size——同源生成）。</summary>
    public static int HeaderSize => FrameHeaderCodec.StructSize;

    /// <summary>本实现讲述的线协议头版本（spec-12 §3.1 = 1）。</summary>
    public const byte CurrentHeaderVersion = 1;

    /// <summary>载荷长度上限（16MB——UDP 路径天然受限，TCP 大块走流式通道；协议常量非配置）。</summary>
    public const uint MaxPayloadLength = 16u * 1024 * 1024;

    /// <summary>整帧字节数（头 + 载荷）。</summary>
    /// <param name="payloadLength">载荷长度（字节；不得超过 <see cref="MaxPayloadLength"/>）。</param>
    /// <returns>整帧总字节数（字节 = <see cref="HeaderSize"/> + 载荷长度）。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="payloadLength"/> 超上限。</exception>
    public static int GetFrameLength(uint payloadLength)
    {
        if (payloadLength > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payloadLength),
                $"载荷长度 {payloadLength} 超上限 {MaxPayloadLength}（大块走流式通道——spec-12 §3.1）。");
        return HeaderSize + (int)payloadLength;
    }

    /// <summary>
    /// 编码一帧（头 + 载荷写入 <paramref name="destination"/>——TCP 流/数据报通吃的成帧原语）。
    /// </summary>
    /// <param name="kind">帧种类（<see cref="FrameKind"/> 常量）。</param>
    /// <param name="channelId">通道标识（<see cref="ChannelIds"/> 常量）。</param>
    /// <param name="protocolId">协议域 ID（<see cref="ProtocolIds"/> 常量）。</param>
    /// <param name="payload">载荷（可为空）。</param>
    /// <param name="destination">目标缓冲（≥ <see cref="GetFrameLength"/>(payload.Length)）。</param>
    /// <returns>写入的字节数（头 + 载荷）。</returns>
    public static int Encode(byte kind, byte channelId, byte protocolId, ReadOnlySpan<byte> payload, Span<byte> destination)
    {
        if (payload.Length > MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(payload),
                $"载荷长度 {payload.Length} 超上限 {MaxPayloadLength}。");
        if (destination.Length < HeaderSize + payload.Length)
            throw new ArgumentException($"目标缓冲 {destination.Length} 不足以容纳帧（需 {HeaderSize + payload.Length}）。", nameof(destination));

        var header = new FrameHeader(
            (uint)payload.Length, kind, channelId, protocolId, CurrentHeaderVersion,
            headerCrc: 0,   // 值依赖已写出的头前缀——先占位，下一步计算补写
            payloadCrc: UnifiedCrc.ComputeCrc32C(payload));
        FrameHeaderCodec.Write(destination, in header);
        // HeaderCrc = 覆盖本字段之前的头前缀（偏移/写入均来自生成物——本层零偏移知识）
        FrameHeaderCodec.Write_HeaderCrc(destination,
            UnifiedCrc.ComputeCrc32C(destination[..FrameHeaderCodec.Offset_HeaderCrc]));
        payload.CopyTo(destination[HeaderSize..]);
        return HeaderSize + payload.Length;
    }

    /// <summary>★ 缓冲复用编码（热路径零稳定分配——缓冲不足扩容复用；返回视图有效至下次复用）。</summary>
    /// <param name="kind">帧种类（<see cref="FrameKind"/> 常量）。</param>
    /// <param name="channelId">通道标识（<see cref="ChannelIds"/> 常量）。</param>
    /// <param name="protocolId">协议域 ID（<see cref="ProtocolIds"/> 常量）。</param>
    /// <param name="payload">载荷（可为空）。</param>
    /// <param name="buffer">复用的编码缓冲（调用前可为 null；不足时由本方法扩容替换）。</param>
    /// <returns>整帧视图（头 + 载荷；底层为 <paramref name="buffer"/>，有效至下次复用/扩容）。</returns>
    public static ReadOnlyMemory<byte> Encode(byte kind, byte channelId, byte protocolId, ReadOnlyMemory<byte> payload, ref byte[]? buffer)
    {
        int len = GetFrameLength((uint)payload.Length);
        if (buffer is null || buffer.Length < len) buffer = new byte[len];
        var written = Encode(kind, channelId, protocolId, payload.Span, buffer.AsSpan(0, len));
        return buffer.AsMemory(0, written);
    }

    /// <summary>
    /// 读取并验证帧头（HeaderCrc 覆盖其前缀——半帧/错位在此拦截；布局读取与 CRC 读写走生成的 FrameHeaderCodec）。
    /// </summary>
    /// <param name="source">≥ <see cref="HeaderSize"/> 字节的帧头（可含后续载荷——只读头部）。</param>
    /// <param name="header">解码出的帧头。</param>
    /// <returns>false = 源不足 / 头 CRC 不符 / 版本 ≠1 / 载荷长度超上限（调用方 Error + 断连）。</returns>
    public static bool TryReadHeader(ReadOnlySpan<byte> source, out FrameHeader header)
    {
        header = default;
        if (source.Length < HeaderSize) return false;

        header = FrameHeaderCodec.Read(source);
        if (header.HeaderCrc != UnifiedCrc.ComputeCrc32C(source[..FrameHeaderCodec.Offset_HeaderCrc]))
        {
            header = default;
            return false;
        }
        return header.PayloadLength <= MaxPayloadLength && header.Version == CurrentHeaderVersion;
    }

    /// <summary>
    /// 验证载荷（长度一致 + CRC 一致——TCP 读循环切出载荷后调用；数据报介质经 <see cref="TryDecode"/> 免调）。
    /// </summary>
    /// <param name="header">已验证的帧头（提供声明的载荷长度与载荷 CRC）。</param>
    /// <param name="payload">待验证的载荷（字节；长度与 CRC 均须与 <paramref name="header"/> 声明一致）。</param>
    /// <returns>true = 长度与 CRC 均一致；false = 长度不符或 CRC 不符（载荷损坏/错位）。</returns>
    public static bool VerifyPayload(in FrameHeader header, ReadOnlySpan<byte> payload)
        => payload.Length == header.PayloadLength
           && UnifiedCrc.ComputeCrc32C(payload) == header.PayloadCrc;

    /// <summary>
    /// 一步解码整帧（数据报介质——帧完整到达，无半帧形态）。
    /// </summary>
    /// <param name="frame">整帧（头 + 载荷；声明长度之后的残余字节被忽略）。</param>
    /// <param name="header">解码出的帧头。</param>
    /// <param name="payload">载荷切片（长度 = 声明的 PayloadLength；有效至 frame 存活期）。</param>
    /// <returns>false = 头/载任何一环验证失败。</returns>
    public static bool TryDecode(ReadOnlySpan<byte> frame, out FrameHeader header, out ReadOnlySpan<byte> payload)
    {
        payload = default;
        if (!TryReadHeader(frame, out header)) return false;
        int declared = HeaderSize + (int)header.PayloadLength;
        if (frame.Length < declared) return false;
        payload = frame.Slice(HeaderSize, (int)header.PayloadLength);
        return VerifyPayload(header, payload);
    }
}
