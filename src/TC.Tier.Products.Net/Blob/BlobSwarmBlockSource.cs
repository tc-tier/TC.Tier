using System.Collections.Concurrent;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Products.Blob;

namespace TC.Tier.Products.Net.Blob;

/// <summary>
/// Blob 块源（#436 件一——holder 侧）：DP 节点把本地已导入对象挂上 Swarm 分发面。
/// <para>★ v1 形态 = 显式 <see cref="AttachAsync"/> 预载（对象经 <c>ITierBlob.GetAsync</c> 一次读入
///   进程内缓存——不可变对象缓存安全）：Swarm 取块接口是同步面而 Blob 读会话是异步面，
///   流式 seek 切块留待读会话异步化（阶段二，按带宽实测驱动）。缓存内容不动整对象——
///   Checksums/Manifest 首次实算后按 Id 缓存（"任何 holder 可重算"权威语义）。</para>
/// <para>★ 只读源：TryWriteBlock 不覆写（默认实现）——不可变对象修复走整对象重拉（定案 3）。</para>
/// </summary>
public sealed class BlobSwarmBlockSource : ISwarmBlockSource, ISwarmManifestSource
{
    private readonly ITierBlob _blob;
    private readonly int _blockSize;
    private readonly ConcurrentDictionary<Opaque16, Entry> _cache = new();

    private sealed class Entry
    {
        public required byte[] Content { get; init; }
        public required long TotalBytes { get; init; }
        public SwarmManifest? Manifest;
    }

    /// <summary>构造。</summary>
    /// <param name="blob">本地 Blob 实例（Attach 的对象来源）。</param>
    /// <param name="blockSize">分发块大小（缺省 64 KiB，与 raft 缺省对齐；单块 ≤ 1 MiB 防御界）。</param>
    public BlobSwarmBlockSource(ITierBlob blob, int blockSize = 64 * 1024)
    {
        _blob = blob ?? throw new ArgumentNullException(nameof(blob));
        if (blockSize is <= 0 or > SwarmMessage.MaxBlockBytes)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
    }

    /// <summary>挂载本地对象进分发缓存（导入完成/本端发布后调用——显式面，不扫库）。</summary>
    /// <param name="objectId">对象句柄。</param>
    /// <param name="ct">取消令牌。</param>
    public async ValueTask AttachAsync(LogicalAddress objectId, CancellationToken ct = default)
    {
        var info = await _blob.GetInfoAsync(objectId, ct).ConfigureAwait(false);
        var content = new byte[info.Length];
        await _blob.GetAsync(objectId, content, ct).ConfigureAwait(false);
        _cache[BlobSwarmManifest.IdFor(objectId)] = new Entry
        {
            Content = content,
            TotalBytes = info.Length,
        };
    }

    /// <summary>已挂载内容数（观测面）。</summary>
    public int AttachedCount => _cache.Count;

    /// <inheritdoc/>
    public bool HasManifest(Opaque16 manifestId) => _cache.ContainsKey(manifestId);

    /// <inheritdoc/>
    public bool TryGetBlock(Opaque16 manifestId, long blockIndex, out ReadOnlyMemory<byte> block)
    {
        block = default;
        if (!_cache.TryGetValue(manifestId, out var entry)) return false;
        var manifest = EnsureManifest(manifestId, entry);
        if ((ulong)blockIndex >= (ulong)manifest.BlockCount) return false;
        var offset = (int)manifest.BlockOffset(blockIndex);
        var length = (int)manifest.BlockLength(blockIndex);
        block = entry.Content.AsMemory(offset, length);
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetChecksums(Opaque16 manifestId, out ReadOnlyMemory<uint> checksums)
    {
        checksums = default;
        if (!_cache.TryGetValue(manifestId, out var entry)) return false;
        var manifest = EnsureManifest(manifestId, entry);
        checksums = manifest.Checksums;
        return true;
    }

    /// <inheritdoc/>
    public bool TryGetManifest(Opaque16 manifestId, out SwarmManifest manifest)
    {
        if (!_cache.TryGetValue(manifestId, out var entry))
        {
            manifest = null!;
            return false;
        }

        manifest = EnsureManifest(manifestId, entry);
        return true;
    }

    private SwarmManifest EnsureManifest(Opaque16 id, Entry entry)
        => entry.Manifest ??= BlobSwarmManifest.Build(entry.Content, _blockSize, id);
}
