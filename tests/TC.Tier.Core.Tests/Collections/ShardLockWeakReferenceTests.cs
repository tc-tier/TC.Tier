using TC.Tier.Core.Collections;
using Xunit;

namespace TC.Tier.Core.Tests.Collections;

/// <summary>
/// ShardLockWeakReference 契约测试——分片锁弱引用表（并发结构，死锁/泄漏高发区）。
/// 契约：AddOrUpdate 覆盖同 key；弱引用语义（值死后 TryGet 失败 + Cleanup 回收）；分片锁下并发 AddOrUpdate/TryGet 安全。
/// </summary>
public class ShardLockWeakReferenceTests
{
    [Fact]
    public void AddThenTryGet_ReturnsValue()
    {
        var map = new ShardLockWeakReference<int, object>();
        var v = new object();
        map.AddOrUpdate(1, v);

        map.TryGet(1, out var got).Should().BeTrue();
        ReferenceEquals(got, v).Should().BeTrue("同 key 应取回同实例");
    }

    [Fact]
    public void AddOrUpdate_SameKey_Overwrites()
    {
        var map = new ShardLockWeakReference<string, object>();
        var a = new object();
        var b = new object();
        map.AddOrUpdate("k", a);
        map.AddOrUpdate("k", b);

        map.TryGet("k", out var got).Should().BeTrue();
        ReferenceEquals(got, b).Should().BeTrue("后写覆盖前写");
    }

    [Fact]
    public void TryGet_MissingKey_False()
    {
        var map = new ShardLockWeakReference<int, object>();
        map.TryGet(42, out _).Should().BeFalse();
    }

    [Fact]
    public void Remove_ThenTryGet_False()
    {
        var map = new ShardLockWeakReference<int, object>();
        var v = new object();
        map.AddOrUpdate(1, v);
        map.Remove(1).Should().BeTrue();
        map.TryGet(1, out _).Should().BeFalse();
        map.Remove(1).Should().BeFalse("重复 Remove 返回 false");
    }

    [Fact]
    public void WeakSemantics_DeadValue_NotReturned_AndCleanedUp()
    {
        var map = new ShardLockWeakReference<int, object>();
        AddDeadValue(map, 1);   // 值死于辅助方法返回——调用方零强引用

        // 强制 GC（重试轮询——GC 异步终结，固定 sleep 不可靠）
        Assert.True(SpinWait.SpinUntil(() =>
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            return !map.TryGet(1, out _);
        }, 5000), "值死后 TryGet 应失败（弱引用语义）");

        // Cleanup 回收死条目
        Assert.True(SpinWait.SpinUntil(() => map.CleanupDeadReferences() >= 1, 5000), "Cleanup 应回收死条目");
        map.GetTotalEntryCount().Should().Be(0);
    }

    /// <summary>弱测试标准模式：NoInlining 辅助方法内创建值并写入——返回后调用方零强引用
    /// （防 Debug 构建 JIT 匿名临时对象活性延长导致的假红）。</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AddDeadValue(ShardLockWeakReference<int, object> map, int key)
    {
        map.AddOrUpdate(key, new object());   // 值死于方法返回
    }

    [Fact]
    public void Clear_Empties_AllShards()
    {
        var map = new ShardLockWeakReference<int, object>(shardCount: 8);
        for (var i = 0; i < 100; i++)
            map.AddOrUpdate(i, new object());
        map.GetTotalEntryCount().Should().Be(100);

        map.Clear();
        map.GetTotalEntryCount().Should().Be(0);
        map.TryGet(0, out _).Should().BeFalse();
    }

    [Fact]
    public void AllValues_LiveValues_Enumerated()
    {
        var map = new ShardLockWeakReference<int, object>(shardCount: 4);
        var keep = new object();
        for (var i = 0; i < 10; i++)
            map.AddOrUpdate(i, keep);
        map.AllValues.Count().Should().Be(10);
    }

    [Fact]
    public async Task ConcurrentAddTryGet_NoExceptionNoLoss()
    {
        var map = new ShardLockWeakReference<int, object>(shardCount: 4);
        const int threads = 8, ops = 2000;
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            gate.Wait();
            for (var i = 0; i < ops; i++)
            {
                var key = (t * ops + i) % 64;   // 跨线程共享 key（覆盖分片锁互斥）
                map.AddOrUpdate(key, key.ToString());
                map.TryGet(key, out _);
            }
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));

        map.GetTotalEntryCount().Should().Be(64, "64 个 key 最终各存一条");
    }
}
