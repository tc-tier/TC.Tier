using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace TC.Tier.Core.Primitives;
/// <summary>
/// <see cref="NativeArena"/> 是一个简单的非托管内存分配器，提供类似竞技场（arena）的内存管理方式。
/// 它在初始化时分配一块固定大小的非托管内存，并允许在该内存块中进行快速的线性分配。适用于需要高性能、低开销的内存分配场景，例如临时缓冲区、批量数据处理等。
/// </summary>
public sealed unsafe class NativeArena : IDisposable
{
    /// <summary>
    /// 非托管内存块的起始指针。
    /// </summary>
    private IntPtr _ptr;

    /// <summary>
    /// 竞技场总大小（字节数）。
    /// </summary>
    private readonly int _size;

    /// <summary>
    /// 当前分配偏移量（字节数）。
    /// </summary>
    private int _offset;

    /// <summary>
    /// 标记竞技场是否已被释放（0=未释放，1=已释放）。
    /// ★ F4：用 int + Interlocked.Exchange 保证 Dispose 与 finalizer 并发时的原子进入，
    ///   避免双释放（修复前 bool _disposed 的 check-then-set 非原子，两个线程可能都通过检查后都 Free）。
    /// </summary>
    private int _disposed;

    /// <summary>
    /// 获取非托管内存块的起始指针。
    /// </summary>
    public IntPtr Pointer => _ptr;

    /// <summary>
    /// 获取竞技场总大小（字节数）。
    /// </summary>
    public int Size => _size;

    /// <summary>
    /// 获取已使用的字节数。
    /// </summary>
    public int Used => _offset;

    /// <summary>
    /// 获取剩余可用字节数。
    /// </summary>
    public int Remaining => _size - _offset;

    /// <summary>
    /// 获取竞技场是否已被释放。
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// 初始化竞技场的新实例，分配指定大小的非托管内存。
    /// </summary>
    /// <param name="size">要分配的内存大小（字节数）。</param>
    public NativeArena(int size)
    {
        _size = size;
        _ptr = (IntPtr)NativeMemory.Alloc((nuint)size);
        _offset = 0;
    }

    /// <summary>
    /// 从竞技场中分配指定数量的 T 类型元素的内存空间。
    /// </summary>
    /// <typeparam name="T">元素类型，必须为非托管结构体。</typeparam>
    /// <param name="count">要分配的元素数量。</param>
    /// <returns>指向分配内存的 Span。</returns>
    /// <exception cref="InvalidOperationException">剩余空间不足时抛出。</exception>
    public Span<T> Allocate<T>(int count) where T : struct
    {
        int bytes = count * Unsafe.SizeOf<T>();
        if (_offset + bytes > _size)
            throw new InvalidOperationException($"NativeArena exhausted: requested {bytes}, remaining {Remaining}");
        var span = new Span<T>((_ptr + _offset).ToPointer(), count);
        _offset += bytes;
        return span;
    }

    /// <summary>
    /// 从竞技场中分配指定字节数的内存空间。
    /// </summary>
    /// <param name="count">要分配的字节数。</param>
    /// <returns>指向分配内存的 Span。</returns>
    /// <exception cref="InvalidOperationException">剩余空间不足时抛出。</exception>
    public Span<byte> AllocateBytes(int count)
    {
        if (_offset + count > _size)
            throw new InvalidOperationException($"NativeArena exhausted: requested {count}, remaining {Remaining}");
        var span = new Span<byte>((_ptr + _offset).ToPointer(), count);
        _offset += count;
        return span;
    }

    /// <summary>
    /// 重置竞技场，将偏移量归零以便重新使用已分配的内存。
    /// </summary>
    public void Reset()
    {
        _offset = 0;
    }

    /// <summary>
    /// 释放竞技场占用的非托管内存。
    /// </summary>
    public void Dispose()
    {
        // ★ F4：Interlocked.Exchange 原子进入 —— 保证 Dispose 与 finalizer 并发时只有一个线程执行释放。
        //   修复前 bool _disposed 的 check-then-set 非原子，并发时双释放。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ptr != IntPtr.Zero)
        {
            unsafe { NativeMemory.Free(_ptr.ToPointer()); }
            _ptr = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 析构函数，确保在垃圾回收时释放非托管内存。
    /// </summary>
    ~NativeArena()
    {
        Dispose();
    }
}
