using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// ReSharper disable once CheckNamespace
namespace TC.Tier.Core.Primitives;

/// <summary>
/// 对齐原生内存管理器，适配 O_DIRECT 等硬件对齐要求。
/// 继承 <see cref="MemoryManager{T}"/> 以兼容 .NET 异步 IO 生态。
/// </summary>
public sealed unsafe class AlignedMemoryManager : MemoryManager<byte>
{
    private IntPtr _ptr;

    // 0 = 空闲（未租出），1 = 已租出
    private int _rentState;

    // 0 = 未锁定物理内存，1 = 已锁定物理内存
    private int _locked;

    /// <summary>是否已被租出（仅作为状态展示，勿用于同步）</summary>
    public bool IsRented => Volatile.Read(ref _rentState) == 1;

    /// <summary>是否已锁定到物理内存（禁止swap换出）</summary>
    public bool IsMemoryLocked => Volatile.Read(ref _locked) == 1;

    /// <summary>内存总字节数</summary>
    public int Size { get; }

    /// <summary>物理对齐字节数</summary>
    public int Alignment { get; }

    /// <summary>
    /// 归属池实例 ID（0 表示非池分配）。由 <see cref="PinnedBufferPool"/> 在 RentAligned 时写入，
    /// ReturnAligned 时校验，防止外部 buffer 误归还污染桶或引发重复释放。纳秒级整数比较。
    /// </summary>
    internal int PoolId;

    /// <summary>是否已释放</summary>
    public bool IsDisposed => Volatile.Read(ref _ptr) == IntPtr.Zero;

