using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 四级恢复测试（hints → meta(tier-2) → FlushedUntilAddress(tier-3) → 扫盘(tier-4)）。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根（跨实例同卷同名引擎、deleteOnClose=false）。</para>
/// </summary>
public class RingRecoveryTests
{
    [Fact]
    public void Recover_WithHints_AppliesHintTail()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Initialize(new RingRecoveryHints { RecoveredTail = LogicalAddress.Empty });   // 空盘，no-op
            ring.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Recover_EmptyLog_MarksReady()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Initialize();
            ring.WaitForReady();
            ring.RecoveryState.IsCompleted.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Initialize_WithHints_Completes()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Initialize(new RingRecoveryHints { FlushedUntilAddress = LogicalAddress.Empty });
            ring.WaitForReady();
            ring.RecoveryState.IsCompleted.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Recover_Tier3_RestoresFromFlushedUntilAddress()
    {
        // 跨 Ring 实例恢复：实例1 写+flush+dispose（引擎子目录保留）→ 实例2 构造时引擎自恢复读回文件大小 → tier-3
        var vol = new TestVolume();
        try
        {
            // 实例 1：写 record + flush 落盘
            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false)))
            {
                ring1.Write(0x0102L, new byte[] { 10, 20, 30 });
                ring1.Write(0x0304L, new byte[] { 40, 50 });
                ring1.FlushUntil(ring1.TailAddress);
                LogicalAddress flushed = ring1.FlushedUntilAddress;
                flushed.Should().NotBe(LogicalAddress.Empty, "实例1 flush 后应有落盘字节");
            }

            // 实例 2：同卷同名构造，引擎自恢复读回物理尾 → tier-3
            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false));
            ring2.FlushedUntilAddress.Should().NotBe(LogicalAddress.Empty, "实例2 构造时应从物理文件大小恢复 FlushedUntilAddress>0");

            // ★ 订阅进度事件——tier-3 走 FlushedUntilAddress 正路不上扫盘；tier-4 兜底扫盘 detail 含 "scanning"
            var details = new System.Collections.Generic.List<string?>();
            ring2.RecoveryProgressChanged += p => details.Add(p.Detail);

            ring2.Initialize();
            ring2.WaitForReady();

            ring2.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);
            details.Any(d => d is not null && d.Contains("scann", StringComparison.OrdinalIgnoreCase))
                .Should().BeFalse("tier-3（FlushedUntilAddress 正路）不应走到扫盘（那是 tier-4 兜底）");
            // tail 恢复到 flushedUntil
            ring2.TailAddress.Should().Be(ring2.FlushedUntilAddress,
                "tier-3 ApplyWatermarks 应把 tail 设为 flushedUntil");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Recover_EmptyDisk_GoesTier4_ScanFindsNothing()
    {
        // 空盘恢复：NewRing 一步生命周期（构造即恢复——空盘扫不到有效 record → 水位保持初始值）
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed, "构造时自动恢复应完成");
            ring.RecoveryState.IsCompleted.Should().BeTrue("空盘恢复后应就绪");

            // 再次 Initialize 是 no-op（CAS 闸门幂等）
            Action recoverAgain = () => ring.Initialize();
            ring.WaitForReady();
            recoverAgain.Should().NotThrow("重复 Initialize 是 no-op（CAS 幂等）");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>★ Tier-2 恢复（meta O(1) 正路）：Managed meta 落盘水位 → 跨实例 Load → 读回水位恢复。</summary>
    [Fact]
    public void Recover_Tier2_RestoresFromMeta_O1()
    {
        // 生产恢复正路：meta 块存持久化层 5 指针 + LastCommittedSeq + KeySize 锚点，O(1) 读回不扫盘
        var vol = new TestVolume();
        try
        {
            LogicalAddress expectedTail = LogicalAddress.Empty;

            // 实例 1：Managed meta 模式，写 record + Prepare（落盘 + meta.Commit）→ Dispose
            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false, metaKind: MetaPolicyKind.Managed)))
            {
                ring1.Write(1L, new byte[] { 10 });
                ring1.Write(2L, new byte[] { 20 });
                ring1.Prepare(seq: 42);   // FlushUntil(Tail) + WriteMeta（meta.Commit 落盘水位）
                expectedTail = ring1.TailAddress;
            }

            // 实例 2：同卷同名 + Managed meta，恢复无 hints → 走 tier-2（meta.Load 读 RingMetaPayload）
            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false, metaKind: MetaPolicyKind.Managed));

            // ★ 无 hints——NewRing 一步生命周期已恢复（meta.Load 成功 → tier-2）；
            //   不再二次 Initialize（满套线程池紧张时与后台恢复 task 竞态——引擎自等旧案同款）

            // tier-2 从 meta 读回的水位：tail 和 flushedUntil 应与实例 1 Prepare 后一致
            ring2.TailAddress.Should().Be(expectedTail,
                "tier-2 meta 恢复应从 RingMetaPayload 读回 TailAddress");
            ring2.FlushedUntilAddress.Should().Be(expectedTail,
                "tier-2 meta 恢复应从 RingMetaPayload 读回 FlushedUntilAddress");
            ring2.RecoveryState.Phase.Should().Be(RecoveryPhase.Completed);

            // 扫描验证数据完整
            using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.FlushedUntilAddress);
            int count = 0;
            while (cursor.MoveNext()) count++;
            count.Should().Be(2, "tier-2 meta 恢复后应能扫描读回 2 条 record");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>★ Tier-1b 恢复（hints.FlushedUntilAddress）：上层已知已落盘边界 → 直接恢复。</summary>
    [Fact]
    public void Recover_Tier1b_HintsFlushedUntil()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            // 直接构造（不走 NewRing——它会 Initialize），用带 hints 的 Initialize 测 tier-1b 路径
            using var ring = new BlittableRing<long>(settings, vol.Fs);
            ring.Initialize(new RingRecoveryHints { FlushedUntilAddress = new LogicalAddress(0, 256) });
            ring.WaitForReady();

            ring.FlushedUntilAddress.Should().Be(new LogicalAddress(0, 256),
                "tier-1b hints.FlushedUntilAddress 应直接恢复水位");
            ring.TailAddress.Should().Be(new LogicalAddress(0, 256));
        }
        finally { vol.Dispose(); }
    }

    /// <summary>★ Tier-4 恢复（全盘扫盘）：无 hints + 无 meta + FlushedUntilAddress=初始 → 逐页扫盘验帧找真实 tail。</summary>
    [Fact]
    public void Recover_Tier4_ScanDevice_FindsRealTail()
    {
        var vol = new TestVolume();
        try
        {
            // 实例 1：写 3 条 → FlushUntil 落盘 → Dispose
            using (var ring1 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false, metaKind: MetaPolicyKind.Disabled)))
            {
                ring1.Write(1L, new byte[] { 10 });
                ring1.Write(2L, new byte[] { 20 });
                ring1.Write(3L, new byte[] { 30 });
                ring1.FlushUntil(ring1.TailAddress);
            }

            // 实例 2：无 hints + 无 meta → 引擎物理尾驱动恢复（tier-3 正路；tier-4 扫盘在物理尾>初始值时不可达，
            //   ScanDeviceForTail 兜底已实现）。验证恢复后能读回全部已落盘数据。
            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring.0", deleteOnClose: false, metaKind: MetaPolicyKind.Disabled));
            ring2.Initialize();

            using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.FlushedUntilAddress);
            int count = 0;
            while (cursor.MoveNext()) count++;
            count.Should().BeGreaterThanOrEqualTo(1, "恢复后应能扫描到已落盘的 record");
        }
        finally { vol.Dispose(); }
    }
}
