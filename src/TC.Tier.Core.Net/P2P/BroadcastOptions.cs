namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// Gossip 广播参数（spec-06 §6 语义要点 × spec-12 §4.5 运行参数零写死——
/// 文中数值仅为缺省，装配期全可覆盖）。
/// </summary>
public sealed record BroadcastOptions
{
    /// <summary>默认配置。</summary>
    public static BroadcastOptions Default { get; } = new();

    /// <summary>转发 TTL（默认 4——每中继一跳减一，减尽即停；环形/分区拓扑的环路面保险）。</summary>
    public int Ttl { get; private init; } = 4;

    /// <summary>扇出上限（默认 = 整个活跃视图；spec-06 §6"fanout ≤ k"——缩小即牺牲送达广度换发送量）。</summary>
    public int Fanout { get; private init; } = int.MaxValue;

    /// <summary>去重缓存容量（默认 4096 条 MsgId——LRU 有界；容量 × 在途窗口 = 重复投递的暴露面）。</summary>
    public int DedupCapacity { get; private init; } = 4096;

    /// <summary>单条广播载荷上限（默认 60KB——数据报尽力语义，超限源头 fail-fast 而非静默丢失）。</summary>
    public int MaxPayloadBytes { get; private init; } = 60_000;

    /// <summary>随机源（扇出抽样/MsgId 生成——测试注入固定种子保证确定性；null = Random.Shared）。</summary>
    public Random? Random { get; private init; }

    /// <summary>With 链——转发 TTL。</summary>
    /// <param name="ttl">新转发 TTL（每中继一跳减一，减尽即停）。</param>
    /// <returns>替换了 TTL 的新实例（其余位不变）。</returns>
    public BroadcastOptions WithTtl(int ttl) => this with { Ttl = ttl };

    /// <summary>With 链——扇出上限。</summary>
    /// <param name="fanout">新扇出上限（≤ 活跃视图大小；缩小即牺牲送达广度换发送量）。</param>
    /// <returns>替换了扇出上限的新实例（其余位不变）。</returns>
    public BroadcastOptions WithFanout(int fanout) => this with { Fanout = fanout };

    /// <summary>With 链——去重缓存容量。</summary>
    /// <param name="capacity">新去重缓存容量（MsgId 条数，LRU 有界）。</param>
    /// <returns>替换了去重容量的新实例（其余位不变）。</returns>
    public BroadcastOptions WithDedupCapacity(int capacity) => this with { DedupCapacity = capacity };

    /// <summary>With 链——单条载荷上限。</summary>
    /// <param name="bytes">新单条广播载荷上限（字节——超限源头 fail-fast）。</param>
    /// <returns>替换了载荷上限的新实例（其余位不变）。</returns>
    public BroadcastOptions WithMaxPayloadBytes(int bytes) => this with { MaxPayloadBytes = bytes };

    /// <summary>With 链——随机源。</summary>
    /// <param name="random">新随机源（扇出抽样/MsgId 生成；测试可注入固定种子）。</param>
    /// <returns>替换了随机源的新实例（其余位不变）。</returns>
    public BroadcastOptions WithRandom(Random random) => this with { Random = random };
}
