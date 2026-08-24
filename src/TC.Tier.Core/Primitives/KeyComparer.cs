using System.IO.Hashing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 默认 key 比较器：XxHash64 哈希 + Comparer/EqualityComparer.Default 比较。
/// <para>★ XxHash64 是 .NET 内置高性能非加密哈希（System.IO.Hashing），同款用于上层 HashKV。</para>
/// <para>★ 对 unmanaged TKey 按 <see cref="Unsafe.SizeOf{T}"/> 字节做 hash，分布均匀。</para>
/// </summary>
public sealed class KeyComparer<TKey> : IKeyComparer<TKey> where TKey : unmanaged
{
    private static readonly EqualityComparer<TKey> EqComparer = EqualityComparer<TKey>.Default;
    private static readonly Comparer<TKey> CmpComparer = Comparer<TKey>.Default;

    public ulong GetHashCode64(TKey key)
    {
        // ★ XxHash64 over TKey 字节（blittable，零装箱）。8 字节 key ~3ns。
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<TKey>(in key));
        return XxHash64.HashToUInt64(bytes);
    }

    public bool Equals(TKey x, TKey y) => EqComparer.Equals(x, y);
    public int Compare(TKey x, TKey y) => CmpComparer.Compare(x, y);
}