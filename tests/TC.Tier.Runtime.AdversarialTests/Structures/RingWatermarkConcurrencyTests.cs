using FluentAssertions;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 水位 CAS128 原子基座并发压测（#163 回归门）——并发推进 + 采样读，断言水位单调不回退。
/// <para>★ 多线程交错推进（旧 check-then-write 可回退：A 读 100 写 200 ⊕ B 读 100 写 150
/// → 200 被压回 150）；CAS128 单调推进下任何交错读出的水位序列必须非降，终值 = 全部提交中的最大值。</para>
/// <para>★ 放置于对抗性项目（并发压测拆分纪律——不进 Runtime.Tests）。</para>
/// </summary>
public class RingWatermarkConcurrencyTests
{
    [Fact]
    public void Concurrent_MonotonicAdvance_NeverRegresses()
    {
        var vol = new TestVolume();
        try
        {
            var ring = new BlittableRing<long>(
                TestRingSettings(vol, "wm-adv"), vol.Fs);
            ring.Initialize();
            ring.WaitForReady();
            using (ring as IDisposable)
            {
                const int threads = 8, opsPerThread = 4000;
                const long baseOff = 0x100000;
                var maxSubmitted = new LogicalAddress(0, baseOff + threads * (long)opsPerThread);

                using var cts = new CancellationTokenSource();
                var samples = 0L;
                var sampler = Task.Run(() =>
                {
                    LogicalAddress last = ring.FlushedUntilAddress;
                    while (!cts.IsCancellationRequested)
                    {
                        var cur = ring.FlushedUntilAddress;
                        cur.CompareTo(last).Should().BeGreaterThanOrEqualTo(0,
                            "水位采样序列必须非降（CAS128 单调推进）");
                        last = cur;
                        Interlocked.Increment(ref samples);
                    }
                }, cts.Token);

                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads }, t =>
                {
                    for (var k = 1; k <= opsPerThread; k++)
                    {
                        var v = new LogicalAddress(0, baseOff + t * (long)opsPerThread + k);
                        ring.MonotonicUpdateFlushedUntilForTest(v, out _);
                    }
                });

                var spin = 0;
                while (ring.FlushedUntilAddress != maxSubmitted && spin++ < 1_000_000)
                    Thread.Yield();
                cts.Cancel();
                sampler.Wait(TimeSpan.FromSeconds(5));

                ring.FlushedUntilAddress.Should().Be(maxSubmitted, "终值 = 全部提交中的最大值（无一丢失）");
                samples.Should().BeGreaterThan(0, "采样线程必须实际运行");
            }
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Concurrent_FlushUntil_Converges_TailNoLoss()
    {
        var vol = new TestVolume();
        try
        {
            var ring = new BlittableRing<long>(TestRingSettings(vol, "wm-flush"), vol.Fs);
            ring.Initialize();
            ring.WaitForReady();
            using (ring as IDisposable)
            {
                ring.Write(1L, new byte[64]);
                ring.FlushUntil(ring.TailAddress);   // 预推基线（FlushUntil 内部 >current 才推进）

                const int threads = 6, opsPerThread = 500;
                Parallel.For(0, threads, t =>
                {
                    for (var k = 0; k < opsPerThread; k++)
                        ring.FlushUntil(ring.TailAddress);
                });

                ring.FlushedUntilAddress.Should().Be(ring.TailAddress,
                    "并发 FlushUntil 收敛后水位 = 尾（CAS128 无回退丢失）");
            }
        }
        finally { vol.Dispose(); }
    }

    private static BlittableRingSettings TestRingSettings(TestVolume vol, string name)
        => new(new StorageEngineOptions(name, 1L << 24,
                enableSegmentation: true, preallocateFile: true, deleteOnClose: true))
        {
            PageSize = 4096,
            MemorySize = 64 * 1024,
        };
}
