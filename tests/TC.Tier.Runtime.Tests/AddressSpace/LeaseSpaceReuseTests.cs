using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.AddressSpace;
using TC.Tier.Runtime.AddressSpace.Leases;

namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// lease 空间复用测试——验证 lease 操作回收空间后，后续 Write/Append 能否正确复用。
/// <para>★ 核心关切：地址空间是 MinAddress 到无上限，段被回收后逻辑地址仍占位（Hollow）。</para>
/// <para>★ 覆盖场景：</para>
/// <list type="bullet">
/// <item>ReclaimTail 截断后，Append 从新水位继续 + Write 复用截断区</item>
/// <item>Reclaim 中间打洞后，Write 覆写空洞</item>
/// <item>Compact 替换段后，新段地址可写</item>
/// <item>段被删/Invalid 后，地址复用的边界</item>
/// </list>
/// </summary>
public class LeaseSpaceReuseTests
{
    private static SegmentTable NewTable(long growthLimit = 1000, int minSegId = 0, int capacity = 8)
        => new(new SegmentTableSettings(growthLimit, minSegId, capacity, SpinMilliseconds: 2000), LeaseFactory.WithDiagnostics);

    // ════════════════════════════════════════════════════════════
    //  ReclaimTail 截断后空间复用——Append 从新水位继续
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimTail_Then_AppendContinuesFromNewTail()
    {
        // 截断后 Append 从新水位继续——空间复用的核心场景
        using var table = NewTable();
        table.AppendLease(200).Commit();   // CommittedTail=(0,200)

        table.ReclaimTailLease(new LogicalAddress(0, 100)).Commit();   // 截断到 (0,100)

        // Append 从 (0,100) 继续
        using var lease = table.AppendLease(50);
        Assert.Equal(new LogicalAddress(0, 100), lease.Start);
        Assert.Equal(new LogicalAddress(0, 150), lease.End);
        lease.Commit();
        Assert.Equal(new LogicalAddress(0, 150), table.CommittedTail);
    }

    [Fact]
    public void ReclaimTail_Then_WriteReusesTruncatedSpace()
    {
        // 截断后 [100,200) 变成可复用空间——但 CommittedTail 退到 100，
        //   Write 必须 ≤ CommittedTail。所以先 Append 再 Write 覆写。
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimTailLease(new LogicalAddress(0, 100)).Commit();

        table.AppendLease(100).Commit();   // CommittedTail=(0,200)

        using var lease = table.WriteLease(new LogicalAddress(0, 100), 100);
        Assert.Equal(LeaseState.Active, lease.State);
        lease.Commit();
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    [Fact]
    public void ReclaimTail_CrossSegment_ThenAppendContinuesCorrectSegment()
    {
        // 跨段截断：seg0(100)+seg1(100)+seg2(50)，截到 (1,30)
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // CommittedTail=(2,50)

        table.ReclaimTailLease(new LogicalAddress(1, 30)).Commit();   // 截到 (1,30)

        // Append 从 (1,30) 继续——seg1 剩 70 字节（30+70=100 恰满，尾停驻段末边界）
        using var lease = table.AppendLease(70);
        Assert.Equal(new LogicalAddress(1, 30), lease.Start);
        Assert.Equal(new LogicalAddress(1, 100), lease.End);   // 恰满停驻段末（区间统一，不再进位 (2,0)）
        lease.Commit();
    }

    // ════════════════════════════════════════════════════════════
    //  Reclaim 中间打洞后空间复用——Write 覆写空洞
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimHole_Then_WriteFillsHole_CommittedChain()
    {
        // Append → Reclaim 打洞 → Write 填洞 → 全可读
        using var table = NewTable();
        table.AppendLease(300).Commit();
        table.ReclaimLease(new LogicalAddress(0, 100), new LogicalAddress(0, 200)).Commit();
        Assert.True(table.IsRangeFullyReadable(0, 0, 300));    // 558fe3b9：打洞=Committed+sparse 读零仍可读

        table.WriteLease(new LogicalAddress(0, 100), 100).Commit();   // 填洞
        Assert.True(table.IsRangeFullyReadable(0, 0, 300));   // 全可读
    }

    [Fact]
    public void MultipleReclaimHoles_AllFillableByWrite()
    {
        // 多个洞都能被 Write 逐个填回
        using var table = NewTable();
        table.AppendLease(500).Commit();
        // 打 3 个洞
        table.ReclaimLease(new LogicalAddress(0, 50), new LogicalAddress(0, 100)).Commit();
        table.ReclaimLease(new LogicalAddress(0, 200), new LogicalAddress(0, 250)).Commit();
        table.ReclaimLease(new LogicalAddress(0, 350), new LogicalAddress(0, 400)).Commit();
        Assert.True(table.IsRangeFullyReadable(0, 0, 500));    // 558fe3b9：打洞读零仍可读

        // 逐个填
        table.WriteLease(new LogicalAddress(0, 50), 50).Commit();
        table.WriteLease(new LogicalAddress(0, 200), 50).Commit();
        table.WriteLease(new LogicalAddress(0, 350), 50).Commit();
        Assert.True(table.IsRangeFullyReadable(0, 0, 500));
    }

    // ════════════════════════════════════════════════════════════
    //  Compact 替换段后空间复用
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void Compact_Then_WriteToReplacedSegmentSucceeds()
    {
        // Compact 替换段后，新段地址可写
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(100).Commit();

        var lease = table.CompactLeaseForTest(new LogicalAddress(0, 0), new LogicalAddress(0, 100));
        foreach (var chunk in lease.Chunks)
            chunk.SetReplacement(growthLimit: 100, maxOffset: 80);
        lease.Commit();

        // 新段 maxOffset=80，Write 覆写 [0,80) 内的区间
        using var write = table.WriteLease(new LogicalAddress(0, 0), 80);
        Assert.Equal(LeaseState.Active, write.State);
        write.Commit();
        Assert.Equal(LeaseState.Committed, write.State);
    }

    // ════════════════════════════════════════════════════════════
    //  段被删/Invalid 后——地址复用边界
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void ReclaimHead_DeletesSegment_Then_AddressBelowMinRejected()
    {
        // ReclaimHead 删段后，被删段地址 < MinAddress，Write 应拒绝
        using var table = NewTable(growthLimit: 100);
        table.AppendLease(250).Commit();   // seg0/1/2

        table.ReclaimHeadLease(new LogicalAddress(1, 0)).Commit();   // 删 seg0，MinAddress=(1,0)

        // Write 到 seg0 地址（< MinAddress）——应被 ValidateRange 拒绝
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            table.WriteLease(new LogicalAddress(0, 0), 50));
    }

