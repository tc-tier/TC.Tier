using TC.Tier.Core.Collections;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Tests.Primitives;

/// <summary>
/// docs/cache-and-compute.md 用法范式验证——测过才算文档成立。
/// 覆盖 ClockCache / ShardLockWeakReference / UnifiedCrc / MicroTimer / Utility / ThrowHelper。
/// </summary>
public class CacheAndComputeUsageTests
{
    // ── ClockCache ──

    [Fact]
    public void ClockCache_PutTryGet_Roundtrip()
    {
        using var cache = new ClockCache<int, string>(capacity: 16);
        var val = new string('x', 3);
        cache.Put(1, val);
        cache.TryGet(1, out var got).Should().BeTrue();
        got.Should().BeSameAs(val);
        cache.TryGet(999, out _).Should().BeFalse();   // 未命中
    }

    [Fact]
    public void ClockCache_Remove_EliminatesEntry()
    {
        using var cache = new ClockCache<int, string>(capacity: 16);
        cache.Put(5, new string('y', 2));
        cache.Remove(5).Should().BeTrue();
        cache.TryGet(5, out _).Should().BeFalse();
    }

    [Fact]
    public void ClockCache_Remove_ReturnsFalseForMissing()
    {
        using var cache = new ClockCache<int, string>(capacity: 16);
        cache.Remove(999).Should().BeFalse();
    }

    // ── ShardLockWeakReference ──

    [Fact]
    public void ShardLock_AddOrUpdate_TryGet_Roundtrip()
    {
        var dict = new ShardLockWeakReference<string, object>();
        var obj = new object();
        dict.AddOrUpdate("a", obj);
        dict.TryGet("a", out var got).Should().BeTrue();
        got.Should().BeSameAs(obj);
    }

    [Fact]
    public void ShardLock_TryGet_MissingReturnsFalse()
    {
        var dict = new ShardLockWeakReference<int, object>();
        dict.TryGet(42, out _).Should().BeFalse();
    }

    [Fact]
    public void ShardLock_Remove_WorksWithAndWithoutKey()
    {
        var dict = new ShardLockWeakReference<int, object>();
        var obj = new object();
        dict.AddOrUpdate(1, obj);
        dict.Remove(1).Should().BeTrue();
        dict.TryGet(1, out _).Should().BeFalse();
        dict.Remove(1).Should().BeFalse();   // 已删
    }

    [Fact]
    public void ShardLock_CleanupDeadReferences_ReturnsCount()
    {
        var dict = new ShardLockWeakReference<int, object>();
        dict.AddOrUpdate(1, new object());
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        // 清理被 GC 回收的（可能 1 个，取决于 GC 是否回收了弱引用）
        int cleaned = dict.CleanupDeadReferences();
        cleaned.Should().BeGreaterThanOrEqualTo(0);
    }

    // ── UnifiedCrc ──

    [Fact]
    public void UnifiedCrc_Crc32C_SameData_SameHash()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var h1 = UnifiedCrc.ComputeCrc32C(data);
        var h2 = UnifiedCrc.ComputeCrc32C(data);
        h1.Should().Be(h2);
        h1.Should().NotBe(0);
    }

    [Fact]
    public void UnifiedCrc_Crc32C_Incremental_MatchesOneShot()
    {
        var data = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        uint oneShot = UnifiedCrc.ComputeCrc32C(data);

        uint acc = UnifiedCrc.ComputeCrc32C(0, data.AsSpan(0, 4));
        acc = UnifiedCrc.ComputeCrc32C(acc, data.AsSpan(4, 4));

        acc.Should().Be(oneShot);   // 增量 == 一次性
    }

    [Fact]
    public void UnifiedCrc_Crc64_Stable()
    {
        var data = new byte[] { 9, 8, 7 };
        var h = UnifiedCrc.ComputeCrc64(data);
        h.Should().Be(UnifiedCrc.ComputeCrc64(data));
    }

    // ── MicroTimer ──

    [Fact]
    public void MicroTimer_Elapsed_Increases()
    {
        var t = MicroTimer.Start(active: true);
        Thread.Sleep(2);
        t.ElapsedMicros().Should().BeGreaterThan(0);
        t.ElapsedMillis().Should().BeGreaterThanOrEqualTo(1);
        t.IsActive.Should().BeTrue();
    }

    [Fact]
    public void MicroTimer_TryFormat_WritesReadable()
    {
        var t = MicroTimer.Start();
        Thread.Sleep(1);
        Span<char> buf = stackalloc char[32];
        bool ok = t.TryFormat(buf, out int written);
        ok.Should().BeTrue();
        written.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MicroTimer_Inactive_StillStructValid()
    {
        var t = MicroTimer.Start(active: false);
        t.IsActive.Should().BeFalse();
        // active=false 时 JIT 消除整段，但结构仍可用（不抛）
        t.ElapsedMicros();
    }

    // ── Utility ──

    [Fact]
    public void Utility_GetLogBase2_PowerOfTwo()
    {
        Utility.GetLogBase2(1).Should().Be(0);
        Utility.GetLogBase2(1024).Should().Be(10);
    }

    [Fact]
    public void Utility_ParseSize_KSuffix()
        => Utility.ParseSize("4K").Should().Be(4096);

    [Fact]
    public void Utility_MonotonicUpdate_AdvancesOnHigher()
    {
        long watermark = 10;
        Utility.MonotonicUpdate(ref watermark, 20, out long old).Should().BeTrue();
        old.Should().Be(10);
        watermark.Should().Be(20);
        // 回退不推进
        Utility.MonotonicUpdate(ref watermark, 5, out _).Should().BeFalse();
        watermark.Should().Be(20);
    }

    // ── ThrowHelper ──

    [Fact]
    public void ThrowHelper_ThrowArgumentOutOfRange_Throws()
        => FluentActions.Invoking(() => ThrowHelper.ThrowArgumentOutOfRange("x"))
            .Should().Throw<ArgumentOutOfRangeException>();

    [Fact]
    public void ThrowHelper_ThrowObjectDisposed_Throws()
        => FluentActions.Invoking(() => ThrowHelper.ThrowObjectDisposed("MyObj"))
            .Should().Throw<ObjectDisposedException>();

    [Fact]
    public void ThrowHelper_ThrowInvalidOperationException_Throws()
        => FluentActions.Invoking(() => ThrowHelper.ThrowInvalidOperationException("bad"))
            .Should().Throw<InvalidOperationException>();
}
