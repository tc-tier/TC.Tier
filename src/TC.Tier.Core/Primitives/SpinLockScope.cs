using System.Runtime.CompilerServices;

namespace TC.Tier.Core.Primitives;

/// <summary>
///  <see cref="SpinLockScope"/>——using 块自动获取/释放 <see cref="SpinLock"/>（避免忘记释放锁）。
/// <para>★ C# 12 兼容：ref struct 不实现 <see cref="IDisposable"/>（ref struct interfaces 是 C# 13），
///   靠 pattern-based using 自动调用公开的 <see cref="Dispose"/>。</para>
/// </summary>
public ref struct SpinLockScope
{
    private ref SpinLock _lock;
    private readonly bool _taken;

    /// <summary>
    /// 获取 <see cref="SpinLock"/> 的 scope（using 块自动 Enter/Exit <see cref="SpinLock"/>，零分配）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static SpinLockScope Enter(ref SpinLock spinLock)
        => new(ref spinLock);

    private SpinLockScope(ref SpinLock spinLock)
    {
        _lock = ref spinLock;
        var taken = false; // 局部变量传 ref——Enter 写入
        _lock.Enter(ref taken);
        _taken = taken; // 复制到 readonly 字段（构造函数内赋值合法）
    }

    /// <summary>
    /// 释放锁。如果锁已被获取，则调用 <see cref="SpinLock.Exit()"/> 方法释放锁。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (_taken) _lock.Exit();
    }
}