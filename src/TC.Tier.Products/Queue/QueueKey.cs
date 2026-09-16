using TC.Tier.CodeGen;

// ★ QueueKey 的 [RingKey] 封闭注册（ring-generic-key 设计稿 §2）——一行声明产出
//   RingOfQueueKey/HashOfQueueKey/BTreeOfQueueKey/SkipListOfQueueKey 全套封闭形态；
//   消费面只见封闭类型（TierQueue 持 RingOfQueueKey）。
[assembly: RingKey(typeof(TC.Tier.Products.Queue.QueueKey))]

namespace TC.Tier.Products.Queue;

/// <summary>
/// TierQueue 消息键（16B 复合键——tc-tier-queue-spec §2/定案④）。
/// <para>★ 双形态（同一队列并存）：即时消息 = default(0,0)（key 空、不进索引）；
///   延迟消息（P3）= (DueTime, Tiebreaker=分配地址)——同 dueTime 稳定 FIFO + 键唯一。</para>
/// </summary>
public readonly record struct QueueKey(long DueTime, long Tiebreaker);
