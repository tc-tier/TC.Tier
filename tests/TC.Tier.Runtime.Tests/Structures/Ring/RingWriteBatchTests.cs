using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 批量写（BeginWriteBatch——独占写窗口）契约测试：
/// 读回等价/地址单调连续/跨页批/与单条混用/空批/窗口耗尽自动换页。
/// </summary>
public class RingWriteBatchTests
{
    private static BlittableRingSettings Settings(TestVolume vol, int pageSizeBits = 13)
        => TestRingSettingsFactory.On(vol, "wb-ring", deleteOnClose: false,
            pageSize: 1 << pageSizeBits, metaKind: MetaPolicyKind.Managed);

    private static RingOfLong NewRing(TestVolume vol, int pageSizeBits = 13)
    {
        var ring = new RingOfLong(Settings(vol, pageSizeBits), vol.Fs);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }

    [Fact]
    public void BatchWrite_ReadBack_EquivalentToSingleWrites()
    {
        using var vol = new TestVolume();
        using var ring = NewRing(vol);

        var addrs = new LogicalAddress[100];
        using (var batch = ring.BeginWriteBatch())
        {
            for (long k = 0; k < 100; k++)
                addrs[k] = batch.Append(k, BitConverter.GetBytes(k * 3 + 1));
            batch.Count.Should().Be(100);
        }

        var buf = new byte[8];
        for (long k = 0; k < 100; k++)
        {
            ring.TryGetKey(addrs[k], out var key).Should().BeTrue();
            key.Should().Be(k);
            ring.GetValue(addrs[k], buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k * 3 + 1);
        }
    }

    [Fact]
    public void BatchWrite_Addresses_MonotonicContiguous()
    {
        using var vol = new TestVolume();
        using var ring = NewRing(vol);

        // 与单条 Write 对比：批内地址 = 前地址 + aligned 步长（同一页内连续）
        var payload = new byte[64];
        var addr0 = ring.Write(0L, payload);
        using (var batch = ring.BeginWriteBatch())
        {
            var prev = addr0;
            for (long k = 1; k <= 50; k++)
            {
                var a = batch.Append(k, payload);
                a.Should().BeGreaterThan(prev, "批内地址单调递增");
                prev = a;
            }
        }
    }

    [Fact]
    public void BatchWrite_CrossPage_Works()
    {
        using var vol = new TestVolume();
        using var ring = NewRing(vol, pageSizeBits: 12);   // 4KB 页（下限）——小页逼跨页

        var addrs = new LogicalAddress[300];
        var payload = new byte[64];   // ~96B/record → 4KB 页 ≈ 42 record/页 → 300 条跨 8 页
        using (var batch = ring.BeginWriteBatch())
        {
            for (long k = 0; k < 300; k++)
                addrs[k] = batch.Append(k, payload);
        }

        var buf = new byte[64];
        for (long k = 0; k < 300; k++)
        {
            ring.TryGetKey(addrs[k], out var key).Should().BeTrue($"key {k} 跨页后必可读");
            key.Should().Be(k);
            ring.GetValue(addrs[k], buf).Should().Be(64);
        }
    }

    [Fact]
    public void BatchWrite_MixedWithSingleWrites_OrderPreserved()
    {
        using var vol = new TestVolume();
        using var ring = NewRing(vol);

        // 单条 → 批 → 单条：地址流连续（批与单条共用 tail）
        var a1 = ring.Write(1L, BitConverter.GetBytes(11L));
        long a2;
        using (var batch = ring.BeginWriteBatch())
            a2 = batch.Append(2L, BitConverter.GetBytes(22L)).Offset;   // LogicalAddress → 比较用
        var a3 = ring.Write(3L, BitConverter.GetBytes(33L));

        a1.Offset.Should().BeLessThan(a2, "单条先写——地址在前");
        a2.Should().BeLessThan(a3.Offset, "批后单条——地址连续");

        var buf = new byte[8];
        ring.GetValue(a1, buf); BitConverter.ToInt64(buf).Should().Be(11L);
        ring.GetValue(new LogicalAddress(0, a2), buf); BitConverter.ToInt64(buf).Should().Be(22L);
        ring.GetValue(a3, buf); BitConverter.ToInt64(buf).Should().Be(33L);
    }

    [Fact]
    public void BatchWrite_EmptyBatch_DisposeSafe()
    {
        using var vol = new TestVolume();
        using var ring = NewRing(vol);

        using (var batch = ring.BeginWriteBatch())
        {
            batch.Count.Should().Be(0);
        }
        // 空批后单条写正常
        var addr = ring.Write(7L, BitConverter.GetBytes(77L));
        var buf = new byte[8];
        ring.GetValue(addr, buf).Should().Be(8);
        BitConverter.ToInt64(buf).Should().Be(77L);
    }

    [Fact]
    public void BatchWrite_AppendAfterDispose_Throws()
    {
        using var vol = new TestVolume();
        using var ring = NewRing(vol);

        var batch = ring.BeginWriteBatch();
        batch.Dispose();
        bool threw = false;
        try { batch.Append(1L, BitConverter.GetBytes(1L)); }
        catch (ObjectDisposedException) { threw = true; }
        threw.Should().BeTrue("Dispose 后 Append 必抛");
    }

    [Fact]
    public void BatchWrite_FlushPersists_CrossInstanceRecoverable()
    {
        using var vol = new TestVolume();
        using (var ring = NewRing(vol))
        {
            using (var batch = ring.BeginWriteBatch())
            {
                for (long k = 0; k < 200; k++)
                    batch.Append(k, BitConverter.GetBytes(k * 5));
            }
            ring.Prepare(seq: 1);
        }

        using var ring2 = NewRing(vol);
        ring2.FlushedUntilAddress.Should().BeGreaterThan(ring2.BeginAddress, "批数据已落盘");
        var buf = new byte[8];
        // 扫盘验证批记录可读（从 Begin 起逐条）
        using var cursor = ring2.OpenScanCursor(begin: ring2.BeginAddress, end: ring2.TailAddress);
        int count = 0;
        while (cursor.MoveNext())
        {
            if (ring2.TryGetKey(cursor.CurrentAddress, out var key))
            {
                ring2.GetValue(cursor.CurrentAddress, buf).Should().Be(8);
                BitConverter.ToInt64(buf).Should().Be(key * 5);
                count++;
            }
        }
        count.Should().Be(200, "跨实例扫盘读到全部批记录");
    }
}
