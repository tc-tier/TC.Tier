using System.Collections.Concurrent;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 令牌桶限流器（二期-E4 NETGAP-032——QoS 基元）：桶容量 = burst（突发额度），
/// 按 perSecond 匀速回填（惰性结算——按需重算，无后台线程）。线程安全。
/// </summary>
public sealed class TokenBucket
{
    private readonly object _gate = new();
    private readonly double _perSecond;
    private readonly int _burst;
    private double _tokens;
    private long _lastTicks;

    /// <summary>构造。</summary>
    /// <param name="perSecond">回填速率（令牌/秒）。</param>
    /// <param name="burst">桶容量（突发额度；首次即拥有满桶）。</param>
    public TokenBucket(double perSecond, int burst)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(perSecond, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(burst, 1);
        _perSecond = perSecond;
        _burst = burst;
        _tokens = burst;
        _lastTicks = Environment.TickCount64;
    }

    /// <summary>消费 n 个令牌（不足 = false——调用方按策略丢弃/降级/排队）。</summary>
    /// <param name="n">本次消费的令牌数（须 ≥ 1；默认 1——超过桶容量直接 false）。</param>
    /// <returns>true = 消费成功（令牌已扣减）；false = 余额不足或 <paramref name="n"/> 超过桶容量（桶状态不变）。</returns>
    public bool TryConsume(int n = 1)
    {
        if (n > _burst) return false;
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var elapsedMs = now - _lastTicks;
            if (elapsedMs > 0)
            {
                _tokens = Math.Min(_burst, _tokens + elapsedMs * _perSecond / 1000.0);
                _lastTicks = now;
            }
            if (_tokens < n) return false;
            _tokens -= n;
            return true;
        }
    }

    /// <summary>当前令牌数（诊断——近似值）。</summary>
    public double Available
    {
        get { lock (_gate) return _tokens; }
    }

    /// <summary>回滚一次消费（二级准入部分失败——QosManager 来源桶回滚；封顶不溢出）。</summary>
    public void Refund()
    {
        lock (_gate)
        {
            _tokens = Math.Min(_burst, _tokens + 1);
            _lastTicks = Environment.TickCount64;
        }
    }
}

/// <summary>
/// QoS 管理器（二期-E4 NETGAP-032——每协议/每来源配额与限流）：
/// 两级令牌桶——域级聚合 + 域内按来源（租户=来源节点语义）；TryAdmit 双级全过才准入。
/// 策略运行期可变（重复 Set = 重置桶）；未设置 = 不限流（既有语义）。
/// </summary>
public sealed class QosManager
{
    private readonly ConcurrentDictionary<byte, TokenBucket> _domainBuckets = new();
    private readonly ConcurrentDictionary<(byte Domain, NodeId Source), TokenBucket> _sourceBuckets = new();

    /// <summary>域级聚合限流（该域全部来源共享一个桶）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="perSecond">令牌回填速率（令牌/秒，须 &gt; 0）。</param>
    /// <param name="burst">桶容量（突发额度，须 ≥ 1；重复设置 = 重置桶）。</param>
    public void SetDomainLimit(byte domain, double perSecond, int burst)
        => _domainBuckets[domain] = new TokenBucket(perSecond, burst);

    /// <summary>来源级限流（租户=来源节点——每来源独立桶）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="source">来源节点 ID（租户语义）。</param>
    /// <param name="perSecond">令牌回填速率（令牌/秒，须 &gt; 0）。</param>
    /// <param name="burst">桶容量（突发额度，须 ≥ 1；重复设置 = 重置桶）。</param>
    public void SetSourceLimit(byte domain, NodeId source, double perSecond, int burst)
        => _sourceBuckets[(domain, source)] = new TokenBucket(perSecond, burst);

    /// <summary>数据报域级限流（尽力语义——超限静默丢弃 + 计数）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="perSecond">令牌回填速率（令牌/秒，须 &gt; 0）。</param>
    /// <param name="burst">桶容量（突发额度，须 ≥ 1；重复设置 = 重置桶）。</param>
    public void SetDatagramDomainLimit(byte domain, double perSecond, int burst)
        => _domainBuckets[domain] = new TokenBucket(perSecond, burst);

    /// <summary>清除域级限流（恢复不限）。</summary>
    public void ClearDomainLimit(byte domain) => _domainBuckets.TryRemove(domain, out _);

    /// <summary>清除来源级限流。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="source">来源节点 ID（未设置过 = 无操作）。</param>
    public void ClearSourceLimit(byte domain, NodeId source)
        => _sourceBuckets.TryRemove((domain, source), out _);

    /// <summary>请求准入判定（域级 + 来源级两级全过 = true）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="source">来源节点 ID。</param>
    /// <returns>true = 两级桶均足额并已扣减（准入）；false = 任一级不足（未准入，已扣部分回滚）。</returns>
    public bool TryAdmitRequest(byte domain, NodeId source) => TryAdmit(domain, source);

    /// <summary>数据报准入判定（域级 + 来源级两级全过 = true）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="source">来源节点 ID。</param>
    /// <returns>true = 两级桶均足额并已扣减（准入）；false = 任一级不足（未准入，已扣部分回滚）。</returns>
    public bool TryAdmitDatagram(byte domain, NodeId source) => TryAdmit(domain, source);

    /// <summary>准入判定（两级全过 = true；任一桶不足 = false——先来源后域，域失败回滚来源）。</summary>
    /// <param name="domain">协议域 ID。</param>
    /// <param name="source">来源节点 ID。</param>
    /// <returns>true = 来源级与域级桶均足额并已扣减；false = 任一桶不足（来源桶已扣部分回滚——无部分消费）。</returns>
    public bool TryAdmit(byte domain, NodeId source)
    {
        if (_sourceBuckets.TryGetValue((domain, source), out var sourceBucket) && !sourceBucket.TryConsume())
            return false;   // 来源桶不足——域桶未动
        if (_domainBuckets.TryGetValue(domain, out var domainBucket) && !domainBucket.TryConsume())
        {
            sourceBucket?.Refund();   // 域桶不足——回滚来源消费（部分失败回滚）
            return false;
        }
        return true;
    }
}
