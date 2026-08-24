
using TC.Tier.Contracts.Structures;
namespace TC.Tier.Runtime.Tests.Structures.SortedIndex;

/// <summary>
/// IndexScope.Find（scope 内单查——epoch 由 scope 持有，省逐次 Resume/Suspend）契约测试：
/// 与逐次 Find 同结果（两族）。
/// </summary>
public class IndexScopeFindTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScopeFind_MatchesPerCallFind(bool useBTree)
    {
        var vol = new TestVolume();
        try
        {
            SortedIndexBase<long> index = useBTree
                ? TestSortedIndexSettingsFactory.NewBTree<long>(vol,
                    TestSortedIndexSettingsFactory.BTreeOn(vol, "scope-bt"))
                : TestSortedIndexSettingsFactory.NewSkipList<long>(vol,
                    TestSortedIndexSettingsFactory.SkipListOn(vol, "scope-sl"));

            using (index)
            {
                var value = new LogicalAddress(0, 256);
                for (long k = 0; k < 200; k++)
                    index.Insert(k * 7, value, LogicalAddress.Empty);

                using var scope = index.EnterScope();
                for (long k = 0; k < 200; k++)
                {
                    var viaScope = scope.Find(k * 7);
                    viaScope.Should().Be(value, $"scope 查命中（key={k * 7}）");
                }
                scope.Find(999_999).Should().Be(LogicalAddress.Empty, "miss 同语义");
            }
        }
        finally { vol.Dispose(); }
    }
}
