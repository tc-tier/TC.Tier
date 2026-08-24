using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 2PC Abort 测试（D2 落地）——回退到上一已确认提交边界（CommittedTailAddress）。
/// <para>★ 覆盖：Abort 尾截断（引擎回收+页池清零）/ 陈旧 seq 仅复位 / 已提交 no-op /
///   首事务无边界不截断 / 截断后可继续写（回退区重推进）/ 恢复还原事务水位后 Abort /
///   TruncateSuffix 守卫（越界 / 已驱逐区）。</para>
/// <para>★ 跨实例场景用 Managed meta + DeleteOnClose=false。</para>
/// </summary>
public class RingAbortTests
{
    [Fact]
    public void Abort_TruncatesDanglingWrites_ToCommittedBoundary()
    {
        var vol = new TestVolume();
        var settings = TestRingSettingsFactory.On(vol, "ring", metaKind: MetaPolicyKind.Managed, deleteOnClose: false);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            // tx1：写 A → Prepare(1) → Confirm(1)——建立提交边界
            ring.Write(1, new byte[64]);
            ring.Write(2, new byte[64]);
            ring.Prepare(1);
            ring.ConfirmCommitted(1);
            var boundary = ring.TailAddress;

            // tx2：写 B → Prepare(2) → Abort(2)——悬干回退
            ring.Write(3, new byte[64]);
            ring.Write(4, new byte[64]);
            ring.Prepare(2);
            ring.Abort(2);

            ring.TailAddress.Should().Be(boundary, "Abort 应回退到上一提交边界（D2 放松单调铁律的唯一异常路径）");
            ring.FlushedUntilAddress.Should().BeLessOrEqualTo(boundary, "flushed 水位一并回退（条件回退）");
            ring.LastPreparedSeq.Should().Be(1, "Abort 复位 prepared seq 到 committed");

            // 回退区可重新推进（引擎容量已随 ReclaimTail 退回、EnsureSpace 重扩）
            var addr5 = ring.Write(5, new byte[64]);
            ring.FlushUntil(ring.TailAddress);
            var buf = new byte[64];
            ring.GetValue(addr5, buf).Should().Be(64, "截断后继续写读正常（地址空间复用回退区）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_StaleSeq_OnlyResetsBookkeeping()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1, new byte[64]);
            ring.Prepare(1);
            ring.ConfirmCommitted(1);
            ring.Write(2, new byte[64]);
            ring.Prepare(2);

            var tail = ring.TailAddress;
            ring.Abort(999);

            ring.TailAddress.Should().Be(tail, "陈旧 Abort 不截断");
            ring.LastPreparedSeq.Should().Be(1);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_AlreadyCommittedSeq_IsNoOp()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1, new byte[64]);
            ring.Prepare(1);
            ring.ConfirmCommitted(1);
            var tail = ring.TailAddress;

            var act = () => ring.Abort(1);
            act.Should().NotThrow();
            ring.TailAddress.Should().Be(tail, "已提交数据不可回滚");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Abort_FirstTransaction_NoBoundary_DoesNotTruncate()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1, new byte[64]);
            ring.Prepare(1);
            var tail = ring.TailAddress;

            ring.Abort(1);

            ring.TailAddress.Should().Be(tail, "首事务无既有提交边界——只复位记账不截断（混合非事务写不误伤）");
            ring.LastPreparedSeq.Should().Be(-1);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Recovery_RestoresTxWatermarks_ThenAbortTruncatesDangling()
    {
        var vol = new TestVolume();
        var settings = TestRingSettingsFactory.On(vol, "ring", metaKind: MetaPolicyKind.Managed, deleteOnClose: false);
        LogicalAddress boundary;
        using (var ring = TestRingSettingsFactory.NewRing<long>(vol, settings))
        {
            ring.Write(1, new byte[64]);
            ring.Prepare(1);
            ring.ConfirmCommitted(1);
            boundary = ring.TailAddress;
            ring.Write(2, new byte[64]);   // tx2 悬干
            ring.Prepare(2);
            // 不 Confirm——模拟崩溃
        }

        using (var ring2 = TestRingSettingsFactory.NewRing<long>(vol, settings))
        {
            ring2.LastPreparedSeq.Should().Be(2, "恢复必须还原 prepared seq（悬干可见）");
            ring2.LastCommittedSeq.Should().Be(1, "恢复必须还原 committed seq");

            ring2.Abort(ring2.LastPreparedSeq);   // LoadAndReconcile 同型裁决
            ring2.TailAddress.Should().Be(boundary, "恢复后 Abort 截断悬干到提交边界");
        }

        using (var ring3 = TestRingSettingsFactory.NewRing<long>(vol, settings))
        {
            ring3.TailAddress.Should().Be(boundary, "Abort 回退状态跨实例持久（meta 重写生效）");
        }
        vol.Dispose();
    }

    [Fact]
    public void TruncateSuffix_OutOfRange_Throws()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1, new byte[64]);

            var beyondTail = new LogicalAddress(9999, 0);   // 常量构造远段地址（§8 铁律：不做 Offset 算术）
            var act = () => ring.TruncateSuffix(beyondTail);
            act.Should().Throw<InvalidOperationException>("超出当前尾的截断点必须 fail-fast");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void TruncateSuffix_IntoEvictedRegion_Throws()
    {
        var epoch = new LightEpoch();
        var (settings, vol) = TestRingSettingsFactory.Create(pageSize: AlignmentConst.Alignment4K, memorySize: 64 * 1024);
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings, epoch: epoch);
            for (int i = 0; i < 40; i++)
                ring.Write(i, new byte[256]);
            ring.FlushUntil(ring.TailAddress);   // 尾必须越过 page2 起点否则 head 被 flushed 钳制、驱逐不生效

            // 驱逐到 page 2 起点（page 0/1 释放）
            var page2Start = ring._pageLogicalBySlot[2];
            epoch.Resume();
            try { ring.ShiftHeadAddress(page2Start); }
            finally { epoch.Suspend(); }
            ring.SafeHeadAddress.Should().Be(page2Start, "前置：驱逐已排水生效");

            var early = ring.BeginAddress;
            var act = () => ring.TruncateSuffix(early);
            act.Should().Throw<InvalidOperationException>("落入已驱逐区的截断点必须 fail-fast（水位格不可静默损坏）");
        }
        finally { vol.Dispose(); }
    }
}
