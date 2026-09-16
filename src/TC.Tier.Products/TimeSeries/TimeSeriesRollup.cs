using System.Buffers.Binary;

namespace TC.Tier.Products.TimeSeries;

/// <summary>
/// 降采样 Rollup 算子（tc-tier-timeseries-spec §6 定案⑤——组合糖）：
/// 读源序列 [fromTs, toTs) 按 windowTicks 分窗聚合 → 逐窗写目标序列（窗起点, 8B double）。
/// <para>★ 形态 = 原子算子（对标 InfluxDB CQ/Timescale caggs 的算子面——连续调度归业务 cron/后台任务）；
///   多档级联（1m→10m→1h）= 目标序列再作源（值编码 8B double 定档，组合律成立）。</para>
/// <para>★ 窗口对齐 = epoch 对齐（windowTicks 整数倍，向下取整）——级联档窗口天然嵌套。</para>
/// </summary>
public static class TimeSeriesRollup
{
    /// <summary>标量聚合族（非标量聚合 p99/直方图 = 业务面自聚——算子面不膨胀，spec §6）。</summary>
    public enum Aggregation
    {
        /// <summary>窗内最小值。</summary>
        Min,

        /// <summary>窗内最大值。</summary>
        Max,

        /// <summary>窗内总和。</summary>
        Sum,

        /// <summary>窗内样本数（值 = 计数，不读样本值）。</summary>
        Count,

        /// <summary>窗内算术平均。</summary>
        Avg,

        /// <summary>窗内首样本值（时间序最早）。</summary>
        First,

        /// <summary>窗内末样本值（时间序最晚）。</summary>
        Last,
    }

    /// <summary>降采样：源 [fromTs, toTs) 按 windowTicks 分窗 → 聚合值（8B double LE）→ target.Append(窗起点, 值)。</summary>
    /// <param name="source">源序列（值编码须为 8B double LE——Count 聚合除外）。</param>
    /// <param name="target">目标序列（独立实例——InfluxDB rollup RP / Timescale 物化视图对应物）。</param>
    /// <param name="windowTicks">窗口宽度（Ticks，&gt; 0）。</param>
    /// <param name="agg">聚合函数。</param>
    /// <param name="fromTs">源范围起点（含）。</param>
    /// <param name="toTs">源范围终点（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入目标序列的窗口数（空窗不落——只有含样本的窗才写）。</returns>
    /// <exception cref="ArgumentOutOfRangeException">windowTicks ≤ 0。</exception>
    /// <exception cref="InvalidOperationException">源样本值非 8B double（Count 除外）。</exception>
    public static ValueTask<int> RollupAsync(ITierTimeSeries source, ITierTimeSeries target,
        long windowTicks, Aggregation agg, long fromTs, long toTs, CancellationToken ct)
        => RollupAsync(source, target, windowTicks, agg, fromTs, toTs, ITierTimeSeries.DefaultSeriesId, ct);

    /// <summary>降采样（dense 命名序列——源 s₁ [fromTs, toTs) 分窗聚合 → 目标 s₁ 同序列落点）。</summary>
    /// <param name="seriesId">源/目标共用的序列标识（逐序列算子——#443 设计稿 §5.6）。</param>
    /// <param name="source">源序列。</param>
    /// <param name="target">目标序列。</param>
    /// <param name="windowTicks">窗口宽度（Ticks，&gt; 0）。</param>
    /// <param name="agg">聚合函数。</param>
    /// <param name="fromTs">源范围起点（含）。</param>
    /// <param name="toTs">源范围终点（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入目标序列的窗口数。</returns>
    public static ValueTask<int> RollupAsync(ITierTimeSeries source, ITierTimeSeries target,
        long windowTicks, Aggregation agg, long fromTs, long toTs, uint seriesId, CancellationToken ct)
        => RollupAsync(source, target, windowTicks, agg, fromTs, toTs, seriesId, seriesId, ct);

