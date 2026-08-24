using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring EnsureReady 门控测试——验证"构造即恢复 + Initialize 后读写"的语义。
/// <para>★ NewRing 一步生命周期（Initialize + WaitForReady）后 EnsureReady 放行读写。</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt;。</para>
/// </summary>
public class RingReadyGuardTests
{
    /// <summary>构造后自动恢复完成——读写正常，不抛 EnsureReady 异常。</summary>
    [Fact]
    public void Construct_AutoRecovers_ReadWriteSucceeds()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            // 构造即恢复——读写不应抛 EnsureReady 异常
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20 });
            addr.Should().NotBe(LogicalAddress.Empty);

            var rec = ring.GetKey(addr);
            rec.ValueLength.Should().Be(2);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>构造后 OpenScanCursor 正常——EnsureReady 放行。</summary>
    [Fact]
    public void Construct_AutoRecovers_OpenScanCursorSucceeds()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[] { 10 });

            using var cursor = ring.OpenScanCursor();
            cursor.MoveNext().Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Initialize 后应恢复就绪——写不抛异常。</summary>
    [Fact]
    public void Initialize_AfterConstruct_WriteSucceeds()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Initialize();
            ring.WaitForReady();

            Action write = () => ring.Write(1L, new byte[] { 10 });
            write.Should().NotThrow("Initialize 后 EnsureReady 应放行写");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Initialize 后应恢复就绪——读不抛异常。</summary>
    [Fact]
    public void Initialize_AfterConstruct_ReadSucceeds()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[] { 10 });
            ring.Initialize();
            ring.WaitForReady();

            Action read = () => ring.GetKey(new LogicalAddress(0, 100));
            read.Should().NotThrow("Initialize 后 EnsureReady 应放行读");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Initialize 后应恢复就绪——OpenScanCursor 不抛异常。</summary>
    [Fact]
    public void Initialize_AfterConstruct_OpenScanCursorSucceeds()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[] { 10 });
            ring.Initialize();
            ring.WaitForReady();

            Action scan = () => ring.OpenScanCursor();
            scan.Should().NotThrow("Initialize 后 EnsureReady 应放行扫描");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Initialize(hints) → 读写恢复正常。</summary>
    [Fact]
    public void Initialize_WithHints_ReadWriteSucceeds()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 10, 20 });
            LogicalAddress tail = ring.TailAddress;

            ring.Initialize(new RingRecoveryHints { RecoveredTail = tail });   // 用 hints 恢复

            // 恢复后读写正常
            LogicalAddress addr2 = ring.Write(2L, new byte[] { 30 });
            addr2.Should().BeGreaterThan(addr);
        }
        finally { vol.Dispose(); }
    }
}
