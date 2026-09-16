namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 请求回调帧薄层（spec-12 §5.2——Request 0x08 / Response 0x09，载荷 = [CorrId 8B][payload]）。
/// <para>★ 布局知识在 <see cref="CorrelationPrefix"/>（[BinaryLayout] 声明式生成——零手写
///   字节序/偏移）；本层只做前缀拆装与载荷切片边界。</para>
/// <para>防御：载荷 &lt; 前缀尺寸 = 违规帧（返回 false——丢弃 + 计数，不致命）。</para>
/// </summary>
public static class RequestCodec
{
    /// <summary>线编码前缀字节数（生成物同源——布局变更零漂移）。</summary>
    public static int PrefixSize => CorrelationPrefixCodec.StructSize;

    /// <summary>编码 Request/Response 载荷（[CorrId 8B][payload] 连续缓冲——介质帧发送路径）。</summary>
    /// <param name="corrId">关联 ID。</param>
    /// <param name="payload">业务载荷。</param>
    /// <param name="target">目标缓冲（≥ <see cref="PrefixSize"/> + payload 长度）。</param>
    /// <returns>写入字节数。</returns>
    public static int EncodeInto(ulong corrId, ReadOnlySpan<byte> payload, Span<byte> target)
    {
        CorrelationPrefixCodec.Write(target, new CorrelationPrefix(corrId));
        payload.CopyTo(target[PrefixSize..]);
        return PrefixSize + payload.Length;
    }

    /// <summary>解码前缀（载荷 ≥ 8B 才有 CorrId；payload = 前缀后的余量——帧头 PayloadLen 权威）。</summary>
    /// <param name="framePayload">帧载荷。</param>
    /// <param name="corrId">关联 ID。</param>
    /// <param name="payload">业务载荷（切片视图）。</param>
    /// <returns>false = 载荷短于前缀（违规帧——丢弃 + 计数）。</returns>
    public static bool TryRead(ReadOnlySpan<byte> framePayload, out ulong corrId, out ReadOnlySpan<byte> payload)
    {
        if (framePayload.Length < PrefixSize)
        {
            corrId = 0;
            payload = default;
            return false;
        }
        corrId = CorrelationPrefixCodec.Read_CorrId(framePayload);
        payload = framePayload[PrefixSize..];
        return true;
    }
}