    /// <summary>降采样（dense 跨序列算子——源 s₁ 范围聚合 → 目标 s₂ 窗起点落点）。</summary>
    /// <param name="sourceSeriesId">源序列标识。</param>
    /// <param name="targetSeriesId">目标序列标识。</param>
    /// <param name="source">源序列。</param>
    /// <param name="target">目标序列。</param>
    /// <param name="windowTicks">窗口宽度（Ticks，&gt; 0）。</param>
    /// <param name="agg">聚合函数。</param>
    /// <param name="fromTs">源范围起点（含）。</param>
    /// <param name="toTs">源范围终点（不含）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入目标序列的窗口数。</returns>
    public static async ValueTask<int> RollupAsync(ITierTimeSeries source, ITierTimeSeries target,
        long windowTicks, Aggregation agg, long fromTs, long toTs,
        uint sourceSeriesId, uint targetSeriesId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (windowTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowTicks), windowTicks, "窗口宽度必须 > 0");
        if (fromTs >= toTs) return 0;

        int written = 0;
        long currentWindow = 0;
        bool open = false;
        double acc = 0, firstVal = 0, lastVal = 0;
        long count = 0;

        await foreach (var (ts, value, _) in source.RangeAsync(sourceSeriesId, fromTs, toTs, ct).ConfigureAwait(false))
        {
            long window = FloorDiv(ts, windowTicks);
            if (!open || window != currentWindow)
            {
                if (open)
                {
                    await AppendWindowAsync(target, targetSeriesId, currentWindow * windowTicks, agg, acc, count, firstVal, lastVal, ct)
                        .ConfigureAwait(false);
                    written++;
                }
                currentWindow = window;
                open = true;
                acc = 0;
                count = 0;
            }
            count++;
            lastVal = agg == Aggregation.Count ? 0 : DecodeDouble(value);
            if (count == 1) firstVal = lastVal;
            switch (agg)
            {
                case Aggregation.Min: acc = count == 1 ? lastVal : Math.Min(acc, lastVal); break;
                case Aggregation.Max: acc = count == 1 ? lastVal : Math.Max(acc, lastVal); break;
                case Aggregation.Sum or Aggregation.Avg or Aggregation.First or Aggregation.Last:
                    acc += lastVal;   // First/Last 在 AppendWindow 按位置取，Sum/Avg 直接累加
                    break;
                case Aggregation.Count: break;
            }
        }
        if (open)
        {
            await AppendWindowAsync(target, targetSeriesId, currentWindow * windowTicks, agg, acc, count, firstVal, lastVal, ct)
                .ConfigureAwait(false);
            written++;
        }
        return written;
    }

    /// <summary>单窗落点（聚合结果 8B double LE → 窗起点 Append）。</summary>
    private static async ValueTask AppendWindowAsync(ITierTimeSeries target, uint seriesId, long windowStart,
        Aggregation agg, double acc, long count, double firstVal, double lastVal, CancellationToken ct)
    {
        double result = agg switch
        {
            Aggregation.Min or Aggregation.Max => acc,
            Aggregation.Sum => acc,
            Aggregation.Count => count,
            Aggregation.Avg => count > 0 ? acc / count : 0,
            Aggregation.First => firstVal,
            Aggregation.Last => lastVal,
            _ => throw new ArgumentOutOfRangeException(nameof(agg), agg, "未知聚合函数"),
        };
        var buf = new byte[8];
        BinaryPrimitives.WriteDoubleLittleEndian(buf, result);
        await target.AppendAsync(seriesId, windowStart, buf, ct).ConfigureAwait(false);
    }

    /// <summary>样本值解码（8B double LE——长度不符 fail-fast，源编码契约见 RollupAsync）。</summary>
    private static double DecodeDouble(ReadOnlyMemory<byte> value)
    {
        if (value.Length < 8)
            throw new InvalidOperationException($"Rollup 源样本值须为 8B double（实际 {value.Length}B）——值编码契约见 spec §6");
        return BinaryPrimitives.ReadDoubleLittleEndian(value.Span);
    }

    /// <summary>向下取整除法（负 ts 安全——DateTime.Ticks 恒 ≥ 0，防御性兜底）。</summary>
    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
