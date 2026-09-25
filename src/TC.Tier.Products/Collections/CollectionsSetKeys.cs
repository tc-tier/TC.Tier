using System.Runtime.InteropServices;
using TC.Tier.CodeGen;
using TC.Tier.Core.Primitives;

// ★ SetKey 的 [RingKey] 封闭注册（tc-tier-collections-spec §1/§2）。
[assembly: RingKey(typeof(TC.Tier.Products.Collections.SetKey))]

namespace TC.Tier.Products.Collections;

/// <summary>
/// TierSet 成员判重键（20B 复合键——spec §2，HashKey/ZSetKey 同构形态）：
/// record key 与 Hash 索引键同形（(域, member 哈希) → 地址）。
/// <para>★ Pack=4 紧凑布局：sizeof 恒 20，键字节面零填充。</para>
/// </summary>
/// <param name="DomainId">域标识（uint 4B——惰性注册的内部实例 id；0 = 默认域）。</param>
/// <param name="HashLo">member 强哈希低 64B 字。</param>
/// <param name="HashHi">member 强哈希高 64B 字。</param>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct SetKey(uint DomainId, ulong HashLo, ulong HashHi);

/// <summary>SetKey 专用比较器（Hash 索引注入——HashKeyComparer 同构形态）。</summary>
public sealed class SetKeyComparer : IKeyComparer<SetKey>
{
    /// <inheritdoc/>
    public ulong GetHashCode64(SetKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<SetKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }

    /// <inheritdoc/>
    public bool Equals(SetKey x, SetKey y) => x == y;

    /// <inheritdoc/>
    public int Compare(SetKey x, SetKey y)
    {
        int c = x.DomainId.CompareTo(y.DomainId);
        if (c != 0) return c;
        c = x.HashLo.CompareTo(y.HashLo);
        return c != 0 ? c : x.HashHi.CompareTo(y.HashHi);
    }
}
