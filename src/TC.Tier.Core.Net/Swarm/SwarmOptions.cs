namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// SwarmSync 参数（spec-12 §6.1/§4.5——运行参数零写死，文中数值仅为缺省）。
/// </summary>
public sealed record SwarmOptions
{
    /// <summary>默认配置。</summary>
    public static SwarmOptions Default { get; } = new();

    /// <summary>并行度 K（默认 4——并发拉取块数；块级换源不占额外并发）。</summary>
    public int Parallelism { get; private init; } = 4;

    /// <summary>单请求超时（缺省走传输 per-call 缺省——null 不覆盖；慢源在此窗内换源）。</summary>
    public TimeSpan? RequestTimeout { get; private init; }

    /// <summary>是否注册取块 handler、参与持有表（默认 true）。false = 纯消费者节点——
    /// 半可信内网省磁盘读带宽形态：不服务 GetBlock、不广播持有（静态 holders 仍可指名拉取但拿不到块）。</summary>
    public bool ServeBlocks { get; private init; } = true;

    /// <summary>是否广播持有上报（默认 true；须与 ServeBlocks 同 true 才发）。false = 完全退出
    /// 广播面——不主动上报（取块 handler 照常注册：静态 holders 指名拉取仍可用）。</summary>
    public bool AnnounceOnStart { get; private init; } = true;

    /// <summary>反熵对账周期（spec-12 §6.1 件 C——缺省 Zero = 关闭；LeaderLocal/Swarm-only 档
    /// 建议开启，Majority 档无需）。周期触发与成员轮转归装配面（机制面 =
    /// <see cref="SwarmAntiEntropy.RunOnceAsync"/> 单轮比对修复）。</summary>
    public TimeSpan AntiEntropyInterval { get; private init; } = TimeSpan.Zero;

    /// <summary>With 链——并行度。</summary>
    /// <param name="k">新并行度（并发拉取块数）。</param>
    /// <returns>替换了并行度的新实例（其余位不变）。</returns>
    public SwarmOptions WithParallelism(int k) => this with { Parallelism = k };

    /// <summary>With 链——请求超时。</summary>
    /// <param name="timeout">新单请求超时（慢源在此窗内换源）。</param>
    /// <returns>替换了请求超时的新实例（其余位不变）。</returns>
    public SwarmOptions WithRequestTimeout(TimeSpan timeout) => this with { RequestTimeout = timeout };

    /// <summary>With 链——是否服务取块 + 参与持有表（false = 纯消费者）。</summary>
    /// <param name="serve">true = 注册取块 handler 并广播持有；false = 纯消费者节点。</param>
    /// <returns>替换了服务开关的新实例（其余位不变）。</returns>
    public SwarmOptions WithServeBlocks(bool serve) => this with { ServeBlocks = serve };

    /// <summary>With 链——是否广播持有上报（false = 完全退出广播面）。</summary>
    /// <param name="announce">true = 启动时广播持有上报；false = 不主动上报。</param>
    /// <returns>替换了上报开关的新实例（其余位不变）。</returns>
    public SwarmOptions WithAnnounceOnStart(bool announce) => this with { AnnounceOnStart = announce };

    /// <summary>With 链——反熵对账周期（Zero = 关闭）。</summary>
    /// <param name="interval">新反熵对账周期（Zero = 关闭）。</param>
    /// <returns>替换了对账周期的新实例（其余位不变）。</returns>
    public SwarmOptions WithAntiEntropyInterval(TimeSpan interval) => this with { AntiEntropyInterval = interval };
}
