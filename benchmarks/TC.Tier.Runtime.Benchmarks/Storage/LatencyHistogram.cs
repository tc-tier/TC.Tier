using System.Diagnostics;

namespace TC.Tier.Runtime.Benchmarks.Storage;

/// <summary>
/// 轻量延迟直方图——预分配环形 buffer 记录每次操作的 Stopwatch ticks，结束时排序取分位。
/// <para>★ 零 GC 压力：固定大小 long[]，热路径只做一次 ticks 写入 + 计数自增，不分配对象。</para>
/// <para>★ 用途：基准/探针记录 p50/p95/p99/p999/max，量化"卡死前兆"的尾延迟尖峰。</para>
/// <para>★ 设计取舍：不做 fixed-bucket histogram（HDR 风格）——简单排序对百万级样本足够快（~100ms），
///   且精度无损。若样本数超过 Capacity，按固定步长抽样保留均匀分布。</para>
/// </summary>
internal sealed class LatencyHistogram
{
    private readonly long[] _samples;
    private int _count;
    private long _sumTicks;
    private long _maxTicks;
    private readonly int _stride; // 样本数超过 Capacity 时的抽样步长

    /// <summary>Stopwatch ticks 转 μs 的除数（Stopwatch.Frequency ticks/秒 → 10^6 μs/秒）。</summary>
    private static readonly double TicksPerMicrosecond = Stopwatch.Frequency / 1_000_000.0;

    public LatencyHistogram(int capacity = 65_536)
    {
        _samples = new long[capacity];
        _count = 0;
        _sumTicks = 0;
        _maxTicks = 0;
        _stride = 1;
    }

    /// <summary>记录一次操作的耗时（Stopwatch.GetTimestamp() 差值，单位 ticks）。</summary>
    public void Record(long elapsedTicks)
    {
        if (elapsedTicks < 0) return;
        // 热路径：先累加统计量（无锁，单基准线程内串行调用）
        _sumTicks += elapsedTicks;
        if (elapsedTicks > _maxTicks) _maxTicks = elapsedTicks;

        // 写入样本：未满直接写；满了按 stride 抽样（保留均匀分布的早期样本）
        int idx = _count;
        if (idx < _samples.Length)
        {
            _samples[idx] = elapsedTicks;
            _count = idx + 1;
        }
        else
        {
            // 满了：偶发落槽（每 stride 次落一次，避免完全丢失尾部样本）
            if ((idx & (_stride * 8 - 1)) == 0)
            {
                int slot = (idx / (_stride * 8)) % _samples.Length;
                _samples[slot] = elapsedTicks;
            }
        }
    }

    /// <summary>开始计时——返回 Stopwatch.GetTimestamp()，调用方算差后传给 Record。</summary>
    public static long Start() => Stopwatch.GetTimestamp();

    /// <summary>结束计时并记录——封装 Start → 计算 → Record 的常用模式。</summary>
    public void Measure(long startTicks)
    {
        Record(Stopwatch.GetTimestamp() - startTicks);
    }

    public int Count => _count;
    public double AverageMicroseconds => _count > 0 ? (_sumTicks / _count) / TicksPerMicrosecond : 0;
    public double MaxMicroseconds => _maxTicks / TicksPerMicrosecond;

    /// <summary>取分位（pct ∈ [0,100]，50=p50, 99=p99, 99.9=p999, 100=max）。</summary>
    public double PercentileMicroseconds(double pct)
    {
        int n = Math.Min(_count, _samples.Length);
        if (n == 0) return 0;

        // 排序副本（不污染原数组）
        var copy = new long[n];
        Array.Copy(_samples, copy, n);
        Array.Sort(copy);

        // pct=100 取最大；否则按线性插值取分位索引
        double rank = pct / 100.0 * (n - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return copy[lo] / TicksPerMicrosecond;
        double frac = rank - lo;
        return (copy[lo] + (copy[hi] - copy[lo]) * frac) / TicksPerMicrosecond;
    }

    /// <summary>一次性输出 p50/p95/p99/p999/max + 平均（μs），格式化为单行字符串。</summary>
    public string Summary()
    {
        if (_count == 0) return "no samples";
        return $"n={_count} avg={AverageMicroseconds:F1}μs p50={PercentileMicroseconds(50):F1}μs "
             + $"p95={PercentileMicroseconds(95):F1}μs p99={PercentileMicroseconds(99):F1}μs "
             + $"p999={PercentileMicroseconds(99.9):F1}μs max={MaxMicroseconds:F1}μs";
    }
}
