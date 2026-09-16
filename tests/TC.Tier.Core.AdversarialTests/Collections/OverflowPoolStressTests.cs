namespace TC.Tier.Core.AdversarialTests.Collections;

/// <summary>
/// OverflowPool 并发压测（对抗性——§4.1 拆分纪律：多线程注入/压测以慢时序为常态，
/// 不与 TC.Tier.Core.Tests 单测套件混跑；池语义的确定性单测留守原套件）。
/// </summary>
public class OverflowPoolStressTests
{
    [Fact]
    public async Task Concurrent_TryAddTryGet_Stress()
    {
        // 多生产者 + 多消费者并发压测：容量 64 << 生产量 8000（故意打满——把 overflow 丢弃分支在并发下真实压到）。
        // 消费侧退出 = 生产者已结束且池干涸（不依赖 consumed==total——TryAdd 满拒丢弃后 total 永不可达）；
        // 验证面 = 并发不崩 + 溢出路径触发 + 精确守恒（成功入池的每个条目必在"被取走/被拒/池内"三态之一）。
        var pool = new OverflowPool<int>(64);
        const int perProducer = 2000;
        const int producerCount = 4;
        const int total = perProducer * producerCount;
        int consumed = 0;
        int producersDone = 0;
        var producers = new Task[producerCount];
        var consumers = new Task[producerCount];

        // 生产者：尝试入池（部分会被拒 overflow，因容量 64 << total）
        for (int p = 0; p < producerCount; p++)
        {
            int pid = p;
            producers[p] = Task.Run(() =>
            {
                for (int i = 0; i < perProducer; i++)
                    pool.TryAdd(pid * perProducer + i);
            });
        }
        // 消费者：取到"池空且生产者已结束"（干涸）为止
        for (int c = 0; c < producerCount; c++)
        {
            consumers[c] = Task.Run(() =>
            {
                while (true)
                {
                    if (pool.TryGet(out _))
                    {
                        Interlocked.Increment(ref consumed);
                        continue;
                    }
                    if (Volatile.Read(ref producersDone) != 0) break;   // 干涸——正常毫秒级收敛
                    Thread.SpinWait(64);   // 空手让步（生产者还在灌）
                }
            });
        }

        await Task.WhenAll(producers);
        Volatile.Write(ref producersDone, 1);
        await Task.WhenAll(consumers);   // 正确退出条件下必然完成——无超时兜底（挂了按卡死流程取证，不假通过）

        // 守恒：每个条目必居三态之一（被取走 / 被拒丢弃 / 池内）——无中生有与双重消费都逃不过
        (Volatile.Read(ref consumed) + pool.Overflows + pool.Count).Should().Be(total);
        pool.Hits.Should().Be(Volatile.Read(ref consumed), "TryGet 成功数与测试侧消费计数一致");
        pool.Overflows.Should().BePositive("容量 64 远小于生产量 8000，必有 overflow");
    }
}
