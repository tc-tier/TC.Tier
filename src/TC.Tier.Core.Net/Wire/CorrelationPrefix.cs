using System.Runtime.InteropServices;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 请求回调载荷公共前缀（[CorrId 8B]——spec-12 §5.2；Request/Response 帧共享）：
/// `[CorrId 8B][payload]`——payload 长度由帧头 PayloadLen 推导（无内部长度前缀）。
/// <para>★ [BinaryLayout] 声明（生成 <c>CorrelationPrefixCodec</c>——读写零手写字节序）；
///   CorrId 由传输内核生成（机制消息族不再各自携带关联字段——spec-12 §5.2）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = 8)]
public readonly struct CorrelationPrefix
{
    /// <summary>关联 ID（发起端生成——应答按此回带，发起端 pending 关联表定位）。</summary>
    [FieldOffset(0)] public readonly ulong CorrId;

    /// <summary>全字段构造（按偏移序——BinaryLayout 生成 Read 的构造路径）。</summary>
    /// <param name="corrId">关联 ID。</param>
    public CorrelationPrefix(ulong corrId)
    {
        CorrId = corrId;
    }
}
