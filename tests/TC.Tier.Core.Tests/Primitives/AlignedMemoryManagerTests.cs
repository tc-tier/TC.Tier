
namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// AlignedMemoryManager 单元测试 —— 覆盖构造/Dispose/GetSpan/Pin/GetRef/rent状态。
/// ★ 不测 lockPhysicalMemory=true 路径（需要 SE_LOCK_MEMORY_NAME 特权）。
/// </summary>
public sealed class AlignedMemoryManagerTests
{
    [Fact]
    public void Constructor_ValidParams_AllocatesMemory()
    {
        using var mgr = new AlignedMemoryManager(1024, alignment: 4096);
        mgr.Size.Should().Be(1024);
        mgr.Alignment.Should().Be(4096);
        mgr.IsDisposed.Should().BeFalse();
        mgr.IsRented.Should().BeFalse();
        mgr.IsMemoryLocked.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ZeroSize_Throws()
    {
        Action act = () => _ = new AlignedMemoryManager(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Constructor_NonPowerOfTwoAlignment_Throws()
    {
        Action act = () => _ = new AlignedMemoryManager(1024, alignment: 1023);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetSpan_Full_ReturnsCorrectSize()
    {
        using var mgr = new AlignedMemoryManager(256);
        var span = mgr.GetSpan();
        span.Length.Should().Be(256);
    }

    [Fact]
    public void GetSpan_Offset_ReturnsRemainingBytes()
    {
        using var mgr = new AlignedMemoryManager(256);
        var span = mgr.GetSpan(128);
        span.Length.Should().Be(128);
    }

    [Fact]
    public void GetSpan_OffsetAndLength_ReturnsCorrectSlice()
    {
        using var mgr = new AlignedMemoryManager(256);
        var span = mgr.GetSpan(32, 64);
        span.Length.Should().Be(64);
    }

    [Fact]
    public void GetSpan_OffsetOutOfRange_Throws()
    {
        using var mgr = new AlignedMemoryManager(256);
        Action act = () => mgr.GetSpan(300);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetRef_ReturnsTypedReference()
    {
        using var mgr = new AlignedMemoryManager(16, zeroed: true);
        ref var value = ref mgr.GetRef<int>(0);
        value.Should().Be(0);
        value = 42;
        mgr.GetRef<int>(0).Should().Be(42);
    }

    [Fact]
    public void Pin_ReturnsMemoryHandle()
    {
        using var mgr = new AlignedMemoryManager(256);
        var handle = mgr.Pin(0);
        unsafe { ((IntPtr)handle.Pointer).Should().NotBe(IntPtr.Zero); }
        handle.Dispose();
    }

    [Fact]
    public void Pin_ElementIndexOutOfRange_Throws()
    {
        using var mgr = new AlignedMemoryManager(256);
        Action act = () => mgr.Pin(300);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Dispose_MarksAsDisposed()
    {
        var mgr = new AlignedMemoryManager(256);
        mgr.Dispose();
        mgr.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DoubleDispose_DoesNotThrow()
    {
        var mgr = new AlignedMemoryManager(256);
        mgr.Dispose();
        Action act = () => mgr.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void GetSpan_AfterDispose_Throws()
    {
        var mgr = new AlignedMemoryManager(256);
        mgr.Dispose();
        Action act = () => mgr.GetSpan();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void GetRef_AfterDispose_Throws()
    {
        var mgr = new AlignedMemoryManager(16);
        mgr.Dispose();
        Action act = () => { mgr.GetRef<int>(0); };
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void TryMarkRented_InitiallySucceeds()
    {
        using var mgr = new AlignedMemoryManager(256);
        mgr.IsRented.Should().BeFalse();
        bool result = mgr.TryMarkRented();
        result.Should().BeTrue();
        mgr.IsRented.Should().BeTrue();
    }

    [Fact]
    public void TryMarkRented_AlreadyRented_Fails()
    {
        using var mgr = new AlignedMemoryManager(256);
        mgr.TryMarkRented().Should().BeTrue();
        mgr.TryMarkRented().Should().BeFalse();
    }

    [Fact]
    public void TryMarkReturned_OnlyWorksWhenRented()
    {
        using var mgr = new AlignedMemoryManager(256);
        mgr.TryMarkReturned().Should().BeFalse(); // 未租出 → 失败
        mgr.TryMarkRented().Should().BeTrue();
        mgr.TryMarkReturned().Should().BeTrue();
        mgr.IsRented.Should().BeFalse();
    }

    [Fact]
    public void ResetForRent_ClearsMemory()
    {
        using var mgr = new AlignedMemoryManager(128);
        // 模拟池化流程：先租出，写入脏数据，归还，再 Reset
        mgr.TryMarkRented();
        mgr.GetSpanUnsafe(0, 128).Fill(0xFF);
        mgr.TryMarkReturned().Should().BeTrue();
        // 现在已归还，ResetForRent 应成功
        mgr.ResetForRent(zeroMemory: true);
        // 清零后前几个字节应为 0
        var span = mgr.GetSpan(0, 8);
        foreach (byte b in span) b.Should().Be(0);
    }

    [Fact]
    public void ResetForRent_AlreadyRented_Throws()
    {
        using var mgr = new AlignedMemoryManager(256);
        mgr.TryMarkRented().Should().BeTrue(); // 先租出
        Action act = () => mgr.ResetForRent(zeroMemory: true);
        act.Should().Throw<InvalidOperationException>(); // 已租出 → 抛异常
    }

    [Fact]
    public void ResetForRent_AfterDispose_Throws()
    {
        var mgr = new AlignedMemoryManager(256);
        mgr.Dispose();
        Action act = () => mgr.ResetForRent();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void GetSpanUnsafe_NoValidation_FastPath()
    {
        using var mgr = new AlignedMemoryManager(128);
        var span = mgr.GetSpanUnsafe(16, 32);
        span.Length.Should().Be(32);
        // 不抛异常即使 offset 超大
        mgr.GetSpanUnsafe(999, 1).Length.Should().Be(1);
    }

    [Fact]
    public void Ptr_NonZero_AfterAlloc()
    {
        using var mgr = new AlignedMemoryManager(64);
        unsafe { ((IntPtr)mgr.Ptr).Should().NotBe(IntPtr.Zero); }
    }

    [Fact]
    public void Alignment_4096_AddressAligned()
    {
        using var mgr = new AlignedMemoryManager(4096, alignment: 4096);
        unsafe { long addr = (long)mgr.Ptr; (addr % 4096).Should().Be(0, "NativeMemory.AlignedAlloc 应满足对齐"); }
    }
}