    [Fact]
    public void ReclaimHead_SameSegment_Then_WriteAboveMinOffsetSucceeds()
    {
        // 同段 ReclaimHead 推 minOffset 后，[minOffset, maxOffset) 仍可 Write
        using var table = NewTable(growthLimit: 1000);
        table.AppendLease(500).Commit();
        table.ReclaimHeadLease(new LogicalAddress(0, 100)).Commit();   // minOffset=100

        // Write [100,200)——在新 minOffset 之上，应成功
        using var lease = table.WriteLease(new LogicalAddress(0, 100), 100);
        Assert.Equal(LeaseState.Active, lease.State);
        lease.Commit();
        Assert.Equal(LeaseState.Committed, lease.State);
    }

    [Fact]
    public void Append_AfterFullReclaimTail_ReusesFromZero()
    {
        // 完全截断到 (0,0) 后，Append 从头开始——空间完全复用
        using var table = NewTable();
        table.AppendLease(200).Commit();
        table.ReclaimTailLease(new LogicalAddress(0, 0)).Commit();   // 截到 (0,0)

        Assert.Equal(new LogicalAddress(0, 0), table.CommittedTail);
        // Append 重新从 (0,0) 开始
        using var lease = table.AppendLease(100);
        Assert.Equal(new LogicalAddress(0, 0), lease.Start);
        lease.Commit();
        Assert.Equal(new LogicalAddress(0, 100), table.CommittedTail);
    }

    // ════════════════════════════════════════════════════════════
    //  Append-Read-Reclaim-Rewrite 完整闭环（多次循环）
    // ════════════════════════════════════════════════════════════

    [Fact]
    public void FullCycle_AppendReadReclaimRewrite_MultipleRounds()
    {
        // 多轮循环：Append → Read 可读 → Reclaim 打洞 → 不可读 → Write 填洞 → 又可读
        using var table = NewTable();
        for (var round = 0; round < 3; round++)
        {
            var base_ = round * 300;
            table.AppendLease(300).Commit();
            Assert.True(table.IsRangeFullyReadable(0, base_, base_ + 300));

            // 打洞
            table.ReclaimLease(
                new LogicalAddress(0, base_ + 100),
                new LogicalAddress(0, base_ + 200)).Commit();
            Assert.True(table.IsRangeFullyReadable(0, base_, base_ + 300));    // 558fe3b9：打洞读零仍可读

            // 填洞
            table.WriteLease(new LogicalAddress(0, base_ + 100), 100).Commit();
            Assert.True(table.IsRangeFullyReadable(0, base_, base_ + 300));
        }
    }
}
