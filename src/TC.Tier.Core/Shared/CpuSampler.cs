using System.Diagnostics;
using TC.Tier.Core.Observability;

namespace TC.Tier.Core.Shared;

/// <summary>
/// 通用 CPU 利用率采样 + 限流系数计算（Core/Shared）。继承 <see cref="BackgroundWorkerLoop"/>——
/// PeriodicTimer 驱动采样，Start/Stop/Dispose 全由基类。
/// <para>★ <b>计算模型（明确，不靠概念）</b>：采样<b>本进程</b> CPU——
///   <c>raw = ΔProcess.TotalProcessorTime / (Δ实际墙钟 × ProcessorCount)</c>，归一化 0~1
///   （1.0 = 本进程跑满所有核）。是<b>进程口径</b>而非整机口径：其它进程造成的系统饱和本采样器看不见
///   （整机口径需性能计数器，跨平台不可用，故明确取进程口径）。</para>
/// <para>★ 平滑：EMA——<c>ema' = α·raw + (1−α)·ema</c>，<b>首样本标志位</b>初始化（不拿 ema==0 当哨兵：
///   旧实现空闲→高载第一个样本直接跳变，无平滑——"不平稳过渡"的根因）。</para>
/// <para>★ 限流系数 <see cref="ThrottleFactor"/>：分段线性——CPU ≤ lowCutoff → 0（不限流）；
///   lowCutoff~highCutoff → 线性 0→1（软降速）；≥ highCutoff → 1（强阻塞）。
///   系数计算是纯函数（<see cref="MapThrottleFactor"/>，internal 可测）。</para>
/// <para>★ Hub 折叠：注入 <see cref="ObservabilityHub"/> 后每采样发布
///   <c>cpu.utilization</c> / <c>cpu.throttle_factor</c> Gauge，档位切换（正常→限流→强阻塞→恢复）打日志——
///   限流判断进可观测视图，不再隐形。</para>
/// <para>★ 热路径零开销：读 <see cref="CpuUtilization"/> / <see cref="ThrottleFactor"/> 走 Volatile.Read；
///   Hub 未注入（默认 Disabled）时指标短路零开销。</para>
/// </summary>
public sealed class CpuSampler : BackgroundWorkerLoop
{
    // === 配置（构造注入，构造时全量校验 fail-fast）===
    private readonly TimeSpan _sampleInterval;   // 采样周期，默认 1s（> 0）
    private readonly double _emaAlpha;           // EMA 平滑系数，默认 0.5（0 < α ≤ 1：大=跟手、小=平滑）
    private readonly double _throttleLowCutoff;  // 不限流阈值，默认 0.70（0 ≤ low < high ≤ 1）
    private readonly double _throttleHighCutoff; // 强阻塞阈值，默认 0.90
    private readonly int _processorCount = Math.Max(1, Environment.ProcessorCount);
    private readonly ObservabilityHub _hub;      // 指标折叠（默认 Disabled 零开销）
    private readonly ILogger? _logger;
    private readonly string _name;               // 诊断标识（基类 _name 私有，本地留一份供日志）

    // === 采样状态（Volatile 读写）===
    private double _cpuUtilization;
    private double _throttleFactor;
    private Process? _process;
    private TimeSpan _lastCpuTime;
    private long _lastSampleTicks;
    private bool _hasSample;                     // ★ 首样本标志（EMA 初始化）——不用 ema==0 当哨兵
    private int _lastLevel;                      // 0=正常 1=限流 2=强阻塞（档位切换日志用）

    /// <summary>当前 CPU 利用率（0~1，EMA 平滑，进程口径）。热路径 Volatile.Read。</summary>
    public double CpuUtilization => Volatile.Read(ref _cpuUtilization);

    /// <summary>
    /// 限流系数（0.0~1.0，0=不限流，1.0=强阻塞）。热路径 Volatile.Read。
    /// <para>消费方按此做三档背压（平稳过渡）：正常（系数 0，放行）→ 限流（系数居中，拒绝/降级）
    ///   → 直接报错（系数 1，抛过载）。</para>
    /// </summary>
    public double ThrottleFactor => Volatile.Read(ref _throttleFactor);

    /// <param name="sampleInterval">采样周期（默认 1s）。须 &gt; 0。</param>
    /// <param name="emaAlpha">EMA 平滑系数（默认 0.5——大=跟手、小=平滑）。须 ∈ (0, 1]。</param>
    /// <param name="throttleLowCutoff">不限流阈值（默认 0.70）。须 ∈ [0,1) 且 &lt; highCutoff。</param>
    /// <param name="throttleHighCutoff">强阻塞阈值（默认 0.90）。须 ∈ (0,1]。倒挂（low ≥ high）构造即 throw——
    ///   否则分段线性斜率为负/除零 → CPU 越高系数越低的反向限流/NaN（算法错误，fail-fast 拒绝）。</param>
    /// <param name="hub">可观测 hub（采样折叠：cpu.utilization / cpu.throttle_factor Gauge + 档位日志）。默认 Disabled。</param>
    /// <param name="name">诊断标识（默认 "CpuSampler"）。</param>
    /// <param name="logger">日志（可选）。</param>
    public CpuSampler(
        TimeSpan? sampleInterval = null, double emaAlpha = 0.5,
        double throttleLowCutoff = 0.70, double throttleHighCutoff = 0.90,
        ObservabilityHub? hub = null,
        string? name = null, ILogger? logger = null)
        : base(name: name ?? "CpuSampler", logger: logger)
    {
        _sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);
        _emaAlpha = emaAlpha;
        _throttleLowCutoff = throttleLowCutoff;
        _throttleHighCutoff = throttleHighCutoff;
        _hub = hub ?? ObservabilityHub.Disabled;
        _logger = logger;
        _name = name ?? "CpuSampler";