    /// <summary>
    /// 获取原生指针，供 unsafe 直接访问内存。
    /// </summary>
    public void* Ptr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (void*)_ptr;
    }

    /// <summary>
    /// 获取原生 byte* 指针（同程序集 hot path 用，不走 Span 分配）。
    /// </summary>
    public byte* BytePtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (byte*)_ptr;
    }

    /// <summary>
    /// 内部 void* 指针，统一 RawPtr 和 BytePtr 的底层转换。
    /// </summary>
    private void* RawPtr
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (void*)_ptr;
    }

    /// <summary>
    /// 构造函数，分配指定大小和对齐的原生内存。
    /// </summary>
    /// <param name="size">需要分配的逻辑字节数</param>
    /// <param name="alignment">物理对齐要求，必须为正且是 2 的幂</param>
    /// <param name="zeroed">是否清零内存。大部分调用方构造后立即覆写，建议 false；安全敏感场景开启。</param>
    /// <param name="lockPhysicalMemory">是否锁定到物理内存（禁止swap，低延迟IO场景开启）</param>
    public AlignedMemoryManager(int size, int alignment = AlignmentConst.Alignment4K, bool zeroed = false, bool lockPhysicalMemory = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
            ThrowHelper.ThrowArgumentOutOfRange(nameof(alignment));

        Size = size;
        Alignment = alignment;
        _ptr = (IntPtr)NativeMemory.AlignedAlloc((nuint)size, (nuint)alignment);

        if (lockPhysicalMemory)
        {
            if ((uint)alignment < (uint)Environment.SystemPageSize)
            {
                NativeMemory.AlignedFree(RawPtr);
                _ptr = IntPtr.Zero;
                ThrowHelper.ThrowArgumentOutOfRange(nameof(alignment),
                    $"锁定物理内存要求对齐值不小于系统页大小({Environment.SystemPageSize}字节)");
            }

            if (!MemoryNative.LockMemory(RawPtr, (nuint)size))
            {
                int errorCode = Marshal.GetLastPInvokeError();
                NativeMemory.AlignedFree(RawPtr);
                _ptr = IntPtr.Zero;
                ThrowHelper.ThrowInvalidOperationException(
                    $"物理内存锁定失败（VirtualLock/MLock 返回错误码 {errorCode}）。" +
                    $"可能原因：权限不足（Linux 需 CAP_IPC_LOCK / 调高 RLIMIT_MEMLOCK）或锁定总量超限。");
            }
            Volatile.Write(ref _locked, 1);
        }

        if (zeroed)
            new Span<byte>(RawPtr, size).Clear();
    }

    #region 原子状态转换（供池使用）
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryMarkRented() =>
        Interlocked.CompareExchange(ref _rentState, 1, 0) == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryMarkReturned() =>
        Interlocked.CompareExchange(ref _rentState, 0, 1) == 1;
    #endregion

    #region 切片与强类型访问
    /// <summary>
    /// 获取整个内存切片（含 ThrowIfDisposed，仅对外接口使用）。
    /// Hot path 用 <see cref="GetSpanUnsafe"/> 走零校验通道。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override Span<byte> GetSpan()
    {
        ThrowIfDisposed();
        return new Span<byte>(BytePtr, Size);
    }

    /// <summary>
    /// 获取指定偏移的内存切片（含校验，仅对外接口使用）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int offset)
    {
        ThrowIfDisposed();
        if ((uint)offset > (uint)Size)
            ThrowHelper.ThrowArgumentOutOfRange();
        return new Span<byte>(BytePtr + offset, Size - offset);
    }

    /// <summary>
    /// 获取指定偏移和长度的内存切片（含校验，仅对外接口使用）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpan(int offset, int length)
    {
        ThrowIfDisposed();
        ValidateOffsetAndLength(offset, length);
        return new Span<byte>(BytePtr + offset, length);
    }

    /// <summary>
    /// 获取指定偏移的强类型引用（含校验，仅对外接口使用）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRef<T>(int offset) where T : struct
    {
        ThrowIfDisposed();
        if ((uint)offset > (uint)Size || (uint)(Size - offset) < (uint)Unsafe.SizeOf<T>())
            ThrowHelper.ThrowArgumentOutOfRange(nameof(offset));
        return ref Unsafe.As<byte, T>(ref Unsafe.AddByteOffset(ref *BytePtr, offset));
    }

    /// <summary>
    /// 获取指定偏移和长度的 Span（零校验，hot path 专用）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Span<byte> GetSpanUnsafe(int offset, int length) =>
        new(BytePtr + offset, length);

    /// <summary>
    /// 获取强类型引用（零校验，hot path 专用）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetRefUnsafe<T>(int offset) where T : struct =>
        ref Unsafe.As<byte, T>(ref Unsafe.AddByteOffset(ref *BytePtr, offset));
    #endregion

    #region MemoryManager 核心
    /// <summary>固定内存（O_DIRECT 缓冲本就驻留，返回直接指向内存的句柄，零额外 pin）。</summary>
    /// <param name="elementIndex">起始字节偏移。</param>
    /// <returns>指向 <paramref name="elementIndex"/> 处字节的内存句柄。</returns>
    /// <exception cref="ObjectDisposedException">管理器已释放。</exception>
    /// <exception cref="ArgumentOutOfRangeException">elementIndex 越界。</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override MemoryHandle Pin(int elementIndex = 0)
    {
        ThrowIfDisposed();
        if ((uint)elementIndex >= (uint)Size)
            ThrowHelper.ThrowArgumentOutOfRange();
        return new MemoryHandle(BytePtr + elementIndex);
    }

    /// <summary>取消固定 —— 空操作（内存始终固定，Pin 未登记任何额外状态）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override void Unpin() { }

    /// <summary>释放原生内存（解除物理内存锁定后 AlignedFree；幂等，线程安全）。</summary>
    /// <param name="disposing">保留参数（本类无托管资源，两路径同行为）。</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected override void Dispose(bool disposing)
    {
        var ptr = Interlocked.Exchange(ref _ptr, IntPtr.Zero);
        if (ptr == IntPtr.Zero) return;

        if (Interlocked.Exchange(ref _locked, 0) == 1)
            MemoryNative.UnlockMemory((void*)ptr, (nuint)Size);

        NativeMemory.AlignedFree((void*)ptr);
        Volatile.Write(ref _rentState, 0);
    }
    #endregion

    #region 标准 Dispose 模式
    /// <summary>释放内存管理器（解除物理内存锁定并释放对齐内存；幂等）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        Dispose(true);
    }
    #endregion

    #region 池化内部方法
    /// <summary>
    /// 重置内存管理器状态以便再次租出，支持可选清零。
    /// </summary>
    /// <param name="zeroMemory">true 全量清零（忽略 clearBytes）。</param>
    /// <param name="clearBytes">当 zeroMemory=false 时，仅清零前 N 字节。0=不清零。</param>
    public void ResetForRent(bool zeroMemory = false, int clearBytes = 0)
    {
        ThrowIfDisposed();
        if (!TryMarkRented())
            ThrowHelper.ThrowInvalidOperationException("Buffer is already rented or disposed.");

        if (!zeroMemory && clearBytes <= 0)
            return;

        if (zeroMemory)
            GetSpanUnsafe(0, Size).Clear();
        else
            GetSpanUnsafe(0, Math.Min(clearBytes, Size)).Clear();
    }
    #endregion

    #region 辅助方法
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _ptr) == IntPtr.Zero)
            ThrowHelper.ThrowObjectDisposed(nameof(AlignedMemoryManager));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ValidateOffsetAndLength(int offset, int length)
    {
        if ((uint)offset > (uint)Size || (uint)length > (uint)(Size - offset))
            ThrowHelper.ThrowArgumentOutOfRange();
    }
    #endregion
}
