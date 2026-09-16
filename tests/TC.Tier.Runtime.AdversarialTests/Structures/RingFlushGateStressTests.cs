using FluentAssertions;
using TC.Tier.Runtime.Structures.Ring;
using Xunit;

namespace TC.Tier.Runtime.AdversarialTests.Structures;

/// <summary>
/// STORAGE-009/087 回归压测（#427——并发裸 flush × 页回绕 FreePage 交错）：
/// 产品层组提交形态（KvSession.ApplyPolicyAsync → FlushUntilAsync，不持 Ring epoch）在
/// 小环高频回绕下的并发裸 flush——修复前关页链 FreePage 可把在途 flush 迭代中的页置 null
/// 撞 fail-fast；修复后（_flushGate 串行门）必须全程无违约。
/// <para>★ 放大原理：16 页小环让写入几页即回绕，关页链高频触发——把慢盘 DIO 的秒级窗口
/// 折算成本地毫秒级高频交错。</para>
/// </summary>
public class RingFlushGateStressTests
{
    private static BlittableRing<long> NewTinyRing(TestVolume vol, string name)
    {
        var settings = new BlittableRingSettings(
            new StorageEngineOptions(name, 1L << 24, enableSegmentation: true,
                preallocateFile: true, deleteOnClose: true))
        {
            PageSize = AlignmentConst.Alignment4K,
            MemorySize = AlignmentConst.Alignment4K * 16,   // 16 页——写入数页即回绕
            MutableFraction = 0.5,
            Preallocate = true,
            MetaPolicyKind = MetaPolicyKind.Disabled,
            ColdReadRatio = 0.25,
        };
        var ring = new BlittableRing<long>(settings, vol.Fs);
        ring.Initialize();
        ring.WaitForReady();
        return ring;
    }

    [Fact]
    public async Task ConcurrentBareFlush_WrapStress_FlushRangeInvariantHolds()
    {
        const int rounds = 6;
        const int writers = 8;

        for (var round = 0; round < rounds; round++)
        {
            using var vol = new TestVolume();
            var ring = NewTinyRing(vol, "ring-flushgate-" + round);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var violations = new List<Exception>();

            var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
            {
                var keyBase = w * 1_000_000L;
                long i = 0;
                try
                {
                    while (!cts.IsCancellationRequested)
                    {
                        var value = BitConverter.GetBytes(i);
                        var addr = await ring.WriteAsync(keyBase + (i++ % 64), value, cts.Token)
                            .ConfigureAwait(false);
                        // 产品层裸 flush 形态——不持 ring epoch（KvSession.ApplyPolicyAsync 同路）
                        await ring.FlushUntilAsync(addr, cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    lock (violations) violations.Add(ex);
                }
            })).ToArray();
            await Task.WhenAll(tasks);

            // 取消导致的收尾不算违约；只有环自身的不变量违约才算
            var invariantBreaks = violations
                .Where(ex => ex is InvalidOperationException
                    && ex.Message.Contains("STORAGE-009/087", StringComparison.Ordinal))
                .ToList();
            invariantBreaks.Should().BeEmpty(
                "并发裸 flush × 页回绕下 flush range 页驻留不变量不得被破坏（round {0}，其余异常：{1}）",
                round, string.Join("; ", violations.Except(invariantBreaks).Select(ex => ex.Message)));

            violations.Should().BeEmpty("除不变量违约外不得有任何写/flush 异常");
        }
    }
}
