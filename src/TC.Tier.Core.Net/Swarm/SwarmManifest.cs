using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// 块清单（spec-12 §6.1——内容寻址基线数据的描述：块号/偏移/长度/校验和）。
/// <para>★ 权威源 = raft committed 内容或通告水位——manifest 由权威方构建供给（装配点幂等：
///   同一内容同一 Id 重下载结果一致）；块定位纯几何推导（偏移 = 块号 × 块大小，末块截短）。</para>
/// <para>★ 校验和 = 每块 CRC32C（<see cref="UnifiedCrc"/>——错块拦截的依据）；
///   块大小由构建方定（运行参数零写死——上限受 <see cref="SwarmMessage.MaxBlockBytes"/>
///   线协议防御界约束）。</para>
/// </summary>
public sealed record SwarmManifest
{
    /// <summary>内容标识（构建方供给唯一性——快照=覆盖点 index 承载；Opaque16 字节原序）。</summary>
    public required Opaque16 Id { get; init; }

    /// <summary>内容总字节数。</summary>
    public required long TotalBytes { get; init; }

    /// <summary>块大小（末块截短——块定位纯几何推导）。</summary>
    public required int BlockSize { get; init; }

    /// <summary>每块校验和（CRC32C——按块号序，长度 = <see cref="BlockCount"/>）。</summary>
    public required uint[] Checksums { get; init; }

    /// <summary>块数（TotalBytes 0 = 空内容零块）。</summary>
    public int BlockCount => Checksums.Length;

    /// <summary>块偏移（几何推导——块号 × 块大小）。</summary>
    /// <param name="blockIndex">块号。</param>
    /// <returns>块起始偏移（字节，相对内容起点）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">块号超清单范围。</exception>
    public long BlockOffset(long blockIndex)
    {
        if ((ulong)blockIndex >= (ulong)BlockCount)
            throw new ArgumentOutOfRangeException(nameof(blockIndex), blockIndex, "块号超清单范围。");
        return blockIndex * BlockSize;
    }

    /// <summary>块长度（末块截短——TotalBytes 边界）。</summary>
    /// <param name="blockIndex">块号。</param>
    /// <returns>块长度（字节——末块 = 剩余字节，其余 = 块大小）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">块号超清单范围。</exception>
    public long BlockLength(long blockIndex)
    {
        var offset = BlockOffset(blockIndex);
        return Math.Min(BlockSize, TotalBytes - offset);
    }

    /// <summary>块校验（长度 + CRC32C——错块拦截依据）。</summary>
    /// <param name="blockIndex">块号。</param>
    /// <param name="block">块数据。</param>
    /// <returns>长度与校验和一致。</returns>
    public bool VerifyBlock(long blockIndex, ReadOnlySpan<byte> block)
    {
        if ((ulong)blockIndex >= (ulong)BlockCount) return false;
        if (block.Length != BlockLength(blockIndex)) return false;
        return UnifiedCrc.ComputeCrc32C(block) == Checksums[(int)blockIndex];
    }

    /// <summary>
    /// 从内容构建（权威方——分块 + 每块 CRC32C）。
    /// </summary>
    /// <param name="data">完整内容。</param>
    /// <param name="blockSize">块大小（≤ <see cref="SwarmMessage.MaxBlockBytes"/>——线协议防御界）。</param>
    /// <param name="id">内容标识。</param>
    /// <returns>块清单（Id/TotalBytes/BlockSize/每块 CRC32C）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">块大小超线协议防御界。</exception>
    public static SwarmManifest Build(ReadOnlyMemory<byte> data, int blockSize, Opaque16 id)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSize);
        if (blockSize > SwarmMessage.MaxBlockBytes)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                $"块大小 {blockSize} 超线协议防御界 {SwarmMessage.MaxBlockBytes}。");
        var total = data.Length;
        var count = total == 0 ? 0 : (total + blockSize - 1) / blockSize;
        var checksums = new uint[count];
        for (var i = 0; i < count; i++)
        {
            var offset = i * blockSize;
            var length = Math.Min(blockSize, total - offset);
            checksums[i] = UnifiedCrc.ComputeCrc32C(data.Span.Slice(offset, length));
        }
        return new SwarmManifest { Id = id, TotalBytes = total, BlockSize = blockSize, Checksums = checksums };
    }
}
