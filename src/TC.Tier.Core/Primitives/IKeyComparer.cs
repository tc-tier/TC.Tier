namespace TC.Tier.Core.Primitives;

/// <summary>
/// 索引 key 比较器 + 64 位哈希抽象（公共，跨结构）。
/// <para>★ 对齐 base.md §2.8「统一 IKeyComparer 比较器 + 哈希函数抽象」。</para>
/// <para>★ 64 位 hash 是性能命脉：高位取 tag（14 位，熵充分），低位取 bucket index，两段独立。</para>
/// <para>★ 默认实现 <see cref="KeyComparer{TKey}"/>：XxHash64 over TKey 字节（unmanaged，blittable）。</para>
/// <para>★ 可注入：自定义 key 类型可提供专用比较器（如变长 key 的前缀哈希、特定分布优化）。</para>
/// </summary>
public interface IKeyComparer<in TKey> where TKey : unmanaged
{
    /// <summary>64 位哈希（高位做 tag，低位做 bucket index——两段独立充分，避免 32 位 hash 的生日碰撞）。</summary>
    ulong GetHashCode64(TKey key);

    /// <summary>相等比较（HashIndex 判等闭环 + 通用判等用）。</summary>
    bool Equals(TKey x, TKey y);

    /// <summary>有序比较（BTree/SkipList 路由/分裂用；HashIndex 不用）。</summary>
    int Compare(TKey x, TKey y);
}