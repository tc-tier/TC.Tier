namespace TC.Tier.Contracts.Meta;

// ════════════════════════════════════════════════════════════════════════════
//  Meta 布局契约——使用方（THeader/TPayload）的二进制形态描述，策略据此读写块。
//
//  统一布局：[Header 纯规范 N B][Payload 结构化水位 + opaque 扩展插槽][Footer Crc32C]
//  Header 纯规范字段，Payload 放使用方独有的结构化水位 + opaque 原始扩展。
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Meta 布局契约
/// </summary>
/// <typeparam name="THeader">Header 类型。</typeparam>
/// <typeparam name="TPayload">Payload 类型。</typeparam>
public interface IMetaLayout<THeader, TPayload>
    where THeader : struct
    where TPayload : struct
{
    /// <summary>规范 header 字节大小（12B 规范字段布局）。</summary>
    int HeaderSize { get; }

    /// <summary>结构化 payload 字节大小（Log=64, Ring=112, Blob=56）。</summary>
    int PayloadSize { get; }

    /// <summary>
    /// 结构化 payload Opaque 字节大小
    /// </summary>
    int PayloadOpaqueSize { get; }

    /// <summary>Magic 常量（由使用方定义，用于块身份校验）。</summary>
    uint Magic { get; }

    /// <summary>当前版本号。</summary>
    ushort CurrentVersion { get; }

    /// <summary>默认 Flags（含 CRC 模式等）。</summary>
    ushort DefaultFlags { get; }

    /// <summary>序列化 header 到 Span（可选 validate 校验 Magic/Version）。</summary>
    /// <param name="dst">目标 Span。</param>
    /// <param name="header">Header 实例。</param>
    /// <param name="validate">是否校验 Magic/Version。</param>
    void WriteHeader(Span<byte> dst, in THeader header, bool validate);

    /// <summary>从 Span 反序列化 header。</summary>
    /// <param name="src">源 Span。</param>
    /// <returns>Header 实例。</returns>
    THeader ReadHeader(ReadOnlySpan<byte> src);

    /// <summary>序列化 payload 到 Span。</summary>
    /// <param name="dst">目标 Span。</param>
    /// <param name="payload">Payload 实例。</param>
    void WritePayload(Span<byte> dst, in TPayload payload);

    /// <summary>从 Span 反序列化 payload。</summary>
    /// <param name="src">源 Span。</param>
    /// <returns>Payload 实例。</returns>
    TPayload ReadPayload(ReadOnlySpan<byte> src);

    // === Header 规范字段访问（泛型策略需通过 layout 读写 header 的规范字段）===

    /// <summary>读 header 的 MagicValue（用于校验）。</summary>
    /// <param name="header">Header 实例。</param>
    /// <returns>MagicValue。</returns>
    uint GetMagicValue(in THeader header);

    /// <summary>读 header 的 Version（用于校验）。</summary>
    /// <param name="header">Header 实例。</param>
    /// <returns>Version。</returns>
    ushort GetVersion(in THeader header);

    /// <summary>读 header 的 PayloadLength（用于计算 opaque 长度）。</summary>
    /// <param name="header">Header 实例。</param>
    /// <returns>PayloadLength。</returns>
    ushort GetPayloadLength(in THeader header);

    /// <summary>设置 header 的 PayloadLength（WritePayload 后更新）。</summary>
    /// <param name="header">Header 实例。</param>
    /// <param name="payloadLength">PayloadLength 值。</param>
    /// <returns>更新后的 Header 实例。</returns>
    THeader WithPayloadLength(in THeader header, ushort payloadLength);

    /// <summary>创建一个填好规范字段（Magic/Version/Flags）的默认 header。</summary>
    /// <returns>默认 Header 实例。</returns>
    THeader CreateDefaultHeader();
}