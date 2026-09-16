using System.Text;

namespace TC.Tier.Products.Tests.Wal;

/// <summary>
/// TierWAL 批追加租约契约（<see cref="ITierWal.BeginAppendBatch"/> → WalAppendLease）——
/// 消费方控制形态：逐条 Append 返回 index（页满 = 底层让渡零阻塞）/ AppendAsync 双轨 /
/// StartIndex 无漂移 / Dispose 收尾。★ 集合形态 AppendBatchAsync 内部 = lease 包装（单一真源）——
/// 本类同时钉住两形态的语义等价。
/// <para>★ 消费形态（C#12）：ref struct 租约不跨 await 持有——租约存活期全部封装在
/// sync helper（C#12 编译器强制；async 消费方 = 外壳 + sync 核心）。</para>
/// </summary>
public class TierWalLeaseTests
{
    [Fact]
    public async Task Lease_Append_AssignsSequentialIndexes_AndReadbackMatches()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        AppendFiveViaLease(wal);

        await wal.CommitAsync(default);
        wal.PersistedIndex.Should().Be(5);

        var read = new List<long>();
        foreach (var e in wal.ReadFromSync(1))
            read.Add(e.Index);
        read.Should().BeEquivalentTo(new long[] { 1, 2, 3, 4, 5 });
    }

    [Fact]
    public async Task Lease_AppendAsync_FastPathCompletesSynchronously()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        AppendAsyncFastPath(wal);

        await wal.CommitAsync(default);
        wal.PersistedIndex.Should().Be(2);
    }

    [Fact]
    public async Task Lease_MixedWithAppendSingle_IndexSpaceUnified()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);

        AppendTwoViaLease(wal);   // index 2-3

        var r = await wal.AppendBatchAsync(new[] { (ReadOnlyMemory<byte>)WalTestFactory.Entry(4) }, default);
        r.StartIndex.Should().Be(4);   // 三形态共享同一 index 空间
        wal.AllocatedIndex.Should().Be(4);
    }

    [Fact]
    public async Task Lease_AppendBatchAsync_CollectionFormParity()
    {
        // 集合形态 = lease 包装（单一真源）——语义与租约直用完全一致
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        var entries = new ReadOnlyMemory<byte>[] { WalTestFactory.Entry(1), WalTestFactory.Entry(2), WalTestFactory.Entry(3) };
        var r = await wal.AppendBatchAsync(entries, default);
        r.StartIndex.Should().Be(1);
        r.Count.Should().Be(3);
        wal.AllocatedIndex.Should().Be(3);

        // 读回内容逐条一致（地址→index 簿记随 lease Append 逐条发生；ManualCommit 下先提交再读——
        // 重放边界 = CommittedOffset，未提交数据不读）
        await wal.CommitAsync(default);
        var collected = new List<byte[]>();
        foreach (var e in wal.ReadFromSync(1))
            collected.Add(e.Data.ToArray());
        collected.Count.Should().Be(3);
        collected[0].Should().Equal(WalTestFactory.Entry(1));
        collected[2].Should().Equal(WalTestFactory.Entry(3));
    }

    [Fact]
    public async Task Lease_EmptyBatchDispose_NoIndexAllocated()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        EmptyLeaseNoBookkeeping(wal);

        wal.AllocatedIndex.Should().Be(0);
        var r = await wal.AppendSingleAsync(WalTestFactory.Entry(1), default);
        r.StartIndex.Should().Be(1);   // 空租约不消耗 index
    }

    [Fact]
    public async Task Lease_AppendAfterDispose_Throws()
    {
        using var vol = new TestVolume();
        await using var wal = await WalTestFactory.StartAsync(vol, WalTestFactory.ManualCommit);

        DisposedLeaseRejectsAppend(wal);

        wal.AllocatedIndex.Should().Be(1);   // 首条已入，第二条被拒
    }

    // ════ sync helper——租约存活期零 await（C#12 ref struct 约束下的合法消费形态）════

    private static void AppendFiveViaLease(TierWal wal)
    {
        using var lease = wal.BeginAppendBatch();
        var first = lease.StartIndex;
        first.Should().Be(1);
        for (var i = 0; i < 5; i++)
        {
            var index = lease.Append(Encoding.UTF8.GetBytes($"lease-{i}"));
            index.Should().Be(first + i);
        }
        lease.Count.Should().Be(5);
        wal.AllocatedIndex.Should().Be(5);
        wal.PersistedIndex.Should().Be(0);   // 未提交——双水位分离
    }

    private static void AppendAsyncFastPath(TierWal wal)
    {
        using (var lease = wal.BeginAppendBatch())
        {
            var vt = lease.AppendAsync(Encoding.UTF8.GetBytes("fast"));   // 页有空间——同步快路
            vt.IsCompletedSuccessfully.Should().BeTrue("页有空间的 AppendAsync 必须同步完成（趋零开销契约）。");
            var index = vt.IsCompletedSuccessfully ? vt.Result : 0;
            index.Should().Be(1);
        }

        using (var lease = wal.BeginAppendBatch())
        {
            lease.StartIndex.Should().Be(2);   // StartIndex 无漂移
            var vt = lease.AppendAsync(Encoding.UTF8.GetBytes("second"));
            var index = vt.IsCompletedSuccessfully ? vt.Result : 0;
            index.Should().Be(2);
        }
    }

    private static void AppendTwoViaLease(TierWal wal)
    {
        using var lease = wal.BeginAppendBatch();
        lease.StartIndex.Should().Be(2);
        _ = lease.Append(Encoding.UTF8.GetBytes("a"));
        _ = lease.Append(Encoding.UTF8.GetBytes("b"));
    }

    private static void EmptyLeaseNoBookkeeping(TierWal wal)
    {
        using var lease = wal.BeginAppendBatch();
        lease.StartIndex.Should().Be(1);
        lease.Count.Should().Be(0);
    }   // 空批 Dispose——零簿记

    private static void DisposedLeaseRejectsAppend(TierWal wal)
    {
        var lease = wal.BeginAppendBatch();
        _ = lease.Append(WalTestFactory.Entry(1));
        lease.Dispose();
        lease.Dispose();   // 幂等

        Exception? caught = null;
        try { lease.Append(WalTestFactory.Entry(2)); }
        catch (ObjectDisposedException ex) { caught = ex; }
        caught.Should().BeOfType<ObjectDisposedException>();
    }
}
