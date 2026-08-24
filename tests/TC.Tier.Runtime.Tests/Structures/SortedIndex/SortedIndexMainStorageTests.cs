using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.SortedIndex;
using TC.Tier.Runtime.Tests.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// Sorted 族（BTree/SkipList）主存储（Builtin——后台 dump 锚点帧 + 恢复三级回退中间级）契约测试。
/// <para>★ 契约面：净重开零重放/增量重放（W&lt;尾）/无帧回退全量 + 扫描链完整（主存储不只要 Find）。</para>
/// <para>★ W = ring 已落盘水位（FlushedUntilAddress——组合层契约：Insert 先于落盘，已落盘必已入索引）；
///   dump 前须 FlushUntil(Tail) 推进落盘水位，恢复重放 (W, End] 补齐 dump 后新写。</para>
/// </summary>
public class SortedIndexMainStorageTests
{
    static BlittableRingSettings RingSettings(TestVolume vol)
        => TestRingSettingsFactory.On(vol, "ms-ring", deleteOnClose: false,
            metaKind: MetaPolicyKind.Managed);

    static SortedIndexPersistencePolicy NoAutoPolicy() => new()
    {
        Interval = TimeSpan.FromMinutes(10),   // 策略不自动触发——测试显式 TryDump
        EntryDeltaThreshold = long.MaxValue,
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MainStorageRecovery_ZeroReplay_AllFound(bool useBTree)
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            SortedIndexBase<long> index = useBTree
                ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                    TestSortedIndexSettingsFactory.BTreeOn(vol, "ms-bt", deleteOnClose: false,
                        persistencePolicy: NoAutoPolicy()),
                    keyResolver: ring)
                : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                    TestSortedIndexSettingsFactory.SkipListOn(vol, "ms-sl", deleteOnClose: false,
                        persistencePolicy: NoAutoPolicy()),
                    keyResolver: ring);

            for (long k = 1; k <= 200; k++)
                index.Insert(k, ring.Write(k, BitConverter.GetBytes(k * 7 + 1)), LogicalAddress.Empty);
            ring.FlushUntil(ring.TailAddress);   // ★ 推进已落盘水位 W=尾：dump 后重开零重放
            index.TryDump().Should().BeTrue();
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        SortedIndexBase<long> index2 = useBTree
            ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                TestSortedIndexSettingsFactory.BTreeOn(vol, "ms-bt", deleteOnClose: false,
                    persistencePolicy: NoAutoPolicy()),
                keyResolver: ring2, hints: new SortedIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress))
            : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                TestSortedIndexSettingsFactory.SkipListOn(vol, "ms-sl", deleteOnClose: false,
                    persistencePolicy: NoAutoPolicy()),
                keyResolver: ring2, hints: new SortedIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeTrue("锚点帧有效且 W=尾——走主存储零重放路径");
        index2.EntryCount.Should().Be(200);
        var buf = new byte[16];
        for (long k = 1; k <= 200; k += 17)
        {
            var addr = index2.Find(k);
            addr.Should().NotBe(LogicalAddress.Empty, $"key {k} 锚点物化后必命中");
            ring2.GetValue(addr, buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k * 7 + 1);
        }

        // 有序遍历（叶子链/层 0 链跨锚点完整——主存储不只要 Find，还要 scan 链）
        var cursor = index2.CreateScanCursor(ReadDirection.Forward);
        long prev = long.MinValue; int count = 0;
        while (cursor.MoveNext())
        {
            cursor.CurrentKey.Should().BeGreaterThan(prev, "有序遍历严格递增（链完整）");
            prev = cursor.CurrentKey;
            count++;
        }
        count.Should().Be(200, "扫描游标走完锚点物化重建的全链");
        ((IDisposable)cursor).Dispose();
        index2.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MainStorageRecovery_IncrementalReplay(bool useBTree)
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            SortedIndexBase<long> index = useBTree
                ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                    TestSortedIndexSettingsFactory.BTreeOn(vol, "ms-bt3", deleteOnClose: false,
                        persistencePolicy: NoAutoPolicy()),
                    keyResolver: ring)
                : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                    TestSortedIndexSettingsFactory.SkipListOn(vol, "ms-sl3", deleteOnClose: false,
                        persistencePolicy: NoAutoPolicy()),
                    keyResolver: ring);

            for (long k = 1; k <= 300; k++)
                index.Insert(k, ring.Write(k, BitConverter.GetBytes(k)), LogicalAddress.Empty);
            ring.FlushUntil(ring.TailAddress);
            index.TryDump().Should().BeTrue();

            // 锚点后增量 20 条（W < 尾——增量重放路径）
            for (long k = 301; k <= 320; k++)
                index.Insert(k, ring.Write(k, BitConverter.GetBytes(k)), LogicalAddress.Empty);
            ring.Prepare(seq: 2);
            index.Dispose();
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        SortedIndexBase<long> index2 = useBTree
            ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                TestSortedIndexSettingsFactory.BTreeOn(vol, "ms-bt3", deleteOnClose: false,
                    persistencePolicy: NoAutoPolicy()),
                keyResolver: ring2, hints: new SortedIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress))
            : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                TestSortedIndexSettingsFactory.SkipListOn(vol, "ms-sl3", deleteOnClose: false,
                    persistencePolicy: NoAutoPolicy()),
                keyResolver: ring2, hints: new SortedIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeTrue("锚点有效且 W<尾——增量路径");
        index2.EntryCount.Should().Be(320, "锚点 300 + 增量 20");

        var buf = new byte[16];
        for (long k = 1; k <= 320; k += 37)
        {
            var addr = index2.Find(k);
            addr.Should().NotBe(LogicalAddress.Empty, $"key {k} 必命中");
            ring2.GetValue(addr, buf).Should().Be(8);
            BitConverter.ToInt64(buf).Should().Be(k);
        }
        index2.Dispose();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoFrame_FallsBackToFullReplay(bool useBTree)
    {
        using var vol = new TestVolume();
        using (var ring = RingOfLong.Create(RingSettings(vol), vol.Fs))
        {
            SortedIndexBase<long> index = useBTree
                ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                    TestSortedIndexSettingsFactory.BTreeOn(vol, "ms-bt2", deleteOnClose: false,
                        persistencePolicy: NoAutoPolicy()),
                    keyResolver: ring)
                : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                    TestSortedIndexSettingsFactory.SkipListOn(vol, "ms-sl2", deleteOnClose: false,
                        persistencePolicy: NoAutoPolicy()),
                    keyResolver: ring);
            for (long k = 1; k <= 80; k++)
                index.Insert(k, ring.Write(k, BitConverter.GetBytes(k)), LogicalAddress.Empty);
            ring.Prepare(seq: 1);
            index.Dispose();   // ★ 从未 TryDump——账面无锚点帧
        }

        using var ring2 = RingOfLong.Create(RingSettings(vol), vol.Fs);
        SortedIndexBase<long> index2 = useBTree
            ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                TestSortedIndexSettingsFactory.BTreeOn(vol, "ms-bt2", deleteOnClose: false,
                    persistencePolicy: NoAutoPolicy()),
                keyResolver: ring2, hints: new SortedIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress))
            : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                TestSortedIndexSettingsFactory.SkipListOn(vol, "ms-sl2", deleteOnClose: false,
                    persistencePolicy: NoAutoPolicy()),
                keyResolver: ring2, hints: new SortedIndexRecoveryHints(ring2.BeginAddress, ring2.TailAddress));

        index2.MainStorageAppliedLastRecovery.Should().BeFalse("无锚点帧=fail-safe 全量重放");
        index2.EntryCount.Should().Be(80);
        index2.Find(79).Should().NotBe(LogicalAddress.Empty);
        index2.Dispose();
    }
}
