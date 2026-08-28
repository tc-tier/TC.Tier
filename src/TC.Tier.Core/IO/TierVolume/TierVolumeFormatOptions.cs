using TC.Tier.CodeGen;
namespace TC.Tier.Core.IO.TierVolume;

/// <summary>
/// 虚拟文件系统格式化选项（medium-protocol-and-parity-design §6——继承 FileSystemOptions 基类）。
/// <para>★ 一词制：基类 <see cref="FileSystemOptions.QuotaBytes"/> 即供给（原 CapacityBytes——New 时刻物化，
///   位图按此预留）；<see cref="FileSystemOptions.Label"/> New = 写入 superblock。</para>
/// </summary>
[MediumOptions("virtual", Verbs = "New")]
public sealed class TierVolumeFormatOptions : FileSystemOptions
{
    /// <summary>内部块大小（默认 4096；须为 2 的幂且 ≥512）。</summary>
    public int BlockSize { get; init; } = 4096;

    /// <summary>
    /// 日志物理预留（字节，默认 8 MiB——§3.9 两级预留的物理级：位图标记占用、对数据不可见；
    /// v2 完整日志直接启用零碎片。0 = 不预留）。
    /// </summary>
    public long JournalReserveBytes { get; init; } = 8L << 20;

    /// <summary>
    /// 载体预分配方式（IS-02，默认 <see cref="PreallocationMode.Metadata"/> = 现行稀疏档）。
    /// <para><see cref="PreallocationMode.Full"/> = 载体物理占位（不标记稀疏 + 创建时物化全部空间——
    ///   一次性成本显式化由部署方承担，换运行时零分配抖动；无特权 Windows/Linux 无 fallocate 时
    ///   转零写物化，全失败 fail-fast 不静默降级）。</para>
    /// </summary>
    public PreallocationMode Preallocation { get; init; } = PreallocationMode.Metadata;

    /// <summary>
    /// 载体句柄写穿档（IS-03，默认 false）：载体以 FILE_FLAG_WRITE_THROUGH/O_SYNC 打开——
    /// 每写完成即达稳定存储；journal 提交免独立 fsync（写穿完成即单屏障），Flush 对已写穿数据短路。
    /// 与句柄级 <see cref="FileOpenHints.WriteThrough"/>（RM-07 逐写 journal 提交）正交：
    /// 前者是载体物理写穿，后者是卷级一致性提交。
    /// </summary>
    public bool CarrierWriteThrough { get; init; }

    /// <summary>
    /// 同文件写并发档（V2 §2.1，默认 <see cref="WriteConcurrencyMode.Serial"/>——现状行为）：
    /// <para><see cref="WriteConcurrencyMode.Serial"/> = 同文件写全串行（强序、零争用——快速载体上
    /// 同文件多写者最优；实测判定门：64KB 覆写 4 写者 Serial ≈ 1×，Parallel ≈ 0.57×——争用损失）；</para>
    /// <para><see cref="WriteConcurrencyMode.Parallel"/> = 同文件不相交区间写并发（数据段锁外 + 合并提交——
    /// 慢载体（真磁盘 IO 主导数据段）并行收益大；实测两极显式旋钮）。</para>
    /// </summary>
    public WriteConcurrencyMode WriteConcurrency { get; init; } = WriteConcurrencyMode.Serial;

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