        // ★ 构造全量校验（fail-fast——参数错误当场抛，不让算法带病运行）
        if (_sampleInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(sampleInterval), _sampleInterval, "sampleInterval 必须 > 0");
        if (emaAlpha is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(emaAlpha), emaAlpha, "emaAlpha 必须 ∈ (0,1]——越界会震荡发散或永不更新");
        if (throttleLowCutoff < 0 || throttleLowCutoff >= throttleHighCutoff || throttleHighCutoff > 1)
            throw new ArgumentOutOfRangeException(nameof(throttleHighCutoff),
                $"cutoff 区间非法：low={throttleLowCutoff} high={throttleHighCutoff}（须 0 ≤ low < high ≤ 1）");
    }

    /// <summary>
    /// 采样循环——PeriodicTimer 驱动；Stop 时 ct.Cancel 让 <c>WaitForNextTickAsync</c> 抛 OCE 正常退出。
    /// </summary>
    protected override async ValueTask<bool> RunOneCycleAsync(CancellationToken ct)
    {
        _process ??= Process.GetCurrentProcess();
        using var timer = new PeriodicTimer(_sampleInterval);
        // 基准
        _lastCpuTime = _process.TotalProcessorTime;
        _lastSampleTicks = Environment.TickCount64;

        while (!ct.IsCancellationRequested)
        {
            if (!await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) return false;
            Sample();
        }
        return false;
    }

    /// <summary>采一次：时间差法算 raw → EMA 平滑 → 分段限流系数，发布 + 折叠 Hub。</summary>
    private void Sample()
    {
        var p = _process;
        if (p is null) return;
        TimeSpan cpuNow;
        try { cpuNow = p.TotalProcessorTime; }
        catch { return; }   // 进程信息读取失败——跳过本次（不推进基准）

        var wallMs = Environment.TickCount64 - _lastSampleTicks;
        if (wallMs <= 0)
            return;   // ★ 墙钟差非正（tick 粒度）——跳过且【不推进基准】（旧实现跳过却推基准=丢样本窗口）

        var raw = cpuNow.Subtract(_lastCpuTime).TotalMilliseconds / (wallMs * _processorCount);
        var hasPrev = _hasSample;
        var prev = Volatile.Read(ref _cpuUtilization);

        var ema = ApplyEma(hasPrev, prev, raw, _emaAlpha);
        var cpu = Math.Clamp(ema, 0.0, 1.0);
        var factor = MapThrottleFactor(cpu, _throttleLowCutoff, _throttleHighCutoff);

        _hasSample = true;
        Volatile.Write(ref _cpuUtilization, cpu);
        Volatile.Write(ref _throttleFactor, factor);
        _lastCpuTime = cpuNow;
        _lastSampleTicks = Environment.TickCount64;

        FoldToHub(cpu, factor);
    }

    /// <summary>指标/日志折叠到 Hub（判断进视图）：每采样两个 Gauge + 档位切换日志。</summary>
    private void FoldToHub(double cpu, double factor)
    {
        var level = factor <= 0.0 ? 0 : factor >= 1.0 ? 2 : 1;
        var prevLevel = Interlocked.Exchange(ref _lastLevel, level);
        if (level != prevLevel)
        {
            var msg = level switch
            {
                0 => "CPU 回落——恢复放行",
                1 => $"CPU 进入限流档（软降速）utilization={cpu:F2} factor={factor:F2}",
                _ => $"CPU 进入强阻塞档 utilization={cpu:F2}——消费方应拒绝/报过载",
            };
            _logger?.LogWarning("CpuSampler {Name} 档位切换：{Message}", _name, msg);
        }

        if (!_hub.Metrics.IsEnabled) return;
        _hub.Metrics.Gauge("cpu.utilization", cpu, []);
        _hub.Metrics.Gauge("cpu.throttle.factor", factor, []);
    }

    // ════════════════════════════════════════════════════════════
    //  纯函数（internal 可测性缝——数学契约由单测锁定）
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// EMA 平滑一步（纯函数）：<c>首样本直接取 raw</c>（标志位初始化，<b>不</b>拿 prev==0 当哨兵）；
    /// 此后 <c>ema' = α·raw + (1−α)·prev</c>。
    /// </summary>
    internal static double ApplyEma(bool hasPrev, double prev, double raw, double alpha)
        => hasPrev ? prev * (1 - alpha) + raw * alpha : raw;

    /// <summary>
    /// 分段线性限流映射（纯函数）：cpu ≤ low → 0；cpu ≥ high → 1；中间线性
    /// <c>(cpu − low) / (high − low)</c>。调用方保证 0 ≤ low &lt; high ≤ 1（ctor 校验）。
    /// </summary>
    internal static double MapThrottleFactor(double cpu, double low, double high)
        => cpu <= low ? 0.0
         : cpu >= high ? 1.0
         : (cpu - low) / (high - low);
}
