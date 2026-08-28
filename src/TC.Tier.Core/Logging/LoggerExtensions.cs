using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TC.Tier.Core.Logging;

/// <summary>
/// Logger 扩展方法 — 提供 LogInformation/LogWarning/LogError 等便捷方法。
/// <para>★ 零开销短路：每个重载内部先 <see cref="ILogger.IsEnabled"/> 再格式化——<b>调用方无需手写 IsEnabled</b>。
///   0~3 参数用强类型重载（关闭时不装箱、不分配、不格式化）。</para>
/// <para>★ 超过 3 个参数走 <c>params</c> 重载：内部仍判 IsEnabled（不格式化），但数组+装箱在<b>调用点</b>
///   发生（进扩展之前）——正确性无恙，仅<b>热路径</b>值得先手动 <c>IsEnabled</c> 保护或拆成 ≤3 参。</para>
/// <para>★ 格式化用 <c>string.Format</c>（仅 IsEnabled true 时）。
///   命名占位符（<c>{name}</c>）按参数顺序映射为数字索引，兼容 M.E.Logging 惯例。</para>
/// </summary>
public static partial class LoggerExtensions
{
    /// <summary>匹配命名占位符 {Name}（不含数字、不含空格），用于映射为数字索引。</summary>
    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*)(?::[^}]*)?\}", RegexOptions.Compiled)]
    private static partial Regex NamedPlaceholderRegex();

    /// <summary>把命名占位符 {Name} 按出现顺序替换为 {0},{1},... 供 string.Format 使用。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string NormalizeNamedPlaceholders(string message)
    {
        if (message.IndexOf('{') < 0) return message;
        int index = 0;
        return NamedPlaceholderRegex().Replace(message, _ => $"{{{index++}}}");
    }

    // ===== Trace =====
    /// <summary>以 Trace 级别记录日志（logger 为 null 或未启用 Trace 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(this ILogger? logger, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Trace)) logger.Log(LogLevel.Trace, message);
    }
    /// <summary>以 Trace 级别记录日志并填充格式化参数（logger 为 null 或未启用 Trace 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(this ILogger? logger, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Trace)) logger.Log(LogLevel.Trace, string.Format(NormalizeNamedPlaceholders(message), arg1));
    }
    /// <summary>以 Trace 级别记录日志并填充格式化参数（logger 为 null 或未启用 Trace 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(this ILogger? logger, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Trace)) logger.Log(LogLevel.Trace, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2));
    }
    /// <summary>以 Trace 级别记录日志并填充格式化参数（logger 为 null 或未启用 Trace 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    /// <param name="arg3">格式化参数 3。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(this ILogger? logger, string message, object? arg1, object? arg2, object? arg3)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Trace)) logger.Log(LogLevel.Trace, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2, arg3));
    }

    // ===== Debug =====
    /// <summary>以 Debug 级别记录日志（logger 为 null 或未启用 Debug 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(this ILogger? logger, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.Log(LogLevel.Debug, message);
    }
    /// <summary>以 Debug 级别记录日志并填充格式化参数（logger 为 null 或未启用 Debug 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(this ILogger? logger, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.Log(LogLevel.Debug, string.Format(NormalizeNamedPlaceholders(message), arg1));
    }
    /// <summary>以 Debug 级别记录日志并填充格式化参数（logger 为 null 或未启用 Debug 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(this ILogger? logger, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.Log(LogLevel.Debug, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2));
    }
    /// <summary>以 Debug 级别记录日志并填充格式化参数（logger 为 null 或未启用 Debug 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    /// <param name="arg3">格式化参数 3。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(this ILogger? logger, string message, object? arg1, object? arg2, object? arg3)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.Log(LogLevel.Debug, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2, arg3));
    }

    // ===== Information =====
    /// <summary>以 Information 级别记录日志（logger 为 null 或未启用 Information 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(this ILogger? logger, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Information)) logger.Log(LogLevel.Information, message);
    }
    /// <summary>以 Information 级别记录日志并填充格式化参数（logger 为 null 或未启用 Information 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(this ILogger? logger, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Information)) logger.Log(LogLevel.Information, string.Format(NormalizeNamedPlaceholders(message), arg1));
    }
    /// <summary>以 Information 级别记录日志并填充格式化参数（logger 为 null 或未启用 Information 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(this ILogger? logger, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Information)) logger.Log(LogLevel.Information, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2));
    }
    /// <summary>以 Information 级别记录日志并填充格式化参数（logger 为 null 或未启用 Information 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    /// <param name="arg3">格式化参数 3。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(this ILogger? logger, string message, object? arg1, object? arg2, object? arg3)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Information)) logger.Log(LogLevel.Information, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2, arg3));
    }

    // ===== Warning =====
    /// <summary>以 Warning 级别记录日志（logger 为 null 或未启用 Warning 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, message);
    }
    /// <summary>以 Warning 级别记录日志并填充格式化参数（logger 为 null 或未启用 Warning 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, string.Format(NormalizeNamedPlaceholders(message), arg1));
    }
    /// <summary>以 Warning 级别记录日志并填充格式化参数（logger 为 null 或未启用 Warning 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2));
    }
    /// <summary>以 Warning 级别记录日志并填充格式化参数（logger 为 null 或未启用 Warning 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    /// <param name="arg3">格式化参数 3。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, string message, object? arg1, object? arg2, object? arg3)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2, arg3));
    }
    /// <summary>以 Warning 级别记录日志并附带异常（logger 为 null 或未启用 Warning 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, Exception exception, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, message, exception);
    }
    /// <summary>以 Warning 级别记录日志并附带异常、填充格式化参数（logger 为 null 或未启用 Warning 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, Exception exception, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, string.Format(NormalizeNamedPlaceholders(message), arg1), exception);
    }
    /// <summary>以 Warning 级别记录日志并附带异常、填充任意数量参数（logger 为 null 或未启用 Warning 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, Exception exception, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, string.Format(NormalizeNamedPlaceholders(message), args), exception);
    }

    // ===== Error =====
    /// <summary>以 Error 级别记录日志（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, message);
    }
    /// <summary>以 Error 级别记录日志并填充格式化参数（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), arg1));
    }
    /// <summary>以 Error 级别记录日志并填充格式化参数（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2));
    }
    /// <summary>以 Error 级别记录日志并填充格式化参数（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    /// <param name="arg3">格式化参数 3。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, string message, object? arg1, object? arg2, object? arg3)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2, arg3));
    }
    /// <summary>以 Error 级别记录日志并附带异常（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, Exception exception, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, message, exception);
    }
    /// <summary>以 Error 级别记录日志并附带异常、填充格式化参数（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, Exception exception, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), arg1), exception);
    }
    /// <summary>以 Error 级别记录日志并附带异常、填充格式化参数（logger 为 null 或未启用 Error 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, Exception exception, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2), exception);
    }

    // ===== Critical =====
    /// <summary>以 Critical 级别记录日志（logger 为 null 或未启用 Critical 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(this ILogger? logger, string message)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Critical)) logger.Log(LogLevel.Critical, message);
    }
    /// <summary>以 Critical 级别记录日志并填充格式化参数（logger 为 null 或未启用 Critical 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(this ILogger? logger, string message, object? arg1)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Critical)) logger.Log(LogLevel.Critical, string.Format(NormalizeNamedPlaceholders(message), arg1));
    }
    /// <summary>以 Critical 级别记录日志并填充格式化参数（logger 为 null 或未启用 Critical 时零分配短路）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="arg1">格式化参数 1。</param>
    /// <param name="arg2">格式化参数 2。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(this ILogger? logger, string message, object? arg1, object? arg2)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Critical)) logger.Log(LogLevel.Critical, string.Format(NormalizeNamedPlaceholders(message), arg1, arg2));
    }

    // ===== 超过 3 个参数的兜底：调用方手动 IsEnabled 保护 =====
    /// <summary>
    /// 以 Information 级别记录日志并填充任意数量参数（4+ 参数场景的兜底重载）。
    /// <b>调用方应在 <c>IsEnabled</c> 为 true 时才调用</b>，否则 <c>params</c> 数组仍会分配。
    /// <code>
    /// if (logger is not null &amp;&amp; logger.IsEnabled(LogLevel.Information))
    ///     logger.LogInformation("...", arg1, arg2, arg3, arg4);
    /// </code>
    /// </summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogInformation(this ILogger? logger, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Information)) logger.Log(LogLevel.Information, string.Format(NormalizeNamedPlaceholders(message), args));
    }
    /// <summary>以 Warning 级别记录日志并填充任意数量参数（logger 为 null 或未启用 Warning 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogWarning(this ILogger? logger, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Warning)) logger.Log(LogLevel.Warning, string.Format(NormalizeNamedPlaceholders(message), args));
    }
    /// <summary>以 Error 级别记录日志并填充任意数量参数（logger 为 null 或未启用 Error 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), args));
    }
    /// <summary>以 Error 级别记录日志并附带异常、填充任意数量参数（logger 为 null 或未启用 Error 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="exception">附带记录的异常。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogError(this ILogger? logger, Exception exception, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Error)) logger.Log(LogLevel.Error, string.Format(NormalizeNamedPlaceholders(message), args), exception);
    }
    /// <summary>以 Trace 级别记录日志并填充任意数量参数（logger 为 null 或未启用 Trace 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogTrace(this ILogger? logger, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Trace)) logger.Log(LogLevel.Trace, string.Format(NormalizeNamedPlaceholders(message), args));
    }
    /// <summary>以 Debug 级别记录日志并填充任意数量参数（logger 为 null 或未启用 Debug 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogDebug(this ILogger? logger, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Debug)) logger.Log(LogLevel.Debug, string.Format(NormalizeNamedPlaceholders(message), args));
    }
    /// <summary>以 Critical 级别记录日志并填充任意数量参数（logger 为 null 或未启用 Critical 时零分配短路；
    /// params 数组在调用点分配，热路径建议先手动 IsEnabled 保护）。</summary>
    /// <param name="logger">目标日志器（null 则跳过）。</param>
    /// <param name="message">日志消息模板（支持 {Named} 命名占位符，按出现顺序映射为数字索引）。</param>
    /// <param name="args">格式化参数数组。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void LogCritical(this ILogger? logger, string message, params object?[] args)
    {
        if (logger is not null && logger.IsEnabled(LogLevel.Critical)) logger.Log(LogLevel.Critical, string.Format(NormalizeNamedPlaceholders(message), args));
    }
}
