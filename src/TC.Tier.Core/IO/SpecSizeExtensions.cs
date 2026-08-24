namespace TC.Tier.Core.IO;

/// <summary>
/// spec DSL 尺寸后缀人机工学（spec-typed-frontend-and-generator-design §4 P-b）——
/// <c>1.Giga()</c> ≡ spec 字符串 <c>quota=1G</c>（1024 基；往返同义由 DSL 契约测试钉死）。
/// </summary>
public static class SpecSizeExtensions
{
    /// <summary>千字节（KiB）。</summary>
    public static long Kilo(this long value) => value << 10;

    /// <summary>兆字节（MiB）。</summary>
    public static long Mega(this long value) => value << 20;

    /// <summary>吉字节（GiB）。</summary>
    public static long Giga(this long value) => value << 30;

    /// <summary>太字节（TiB）。</summary>
    public static long Tera(this long value) => value << 40;

    /// <summary>千字节（int 字面量人机工学——1.Kilo()）。</summary>
    public static long Kilo(this int value) => (long)value << 10;

    /// <summary>兆字节（int 字面量人机工学）。</summary>
    public static long Mega(this int value) => (long)value << 20;

    /// <summary>吉字节（int 字面量人机工学——1.Giga()）。</summary>
    public static long Giga(this int value) => (long)value << 30;

    /// <summary>太字节（int 字面量人机工学）。</summary>
    public static long Tera(this int value) => (long)value << 40;
}
