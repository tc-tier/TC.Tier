using TC.Tier.Core.Collections;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// docs/memory.md 用法范式验证——测过才算文档成立。
/// 覆盖 AlignedMemoryManager / NativeArena 的核心用法（PinnedBufferPool/OverflowPool 各有专门测试文件，此处不重复）。
/// </summary>
public class MemoryPrimitivesUsageTests
{
    // ── AlignedMemoryManager ──

    [Fact]
    public void AlignedMemoryManager_GetSpan_ReadWrite_Roundtrip()
    {
        using var mem = new AlignedMemoryManager(size: 128, alignment: AlignmentConst.Alignment4K);
        var span = mem.GetSpan();
        span[0] = 0xAB;
        span[127] = 0xCD;
        span[0].Should().Be(0xAB);
        span[127].Should().Be(0xCD);
    }

    [Fact]
    public void AlignedMemoryManager_GetSpan_OffsetLength_SlicesCorrectly()
    {
        // zeroed=true：确保切片外区域确定（验证 zeroed 开关有效）
        using var mem = new AlignedMemoryManager(size: 64, zeroed: true);
        var part = mem.GetSpan(offset: 4, length: 8);
        part.Length.Should().Be(8);
        part.Fill(0x42);
        mem.GetSpan()[4].Should().Be(0x42);
        mem.GetSpan()[11].Should().Be(0x42);
        mem.GetSpan()[12].Should().Be(0);   // 切片之外，zeroed 保证为 0
    }

    [Fact]
    public void AlignedMemoryManager_GetRef_StrongType_Roundtrip()
    {
        using var mem = new AlignedMemoryManager(size: 32);
        ref long slot = ref mem.GetRef<long>(offset: 0);
        slot = 0x1234_5678_9ABC_DEF0;
        slot.Should().Be(0x1234_5678_9ABC_DEF0);
    }

    [Fact]
    public void AlignedMemoryManager_Unsafe_MatchesSafe()
    {
        using var mem = new AlignedMemoryManager(size: 64);
        mem.GetSpanUnsafe(0, 4).Fill(0x11);
        mem.GetRefUnsafe<int>(0).Should().Be(0x11111111);
    }

    [Fact]
    public void AlignedMemoryManager_Dispose_MakesIsDisposedTrue()
    {
        var mem = new AlignedMemoryManager(size: 16);
        mem.IsDisposed.Should().BeFalse();
        mem.Dispose();
        mem.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void AlignedMemoryManager_BadAlignment_Throws()
        => FluentActions.Invoking(() => new AlignedMemoryManager(size: 16, alignment: 0))
            .Should().Throw<ArgumentOutOfRangeException>();
    // alignment=0 非正数 → ThrowHelper.ThrowArgumentOutOfRange

    [Fact]
    public void AlignedMemoryManager_BadOffset_Throws()
    {
        using var mem = new AlignedMemoryManager(size: 16);
        FluentActions.Invoking(() => mem.GetSpan(offset: 32))
            .Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AlignedMemoryManager_MemoryNotZeroed_ByDefault()
    {
        // 默认 zeroed=false——契约只保证分配成功，不保证内容（调用方须自填）
        using var mem = new AlignedMemoryManager(size: 16, zeroed: false);
        // 无法断言具体值（非确定），但应能正常 GetSpan 不抛
        var s = mem.GetSpan();
        s.Length.Should().Be(16);
    }

    // ── NativeArena ──

    [Fact]
    public void NativeArena_Allocate_BumpAdvancesOffset()
    {
        using var arena = new NativeArena(size: 1024);
        arena.Used.Should().Be(0);
        var a = arena.Allocate<int>(count: 10);   // 40 bytes
        arena.Used.Should().Be(40);
        var b = arena.AllocateBytes(count: 8);
        arena.Used.Should().Be(48);
        a.Length.Should().Be(10);
        b.Length.Should().Be(8);
    }

    [Fact]
    public void NativeArena_Allocate_Exhausted_Throws()
    {
        using var arena = new NativeArena(size: 16);
        arena.AllocateBytes(16);                   // 用满
        FluentActions.Invoking(() => arena.AllocateBytes(1))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void NativeArena_Reset_ReusesMemory()
    {
        using var arena = new NativeArena(size: 256);
        arena.Allocate<int>(count: 10);
        arena.Used.Should().Be(40);
        arena.Reset();
        arena.Used.Should().Be(0);
        arena.Remaining.Should().Be(256);
        // Reset 后可重新分配
        arena.AllocateBytes(64);
        arena.Used.Should().Be(64);
    }

    [Fact]
    public void NativeArena_Allocate_StrongType_WritesBack()
    {
        using var arena = new NativeArena(size: 64);
        var nums = arena.Allocate<long>(count: 4);
        nums[0] = 0x0F0F0F0F0F0F0F0F;
        nums[3] = -1;
        nums[0].Should().Be(0x0F0F0F0F0F0F0F0F);
        nums[3].Should().Be(-1);
    }

    [Fact]
    public void NativeArena_Dispose_MakesIsDisposedTrue()
    {
        var arena = new NativeArena(size: 32);
        arena.IsDisposed.Should().BeFalse();
        arena.Dispose();
        arena.IsDisposed.Should().BeTrue();
    }

    // ── PinnedBufferPool（范式验证；完整测试见 PinnedBufferPoolTests）──

    [Fact]
    public void PinnedBufferPool_RentReturn_RoundtripsAndReuses()
    {
        using var pool = new PinnedBufferPool(maxPerBucket: 8);
        var buf = pool.Rent(size: 100);    // 取整到 128
        buf.Length.Should().BeGreaterThanOrEqualTo(100);
        buf[0] = 0x77;
        pool.Return(buf);
        // 归还后应能再次租到（命中缓存）
        var buf2 = pool.Rent(size: 100);
        buf2.Should().NotBeNull();
        pool.Return(buf2);
    }

    [Fact]
    public void PinnedBufferPool_RentAligned_ReturnsUsableManager()
    {
        using var pool = new PinnedBufferPool();
        var mem = pool.RentAligned(size: 512, alignment: AlignmentConst.Alignment4K);
        mem.Should().NotBeNull();
        var span = mem.GetSpan();
        span.Length.Should().BeGreaterThanOrEqualTo(512);
        span[0] = 0x33;
        span[0].Should().Be(0x33);
        pool.ReturnAligned(mem);
    }

    // ── OverflowPool（范式验证；完整测试见 OverflowPoolTests）──

    [Fact]
    public void OverflowPool_TryGetEmpty_Misses_TryAddReuses()
    {
        var disposed = 0;
        using var pool = new OverflowPool<string>(size: 2, disposer: _ => disposed++);
        // 空：未命中
        pool.TryGet(out var item).Should().BeFalse();
        pool.Misses.Should().Be(1);
        // 归还 2 个（填满，count=2=size）
        pool.TryAdd("a").Should().BeTrue();
        pool.TryAdd("b").Should().BeTrue();
        // 满：第 3 个被 disposer 回收（容量 2）
        pool.TryAdd("c").Should().BeFalse();
        disposed.Should().Be(1);
        // 取出 1 个（count=1），空出槽位 → 再加可入池
        pool.TryGet(out item).Should().BeTrue();
        pool.Hits.Should().Be(1);
        pool.TryAdd("d").Should().BeTrue();
    }
}
