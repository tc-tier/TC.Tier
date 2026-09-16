using System.IO.Compression;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 流内压缩包络（二期-E1 NETGAP-031——快照流逐帧"可选压缩"，帧头 16B v1 不动——流内编码）：
/// 包络自描述 <c>[kind 1B][origLen 4B][payload]</c>——kind=raw 原样透传（小帧/不可压形态），
/// kind=deflate 载荷为 DEFLATE 压缩字节（origLen 供防御校验）。
/// <para>★ 逐帧独立包络——无跨帧字典/状态；对端零协商成本（包络自描述，读侧按 kind 分流）。
/// 小帧（&lt; <see cref="MinCompressBytes"/>）走 raw 直通——压缩不可压数据只亏不赚。</para>
/// </summary>
internal static class PayloadCompression
{
    /// <summary>包络：原样透传（payload = 原始字节）。</summary>
    public const byte KindRaw = 0;

    /// <summary>包络：DEFLATE 压缩（payload = 压缩字节）。</summary>
    public const byte KindDeflate = 1;

    /// <summary>压缩起压阈值（低于此值直接 raw——压缩开销/收益不匹配）。</summary>
    public const int MinCompressBytes = 256;

    /// <summary>包络头部（kind 1B + origLen 4B）。</summary>
    public const int EnvelopeHeaderSize = 5;

    /// <summary>压缩包络封装：≥ 阈值先 DEFLATE 试压——更小才采用 deflate，否则 raw 原样透传。</summary>
    public static byte[] Deflate(ReadOnlySpan<byte> source)
    {
        if (source.Length >= MinCompressBytes)
        {
            try
            {
                using var output = new MemoryStream();
                using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
                    deflate.Write(source);
                var compressed = output.ToArray();
                if (compressed.Length < source.Length)
                    return BuildEnvelope(KindDeflate, compressed, source.Length);
            }
            catch (InvalidOperationException)
            {
                // 压缩管线异常——回落 raw（尽力语义：可用性优先）
            }
        }
        return BuildEnvelope(KindRaw, source, source.Length);
    }

    /// <summary>包络解封（读侧——按 kind 分流还原原始字节；畸形即抛）。</summary>
    public static byte[] Inflate(ReadOnlyMemory<byte> envelope)
    {
        if (envelope.Length < EnvelopeHeaderSize)
            throw new InvalidOperationException($"压缩包络截断：len={envelope.Length} < {EnvelopeHeaderSize}。");
        var kind = envelope.Span[0];
        var origLen = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(envelope.Span[1..]);
        var payload = envelope[EnvelopeHeaderSize..];
        if (kind == KindRaw)
        {
            if (payload.Length != origLen)
                throw new InvalidOperationException($"raw 包络长度失配：len={payload.Length} 期望 {origLen}。");
            return payload.ToArray();
        }
        if (kind == KindDeflate)
        {
            var data = InflateDeflate(payload);
            if (data.Length != origLen)
                throw new InvalidOperationException($"deflate 解压长度失配：len={data.Length} 期望 {origLen}。");
            return data;
        }
        throw new InvalidOperationException($"压缩包络种类未知：{kind}。");
    }

    private static byte[] BuildEnvelope(byte kind, ReadOnlySpan<byte> payload, int origLen)
    {
        var envelope = new byte[EnvelopeHeaderSize + payload.Length];
        envelope[0] = kind;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(envelope.AsSpan(1), origLen);
        payload.CopyTo(envelope.AsSpan(EnvelopeHeaderSize));
        return envelope;
    }

    private static byte[] InflateDeflate(ReadOnlyMemory<byte> compressed)
    {
        using var input = new MemoryStream(compressed.ToArray());
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(compressed.Length * 4);
        deflate.CopyTo(output);
        return output.ToArray();
    }
}
