namespace TC.Tier.Core.Net.P2P;

/// <summary>
/// PhiAccrual 统计故障检测器（spec-06 §5 参数化——论文 Hayashibara 等）。
/// <para>原理：心跳到达间隔采样窗口 → 正态分布拟合（均值/标准差）→ 当前距上次心跳的时间
/// 换算 φ = -log₁₀(1 − Φ((Δt − μ)/σ))——φ 越大 = 越不可能仍存活。</para>
/// <para>★ 默认 φ=8 ≈ 误判率可忽略（spec-06 §2/§5）；样本不足时 φ=0（不判失败——冷启动宽限）。</para>
/// </summary>
public sealed class PhiAccrualDetector
{
    private readonly double _threshold;
    private readonly int _minSamples;
    private readonly Queue<double> _samples = [];   // 心跳到达间隔（毫秒——有界窗口）
    private readonly int _maxSamples;
    private readonly Func<long> _clock;            // ★ 时钟注入（测试确定性——默认 TickCount64）
    private long _lastHeartbeatTicks;
    private bool _anchored;   // ★ 首样本锚定标志（旧实现用 _samples.Count > 0 判定——锚定后计数仍 0 → 永假永不采样，φ 恒 0 楔死）

    /// <summary>构造。</summary>
    /// <param name="threshold">φ 阈值（默认 8——spec-06 §5）。</param>
    /// <param name="windowSamples">样本窗口容量（默认 12 = 60s 窗口 ÷ 5s 心跳间隔）。</param>
    /// <param name="minSamples">判定前最少样本数（默认 3——不足不判失败）。</param>
    public PhiAccrualDetector(double threshold = 8, int windowSamples = 12, int minSamples = 3)
        : this(threshold, windowSamples, minSamples, () => Environment.TickCount64)
    {
    }

    /// <summary>内部构造（时钟注入——测试确定性：RecordHeartbeat 与 Phi 同源时钟）。</summary>
    internal PhiAccrualDetector(double threshold, int windowSamples, int minSamples, Func<long> clock)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowSamples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minSamples);
        ArgumentNullException.ThrowIfNull(clock);
        _threshold = threshold;
        _maxSamples = windowSamples;
        _minSamples = minSamples;
        _clock = clock;
        _lastHeartbeatTicks = clock();
    }

    /// <summary>收到心跳——记录到达间隔样本（首个心跳仅锚定起点）。</summary>
    public void RecordHeartbeat()
    {
        lock (_samples)
        {
            var now = _clock();
            var interval = now - _lastHeartbeatTicks;
            _lastHeartbeatTicks = now;
            if (_anchored)
            {
                _samples.Enqueue(interval);
                while (_samples.Count > _maxSamples) _samples.Dequeue();
            }
            _anchored = true;
        }
    }

    /// <summary>当前 φ 值（无样本/样本不足 = 0）。</summary>
    /// <param name="nowTicks">当前时刻（TickCount64 ticks；0 = 内部时钟取当前时刻，默认 0）。</param>
    /// <returns>当前 φ 值（越大 = 越不可能仍存活；样本不足 = 0——冷启动宽限）。</returns>
    public double Phi(long nowTicks = 0)
    {
        if (nowTicks == 0) nowTicks = _clock();
        double[] samples;
        long last;
        lock (_samples)
        {
            if (_samples.Count < _minSamples) return 0;
            samples = [.. _samples];
            last = _lastHeartbeatTicks;
        }

        // 正态分布参数（样本均值/标准差——总体估计用 n-1）
        var n = samples.Length;
        var mean = samples.Sum() / n;
        var variance = n > 1 ? samples.Sum(x => (x - mean) * (x - mean)) / (n - 1) : 0;
        if (variance <= 0) variance = 1;   // 零方差防御——定期间隔视为 σ=1ms
        // ★ 最小方差地板（Akka 同款）：定期间隔（进程内投递 σ≈0）时单次调度抖动 z 即爆表 → 误判
        //   级联（实测：50ms 心跳 + 100ms 抖动 → φ>8 全员互判失败、视图雪崩）。
        //   σ 地板 = mean×0.3：φ=8 要求 dt ≳ mean + 2.6×mean ≈ 4× 心跳间隔——真断连检出、抖动不误判。
        var stddev = Math.Max(Math.Sqrt(variance), mean * 0.3);

        var dt = nowTicks - last;
        var z = (dt - mean) / stddev;
        var cdf = NormalCdf(z);
        var survival = Math.Max(1 - cdf, 1e-300);
        return -Math.Log10(survival);
    }

    /// <summary>是否判定故障（φ ≥ 阈值）。</summary>
    /// <param name="nowTicks">当前时刻（TickCount64 ticks；0 = 内部时钟取当前时刻，默认 0）。</param>
    /// <returns>true = φ 已达阈值（判故障）；false = 仍视为存活（含样本不足的宽限期）。</returns>
    public bool IsFailed(long nowTicks = 0) => Phi(nowTicks) >= _threshold;

    /// <summary>零样本超龄（单向边判死——flaky 判例 2026-09-02）：建立以来从未收到对端任何心跳
    /// 且已超出冷启动宽限 = 对端不认为我是邻居（对称视图破坏——满员挤掉/单向补位），此类边
    /// <b>永远不满足 minSamples</b>（φ 恒 0），无此判定则视图悬挂占坑。宽限由调用方按
    /// 心跳间隔 × 数倍给定（覆盖调度抖动）。</summary>
    /// <param name="graceTicks">冷启动宽限（ticks——建议心跳间隔 × 8）。</param>
    /// <returns>true = 建立以来零心跳且已超宽限（调用方可判死单向边）；false = 已有样本或仍在宽限内。</returns>
    public bool IsZeroSampleExpired(long graceTicks)
    {
        lock (_samples)
        {
            return !_anchored && _clock() - _lastHeartbeatTicks > graceTicks;
        }
    }

    /// <summary>重置（节点重启/成员替换——清样本与锚点）。</summary>
    public void Reset()
    {
        lock (_samples)
        {
            _samples.Clear();
            _anchored = false;
            _lastHeartbeatTicks = _clock();
        }
    }

    /// <summary>标准正态 CDF（Abramowitz-Stegun 近似——工程精度足够）。</summary>
    private static double NormalCdf(double x)
    {
        var t = 1.0 / (1.0 + 0.2316419 * Math.Abs(x));
        var d = 0.3989422804014327 * Math.Exp(-x * x / 2.0);
        var p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return x >= 0 ? 1 - p : p;
    }
}
