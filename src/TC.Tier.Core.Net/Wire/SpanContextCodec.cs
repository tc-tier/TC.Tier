namespace TC.Tier.Core.Net.Wire;

/// <summary>跨节点传播的 span 身份（W3C traceparent 同构——trace-id + span-id + flags）。</summary>
public readonly record struct SpanContext(byte[] TraceId, byte[] SpanId, byte Flags);

/// <summary>
/// trace 上下文线格式（二期-I4 定案——请求载荷前缀形态）：
/// <list type="bullet">
/// <item>上下文本体 = <c>[TraceId 16B][SpanId 8B][Flags 1B]</c>（25B 定长）；</item>
/// <item>带前缀载荷 = <c>[Marker 0x54][上下文 25B][业务载荷]</c>（26B 头）。</item>
/// </list>
/// <para>★ 门控纪律：仅握手特性位 <see cref="HandshakeFeatures.Trace"/> 协商成功才加/剥
/// ——混版滚动升级下旧对端载荷零变化（N-1 兼容，H2 升级矩阵的组成纪律）。</para>
/// <para>★ 语义归 tracer：本层只做字节搬运——解释/续链由外部 tracer 的对称
/// CaptureContext/BeginSpan(parent) 承担。</para>
/// </summary>
public static class SpanContextCodec
{
    /// <summary>前缀标记（'T'——仅已协商 Trace 位的连接上判别）。</summary>
    public const byte Marker = 0x54;

    /// <summary>上下文本体字节数（TraceId 16 + SpanId 8 + Flags 1）。</summary>
    public const int ContextSize = 25;

    /// <summary>带标记的完整前缀字节数（载荷偏移量）。</summary>
    public const int PrefixSize = 1 + ContextSize;

    /// <summary>编码上下文本体（25B）。</summary>
    /// <param name="context">待编码上下文（TraceId 须 16B、SpanId 须 8B）。</param>
    /// <returns>上下文本体（25B——[TraceId 16B][SpanId 8B][Flags 1B]）。</returns>
    /// <exception cref="ArgumentException">TraceId/SpanId 长度不符。</exception>
    public static byte[] Encode(SpanContext context)
    {
        if (context.TraceId.Length != 16) throw new ArgumentException($"TraceId 须 16B，实际 {context.TraceId.Length}。");
        if (context.SpanId.Length != 8) throw new ArgumentException($"SpanId 须 8B，实际 {context.SpanId.Length}。");
        var wire = new byte[ContextSize];
        context.TraceId.CopyTo(wire, 0);
        context.SpanId.CopyTo(wire, 16);
        wire[24] = context.Flags;
        return wire;
    }

    /// <summary>解码上下文本体（缺长返回 false——静默降级为无传播）。</summary>
    /// <param name="wire">待解码字节（须 ≥ <see cref="ContextSize"/>）。</param>
    /// <param name="context">解码出的上下文（失败 = default）。</param>
    /// <returns>true = 解码成功；false = 长度不足（<paramref name="context"/> = default——无传播）。</returns>
    public static bool TryDecode(ReadOnlySpan<byte> wire, out SpanContext context)
    {
        if (wire.Length < ContextSize)
        {
            context = default;
            return false;
        }
        context = new SpanContext(wire[..16].ToArray(), wire.Slice(16, 8).ToArray(), wire[24]);
        return true;
    }

    /// <summary>业务载荷加 trace 前缀（调用方保证已协商 Trace 位——本层不复核）。</summary>
    /// <param name="payload">业务载荷（原样复制到前缀之后）。</param>
    /// <param name="contextWire">上下文本体（<see cref="Encode"/> 产出，须 25B）。</param>
    /// <returns>带前缀载荷（[Marker 0x54][上下文 25B][业务载荷]，总长 26B + 载荷长）。</returns>
    /// <exception cref="ArgumentException">contextWire 长度 ≠ 25B。</exception>
    public static byte[] AttachPrefix(ReadOnlyMemory<byte> payload, byte[] contextWire)
    {
        if (contextWire.Length != ContextSize)
            throw new ArgumentException($"trace 上下文须 {ContextSize}B，实际 {contextWire.Length}。");
        var traced = new byte[PrefixSize + payload.Length];
        traced[0] = Marker;
        contextWire.CopyTo(traced, 1);
        payload.Span.CopyTo(traced.AsSpan(PrefixSize));
        return traced;
    }

    /// <summary>
    /// 尝试剥离 trace 前缀（marker + 长度双重判别——不匹配原样返回 false，载荷零改动）。
    /// </summary>
    /// <param name="payload">待判别载荷（可能带前缀）。</param>
    /// <param name="context">剥出的上下文（失败 = default）。</param>
    /// <param name="consumed">已消费的前缀字节数（成功 = <see cref="PrefixSize"/>；失败 = 0）。</param>
    /// <returns>true = 前缀匹配并剥出；false = 非 trace 载荷（marker/长度不符——载荷零改动）。</returns>
    public static bool TryStripPrefix(ReadOnlySpan<byte> payload, out SpanContext context, out int consumed)
    {
        if (payload.Length > PrefixSize && payload[0] == Marker && TryDecode(payload[1..], out context))
        {
            consumed = PrefixSize;
            return true;
        }
        context = default;
        consumed = 0;
        return false;
    }
}
