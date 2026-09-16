using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;

// ★ DenseTimeKey 的 [RingKey] 封闭注册（#443 稠密多序列设计稿 §3）——一行声明产出
//   RingOfDenseTimeKey/HashOfDenseTimeKey/BTreeOfDenseTimeKey/SkipListOfDenseTimeKey 全套封闭形态；
//   消费面只见封闭类型（TierTimeSeries dense 模式持 RingOfDenseTimeKey + BTreeOfDenseTimeKey）。
[assembly: RingKey(typeof(TC.Tier.Products.TimeSeries.DenseTimeKey))]

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// 稠密多序列样本键（20B 复合键——#443 设计稿 §3：SeriesId 领先字节，逐序列 = 键前缀域）。
/// <para>★ 字节序 = (SeriesId 4B, Timestamp 8B, Tiebreaker 8B)：SeriesId 领先 → 单棵共享 BTree 内
///   逐序列键域连续（seek (s, from, min) 迭代至 SeriesId 出域）；同序列内 ts 序、同刻 addr 序——
///   与 TimeKey 语义逐字同构。</para>
/// <para>★ Pack=4 紧凑布局（缺省对齐会在 uint 后垫 4B 得 24B——违反 20B 设计）：sizeof 恒 20，
///   键字节面零填充（哈希按整键 20B 计算，无填充噪声）。字段偏移非 8 对齐由 JIT 安全处理。</para>
/// <para>★ 双键空间（TimeKey 同款）：Ring record key = (SeriesId, Timestamp, 0)——只做 record 分类，
///   envelope 槽自述完整 (sid, ts)；索引 key = (SeriesId, Timestamp, Tiebreaker = record 地址
///   Offset)——写后即知（消鸡生蛋），同刻多样本按写入序稳定排序。</para>
/// <para>★ 只活在 dense 模式（<c>TimeSeriesOptions.DenseSeries</c>）——TimeKey（16B 两参）
///   单序列模式原样保留，两模式互不读对方文件。</para>
/// </summary>
/// <param name="SeriesId">序列标识（uint 4B——惰性注册的内部实例 id；0 = 默认序列）。</param>
/// <param name="Timestamp">样本时刻（UTC Ticks 全精度）。</param>
/// <param name="Tiebreaker">同刻消歧（索引键 = record 地址 Offset；Ring record 键恒 0）。</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct DenseTimeKey(uint SeriesId, long Timestamp, long Tiebreaker);

/// <summary>
/// DenseTimeKey 专用比较器（时间索引注入——<see cref="TimeKeyComparer"/> 的 dense 同构）。
/// <para>★ 字段字典序（SeriesId 主序 + Timestamp 次序 + Tiebreaker 末序）= 设计稿 §3 复合键序——
///   SeriesId 领先字节使单树内逐序列键域连续（前缀域 seek/迭代 + 序列内前缀截断的前提）。</para>
/// </summary>
public sealed class DenseTimeKeyComparer : IKeyComparer<DenseTimeKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(DenseTimeKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<DenseTimeKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(DenseTimeKey x, DenseTimeKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(DenseTimeKey x, DenseTimeKey y)
    {
        int c = x.SeriesId.CompareTo(y.SeriesId);
        if (c != 0) return c;
        c = x.Timestamp.CompareTo(y.Timestamp);
        return c != 0 ? c : x.Tiebreaker.CompareTo(y.Tiebreaker);
    }
}
