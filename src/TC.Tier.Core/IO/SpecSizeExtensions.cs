namespace TC.Tier.Core.IO;

/// <summary>
/// spec DSL 尺寸后缀人机工学（spec-typed-frontend-and-generator-design §4 P-b）——
/// <c>1.Giga()</c> ≡ spec 字符串 <c>quota=1G</c>（1024 基；往返同义由 DSL 契约测试钉死）。
/// </summary>
public static class SpecSizeExtensions
{
    /// <summary>千字节（KiB）。</summary>
    /// <param name="value">数量（以 KiB 为单位的份数），如 <c>4.Kilo()</c> = 4096 字节。</param>
    /// <returns>换算后的字节数（value × 1024）。</returns>
    public static long Kilo(this long value) => value << 10;

    /// <summary>兆字节（MiB）。</summary>
    /// <param name="value">数量（以 MiB 为单位的份数）。</param>
    /// <returns>换算后的字节数（value × 1048576）。</returns>
    public static long Mega(this long value) => value << 20;

    /// <summary>吉字节（GiB）。</summary>
    /// <param name="value">数量（以 GiB 为单位的份数）。</param>
    /// <returns>换算后的字节数（value × 1073741824）。</returns>
    public static long Giga(this long value) => value << 30;

    /// <summary>太字节（TiB）。</summary>
    /// <param name="value">数量（以 TiB 为单位的份数）。</param>
    /// <returns>换算后的字节数（value × 1099511627776）。</returns>
    public static long Tera(this long value) => value << 40;

    /// <summary>千字节（int 字面量人机工学——1.Kilo()）。</summary>
    /// <returns>换算后的字节数（value × 1024）。</returns>
    public static long Kilo(this int value) => (long)value << 10;

    /// <summary>兆字节（int 字面量人机工学）。</summary>
    /// <returns>换算后的字节数（value × 1048576）。</returns>
    public static long Mega(this int value) => (long)value << 20;

    /// <summary>吉字节（int 字面量人机工学——1.Giga()）。</summary>
    /// <returns>换算后的字节数（value × 1073741824）。</returns>
    public static long Giga(this int value) => (long)value << 30;

    /// <summary>太字节（int 字面量人机工学）。</summary>
    /// <returns>换算后的字节数（value × 1099511627776）。</returns>
    public static long Tera(this int value) => (long)value << 40;
}
