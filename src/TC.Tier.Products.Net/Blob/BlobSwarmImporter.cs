using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net.Swarm;
using TC.Tier.Products.Blob;

namespace TC.Tier.Products.Net.Blob;

/// <summary>Blob 导入结果。</summary>
/// <param name="ObjectId">对象句柄。</param>
/// <param name="ManifestId">内容标识（内容寻址派生）。</param>
/// <param name="BytesDownloaded">实际下载字节数（0 = 幂等直返）。</param>
/// <param name="Skipped">true = 本地已持有同 Id 同长度，零动作直返（反熵已判等价则零传输）。</param>
public readonly record struct BlobImportResult(
    LogicalAddress ObjectId, Opaque16 ManifestId, long BytesDownloaded, bool Skipped);

/// <summary>
/// Blob 导入会话（#436 件二——拉取侧）：清单发现 → K=1 顺序下载 → 流式灌 Blob 写会话。
/// <para>★ 会话纪律（2PC 形态）：开始即开会话，任一块失败/源耗尽 = Abort 清理半成品上抛；
///   全块灌完提交（对象表登记 ObjectId）→ Announce 登记 → 多源从字面成立。</para>
/// <para>★ 顺序性 = K=1 顺序拉取（Blob OpenWrite 顺序追加——多源无序块无法落位，定案 4）；
///   零暂存、O(单块) 驻留（流式纪律）。</para>
/// <para>★ 修复粒度 = 整对象重拉：反熵检出漂移 = 删本地 + 本导入幂等重导（ObjectId 不变）。</para>
/// </summary>
public sealed class BlobSwarmImporter
{
    private readonly ITierBlob _blob;
    private readonly SwarmSync _swarm;
    private readonly BlobSwarmBlockSource? _source;
    private readonly int _blockSize;

    /// <summary>构造。</summary>
    /// <param name="blob">本地 Blob 实例（导入终点）。</param>
    /// <param name="swarm">Swarm 分发面（下载 + 上报）。</param>
    /// <param name="source">holder 侧块源（导入完成后自动 Attach——多源从字面成立；null = 不挂分发面）。</param>
    /// <param name="blockSize">分发块大小（与块源一致——清单派生口径）。</param>
    public BlobSwarmImporter(ITierBlob blob, SwarmSync swarm,
        BlobSwarmBlockSource? source = null, int blockSize = 64 * 1024)
    {
        _blob = blob ?? throw new ArgumentNullException(nameof(blob));
        _swarm = swarm ?? throw new ArgumentNullException(nameof(swarm));
        _source = source;
        _blockSize = blockSize;
    }

    /// <summary>
    /// 导入内容（幂等：本地已持有同 Id 同长度 = 直接返回 Skipped）。
    /// </summary>
    /// <param name="objectId">对象句柄（内容寻址——与源端一致）。</param>
    /// <param name="expectedLength">期望长度（定长契约——写满才可提交）。</param>
    /// <param name="seeds">清单发现种子集（应答者即持有者列表）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>导入结果。</returns>
    /// <exception cref="NetIOException">全种子未持有且无退化路径/下载失败（半成品已 Abort 清理）。</exception>
    public async ValueTask<BlobImportResult> ImportAsync(LogicalAddress objectId, long expectedLength,
        IReadOnlyList<NodeId> seeds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var manifestId = BlobSwarmManifest.IdFor(objectId);

        // 幂等前置：本地已持有同 Id 同长度 → 零动作（补挂分发面——重启后缓存丢失的兜底）
        try
        {
            var local = await _blob.GetInfoAsync(objectId, ct).ConfigureAwait(false);
            if (local.Length == expectedLength)
            {
                if (_source is not null && !_source.HasManifest(manifestId))
                    await _source.AttachAsync(objectId, ct).ConfigureAwait(false);
                return new BlobImportResult(objectId, manifestId, 0, true);
            }
        }
        catch (KeyNotFoundException)
        {
            // 本地未持有——继续导入
        }

        // 清单发现：轮询种子（应答者 = 持有者）
        SwarmManifest? manifest = null;
        var holders = new List<NodeId>();
        foreach (var seed in seeds)
        {
            var m = await _swarm.GetManifestAsync(seed, manifestId, ct).ConfigureAwait(false);
            if (m is null) continue;
            manifest ??= m;
            holders.Add(seed);
        }

        if (manifest is null)
        {
            // 退化路径：直连源拉取（消费方装配的读取代理——形态同现有单源）
            var content = await ReadDegradedAsync(objectId, expectedLength, ct).ConfigureAwait(false);
            return await PutAsync(objectId, manifestId, content, seeds, ct).ConfigureAwait(false);
        }

        // K=1 顺序下载 → 流式灌写会话（O(单块) 驻留）
        var session = _blob.OpenWrite(expectedLength);
        try
        {
            long written = 0;
            await foreach (var (index, block) in _swarm.DownloadAsync(manifest, holders, null, ct,
                    parallelismOverride: 1).ConfigureAwait(false))
            {
                session.Write(block);
                written += block.Length;
            }

            await session.CompleteAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            session.Abort();   // 半成品清理（会话失败语义在案——尾截断回滚）
            throw;
        }
        finally
        {
            session.Dispose();
        }

        // 完成 → 挂分发面 + Announce 登记（多源从字面成立）
        if (_source is not null)
            await _source.AttachAsync(objectId, ct).ConfigureAwait(false);
        await _swarm.AnnounceContentAsync(seeds, manifestId, ct).ConfigureAwait(false);
        return new BlobImportResult(objectId, manifestId, expectedLength, false);
    }

    private async ValueTask<byte[]> ReadDegradedAsync(LogicalAddress objectId, long expectedLength,
        CancellationToken ct)
    {
        if (_degradedRead is null)
            throw new NetIOException(
                $"Blob 导入源耗尽且未装配退化直读路径：objectId={objectId} expectedLength={expectedLength}。");
        return await _degradedRead(objectId, expectedLength, ct).ConfigureAwait(false);
    }

    private Func<LogicalAddress, long, CancellationToken, ValueTask<byte[]>>? _degradedRead;

    /// <summary>装配退化直读路径（直连 CP 的 Blob 读取代理——形态同现有单源；构造后一次性装配）。</summary>
    public void AttachDegradedRead(Func<LogicalAddress, long, CancellationToken, ValueTask<byte[]>> read)
        => _degradedRead = read;

    private async ValueTask<BlobImportResult> PutAsync(LogicalAddress objectId, Opaque16 manifestId,
        byte[] content, IReadOnlyList<NodeId> seeds, CancellationToken ct)
    {
        var put = await _blob.PutAsync(content, ct).ConfigureAwait(false);
        if (_source is not null)
            await _source.AttachAsync(objectId, ct).ConfigureAwait(false);
        await _swarm.AnnounceContentAsync(seeds, manifestId, ct).ConfigureAwait(false);
        return new BlobImportResult(objectId, manifestId, content.Length, false);
    }
}
