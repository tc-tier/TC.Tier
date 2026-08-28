namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// Log entry 头尾编解码接口——注入 LogBase，消除虚分发。
/// <para>基类只管传输（页管理/tail/flush/epoch），codec 管格式（header/footer/CRC）。</para>
/// </summary>
public interface ILogCodec
{
    /// <summary>entry header 字节大小（EntryLog=22B，DeltaLog=18B）。</summary>
    int HeaderSize { get; }
    /// <summary>entry 对齐粒度（padding 按此对齐，保证后续 entry 起始对齐）。</summary>
    int Alignment { get; }
    /// <summary>单 entry 最大字节数（读侧 payload 长度合理性上限）。</summary>
    int MaxEntrySize { get; }

    /// <summary>写 entry header 到缓冲（规范字段 + 变化字段 + CRC）。</summary>
    /// <param name="dest">目标缓冲（至少 HeaderSize 长）。</param>
    /// <param name="payloadLength">payload 字节长度。</param>
    /// <param name="paddingLength">padding 字节长度。</param>
    /// <param name="isMeta">是否 meta entry（叠加 FLAG_ENTRY_IS_META）。</param>
    void WriteHeader(Span<byte> dest, int payloadLength, int paddingLength, bool isMeta);
    /// <summary>读并校验 entry header（magic 不匹配或长度非法返回 false）。</summary>
    /// <param name="source">源缓冲。</param>
    /// <param name="payloadLength">输出 payload 字节长度。</param>
    /// <param name="paddingLength">输出 padding 字节长度。</param>
    /// <param name="isMeta">输出是否 meta entry。</param>
    /// <param name="verifyCrc">是否校验 CRC（默认 false 只读头字段）。</param>
    /// <returns>true = header 合法；false = magic 不匹配 / 长度非法 / CRC 校验失败。</returns>
    bool TryReadHeader(ReadOnlySpan<byte> source, out int payloadLength, out int paddingLength, out bool isMeta, bool verifyCrc = false);
}
