using System.Text;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.Net.Wire;

/// <summary>
/// 传输层错误码常量与诊断（spec-12 §3.2——线编码归 <see cref="ErrorPayload"/>/[WireArray] 生成物，
/// 本层只持错误码语义、UTF-8 边界转换与超长截断；D1-T6 挂 [ConstantRegistry]）。
/// </summary>
[ConstantRegistry]
public static partial class TransportError
{
    /// <summary>头版本协商无交集。</summary>
    public const byte VersionRejected = 0x01;

    /// <summary>安全形态拒绝（配置驱动 fail-closed——防降级规则）。</summary>
    public const byte SecurityRejected = 0x02;

    /// <summary>握手序列违规（握手完成前到达非管理帧 / nonce 回带不符 / Final 缺失等）。</summary>
    public const byte HandshakeViolation = 0x03;

    /// <summary>载荷 CRC 校验失败（线路损坏）。</summary>
    public const byte CrcMismatch = 0x04;

    /// <summary>Detail 线字节数上限（= <see cref="ErrorPayloadCodec.MaxCount"/> 同源——超长截断不失败）。</summary>
    public static int MaxDetailLength => ErrorPayloadCodec.MaxCount;

    /// <summary>编码 Error 载荷（detail 超长按线字节上限截断）。</summary>
    /// <param name="code">错误码。</param>
    /// <param name="detail">诊断文本（可选）。</param>
    /// <returns>线载荷（[Code][Count][Detail]）。</returns>
    public static byte[] Encode(byte code, string? detail = null)
    {
        byte[] bytes = [];
        if (!string.IsNullOrEmpty(detail))
        {
            bytes = Encoding.UTF8.GetBytes(detail);
            if (bytes.Length > MaxDetailLength) bytes = bytes[..MaxDetailLength];
        }
        return ErrorPayloadCodec.Encode(new ErrorPayload(code, bytes));
    }

    /// <summary>解码 Error 载荷（false = 截断/Count 超上限——畸形报文拦截）。</summary>
    /// <param name="payload">线载荷。</param>
    /// <param name="code">错误码。</param>
    /// <param name="detail">诊断文本（空 Detail = null）。</param>
    /// <returns>false = 载荷畸形。</returns>
    public static bool TryDecode(ReadOnlySpan<byte> payload, out byte code, out string? detail)
    {
        if (!ErrorPayloadCodec.TryDecode(payload, out var error))
        {
            code = 0;
            detail = null;
            return false;
        }
        code = error.Code;
        detail = error.Detail.Length == 0 ? null : Encoding.UTF8.GetString(error.Detail);
        return true;
    }

    /// <summary>Code 的诊断名（未知 Code 仍可展示——向前兼容）。</summary>
    /// <param name="code">错误码（<see cref="TransportError"/> 常量或对端自定义取值）。</param>
    /// <returns>已知码返回常量名（如 <c>"CrcMismatch"</c>）；未知码返回十六进制形式（如 <c>"0x2A"</c>）。</returns>
    public static string Describe(byte code) => code switch
    {
        VersionRejected => nameof(VersionRejected),
        SecurityRejected => nameof(SecurityRejected),
        HandshakeViolation => nameof(HandshakeViolation),
        CrcMismatch => nameof(CrcMismatch),
        _ => $"0x{code:X2}"
    };
}
