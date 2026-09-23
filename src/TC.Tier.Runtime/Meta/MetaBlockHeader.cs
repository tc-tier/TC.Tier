using TC.Tier.CodeGen;
using System.Runtime.InteropServices;

namespace TC.Tier.Runtime.Meta;

/// <summary>
/// 统一 meta 块头（传输契约的单一布局真源——12B 纯规范）：全部 IMetaPolicy{THeader,TPayload}
/// 变体（Log/Metadata/Mirror/Ring MetaHeader）的 Header 必须与本布局同构
/// （Magic@0 | Version@4 | Flags@6 | PayloadLength@8 | PaddingLength@10）。
/// <para>★ 消费面：<see cref="MetadataMetaTransport"/> 读侧按本布局解码（变长精确块裁剪），
/// 块长 = <see cref="MetaBlockHeaderCodec.StructSize"/> + PayloadLength + Crc32FooterCodec.StructSize；
/// 不再允许各读端手写偏移（偏移/尺寸经生成器单一声明）。</para>
/// </summary>
[BinaryLayout(Features = BinaryLayoutFeatures.All)]
[StructLayout(LayoutKind.Explicit, Size = StructSizeValue)]
internal struct MetaBlockHeader
{
    /// <summary>头字节大小（12B = Magic 4 + Version 2 + Flags 2 + PayloadLength 2 + PaddingLength 2）。</summary>
    public const int StructSizeValue = 12;

    /// <summary>Magic 标识（由各 THeader 变体按 RecordMagic 校验——本布局只定位）。</summary>
    [FieldOffset(0)] public uint MagicValue;

    /// <summary>布局版本号（major&lt;&lt;8|minor）。</summary>
    [FieldOffset(4)] public ushort Version;

    /// <summary>规范旗标位。</summary>
    [FieldOffset(6)] public ushort Flags;

    /// <summary>payload 字节长度（结构化水位 + opaque 扩展）。</summary>
    [FieldOffset(8)] public ushort PayloadLength;

    /// <summary>padding 字节长度（补齐策略布局对齐）。</summary>
    [FieldOffset(10)] public ushort PaddingLength;
}
