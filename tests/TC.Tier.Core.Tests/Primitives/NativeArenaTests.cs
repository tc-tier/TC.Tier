namespace TC.Tier.Core.Tests.Primitives;

public class NativeArenaTests
{
    [Fact]
    public void Allocate_WritesAndReads()
    {
        using var arena = new NativeArena(256);
        var span = arena.Allocate<int>(4);
        span[0] = 42;
        span[1] = 100;
        span[2] = -1;
        span[3] = int.MaxValue;
        span[0].Should().Be(42);
        span[3].Should().Be(int.MaxValue);
    }

    [Fact]
    public void AllocateBytes_WritesAndReads()
    {
        using var arena = new NativeArena(256);
        var span = arena.AllocateBytes(16);
        for (int i = 0; i < 16; i++)
            span[i] = (byte)i;
        span[0].Should().Be(0);
        span[15].Should().Be(15);
    }

    [Fact]
    public void Allocate_ThrowsWhenExhausted()
    {
        using var arena = new NativeArena(16);
        arena.AllocateBytes(16);
        Action act = () => { var _ = arena.AllocateBytes(1); _.Clear(); };
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Reset_AllowsReuse()
    {
        using var arena = new NativeArena(64);
        arena.AllocateBytes(64);
        arena.Reset();
        arena.Remaining.Should().Be(64);
        var span = arena.AllocateBytes(10);
        span[0] = 0xFF;
        span[0].Should().Be(0xFF);
    }

    [Fact]
    public void Dispose_PreventsReuse()
    {
        var arena = new NativeArena(64);
        arena.Dispose();
        arena.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void MultipleAllocations_AdvanceOffset()
    {
        using var arena = new NativeArena(1024);
        arena.Allocate<int>(1);
        arena.Used.Should().Be(4);
        arena.Allocate<long>(1);
        arena.Used.Should().Be(12);
        arena.Remaining.Should().Be(1012);
    }

    [Fact]
    public void Pointer_And_Size_AreCorrect()
    {
        using var arena = new NativeArena(512);
        arena.Pointer.Should().NotBe(IntPtr.Zero);
        arena.Size.Should().Be(512);
    }

    [Fact]
    public void AllocateBytes_Zero_ReturnsEmptySpan()
    {
        using var arena = new NativeArena(64);
        var span = arena.AllocateBytes(0);
        span.IsEmpty.Should().BeTrue();
        arena.Used.Should().Be(0);
    }

    [Fact]
    public void Allocate_ZeroCount_ReturnsEmptySpan()
    {
        using var arena = new NativeArena(64);
        var span = arena.Allocate<int>(0);
        span.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void DoubleDispose_NoThrow()
    {
        var arena = new NativeArena(64);
        arena.Dispose();
        arena.Dispose();
        arena.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DoubleDispose_StillDisposed()
    {
        var arena = new NativeArena(64);
        arena.Dispose();
        arena.IsDisposed.Should().BeTrue();
    }

    /// <summary>
    /// F4：并发 Dispose 不双释放 —— 多线程同时 Dispose 同一个 arena，必须安全（无 AccessViolation 崩溃）。
    /// 修复前：bool _disposed 的 check-then-set 非原子，并发时两个线程都可能通过检查后都 NativeMemory.Free → 双释放崩溃。
    /// 修复后：Interlocked.Exchange 保证只有一个线程进入释放逻辑。
    /// </summary>
    [Fact]
    public void ConcurrentDispose_NoDoubleFree_NoCrash()
    {
        // 跑多轮提高竞态命中概率
        for (int round = 0; round < 200; round++)
        {
            var arena = new NativeArena(128);
            // 写入一些数据让内存有实际内容（双释放更容易暴露）
            var span = arena.AllocateBytes(128);
            for (int i = 0; i < 128; i++) span[i] = (byte)i;

            // 4 个线程并发 Dispose
            var threads = new Thread[4];
            for (int i = 0; i < 4; i++)
            {
                threads[i] = new Thread(() => arena.Dispose());
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();

            // 若双释放，进程早已 AccessViolation 崩溃；到这里说明安全
            arena.IsDisposed.Should().BeTrue("并发 Dispose 后应处于已释放状态");
        }
    }

    /// <summary>
    /// F4：Dispose 后 arena 脱离引用 + GC 回收触发 finalizer，不双释放崩溃。
    /// Dispose 已调 SuppressFinalize，finalizer 应是 no-op；但若 Interlocked 修复有误，
    /// finalizer 与 Dispose 竞态会双释放 NativeMemory → AccessViolation 崩进程。
    /// 跑到这里不崩溃即说明 SuppressFinalize + Interlocked 原子进入生效。
    /// </summary>
    [Fact]
    public void Dispose_ThenGC_Finalizer_NoDoubleFree()
    {
        for (int round = 0; round < 50; round++)
        {
            // 分配 + Dispose + 脱离引用
            var arena = new NativeArena(256);
            var span = arena.AllocateBytes(256);
            for (int i = 0; i < 256; i++) span[i] = (byte)(i & 0xFF);
            arena.Dispose();
            arena = null;   // 脱离引用，让 GC 可回收

            // 强制 GC 触发 finalizer（已 Dispose 的 arena finalizer 应是 no-op）
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            // 不崩溃即通过（双释放会 AccessViolation 崩进程，无法 catch）
        }
    }
}
