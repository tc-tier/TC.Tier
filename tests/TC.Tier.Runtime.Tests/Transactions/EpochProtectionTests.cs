using TC.Tier.Runtime.Tests.Structures.ProbingIndex;
using TC.Tier.Runtime.Tests.Structures.Ring;
using TC.Tier.Runtime.Tests.Structures.SortedIndex;

namespace TC.Tier.Runtime.Tests.Transactions;

/// <summary>
/// IEpochProtected（epoch 读保护协议——Session 读 scope 聚合入口）契约测试：
/// <para>① 三结构基类（Ring/SortedIndex 两族/ProbingIndex）经接口多态进出对——结构可用性无损；
/// ② 保护区纪律：区内零拷贝读（Ring GetValueSpan——无自保护）正常、区内自带保护 API（Ring 写/Index Find）
/// 触发重入绊线立即暴露；</para>
/// <para>③ 同实例不可重入（Enter 未 Exit 又 Enter）；④ 跨实例并发持有（Session 聚合域形态）；
/// ⑤ ref struct scope 与协议形态同真源（先后互用不残留）。</para>
/// <para>断言依据为行为（结构可用/绊线抛异常）而非窥探 epoch 内部表（每实例一张表，无共享可观测位）。</para>
/// </summary>
public class EpochProtectionTests
{
    [Fact]
    public void Ring_ZeroCopyReadInside_ProtocolPairToggles()
    {
        var vol = new TestVolume();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "epoch-ring"));
            var addr = ring.Write(42L, new byte[] { 1, 2, 3 });   // 写在保护区外（写路径自进保护）

            ((IEpochProtected)ring).EnterEpoch();
            ring.GetValueSpan(addr).ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 },
                "区内零拷贝读正常（GetValueSpan 无自保护——聚合协议目标形态）");
            ((IEpochProtected)ring).ExitEpoch();

            ring.Write(43L, new byte[] { 4 });   // 退出后写不受影响（无腐蚀）
        }
        finally { vol.Dispose(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SortedIndex_PairEnterExit_UsableAfterwards(bool useBTree)
    {
        // Index 读自保护（Find/Insert 逐次自带 epoch）——区内只进出对，操作在区外。
        var vol = new TestVolume();
        try
        {
            using SortedIndexBase<long> index = useBTree
                ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                    TestSortedIndexSettingsFactory.BTreeOn(vol, "epoch-bt"))
                : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                    TestSortedIndexSettingsFactory.SkipListOn(vol, "epoch-sl"));

            ((IEpochProtected)index).EnterEpoch();
            ((IEpochProtected)index).ExitEpoch();

            index.Insert(7, new LogicalAddress(0, 256), LogicalAddress.Empty);
            index.Find(7).Should().Be(new LogicalAddress(0, 256), "进出后结构可用");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void ProbingIndex_PairEnterExit_UsableAfterwards()
    {
        var vol = new TestVolume();
        try
        {
            var (settings, _) = TestProbingIndexSettingsFactory.Create();
            var resolver = new MockKeyResolver<long>();
            using var hash = TestProbingIndexSettingsFactory.NewHash<long>(vol, settings, resolver);

            ((IEpochProtected)hash).EnterEpoch();
            ((IEpochProtected)hash).ExitEpoch();

            var addr = hash.Insert(7, new LogicalAddress(0, 256), LogicalAddress.Empty);
            resolver.Put(addr, 7);   // 判等闭环：tag 命中后回读真 key 校验
            hash.Find(7).Should().Be(new LogicalAddress(0, 256), "进出后结构可用");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Ring_SelfProtectedApiInsideProtection_TripwireFires()
    {
#if DEBUG
        // 区内纪律绊线：写路径自进保护——区内调 Ring 写 = 同实例重入，立即暴露（防记账腐蚀远处挂死）。
        var vol = new TestVolume();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "epoch-disc"));

            ((IEpochProtected)ring).EnterEpoch();
            var act = () => ring.Write(1L, new byte[] { 1 });
            act.Should().Throw<InvalidOperationException>("区内自带保护 API 必须立即暴露（Acquire 绊线）");
            ((IEpochProtected)ring).ExitEpoch();
        }
        finally { vol.Dispose(); }
#else
        // ★ Release 构建绊线不存在（LightEpoch 协议违反绊线为 DEBUG-only 零开销设计）——契约仅 Debug 可测
#endif
    }

    [Fact]
    public void SameInstance_ReentrantEnter_TripwireFires()
    {
#if DEBUG
        var vol = new TestVolume();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "epoch-re"));

            ((IEpochProtected)ring).EnterEpoch();
            var act = () => ((IEpochProtected)ring).EnterEpoch();
            act.Should().Throw<InvalidOperationException>("同实例重入必须立即暴露（Acquire 绊线）");
            ((IEpochProtected)ring).ExitEpoch();   // 配对退出——结构 Dispose 不再报持保护残留
        }
        finally { vol.Dispose(); }
#else
        // ★ Release 构建绊线不存在（同上）——契约仅 Debug 可测
#endif
    }

    [Fact]
    public void CrossInstances_ConcurrentHold_SessionAggregationShape()
    {
        // Session 聚合域形态：同线程对 Ring+Index+Hash 各自 Enter 可同时持有、按各自配对 Exit
        // （跨实例并发保护=ThreadEntryIndexCount 计数）。区内只做零拷贝读（纪律），其余操作区外。
        var vol = new TestVolume();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "epoch-agg-ring"));
            using SortedIndexBase<long> index = TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                TestSortedIndexSettingsFactory.BTreeOn(vol, "epoch-agg-bt"));
            var (hSettings, _) = TestProbingIndexSettingsFactory.Create();
            var resolver = new MockKeyResolver<long>();
            using var hash = TestProbingIndexSettingsFactory.NewHash<long>(vol, hSettings, resolver);

            var addr = ring.Write(1L, new byte[] { 9 });   // Ring 写在聚合保护区外
            index.Insert(1, new LogicalAddress(0, 128), LogicalAddress.Empty);
            resolver.Put(hash.Insert(1, new LogicalAddress(0, 128), LogicalAddress.Empty), 1);

            ((IEpochProtected)ring).EnterEpoch();
            ((IEpochProtected)index).EnterEpoch();
            ((IEpochProtected)hash).EnterEpoch();
            ring.GetValueSpan(addr).ToArray().Should().BeEquivalentTo(new byte[] { 9 }, "三结构同持保护，区内零拷贝读正常");
            ((IEpochProtected)ring).ExitEpoch();
            ((IEpochProtected)index).ExitEpoch();
            ((IEpochProtected)hash).ExitEpoch();

            index.Find(1).Should().Be(new LogicalAddress(0, 128), "聚合进出后结构可用");
            hash.Find(1).Should().Be(new LogicalAddress(0, 128), "聚合进出后结构可用");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void RefStructScopes_EquivalentToProtocol_SingleSource()
    {
        // ref struct scope（EnterReadScope）与 IEpochProtected 同真源：
        // scope 内读正常，Dispose 后可再经协议进出（互不残留）。
        var vol = new TestVolume();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "epoch-ring2"));
            var addr = ring.Write(5L, new byte[] { 1, 2, 3 });

            using (var scope = ring.EnterReadScope())
            {
                ring.GetValueSpan(addr).Length.Should().Be(3, "scope 内零拷贝读正常");
            }

            ((IEpochProtected)ring).EnterEpoch();
            ((IEpochProtected)ring).ExitEpoch();

            // scope 与协议两形态先后使用——均正确配对，结构 Dispose 无持保护残留（隐式断言）
        }
        finally { vol.Dispose(); }
    }
}
