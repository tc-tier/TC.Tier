using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
/// ThrowHelper 用于集中处理异常抛出，避免在调用点重复编写 throw 语句，提高代码可读性和维护性。
/// </summary>
public static partial class ThrowHelper
{
    /// <summary>
    /// 抛出 ArgumentOutOfRangeException 异常。
    /// </summary>
    /// <param name="paramName">参数名称</param>
    /// <exception cref="ArgumentOutOfRangeException">抛出参数超出范围异常</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgumentOutOfRange(string? paramName = null) =>
        throw new ArgumentOutOfRangeException(paramName ?? string.Empty);

    /// <summary>
    /// 抛出 ArgumentOutOfRangeException 异常，并附带自定义消息。
    /// </summary>
    /// <param name="paramName">参数名称</param>
    /// <param name="message">自定义异常消息</param>
    /// <exception cref="ArgumentOutOfRangeException">抛出参数超出范围异常</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowArgumentOutOfRange(string? paramName, string? message) =>
        throw new ArgumentOutOfRangeException(paramName ?? string.Empty, message);

    /// <summary>
    /// 抛出 ObjectDisposedException 异常。
    /// </summary>
    /// <param name="objectName">对象名称</param>
    /// <exception cref="ObjectDisposedException">抛出对象已释放异常</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowObjectDisposed(string objectName) =>
        throw new ObjectDisposedException(objectName);

    /// <summary>
    /// 抛出 InvalidOperationException 异常，并附带自定义消息。
    /// </summary>
    /// <param name="message">自定义异常消息</param>
    /// <exception cref="InvalidOperationException">抛出无效操作异常</exception>
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void ThrowInvalidOperationException(string message) =>
        throw new InvalidOperationException(message);
}