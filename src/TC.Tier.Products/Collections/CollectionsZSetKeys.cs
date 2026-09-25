using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;

// ★ ZSetKey/ZScoreKey 的 [RingKey] 封闭注册（tc-tier-collections-spec §1/§2）——一行声明产出
//   RingOfZSetKey/HashOfZSetKey/BTreeOfZScoreKey 等全套封闭形态；
//   消费面只见封闭类型：TierZSet 持 RingOfZSetKey + HashOfZSetKey（member 点查）+ BTreeOfZScoreKey（score 有序）。
[assembly: RingKey(typeof(TC.Tier.Products.Collections.ZSetKey))]
[assembly: RingKey(typeof(TC.Tier.Products.Collections.ZScoreKey))]

namespace TC.Tier.Products.Collections;

/// <summary>
/// ZSet score 全序编码（tc-tier-collections-spec §2/定案③——IEEE 754 符号位翻转技巧，Redis ziplist
/// 同款）：编码后 <b>ulong 无符号比较序</b> = double 全序（负数/±0/正数单调；NaN 入口拒绝）。
/// <para>★ 输出必须按无符号比较（标准变换把正数映到高位半区）——ZScoreKey.Score 槽为 ulong，
///   全部比较走 ulong（Comparer + 区间界哨兵）。</para>
/// <para>★ -0.0 归一为 +0.0（数值相等 → 编码相等——Redis 分数比较语义）；±Inf 合法（Redis 同）。</para>
/// </summary>
public static class ScoreCodec
{
    private const ulong SignBit = 0x8000_0000_0000_0000UL;

    /// <summary>double → 全序编码（NaN 抛 ArgumentException；-0.0 归一 +0.0）。</summary>
    /// <param name="score">语义层分数。</param>
    /// <returns>全序编码（键内 8B——无符号比较序 = 数值序）。</returns>
    public static ulong Encode(double score)
    {
        if (double.IsNaN(score))
            throw new ArgumentException("score 为 NaN——ZSet 分数不接受 NaN（Redis 同构 fail-fast）", nameof(score));
        if (score == 0) score = +0.0;   // -0.0 归一（数值比较相等——编码一致）
        ulong bits = BitConverter.DoubleToUInt64Bits(score);
        // 正数翻符号位 / 负数全翻（掩码显式分支——OR 组合组不出全 1 掩码，负数编码必错）
        ulong mask = (bits >> 63) != 0UL ? ulong.MaxValue : SignBit;
        return bits ^ mask;
    }

    /// <summary>全序编码 → double（<see cref="Encode"/> 逆变换）。</summary>
    /// <param name="encoded">全序编码。</param>
    /// <returns>语义层分数。</returns>
    public static double Decode(ulong encoded)
    {
        ulong mask = (encoded >> 63) != 0UL ? SignBit : ulong.MaxValue;
        return BitConverter.UInt64BitsToDouble(encoded ^ mask);
    }
}

/// <summary>
/// ZSet member 点查键（20B 复合键——spec §2）：record key 与 Hash 索引键同形（(域, member 哈希) → 地址）。
/// <para>★ Pack=4 紧凑布局：sizeof 恒 20，键字节面零填充。</para>
/// </summary>
/// <param name="DomainId">域标识（uint 4B——惰性注册的内部实例 id；0 = 默认域）。</param>
/// <param name="HashLo">member 强哈希低 64B 字。</param>
/// <param name="HashHi">member 强哈希高 64B 字。</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct ZSetKey(uint DomainId, ulong HashLo, ulong HashHi);

/// <summary>
/// ZSet score 有序键（28B 复合键——spec §2：BTree score 有序视图，定案③ BTree 底座）。
/// <para>★ 字节序 = (DomainId, Score 编码, HashLo, HashHi)：DomainId 领先 → 单树内逐域键域连续
/// （域内 seek/迭代出域即停）；Score 全序编码 → 树序 = 分数序；同分成员按 member 哈希序稳定排序。</para>
/// <para>★ 键唯一性：member 哈希在键内——同成员更新分数 = 删旧键插新键（双索引一致性窗口由恢复对账收口）。</para>
/// </summary>
/// <param name="DomainId">域标识（uint 4B）。</param>
/// <param name="Score">score 全序编码（8B ulong——ScoreCodec；无符号比较序 = 数值序）。</param>
/// <param name="HashLo">member 强哈希低 64B 字。</param>
/// <param name="HashHi">member 强哈希高 64B 字。</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct ZScoreKey(uint DomainId, ulong Score, ulong HashLo, ulong HashHi);

/// <summary>ZSetKey 专用比较器（member 点查索引注入——HashKeyComparer 同构形态）。</summary>
public sealed class ZSetKeyComparer : IKeyComparer<ZSetKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(ZSetKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ZSetKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(ZSetKey x, ZSetKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(ZSetKey x, ZSetKey y)
    {
        int c = x.DomainId.CompareTo(y.DomainId);
        if (c != 0) return c;
        c = x.HashLo.CompareTo(y.HashLo);
        return c != 0 ? c : x.HashHi.CompareTo(y.HashHi);
    }
}

/// <summary>ZScoreKey 专用比较器（score 有序 BTree 注入——字段字典序：域 → 分数编码 → member 哈希）。</summary>
public sealed class ZScoreKeyComparer : IKeyComparer<ZScoreKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(ZScoreKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ZScoreKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(ZScoreKey x, ZScoreKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(ZScoreKey x, ZScoreKey y)
    {
        int c = x.DomainId.CompareTo(y.DomainId);
        if (c != 0) return c;
        c = x.Score.CompareTo(y.Score);   // ulong 无符号比较——全序编码契约
        if (c != 0) return c;
        c = x.HashLo.CompareTo(y.HashLo);
        return c != 0 ? c : x.HashHi.CompareTo(y.HashHi);
    }
}
