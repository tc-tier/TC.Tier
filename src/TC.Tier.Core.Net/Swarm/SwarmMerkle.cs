using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// Swarm manifest Merkle 树哈希（spec-12 §6.1 增量——复制家族设计件 C 反熵对账的比对依据）：
/// 块 CRC32C 为叶子，每 <see cref="SubtreeFanout"/> 块聚一个子树根，子树根聚全局根。
/// <para>★ 纯函数（manifest Checksums 的派生计算——零状态）；全局根相等 = 整体一致零传输；
/// 不等 = 沿子树粒度定位差异区间（叶子层区间批量比对，O(块数/扇出) 请求、4B/块比对流量）。</para>
/// </summary>
public static class SwarmMerkle
{
    /// <summary>子树扇出（每 64 块一个子树根——比对流量/定位粒度折中）。</summary>
    public const int SubtreeFanout = 64;

    /// <summary>子树根序列（每 64 块一段——段内叶子 CRC 字节串接的 CRC32C；末段不足按实有）。</summary>
    /// <param name="checksums">每块 CRC32C（按块号序）。</param>
    /// <returns>子树根序列（长度 = ⌈块数 / <see cref="SubtreeFanout"/>⌉；空输入 = 空数组）。</returns>
    public static uint[] ComputeSubtreeRoots(ReadOnlySpan<uint> checksums)
    {
        if (checksums.Length == 0) return [];
        var subtreeCount = (checksums.Length + SubtreeFanout - 1) / SubtreeFanout;
        var roots = new uint[subtreeCount];
        // 串接缓冲复用（单线程纯函数——每段一次 CRC over 64×4B）
        Span<byte> buf = stackalloc byte[SubtreeFanout * 4];
        for (var s = 0; s < subtreeCount; s++)
        {
            var start = s * SubtreeFanout;
            var count = Math.Min(SubtreeFanout, checksums.Length - start);
            for (var i = 0; i < count; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.Slice(i * 4, 4), checksums[start + i]);
            roots[s] = UnifiedCrc.ComputeCrc32C(buf.Slice(0, count * 4));
        }
        return roots;
    }

    /// <summary>全局根（子树根串接的 CRC32C——相等即整体一致零传输）。</summary>
    /// <param name="checksums">每块 CRC32C。</param>
    /// <returns>全局根（空内容 = 0）。</returns>
    public static uint ComputeGlobalRoot(ReadOnlySpan<uint> checksums)
    {
        if (checksums.Length == 0) return 0;
        var roots = ComputeSubtreeRoots(checksums);
        var buf = new byte[roots.Length * 4];
        for (var i = 0; i < roots.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(i * 4, 4), roots[i]);
        return UnifiedCrc.ComputeCrc32C(buf);
    }

    /// <summary>差异块定位（叶子层逐块比对——本端 vs 对端，块号升序）。</summary>
    /// <param name="mine">本端每块 CRC32C。</param>
    /// <param name="theirs">对端每块 CRC32C（长度不一致 = 内容不同代——返回 null 由调用方处置）。</param>
    public static IReadOnlyList<long>? DiffBlocks(ReadOnlySpan<uint> mine, ReadOnlySpan<uint> theirs)
    {
        if (mine.Length != theirs.Length) return null;
        List<long>? diff = null;
        for (var i = 0; i < mine.Length; i++)
        {
            if (mine[i] != theirs[i])
                (diff ??= []).Add(i);
        }
        return diff ?? [];
    }
}
