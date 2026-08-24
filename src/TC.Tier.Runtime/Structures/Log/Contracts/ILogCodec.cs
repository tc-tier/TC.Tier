namespace TC.Tier.Runtime.Structures.Log.Contracts;

/// <summary>
/// Log entry 头尾编解码接口——注入 LogBase，消除虚分发。
/// <para>基类只管传输（页管理/tail/flush/epoch），codec 管格式（header/footer/CRC）。</para>
/// </summary>
public interface ILogCodec
{
    int HeaderSize { get; }
    int Alignment { get; }
    int MaxEntrySize { get; }

    void WriteHeader(Span<byte> dest, int payloadLength, int paddingLength, bool isMeta);
    bool TryReadHeader(ReadOnlySpan<byte> source, out int payloadLength, out int paddingLength, out bool isMeta, bool verifyCrc = false);
}
