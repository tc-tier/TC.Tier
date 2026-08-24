namespace TC.Tier.Runtime.Tests.AddressSpace;

/// <summary>
/// lease chunk 迭代三态模式验证（）：foreach（完整迭代器）/ for（索引器含物理门）/ while（原模式）。
/// <para>★ ChunkScope：几何（SegId/SegOff/SegEnd/Length）+ 分段 Commit/Rollback（doneMask exactly-once）。</para>
/// </summary>
public class LeaseChunkIterationTests
{
    private static SegmentTable NewTable(int growthLimit, int segCount)
    {
        var table = new SegmentTable(new SegmentTableSettings(growthLimit, 0, Math.Max(8, segCount + 4)));
        for (var i = 0; i < segCount; i++)
            table.AllocateLease(growthLimit);
        return table;
    }

    [Fact]
    public void Foreach_CommitAll_LeaseCommitted()
    {
        using var table = NewTable(1000, 2);
        var lease = table.AppendLease(1500);   // 跨 2 chunk（整段 + 部分段）
        // ★ 区间统一：2 次精确填满后尾停驻 seg1 段末 (1,1000)——Append 从边界起步（下一字节 = seg2 首字节）
        lease.Start.Should().Be(new LogicalAddress(1, 0, 1000));

        var visited = 0;
        foreach (var chunk in lease)
        {
            chunk.SegId.Should().Be(2 + visited, "chunk 依序落在 seg2（整段）→ seg3（部分段）");
            visited++;
            chunk.Commit();
        }

        visited.Should().Be(2, "1500B / 1000B 段 = 2 chunk");
        lease.State.Should().Be(LeaseState.Committed);
    }

    [Fact]
    public void Foreach_GeometryMatchesLeaseRange()
    {
        using var table = NewTable(1000, 2);
        var lease = table.AppendLease(1500);

        // ★ start 停驻 seg1 段末边界 (1,1000)——首个 chunk 落在下一段 seg2 首字节（半开区间 [start,end)）
        lease.Start.Should().Be(new LogicalAddress(1, 0, 1000));

        var first = true;
        foreach (var chunk in lease)
        {
            if (first)
            {
                chunk.SegId.Should().Be(lease.Start.SegId + 1);
                chunk.SegOff.Should().Be(0);   // 段末边界起步 → 下一段首字节
                chunk.Length.Should().Be(1000);   // 首段填满
                first = false;
            }
            else
            {
                chunk.Length.Should().Be(500);    // 次段部分
            }
        }
    }

    [Fact]
    public void ForIndexer_CommitAll_LeaseCommitted()
    {
        using var table = NewTable(1000, 2);
        var lease = table.AppendLease(2500);   // 3 chunk

        for (var i = 0; i < lease.ChunkCount; i++)
        {
            var chunk = lease[i];   // 索引器含物理门
            ((int)chunk.Length).Should().BePositive();
            chunk.Commit();
        }

        lease.State.Should().Be(LeaseState.Committed);
    }

    [Fact]
    public void ForIndexer_OutOfRange_Throws()
    {
        using var table = NewTable(1000, 1);
        var lease = table.AppendLease(500);
        var act = () => _ = lease[lease.ChunkCount];
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Mixed_CommitAndRollback_Finalized()
    {
        using var table = NewTable(1000, 2);
        var lease = table.AppendLease(2500);   // 3 chunk：提交 0、回滚 1/2 → 混合方向 Finalized

        lease[0].Commit();
        lease[1].Rollback();
        lease[2].Rollback();

        lease.State.Should().Be(LeaseState.Finalized, "存在回滚 ⟹ 混合方向（设计文档 §2.5）");
    }

    [Fact]
    public void DoubleCommit_Idempotent_DoneMaskExactlyOnce()
    {
        using var table = NewTable(1000, 1);
        var lease = table.AppendLease(500);

        var chunk = lease[0];
        chunk.Commit();
        chunk.Commit();   // 重复提交——doneMask no-op，不抛不双计

        lease.State.Should().Be(LeaseState.Committed, "重复 Commit 被 doneMask 仲裁吞掉，计数恰好一次");
    }

    [Fact]
    public void WhilePattern_StillSupported()
    {
        using var table = NewTable(1000, 2);
        var lease = table.AppendLease(1500);

        var iter = lease.GetEnumerator();
        while (iter.MoveNext())
        {
            _ = iter.Current.SegId;
            iter.CommitCurrent();
        }

        lease.State.Should().Be(LeaseState.Committed, "原 while 模式保留（Reclaim 等消费方不变）");
    }

    [Fact]
    public async Task Foreach_AwaitInsideLoopBody_Legal()
    {
        using var table = NewTable(1000, 2);
        var lease = table.AppendLease(1500);

        foreach (var chunk in lease)
        {
            await Task.Yield();   // ChunkScope 为普通 struct——循环体内 await 合法（CopyChunksAsync 同款）
            chunk.Commit();
        }

        lease.State.Should().Be(LeaseState.Committed);
    }
}
