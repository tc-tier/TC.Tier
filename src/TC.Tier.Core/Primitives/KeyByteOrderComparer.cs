using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// key 字节序比较器（W7 范围扫描——F1 范围/前缀扫描的字节序底座）：Compare = TKey 原始字节的
/// 字典序（MemoryMarshal.AsBytes + 序列比较），Equals = 字节相等。
/// <para>★ 字节序是 KV 范围扫描的通用序（RocksDB/LMDB 同款）：前缀 = 字节区间，多字段结构体 key
/// 按字段布局序排布（D8：定长布局结构体 key 表达）。默认 <c>Comparer&lt;TKey&gt;.Default</c> 是
/// 数值/结构序——负数、多字段布局与字节序不一致，范围索引必须显式挂本比较器。</para>
/// </summary>
/// <typeparam name="TKey">键类型（unmanaged——字节序 = blitted 字节序）。</typeparam>
public sealed class KeyByteOrderComparer<TKey> : IKeyComparer<TKey>
    where TKey : unmanaged
{
    /// <summary>字节字典序比较（ASpan/BSpan 逐字节，长度恒 sizeof(TKey)）。</summary>
    /// <param name="x">左侧键。</param>
    /// <param name="y">右侧键。</param>
    /// <returns>按 <typeparamref name="TKey"/> 原始字节字典序：负 = x &lt; y，0 = 字节相等，正 = x &gt; y。</returns>
    public int Compare(TKey x, TKey y)
    {
        var xa = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in x));
        var ya = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in y));
        return xa.SequenceCompareTo(ya);
    }

    /// <summary>字节相等（字节序的等价谓词）。</summary>
    /// <param name="x">左侧键。</param>
    /// <param name="y">右侧键。</param>
    /// <returns>true = 两键的全部原始字节（sizeof(TKey) 字节）逐一相等；false = 任一字节不同。</returns>
    public bool Equals(TKey x, TKey y)
    {
        var xa = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in x));
        var ya = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in y));
        return xa.SequenceEqual(ya);
    }

    /// <summary>key 前 prefixByteLength 字节是否与 prefix 一致（范围扫描前缀判定——静态助手，
    /// 迭代器内可用；prefixByteLength ∈ [0, sizeof(TKey)]，定长 key 的"前缀"必须显式给字节长度）。</summary>
    /// <param name="key">待判定键。</param>
    /// <param name="prefix">前缀键（取其前 <paramref name="prefixByteLength"/> 字节参与比较）。</param>
    /// <param name="prefixByteLength">前缀字节长度，0 到 sizeof(TKey)；0 表示空前缀（恒真）。</param>
    /// <returns>true = <paramref name="key"/> 的前 <paramref name="prefixByteLength"/> 字节与 <paramref name="prefix"/> 逐字节一致；false = 存在任一差异字节。</returns>
    public static bool IsBytePrefix(TKey key, TKey prefix, int prefixByteLength)
    {
        var kb = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key));
        var pb = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in prefix));
        return prefixByteLength switch
        {
            0 => true,
            8 => kb.SequenceEqual(pb),
            _ => kb.Length >= prefixByteLength && pb.Length >= prefixByteLength
                 && kb[..prefixByteLength].SequenceEqual(pb[..prefixByteLength]),
        };
    }

    /// <summary>XxHash64 over TKey 字节（IKeyComparer 契约面——范围索引排序不消费哈希）。</summary>
    /// <param name="key">要哈希的键。</param>
    /// <returns>键原始字节（blitted 字节）的 64 位 XxHash64 哈希，单位无（哈希值）。</returns>
    public ulong GetHashCode64(TKey key)
    {
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key));
        return UnifiedXxHash.Hash64(bytes);
    }
}
