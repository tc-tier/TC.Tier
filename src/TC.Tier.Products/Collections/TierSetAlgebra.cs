namespace TC.Tier.Products.Collections;

/// <summary>byte[] 结构相等比较器（集合代数成员去重——16B 哈希无关，按原始字节判定）。</summary>
internal sealed class ByteArrayEqualityComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayEqualityComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        return x.AsSpan().SequenceEqual(y);
    }

    public int GetHashCode(byte[] obj)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var b in obj)
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            return (int)(hash ^ (hash >> 32));
        }
    }
}

/// <summary>
/// 集合代数静态算子（tc-tier-collections-spec §3——跨域扫描合并的组合糖，TimeSeriesRollup 同族形态，
/// 零新结构）：Intersect（交集）/ Union（并集）/ Diff（差集 A−B）。
/// <para>★ 结果集不落域——由调用方 <see cref="ITierSet.SAddAsync(uint, IReadOnlyList{ReadOnlyMemory{byte}}, CancellationToken)"/>
/// 承接（spec §3 第一版口径；SINTERSTORE 系落点选项后置，定案⑥）。</para>
/// <para>★ 交付序 = 第一入参域的 Ring 地址序过滤（Redis 集合代数无序契约）；成员按原始字节判等。</para>
/// </summary>
public static class TierSetAlgebra
{
    /// <summary>交集：两域共有成员（O(|A|+|B|) 内存——小域收齐合并）。</summary>
    /// <param name="set">目标实例。</param>
    /// <param name="domainA">域 A（结果序参照域）。</param>
    /// <param name="domainB">域 B。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>两域共有成员（A 的交付序）。</returns>
    public static async ValueTask<IReadOnlyList<byte[]>> IntersectAsync(
        ITierSet set, uint domainA, uint domainB, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        var b = await CollectAsync(set, domainB, ct).ConfigureAwait(false);
        var result = new List<byte[]>();
        await foreach (var member in set.SMembersAsync(domainA, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (b.Contains(member)) result.Add(member);
        }
        return result;
    }

    /// <summary>并集：全体入参域的成员合并去重（结果序 = 首域 Ring 地址序，其余域追加）。</summary>
    /// <param name="set">目标实例。</param>
    /// <param name="domains">域集合（≥ 1）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>去重后的成员并集。</returns>
    public static async ValueTask<IReadOnlyList<byte[]>> UnionAsync(
        ITierSet set, IReadOnlyList<uint> domains, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(domains);
        if (domains.Count == 0) return [];
        var merged = new HashSet<byte[]>(ByteArrayEqualityComparer.Instance);
        var result = new List<byte[]>();
        foreach (var domain in domains)
        {
            await foreach (var member in set.SMembersAsync(domain, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (merged.Add(member)) result.Add(member);
            }
        }
        return result;
    }

    /// <summary>差集：域 A 有而域 B 无的成员（A − B）。</summary>
    /// <param name="set">目标实例。</param>
    /// <param name="domainA">被减域。</param>
    /// <param name="domainB">减域。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>A − B 成员（A 的交付序）。</returns>
    public static async ValueTask<IReadOnlyList<byte[]>> DiffAsync(
        ITierSet set, uint domainA, uint domainB, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        var b = await CollectAsync(set, domainB, ct).ConfigureAwait(false);
        var result = new List<byte[]>();
        await foreach (var member in set.SMembersAsync(domainA, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            if (!b.Contains(member)) result.Add(member);
        }
        return result;
    }

    private static async Task<HashSet<byte[]>> CollectAsync(ITierSet set, uint domain, CancellationToken ct)
    {
        var members = new HashSet<byte[]>(ByteArrayEqualityComparer.Instance);
        await foreach (var member in set.SMembersAsync(domain, ct).ConfigureAwait(false))
            members.Add(member);
        return members;
    }
}
