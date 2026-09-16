using TC.Tier.Runtime.Tests.Structures.ProbingIndex;
using TC.Tier.Runtime.Tests.Structures.SortedIndex;

namespace TC.Tier.Runtime.Tests.Structures;

public class IndexTransactionTests
{
    [Fact]
    public void SortedIndex_FutureCommitCallback_FiresExactlyOnce()
    {
        var (settings, vol) = TestSortedIndexSettingsFactory.CreateBTree();
        try
        {
            using var index = TestSortedIndexSettingsFactory.NewBTree<long>(vol, settings);
            AssertFutureCallback((ITransactionParticipant)index);
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void ProbingIndex_FutureCommitCallback_FiresExactlyOnce()
    {
        var (settings, vol) = TestProbingIndexSettingsFactory.Create(
            hashTableCapacity: 64, overflowPoolCapacity: 16);
        try
        {
            using var index = TestProbingIndexSettingsFactory.NewHash<long>(
                vol, settings, new MockKeyResolver<long>());
            AssertFutureCallback((ITransactionParticipant)index);
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void ProbingIndex_ConcurrentRegisterAndConfirm_NeverLosesCallback()
    {
        var (settings, vol) = TestProbingIndexSettingsFactory.Create(
            hashTableCapacity: 64, overflowPoolCapacity: 16);
        try
        {
            using var index = TestProbingIndexSettingsFactory.NewHash<long>(
                vol, settings, new MockKeyResolver<long>());
            var participant = (ITransactionParticipant)index;

            for (long seq = 1; seq <= 1_000; seq++)
            {
                var fired = 0;
                Parallel.Invoke(
                    () => participant.OnCommitted(seq, () => Interlocked.Increment(ref fired)),
                    () => participant.ConfirmCommitted(seq));
                Volatile.Read(ref fired).Should().Be(1, $"seq {seq} callback 必须恰好触发一次");
            }
        }
        finally
        {
            vol.Dispose();
        }
    }

    private static void AssertFutureCallback(ITransactionParticipant participant)
    {
        var fired = 0;
        participant.OnCommitted(7, () => fired++);
        fired.Should().Be(0);

        participant.ConfirmCommitted(7);
        fired.Should().Be(1);

        participant.ConfirmCommitted(7);
        fired.Should().Be(1);

        participant.OnCommitted(6, () => fired++);
        fired.Should().Be(2, "已提交序号的回调应立即触发");
    }
}
