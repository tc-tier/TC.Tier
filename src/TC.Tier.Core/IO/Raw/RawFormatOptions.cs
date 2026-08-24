using TC.Tier.CodeGen;
namespace TC.Tier.Core.IO.Raw;

/// <summary>
/// 虚拟文件系统格式化选项（medium-protocol-and-parity-design §6——继承 FileSystemOptions 基类）。
/// <para>★ 一词制：基类 <see cref="FileSystemOptions.QuotaBytes"/> 即供给（原 CapacityBytes——New 时刻物化，
///   位图按此预留）；<see cref="FileSystemOptions.Label"/> New = 写入 superblock。</para>
/// </summary>
[MediumOptions("virtual", Verbs = "New")]
public sealed class RawFormatOptions : FileSystemOptions
{
    /// <summary>内部块大小（默认 4096；须为 2 的幂且 ≥512）。</summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>
    /// 日志物理预留（字节，默认 8 MiB——§3.9 两级预留的物理级：位图标记占用、对数据不可见；
    /// v2 完整日志直接启用零碎片。0 = 不预留）。
    /// </summary>
    public long JournalReserveBytes { get; init; } = 8L << 20;

    /// <summary>构造校验（BlockSize 几何 + Label 长度；QuotaBytes 语义 = 正数供给 / -1 自动扩容（文件载体按需增长，设备=设备大小））。</summary>
    internal void Validate()
    {
        if (QuotaBytes == 0)
            throw new ArgumentException("QuotaBytes 非法：0 不是合法供给（正数 = 供给；-1 = 自动扩容——文件载体按需增长，设备载体 = 设备大小）。");
        if (BlockSize is < 512 or > (1 << 20) || (BlockSize & (BlockSize - 1)) != 0)
            throw new ArgumentException($"BlockSize 必须为 2 的幂且在 [512, 1MiB]：{BlockSize}");
        if (System.Text.Encoding.UTF8.GetByteCount(Label ?? "") > 32)
            throw new ArgumentException("Label 超过 32 字节 UTF-8。");
    }
}
