using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;

// ★ HashKey 的 [RingKey] 封闭注册（tc-tier-collections-spec §1/§2——一行声明产出
//   RingOfHashKey/HashOfHashKey/BTreeOfHashKey/SkipListOfHashKey 全套封闭形态；
//   消费面只见封闭类型：TierHash 持 RingOfHashKey + HashOfHashKey）。
[assembly: RingKey(typeof(TC.Tier.Products.Collections.HashKey))]

namespace TC.Tier.Products.Collections;

/// <summary>
/// 集合家族成员/字段强哈希（tc-tier-collections-spec §2/定案④——16B = UnifiedXxHash.Hash128）。
/// <para>★ field/member 任意字节串 → 16B 强哈希入键；envelope 自述完整字节，点查后校验不匹配
///   fail-fast（16B 哈希碰撞工程可忽略——与"内部编码假设唯一"的 Redis 同构）。</para>
/// </summary>
public static class MemberHash
{
    /// <summary>哈希字节数。</summary>
    public const int Size = 16;

    /// <summary>计算 16B 强哈希（UnifiedXxHash.Hash128——Big Endian 16B 输出按两个 64B 字拆分）。</summary>
    /// <param name="data">field/member 原始字节。</param>
    /// <returns>(Lo, Hi) 双 64B 字（键内 8B+8B 拼接）。</returns>
    public static (ulong Lo, ulong Hi) Compute(ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[UnifiedXxHash.Hash128Len];
        UnifiedXxHash.Hash128(data, buf);
        ulong lo = 0, hi = 0;
        for (int i = 0; i < 8; i++)
        {
            lo = (lo << 8) | buf[i];
            hi = (hi << 8) | buf[8 + i];
        }
        return (lo, hi);
    }
}

/// <summary>
/// TierHash 点查键（20B 复合键——spec §2）：record key 与 Hash 索引键同形（(域, field 哈希) → 地址）。
/// <para>★ Pack=4 紧凑布局：sizeof 恒 20，键字节面零填充（哈希按整键 20B 计算，无填充噪声）。</para>
/// <para>★ record key 只做分类（域 + field 哈希）；envelope 槽自述完整 field 字节（碰撞校验面）。</para>
/// </summary>
/// <param name="DomainId">域标识（uint 4B——惰性注册的内部实例 id；0 = 默认域）。</param>
/// <param name="HashLo">field 强哈希低 64B 字。</param>
/// <param name="HashHi">field 强哈希高 64B 字。</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct HashKey(uint DomainId, ulong HashLo, ulong HashHi);

/// <summary>
/// HashKey 专用比较器（Hash 索引注入——TimeSeries DenseTimeKeyComparer 同构形态）。
/// <para>★ GetHashCode64 = 整键 20B XxHash64；点查判等走字段相等（record struct ==）。</para>
/// </summary>
public sealed class HashKeyComparer : IKeyComparer<HashKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(HashKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<HashKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(HashKey x, HashKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(HashKey x, HashKey y)
    {
        int c = x.DomainId.CompareTo(y.DomainId);
        if (c != 0) return c;
        c = x.HashLo.CompareTo(y.HashLo);
        return c != 0 ? c : x.HashHi.CompareTo(y.HashHi);
    }
}
