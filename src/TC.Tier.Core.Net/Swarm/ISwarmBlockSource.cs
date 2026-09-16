namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// 本地块源（spec-12 §6.1——holder 侧：按内容标识+块号提供块字节）。
/// <para>★ 装配面（使用方实现）：快照安装装配 = 本地快照读取器；TierQueue/TierKV 副本引导 =
///   结构导出面。SwarmSync 组件只依赖本端口——内容存取零耦合（机制宿主 Core.Net，
///   内容知识归使用方）。</para>
/// </summary>
public interface ISwarmBlockSource
{
    /// <summary>是否持有该内容（未持有 = 全部请求 HasBlock=false——快速路径）。</summary>
    /// <param name="manifestId">内容标识。</param>
    /// <returns>持有内容。</returns>
    bool HasManifest(Opaque16 manifestId);

    /// <summary>取块（持有内容但块号超界/缺失 = false——与未持有同语义，下载方换源）。</summary>
    /// <param name="manifestId">内容标识。</param>
    /// <param name="blockIndex">块号。</param>
    /// <param name="block">块字节（调用方只读；本实现返回底层缓冲须确保跨调用稳定——快照安装逐块读形态）。</param>
    /// <returns>持有该块。</returns>
    bool TryGetBlock(Opaque16 manifestId, long blockIndex, out ReadOnlyMemory<byte> block);

    /// <summary>取每块校验和（反熵对账依据——spec-12 §6.1 件 C；false = 源不支持对账应答，
    /// EntropyProbe 对其回空集）。默认 false——可选能力，实现方按需覆写。
    /// <para>★ 语义 = <b>实际内容</b>逐块 CRC（按需计算——漂移可观测的根基：回放清单记录值
    /// 会把副本漂移隐藏成"一致"）。</para></summary>
    /// <param name="manifestId">内容标识。</param>
    /// <param name="checksums">每块 CRC32C（按块号序——实算值）。</param>
    /// <returns>提供该内容的校验和。</returns>
    bool TryGetChecksums(Opaque16 manifestId, out uint[] checksums)
    {
        checksums = [];
        return false;
    }

    /// <summary>写回块（反熵修复面——漂移块经校验和验证后落回本地源；false = 只读源——
    /// 可检出但无法自动修复）。默认 false——可选能力，实现方按需覆写。
    /// <para>★ 实现须校验块完整性（长度 + CRC32C 对本源 manifest——防线与下载侧同级）。</para></summary>
    /// <param name="manifestId">内容标识。</param>
    /// <param name="blockIndex">块号。</param>
    /// <param name="block">块字节。</param>
    /// <returns>写回成功。</returns>
    bool TryWriteBlock(Opaque16 manifestId, long blockIndex, ReadOnlyMemory<byte> block) => false;
}
