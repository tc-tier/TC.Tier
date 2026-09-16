using System.Collections.Concurrent;
using static TC.Tier.Core.Net.Tests.Fixtures.ClusterTransportRig;

namespace TC.Tier.Core.Net.AdversarialTests;

/// <summary>
/// ClusterTransport 故障注入对抗场景（§4.1 拆分纪律——乱序/延迟注入以慢时序为常态，
/// 不与 TC.Tier.Core.Net.Tests 单测套件混跑；等待上限放宽至对抗口径）。
/// </summary>
public class ClusterTransportReorderTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(15);   // 对抗口径——注入重排下放宽收敛窗

    [Fact]
    public async Task 乱序注入_消息全达()
    {
        var (low, high, lowId, highId) = await ClusterTransportRig.SetupPairAsync(waitLimit: WaitLimit);
        try
        {
            var sink = new ConcurrentBag<byte>();
            var allReceived = new TaskCompletionSource();
            const int count = 8;
            high.RegisterProtocol(0x64, new CollectingHandler(sink, allReceived, count));
            low.Faults.Reorder(lowId, highId, true);

            for (byte i = 0; i < count; i++)
            {
                byte[] one = [i];
                await low.SendDatagramAsync(highId, 0x64, one);
            }

            await allReceived.Task.WaitAsync(WaitLimit);
            sink.Should().HaveCount(count);
            sink.Distinct().Should().HaveCount(count);   // 无丢失
        }
        finally
        {
            await ClusterTransportRig.DisposeBothAsync(low, high);
        }
    }
}
