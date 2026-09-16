using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// Error 帧载荷（spec-12 §3.2 0x7F——传输层错误报告：管理通道 + 管理协议域）。
/// <para>★ [WireArray] 变长线编码（spec-12 §10 E1）：<c>[Code 1B][Count 4B][Detail UTF-8 字节块]</c>
///   ——计数、位置、字节序全部由生成的 <c>ErrorPayloadCodec</c> 产出；空 Detail = Count 0。</para>
/// <para>收到 Error = 对端拒绝/违规告知 → 断连（拨号方走重连退避）；Detail 诊断文本由
///   <see cref="TransportError"/> 薄层做 UTF-8 边界转换与超长截断。</para>
/// </summary>
/// <param name="code">错误码（<see cref="TransportError"/> 常量）。</param>
/// <param name="detail">诊断文本的 UTF-8 字节（可空数组）。</param>
[WireArray(MaxCount = 64)]
public readonly struct ErrorPayload(byte code, byte[] detail)
{
    /// <summary>错误码（<see cref="TransportError"/> 常量；未知码向前兼容展示）。</summary>
    public readonly byte Code = code;

    /// <summary>诊断文本的 UTF-8 字节块（可为空——Count 0）。</summary>
    public readonly byte[] Detail = detail;
}
