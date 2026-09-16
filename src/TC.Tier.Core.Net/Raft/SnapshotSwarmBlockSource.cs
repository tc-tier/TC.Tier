using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照块源（spec-12 §6.1 装配——<see cref="ISwarmBlockSource"/> 的 holder 侧实现：
/// 按内容标识 + 块号从物化内容做几何切片）。
/// <para>★ 内容知识归使用方（快照安装装配）——本件只认 <see cref="SnapshotSwarmContent"/>
/// 的字节流，几何定位（块偏移 = 块号 × 块大小，末块截短）由 <see cref="SwarmManifest"/> 承载。</para>
/// <para>★ 单 manifest 源（与 <see cref="SwarmSync.SetSource"/> 单槽语义对齐——一次发布一个
/// 快照；多快照并存的 holder 后续按需扩多 manifest 源）。</para>
/// </summary>
public sealed class SnapshotSwarmBlockSource : ISwarmBlockSource
{
    private readonly SwarmManifest _manifest;
    private readonly byte[] _content;

    /// <summary>构造（零 IO——只持块化内容引用）。</summary>
    /// <param name="content">块化快照内容。</param>
    public SnapshotSwarmBlockSource(SnapshotSwarmContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        _manifest = content.Manifest;
        _content = content.Content;
    }

    /// <inheritdoc/>
    /// <param name="manifestId">内容标识。</param>
    /// <returns>true = 与本源持有的 manifest 一致（本端可服务该内容的取块）；false = 不一致。</returns>
    public bool HasManifest(Opaque16 manifestId) => manifestId == _manifest.Id;

    /// <inheritdoc/>
    /// <param name="manifestId">内容标识（与本源不一致 = false）。</param>
    /// <param name="blockIndex">块号（超清单范围 = false）。</param>
    /// <param name="block">块数据切片（物化内容的零拷贝视图）。</param>
    /// <returns>true = 取块成功；false = 内容标识不符/块号越界。</returns>
    public bool TryGetBlock(Opaque16 manifestId, long blockIndex, out ReadOnlyMemory<byte> block)
    {
        block = default;
        if (manifestId != _manifest.Id) return false;
        if ((ulong)blockIndex >= (ulong)_manifest.BlockCount) return false;
        var offset = _manifest.BlockOffset(blockIndex);
        var length = _manifest.BlockLength(blockIndex);
        block = _content.AsMemory((int)offset, (int)length);
        return true;
    }

    /// <summary>每块校验和（实际内容逐块实算——反熵对账依据，spec-12 §6.1 件 C；
    /// 物化内容不可变时实算值恒等于 manifest.Checksums）。</summary>
    /// <param name="manifestId">内容标识（与本源不一致 = false）。</param>
    /// <param name="checksums">每块 CRC32C（按块号序）。</param>
    /// <returns>true = 校验和序列已产出；false = 内容标识不符。</returns>
    public bool TryGetChecksums(Opaque16 manifestId, out uint[] checksums)
    {
        checksums = [];
        if (manifestId != _manifest.Id) return false;
        checksums = new uint[_manifest.BlockCount];
        for (var i = 0; i < _manifest.BlockCount; i++)
            checksums[i] = TC.Tier.Core.Primitives.UnifiedCrc.ComputeCrc32C(
                _content.AsSpan((int)_manifest.BlockOffset(i), (int)_manifest.BlockLength(i)));
        return true;
    }

    /// <summary>写回块（反熵修复面——长度 + CRC32C 双校验落回物化内容；spec-12 §6.1 件 C）。</summary>
    /// <param name="manifestId">内容标识（与本源不一致 = false）。</param>
    /// <param name="blockIndex">目标块号（超清单范围 = false）。</param>
    /// <param name="block">待写块数据（长度与校验和须与 manifest 一致）。</param>
    /// <returns>true = 校验通过并已落回；false = 内容标识不符/块号越界/长度或校验和不符。</returns>
    public bool TryWriteBlock(Opaque16 manifestId, long blockIndex, ReadOnlyMemory<byte> block)
    {
        if (manifestId != _manifest.Id) return false;
        if (!_manifest.VerifyBlock(blockIndex, block.Span)) return false;
        block.Span.CopyTo(_content.AsSpan((int)_manifest.BlockOffset(blockIndex), block.Length));
        return true;
    }
}
