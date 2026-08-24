using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// 微秒计时器 —— 封装 <see cref="Stopwatch.GetTimestamp()"/> 的零分配计时。
/// <para>★ 零分配：readonly struct，栈上分配，无GC压力。</para>
/// <para>★ 采样门控：active=false 时完全零开销，JIT自动消除计时逻辑。</para>
/// <para>★ 整数换算：全程无浮点运算，全量程无精度损失。</para>
/// <para>使用方式：</para>
/// <code>
/// var s = MicroTimer.Start(_hub.Wal.ShouldSampleAppend());
/// // ... 执行业务逻辑 ...
/// if (s.IsActive) _hub.Wal.OnAppend(s.ElapsedMicros(), entry.Length);
/// </code>
/// </summary>
public readonly struct MicroTimer
{
    /// <summary>是否处于激活计时状态。</summary>
    // ReSharper disable once MemberCanBePrivate.Global
    public bool IsActive { get; }
    /// <summary>每秒tick数，静态缓存避免重复读取。</summary>
    private static readonly long Frequency = Stopwatch.Frequency;
    private readonly long _startTimestamp;

    private MicroTimer(bool active, long startTimestamp)
    {
        IsActive = active;
        _startTimestamp = startTimestamp;
    }

    /// <summary>开始计时（active=true 时记时间戳，active=false 时返回空 timer）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MicroTimer Start(bool active = true)
        => active
            ? new MicroTimer(true, Stopwatch.GetTimestamp())
            : default;

    /// <summary>获取已流逝的原始tick数。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ElapsedTicks()
        => IsActive ? Stopwatch.GetTimestamp() - _startTimestamp : 0;

    /// <summary>计算并返回已流逝时间（微秒）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ElapsedMicros()
    {
        var ticks = ElapsedTicks();
        return ticks == 0 ? 0 : ticks * 1_000_000 / Frequency;
    }

    /// <summary>计算并返回已流逝时间（毫秒）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ElapsedMillis()
    {
        var ticks = ElapsedTicks();
        return ticks == 0 ? 0 : ticks * 1_000 / Frequency;
    }

    /// <summary>
    /// 返回可读格式化字符串（自动适配单位）。
    /// <para>注意：该方法会产生字符串分配，热路径请使用 <see cref="TryFormat"/> 零分配版本。</para>
    /// <para>示例：3μs / 12.5ms / 1.23s / 2m05s / 1h30m</para>
    /// </summary>
    public string ElapsedReadable()
    {
        if (!IsActive) return "0";
        var micros = ElapsedMicros();

        return micros switch
        {
            < 1_000 => $"{micros}μs",
            < 1_000_000 => $"{micros / 1000.0:F1}ms",
            < 60_000_000 => $"{micros / 1_000_000.0:F2}s",
            < 3_600_000_000 => $"{micros / 60_000_000}m{micros / 1_000_000 % 60:D2}s",
            _ => $"{micros / 3_600_000_000}h{micros / 60_000_000 % 60:D2}m",
        };
    }

    /// <summary>
    /// ★ 零分配：尝试将已流逝时间格式化为可读字符串（自动适配单位），写入目标 Span。
    /// </summary>
    /// <param name="destination">目标字符 Span。</param>
    /// <param name="charsWritten">写入的字符数。</param>
    /// <returns>如果写入成功返回 true，否则返回 false。</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryFormat(Span<char> destination, out int charsWritten)
    {
        charsWritten = 0;
        if (!IsActive) return false;

        var micros = ElapsedMicros();
        bool result;

        switch (micros)
        {
            case < 1_000:
                result = micros.TryFormat(destination, out charsWritten);
                result &= AppendUnit(destination, "μs", ref charsWritten);
                break;
            case < 1_000_000:
            {
                var ms = micros / 1000.0;
                result = ms.TryFormat(destination, out charsWritten, "F1");
                result &= AppendUnit(destination, "ms", ref charsWritten);
                break;
            }
            case < 60_000_000:
            {
                var sec = micros / 1_000_000.0;
                result = sec.TryFormat(destination, out charsWritten, "F2");
                result &= AppendUnit(destination, "s", ref charsWritten);
                break;
            }
            case < 3_600_000_000:
            {
                var min = micros / 60_000_000;
                var sec = micros / 1_000_000 % 60;
                result = min.TryFormat(destination, out charsWritten);
                result &= AppendUnit(destination, "m", ref charsWritten);
                var secSpan = destination[charsWritten..];
                result &= sec.TryFormat(secSpan, out var secLen, "D2");
                charsWritten += secLen;
                result &= AppendUnit(destination, "s", ref charsWritten);
                break;
            }
            default:
            {
                var hour = micros / 3_600_000_000;
                var min = micros / 60_000_000 % 60;
                result = hour.TryFormat(destination, out charsWritten);
                result &= AppendUnit(destination, "h", ref charsWritten);
                var minSpan = destination[charsWritten..];
                result &= min.TryFormat(minSpan, out var minLen, "D2");
                charsWritten += minLen;
                result &= AppendUnit(destination, "m", ref charsWritten);
                break;
            }
        }

        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool AppendUnit(Span<char> destination, string unit, ref int charsWritten)
    {
        if (!unit.AsSpan().TryCopyTo(destination[charsWritten..])) return false;
        charsWritten += unit.Length;
        return true;
    }
}
