using TC.Tier.CodeGen;

// ★ TimeKey 的 [RingKey] 封闭注册（tc-tier-timeseries-spec §1 四件积木）——一行声明产出
//   RingOfTimeKey/HashOfTimeKey/BTreeOfTimeKey/SkipListOfTimeKey 全套封闭形态；
//   消费面只见封闭类型（TierTimeSeries 持 RingOfTimeKey + BTreeOfTimeKey）。
[assembly: RingKey(typeof(TC.Tier.Products.TimeSeries.TimeKey))]

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// 时序样本键（16B 复合键——tc-tier-timeseries-spec §1/定案②）。
/// <para>★ 双键空间（Queue 延迟索引同款）：Ring record key = (Timestamp, 0)——只做 record 分类，
///   envelope 槽自述完整 ts；索引 key = (Timestamp, Tiebreaker=record 地址 Offset)——写后即知
///   （消鸡生蛋），同刻多样本按地址稳定排序（写入序，定案③同刻不覆盖）。</para>
/// </summary>
public readonly record struct TimeKey(long Timestamp, long Tiebreaker);
