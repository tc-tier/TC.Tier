using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 崩溃耐久性测试——验证热区数据的落盘保障。
/// <para>★★ Ring 的崩溃语义（混合日志 WAL）：</para>
/// <para>- [BeginAddress, FlushedUntilAddress)：已落盘（FlushUntil + device.Flush fsync）——崩溃后可恢复</para>
/// <para>- [FlushedUntilAddress, TailAddress)：mutable 区——只在内存页池，崩溃丢</para>
/// <para>- Write 不自动 flush——耐久性依赖显式 FlushUntil / Prepare（2PC）</para>
/// <para>- Dispose 先 FlushUntil(TailAddress) 落盘 mutable 区再释放页内存（防数据丢失）</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根（跨实例同卷同名引擎）+ hints 驱动恢复
///   （生产恢复走 tier-4 扫盘，本测试用 hints 简化）。</para>
/// </summary>
public class RingCrashDurabilityTests
{
    /// <summary>FlushUntil 真正落盘——跨实例恢复能读回全部已 flush 的数据。</summary>
    [Fact]
    public void FlushUntil_MakesDataDurable_CrossInstance()
    {
        var vol = new TestVolume();
        try
        {
            LogicalAddress flushedTo = LogicalAddress.Empty;

            // 实例 1：写 2 条 → FlushUntil（落盘 + fsync）→ Dispose
            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol)))
            {
                ring1.Write(1L, new byte[] { 10 });
                ring1.Write(2L, new byte[] { 20 });
                ring1.FlushUntil(ring1.TailAddress);
                flushedTo = ring1.FlushedUntilAddress;
                (flushedTo > ring1.BeginAddress).Should().BeTrue("FlushUntil 后 FlushedUntilAddress 推进");
            }

            // 实例 2：用 hints 恢复到 flushedTo，扫描应读回 2 条
            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol));
            ring2.Initialize(new RingRecoveryHints { FlushedUntilAddress = flushedTo });
            ring2.WaitForReady();

            using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.FlushedUntilAddress);
            int count = 0;
            while (cursor.MoveNext()) count++;
            count.Should().Be(2, "FlushUntil 落盘的数据跨实例恢复应全部读回");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Prepare（2PC）落盘保障——写一批 → Prepare → Dispose → 恢复 → 全部读回。</summary>
    [Fact]
    public void Crash_AfterPrepare_AllDataSurvives()
    {
        var vol = new TestVolume();
        try
        {
            LogicalAddress preparedTail = LogicalAddress.Empty;

            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol)))
            {
                ring1.Write(1L, new byte[] { 10 });
                ring1.Write(2L, new byte[] { 20 });
                ring1.Write(3L, new byte[] { 30 });
                ring1.Prepare(seq: 1);   // FlushUntil(Tail) + WriteMeta → 全部落盘 + fsync
                preparedTail = ring1.TailAddress;
                ring1.FlushedUntilAddress.Should().Be(preparedTail, "Prepare 后全部落盘");
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol));
            ring2.Initialize(new RingRecoveryHints { FlushedUntilAddress = preparedTail });
            ring2.WaitForReady();

            using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.FlushedUntilAddress);
            int count = 0;
            while (cursor.MoveNext()) count++;
            count.Should().Be(3, "Prepare 落盘的 3 条 record 崩溃后应全部恢复");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Write 不自动 flush 用户数据——不调 FlushUntil/Prepare 时新写的 record 不进 FlushedUntilAddress。</summary>
    [Fact]
    public void Write_DoesNotAutoFlush_FlushedUntilStaysAtInitial()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(1L, new byte[] { 100 });
            // ★ 新模型：构造/首次写可能把 ring 头部区（addr 之前）落盘（FlushedUntilAddress 推进到 addr），
            //   但用户 record（addr..TailAddress）仍在 mutable 区、未落盘——FlushedUntilAddress 不应覆盖到 Tail。
            //   即"Write 不自动 flush 用户数据"的语义：record 区 [addr, Tail) 不在 FlushedUntilAddress 之内。
            ring.FlushedUntilAddress.Should().BeLessThanOrEqualTo(addr,
                "FlushedUntilAddress 至多到用户 record 起点（头部区落盘），不应越过 record 进 mutable 区");
            ring.FlushedUntilAddress.Should().BeLessThan(ring.TailAddress,
                "新写的 record（[FlushedUntilAddress, TailAddress)）仍在 mutable 区未落盘——Write 不自动 flush 用户数据");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>Dispose 落盘 mutable 区——Dispose 后再恢复能读回 Dispose 前写的数据。</summary>
    [Fact]
    public void Dispose_FlushesMutableRegion_DataSurvives()
    {
        var vol = new TestVolume();
        try
        {
            LogicalAddress tailBeforeDispose = LogicalAddress.Empty;

            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol)))
            {
                ring1.Write(1L, new byte[] { 10 });
                ring1.Write(2L, new byte[] { 20 });
                // ★ 不显式 FlushUntil——Dispose 应自动落盘 mutable 区
                tailBeforeDispose = ring1.TailAddress;
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol));
            // Dispose 落盘了全部（FlushUntil(TailAddress)），恢复到 tailBeforeDispose
            ring2.Initialize(new RingRecoveryHints { FlushedUntilAddress = tailBeforeDispose });
            ring2.WaitForReady();

            using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.FlushedUntilAddress);
            int count = 0;
            while (cursor.MoveNext()) count++;
            count.Should().Be(2, "Dispose 应落盘 mutable 区数据，恢复后全部读回");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>★ DIO 模式（hints=NoBuffering）崩溃耐久性——生产默认配置，必须验证。</summary>
    /// <para>DIO 绕过 OS page cache，数据从 Ring 页池直达磁盘，避免双重缓存。
    /// mem 介质探测结果 Ignored（对齐路径仍生效）；真磁盘介质下走真 DIO。</para>
    [Fact]
    public void Crash_DioMode_DataDurable_CrossInstance()
    {
        var vol = new TestVolume();
        try
        {
            LogicalAddress flushedTo = LogicalAddress.Empty;

            // 实例 1：DIO 模式写 + FlushUntil + Dispose
            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol, directIo: true)))
            {
                ring1.Write(1L, new byte[] { 10 });
                ring1.Write(2L, new byte[] { 20 });
                ring1.FlushUntil(ring1.TailAddress);
                flushedTo = ring1.FlushedUntilAddress;
                (flushedTo > ring1.BeginAddress).Should().BeTrue();
            }

            // 实例 2：DIO 模式恢复
            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, DurabilitySettings(vol, directIo: true));
            ring2.Initialize(new RingRecoveryHints { FlushedUntilAddress = flushedTo });
            ring2.WaitForReady();

            using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.FlushedUntilAddress);
            int count = 0;
            while (cursor.MoveNext()) count++;
            count.Should().Be(2, "DIO 模式 FlushUntil 落盘的数据跨实例恢复应全部读回");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>测试专用 settings：跨实例保留（deleteOnClose=false）+ Disabled meta（强制 hints/tier-4 恢复路径）。</summary>
    private static BlittableRingSettings DurabilitySettings(TestVolume vol, bool directIo = false)
        => TestRingSettingsFactory.On(vol, "ring.0",
            hints: directIo ? FileOpenHints.NoBuffering : FileOpenHints.None,
            deleteOnClose: false, metaKind: MetaPolicyKind.Disabled);
}
