namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// manifest 权威源（#436 件三——任何 holder 可重算应答 <c>ManifestReq</c>）：
/// 块源实现本接口即可应答清单发现请求（实算/缓存语义归实现——"首次实算后进程内缓存"）。
/// </summary>
public interface ISwarmManifestSource
{
    /// <summary>取内容清单（未持有 = false）。</summary>
    /// <param name="manifestId">内容标识。</param>
    /// <param name="manifest">清单（Id/TotalBytes/BlockSize/Checksums 全量）。</param>
    /// <returns>是否持有。</returns>
    bool TryGetManifest(Opaque16 manifestId, out SwarmManifest manifest);
}
