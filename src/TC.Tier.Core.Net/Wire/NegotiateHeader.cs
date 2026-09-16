using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// Negotiate 载荷公共前缀（[Tag 1B]——消息族判别字段，E2 [WireMessage] 化前的手写形态；
/// 全部 V4/V6 载荷共享此前缀，Tag 读取经生成的 <c>NegotiateHeaderCodec.Read_Tag</c>）。
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.StructSize | BinaryLayoutFeatures.FieldReaders)]
[StructLayout(LayoutKind.Explicit, Size = 1)]
public readonly struct NegotiateHeader
{
    /// <summary>特性数据 Tag（<see cref="NegotiateCodec.TagUdpEndpoint"/> 等）。</summary>
    [FieldOffset(0)] public readonly byte Tag;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="tag">Tag。</param>
    public NegotiateHeader(byte tag)
    {
        Tag = tag;
    }
}