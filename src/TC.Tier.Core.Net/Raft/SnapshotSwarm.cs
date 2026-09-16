using System.Buffers;
using System.Runtime.CompilerServices;
using TC.Tier.Core.Net.Swarm;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照条目流块化（spec-12 §6.1 多源增强——快照条目流 → 内容寻址的块清单：
/// SwarmSync 装配的内容源。与单源流式路径（<see cref="SnapshotStreamTransfer"/>）共用同一
/// 条目帧格式（<see cref="SnapshotEntryHeader"/> v2 21B 头 + content），保证两种路径的内容
/// 逐字节同构——多源与单源产物对拍的基础）。
/// <para>★ 内容 = 帧化条目字节流（[头][content]×N 顺序拼接——每条目自描述长度，块边界几何
/// 切分落中间也自愈）；manifest Id = 快照覆盖点 N₀ 承载（spec-12 §6.1 "快照 = 覆盖点
/// index 承载"）；块大小由装配方定（≤ <see cref="SwarmMessage.MaxBlockBytes"/> 线协议防御界）。</para>
/// <para>★ 持有者侧物化一次（O(快照字节) 缓存——随机块访问的换取）；传输线经 SwarmSync
/// 逐块流式（O(块) 驻留——GB 级不驻内存承诺由 SwarmSync 承载，本物化缓存是 holder 侧的
/// 装配选择，磁盘背衬形态后续接 Core.IO）。</para>
/// </summary>
public static class SnapshotBlockizer
{
    /// <summary>快照覆盖点 → 内容标识（字节 0..7 = index 小端，字节 8..15 = 零——确定性映射，
    /// holder 与下载方从同一 N₀ 推同一 Id）。</summary>
    /// <param name="snapshotIndex">快照覆盖点 N₀。</param>
    /// <returns>确定性映射的内容标识（同 N₀ 推同 Id）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">覆盖点为负。</exception>
    public static Opaque16 ManifestIdFor(long snapshotIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotIndex);
        // Opaque16 承载字直赋：_hi = 字节 0..7 的原序承载（低字节 = 字节 0 = 小端）——零字节序手写
        return new Opaque16((ulong)snapshotIndex, 0);
    }

    /// <summary>内容标识 → 快照覆盖点（<see cref="ManifestIdFor"/> 逆映射）。</summary>
    /// <param name="manifestId">内容标识。</param>
    /// <returns>快照覆盖点 N₀（承载于 Id 字节 0..7 的小端 index）。</returns>
    public static long SnapshotIndexOf(Opaque16 manifestId) => (long)manifestId._hi;

    /// <summary>条目帧字节数（头 21B + content）。</summary>
    /// <param name="contentLength">条目内容字节数。</param>
    /// <returns>整帧字节数（头 + 内容）。</returns>
    public static int EntryFrameLength(int contentLength) => SnapshotEntryHeaderCodec.StructSize + contentLength;

    /// <summary>写条目帧（[头][content]——头由生成 codec 写，零偏移字面量）。</summary>
    /// <param name="entry">条目四元组（Index/Term/Kind/Content）。</param>
    /// <param name="destination">目标 span（长度 ≥ <see cref="EntryFrameLength"/>）。</param>
    /// <returns>写入字节数（= <see cref="EntryFrameLength"/>）。</returns>
    public static int WriteEntryFrame((long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content) entry, Span<byte> destination)
    {
        var headerSize = SnapshotEntryHeaderCodec.StructSize;
        SnapshotEntryHeaderCodec.Write(destination, new SnapshotEntryHeader(entry.Index, entry.Term, entry.Kind, entry.Content.Length));
        entry.Content.Span.CopyTo(destination[headerSize..]);
        return headerSize + entry.Content.Length;
    }

    /// <summary>
    /// 从存储构建块化内容（holder/source 侧）：逐条帧化拼接 → 物化 → 建块清单（每块 CRC32C）。
    /// </summary>
    /// <param name="store">存储端口（快照条目源——<see cref="IRaftStore.ReadSnapshotEntriesAsync"/>）。</param>
    /// <param name="snapshotIndex">快照覆盖点 N₀（读 [1..N₀]）。</param>
    /// <param name="blockSize">块大小（≤ 线协议防御界）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>完成后的块化内容（物化字节 + 块清单——holder 侧随机块访问源）。</returns>
    public static async ValueTask<SnapshotSwarmContent> BuildContentAsync(
        IRaftStore store, long snapshotIndex, int blockSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotIndex);
        if (blockSize <= 0 || blockSize > SwarmMessage.MaxBlockBytes)
            throw new ArgumentOutOfRangeException(nameof(blockSize), blockSize,
                $"块大小 {blockSize} 超线协议防御界 {SwarmMessage.MaxBlockBytes}（或非正）。");

        var writer = new ArrayBufferWriter<byte>();
        await foreach (var entry in store.ReadSnapshotEntriesAsync(snapshotIndex, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var len = EntryFrameLength(entry.Content.Length);
            WriteEntryFrame(entry, writer.GetSpan(len));
            writer.Advance(len);
        }

        var content = writer.WrittenSpan.ToArray();
        var manifest = SwarmManifest.Build(content, blockSize, ManifestIdFor(snapshotIndex));
        return new SnapshotSwarmContent(manifest, content);
    }

    /// <summary>
    /// 解析帧化内容（下载侧——重装后的字节流逐条还原 (index, term, kind, content)；按序产出，
    /// 与 <see cref="IRaftStore.ImportSnapshotAsync"/> 的消费契约贴合）。
    /// </summary>
    /// <param name="content">帧化条目字节流。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="InvalidOperationException">内容截断（头/载荷越界）。</exception>
    public static async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ParseEntriesAsync(
        ReadOnlyMemory<byte> content, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var headerSize = SnapshotEntryHeaderCodec.StructSize;
        var offset = 0;
        while (offset < content.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (content.Length - offset < headerSize)
                throw new InvalidOperationException($"快照内容截断：剩余 {content.Length - offset} 字节 < 条目头 {headerSize}。");
            var header = SnapshotEntryHeaderCodec.Read(content.Span.Slice(offset, headerSize));
            if (header.PayloadLength < 0)
                throw new InvalidOperationException($"快照内容畸形：条目 {header.Index} 载荷长度 {header.PayloadLength} 为负。");
            var total = headerSize + header.PayloadLength;
            if (content.Length - offset < total)
                throw new InvalidOperationException(
                    $"快照内容截断：条目 {header.Index} 载荷越界（需 {total} 字节，剩余 {content.Length - offset}）。");
            yield return (header.Index, header.Term, header.Kind, content.Slice(offset + headerSize, header.PayloadLength));
            offset += total;
            await Task.Yield();
        }
    }
}

/// <summary>块化快照内容（manifest + 物化字节流——holder 侧随机块访问的缓存）。</summary>
/// <param name="Manifest">块清单（校验和/几何——下载侧重装依据）。</param>
/// <param name="Content">帧化条目字节流（<see cref="SwarmManifest.TotalBytes"/> 字节）。</param>
public sealed record SnapshotSwarmContent(SwarmManifest Manifest, byte[] Content);
