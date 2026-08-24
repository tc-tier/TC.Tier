using BenchmarkDotNet.Attributes;
using TC.Tier.Core.Epochs;
using TC.Tier.Core.Primitives;

// ★ CA1001 抑制：BDN 基准实例由 harness 生命周期管理（全局/迭代期资源，无 Dispose 时机）
#pragma warning disable CA1001

namespace TC.Tier.Core.Benchmarks.Primitives;

/// <summary>
/// 同步模式非争用微基准——选型指南（docs/locking-and-epoch.md §0 / perf/core-primitives-perf.md）的数据源。
/// <para>★ 只测<b>非争用</b>（单线程进出锁的指令成本）；并发伸缩（缓存行乒乓、写偏向落地延迟）用
///   独立探针 <c>--sync-probe</c>（BDN Parallel 组高度不确定，见 Program.cs 先例）。</para>
/// <para>★ 覆盖六种模式：SpinRWLock 共享/排他/Try 变体、Monitor、ReaderWriterLockSlim、
///   LightEpoch 保护周期、COW 快照读、seqlock 双读校验。</para>
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class SyncModesBench
{
    private readonly SpinRWLock _rw = new();
    private readonly object _monitor = new();
    private readonly ReaderWriterLockSlim _rwls = new();
    private readonly LightEpoch _epoch = new();

    // ── COW 快照载体（SegmentView 同形：5 标量 + volatile 引用发布）──
    private sealed class CowState
    {
        public readonly int A, B, C;
        public readonly long D, E;
        public CowState(int a, int b, int c, long d, long e) { A = a; B = b; C = c; D = d; E = e; }
    }
    private CowState _cow = new(1, 2, 3, 4, 5);

    // ── seqlock 载体（四投影 minOutstanding 同形：版本 + 双字段）──
    private long _seqVersion;
    private long _seqField1, _seqField2;

    private long _sink;

    // ═══ SpinRWLock ═══

    [Benchmark(Baseline = true)]
    public long SpinRWLock_SharedCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            _rw.AcquireShared();
            acc += i;
            _rw.ReleaseShared();
        }
        return acc;
    }

    [Benchmark]
    public long SpinRWLock_ExclusiveCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            _rw.AcquireExclusive();
            acc += i;
            _rw.ReleaseExclusive();
        }
        return acc;
    }

    [Benchmark]
    public long SpinRWLock_TrySharedCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            if (_rw.TryAcquireShared()) acc += i;
            _rw.ReleaseShared();
        }
        return acc;
    }

    [Benchmark]
    public long SpinRWLock_TryExclusiveCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            if (_rw.TryAcquireExclusive()) acc += i;
            _rw.ReleaseExclusive();
        }
        return acc;
    }

    // ═══ 内核/类库参照 ═══

    [Benchmark]
    public long Monitor_Cycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            lock (_monitor) acc += i;
        }
        return acc;
    }

    [Benchmark]
    public long Rwls_ReadCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            _rwls.EnterReadLock();
            acc += i;
            _rwls.ExitReadLock();
        }
        return acc;
    }

    [Benchmark]
    public long Rwls_WriteCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            _rwls.EnterWriteLock();
            acc += i;
            _rwls.ExitWriteLock();
        }
        return acc;
    }

    // ═══ LightEpoch 保护周期 ═══

    [Benchmark]
    public long LightEpoch_ResumeSuspendCycle()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            _epoch.Resume();
            acc += i;
            _epoch.Suspend();
        }
        return acc;
    }

    // ═══ 无锁快照模式 ═══

    /// <summary>COW 快照读——volatile 引用读 + 5 标量拷贝（SegmentView 同形）。</summary>
    [Benchmark]
    public long CowSnapshot_Read()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            var s = Volatile.Read(ref _cow);
            acc += s.A + s.B + s.C + s.D + s.E;
        }
        return acc;
    }

    /// <summary>COW 快照发布——新对象分配 + volatile 引用交换（写侧成本）。</summary>
    [Benchmark]
    public long CowSnapshot_Publish()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            var s = new CowState(i, 2, 3, 4, 5);
            Volatile.Write(ref _cow, s);
            acc += s.A;
        }
        return acc;
    }

    /// <summary>seqlock 读——版本双读 + 载荷 + 复验（四投影 minOutstanding 同形；本基准单线程，复验恒过）。</summary>
    [Benchmark]
    public long Seqlock_Read()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            long v1, v2, f1, f2;
            do
            {
                v1 = Volatile.Read(ref _seqVersion);
                f1 = Volatile.Read(ref _seqField1);
                f2 = Volatile.Read(ref _seqField2);
                v2 = Volatile.Read(ref _seqVersion);
            } while ((v1 & 1) != 0 || v1 != v2);
            acc += f1 + f2;
        }
        return acc;
    }

    /// <summary>seqlock 写——版本奇偶翻转 + 载荷写（单写者语义）。</summary>
    [Benchmark]
    public long Seqlock_Write()
    {
        long acc = 0;
        for (var i = 0; i < 100; i++)
        {
            var v = Volatile.Read(ref _seqVersion);
            Volatile.Write(ref _seqVersion, v + 1);
            Volatile.Write(ref _seqField1, i);
            Volatile.Write(ref _seqField2, i);
            Volatile.Write(ref _seqVersion, v + 2);
            acc += v;
        }
        return acc;
    }
}
