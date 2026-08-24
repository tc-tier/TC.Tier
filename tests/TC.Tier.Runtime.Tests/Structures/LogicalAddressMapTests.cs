using System.Runtime.CompilerServices;
using TC.Tier.Runtime.Structures.SortedIndex;

namespace TC.Tier.Runtime.Tests.Structures;

/// <summary>
/// LogicalAddressMap（扁平开放寻址节点缓存表）契约测试——1:1 于 src/.../Structures/LogicalAddressMap.cs。
/// <para>★ 契约面：哨兵边界（Empty=合法键 / Invalid=空槽+写拒绝）、定容准入、生长重散列、
///   Extension 不参与相等、Clear 非零填充、单写者+并发读者容忍（发布序）。</para>
/// </summary>
public class LogicalAddressMapTests
{
    static LogicalAddress Addr(int seg, long offset) => new(seg, offset);

    // ═══ 哨兵边界（LogicalAddress 语义：Empty=seg0@0 合法键；Invalid=空槽标记）═══

    [Fact]
    public void EmptyAddress_IsLegalKey_NotFreeSlot()
    {
        var map = new LogicalAddressMap<long>(8, growable: false);

        ref var added = ref map.GetOrAdd(LogicalAddress.Empty, 111L);
        Unsafe.IsNullRef(ref added).Should().BeFalse("Empty 是合法键（BTree 根常驻首分配位）");

        ref readonly var found = ref map.Find(LogicalAddress.Empty);
        Unsafe.IsNullRef(in found).Should().BeFalse();
        found.Should().Be(111L);
        map.Count.Should().Be(1);
    }

    [Fact]
    public void InvalidKey_FindMisses_WriteThrows()
    {
        var map = new LogicalAddressMap<long>(8, growable: false);

        ref readonly var found = ref map.Find(LogicalAddress.Invalid);
        Unsafe.IsNullRef(in found).Should().BeTrue("Invalid 保留作空槽标记——读侧恒 miss");

        var act = () => map.GetOrAdd(LogicalAddress.Invalid, 1L);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Extension_DoesNotParticipateInEquality()
    {
        var map = new LogicalAddressMap<long>(8, growable: false);
        var plain = new LogicalAddress(3, 100);
        var withExt = new LogicalAddress(3, extension: 7, offset: 100);

        map.Upsert(plain, 5L);
        map.TryGetValue(withExt, out var value).Should().BeTrue("相等仅 SegId+Offset——对齐 LogicalAddress.Equals");
        value.Should().Be(5L);
    }

    // ═══ 定容语义 ═══

    [Fact]
    public void FixedCapacity_AdmissionStopsAtLimit()
    {
        const int cap = 100;
        var map = new LogicalAddressMap<long>(cap, growable: false);

        for (int i = 0; i < cap + 50; i++)
            map.GetOrAdd(Addr(0, 256L * i), i);

        map.Count.Should().Be(cap, "定容=准入上限，超出静默不进");

        // 先进的前 cap 条全可寻回；后 50 条未进
        for (int i = 0; i < cap; i++)
        {
            ref readonly var slot = ref map.Find(Addr(0, 256L * i));
            Unsafe.IsNullRef(in slot).Should().BeFalse($"先入条目 #{i} 必驻留");
            slot.Should().Be(i);
        }
        ref readonly var rejected = ref map.Find(Addr(0, 256L * (cap + 10)));
        Unsafe.IsNullRef(in rejected).Should().BeTrue("超限条目未入表");
    }

    [Fact]
    public void FixedCapacity_UpsertExisting_StillOverwrites_WhenFull()
    {
        var map = new LogicalAddressMap<long>(4, growable: false);
        for (int i = 0; i < 4; i++)
            map.GetOrAdd(Addr(1, i), i);

        map.Upsert(Addr(1, 2), 99L);           // 满表命中 → 覆写生效
        map.TryGetValue(Addr(1, 2), out var v).Should().BeTrue();
        v.Should().Be(99L);

        map.Upsert(Addr(9, 9), 77L);           // 满表新键 → 静默丢弃
        map.Count.Should().Be(4);
        ref readonly var miss = ref map.Find(Addr(9, 9));
        Unsafe.IsNullRef(in miss).Should().BeTrue();
    }

    [Fact]
    public void ZeroCapacity_Fixed_NeverAdmits()
    {
        var map = new LogicalAddressMap<long>(0, growable: false);

        ref var slot = ref map.GetOrAdd(Addr(0, 1), 1L);
        Unsafe.IsNullRef(ref slot).Should().BeTrue();
        map.Count.Should().Be(0);
    }

    [Fact]
    public void GetOrAdd_Hit_ReturnsExisting_DoesNotOverwrite()
    {
        var map = new LogicalAddressMap<long>(8, growable: false);
        map.GetOrAdd(Addr(0, 10), 1L);

        ref var slot = ref map.GetOrAdd(Addr(0, 10), 2L);
        Unsafe.IsNullRef(ref slot).Should().BeFalse();
        slot.Should().Be(1L, "GetOrAdd 命中返回既有值——覆写走 Upsert");
    }

    // ═══ 生长语义（SkipList 无上限缓存形态）═══

    [Fact]
    public void Growable_ResizesBeyondInitialCapacity()
    {
        const int n = 5000;
        var map = new LogicalAddressMap<long>(4, growable: true);

        for (int i = 0; i < n; i++)
            map.GetOrAdd(Addr(i / 1000, 288L * (i % 1000)), i);

        map.Count.Should().Be(n);

        for (int i = 0; i < n; i++)
        {
            ref readonly var slot = ref map.Find(Addr(i / 1000, 288L * (i % 1000)));
            Unsafe.IsNullRef(in slot).Should().BeFalse($"重散列后条目 #{i} 丢失");
            slot.Should().Be(i);
        }
    }

    // ═══ 碰撞/探测与 Clear ═══

    [Fact]
    public void ConsecutiveAlignedOffsets_AllRetrievable()
    {
        // 节点地址按 NodeSize 对齐→低位同构——抗聚类靠高位散列，此测试钉住该性质
        const int n = 2000;
        var map = new LogicalAddressMap<long>(n, growable: false);
        for (int i = 0; i < n; i++)
            map.GetOrAdd(Addr(0, 256L * i), i);

        for (int i = 0; i < n; i++)
            map.TryGetValue(Addr(0, 256L * i), out var v).Should().BeTrue();
    }

    [Fact]
    public void Clear_AllMisses_CapacityRetained()
    {
        var map = new LogicalAddressMap<long>(64, growable: false);
        for (int i = 0; i < 64; i++)
            map.GetOrAdd(Addr(0, 256L * i), i);

        map.Clear();
        map.Count.Should().Be(0);

        for (int i = 0; i < 64; i++)
        {
            ref readonly var slot = ref map.Find(Addr(0, 256L * i));
            Unsafe.IsNullRef(in slot).Should().BeTrue($"清除后旧键 #{i} 不得残留命中");
        }

        map.GetOrAdd(Addr(5, 500), 42L);       // Clear 后可复用（容量保留）
        map.TryGetValue(Addr(5, 500), out var v).Should().BeTrue();
        v.Should().Be(42L);
    }

    [Fact]
    public void Clear_ZeroFillWouldNotFakeEmptyKeyHit()
    {
        // ★ Clear 若零填充键数组，(0,0,0)=Empty 会被空槽伪命中——此测试钉住 Invalid 回填
        var map = new LogicalAddressMap<long>(8, growable: false);
        map.GetOrAdd(Addr(2, 200), 7L);

        map.Clear();

        ref readonly var slot = ref map.Find(LogicalAddress.Empty);
        Unsafe.IsNullRef(in slot).Should().BeTrue("清空后 Empty（合法键）不得被零值空槽伪命中");
    }

#if DEBUG
    [Fact]
    public void MaxProbeLength_StaysBounded_UnderFullLoad()
    {
        const int n = 2000;
        var map = new LogicalAddressMap<int>(n, growable: false);
        for (int i = 0; i < n; i++)
            map.GetOrAdd(Addr(0, 256L * i), i);

        map.MaxProbeLength.Should().BeLessThan(64, "0.72 装载下线性探测链应有界（死循环=不变量破坏）");
    }
#endif

    // ═══ 并发契约（单写者 + 并发读者容忍——发布序/快照一致性，mem 级轻量冒烟）═══

    [Fact]
    public void SingleWriterConcurrentReaders_NoException_AllVisibleAfterJoin()
    {
        const int n = 50_000;
        var map = new LogicalAddressMap<long>(1024, growable: true);
        for (int i = 0; i < 1000; i++)
            map.GetOrAdd(Addr(0, 256L * i), i);   // 预垫底层数组，制造 Resize 期读者竞态窗口

        var writer = Task.Run(() =>
        {
            for (int i = 1000; i < n; i++)
                map.GetOrAdd(Addr(0, 256L * i), i);
        });

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            long checksum = 0;
            for (int i = 0; i < n; i++)
            {
                map.TryGetValue(Addr(0, 256L * i), out var v);
                checksum += v;   // 读侧零异常即达标——命中新旧/miss 均为合法中间态
            }
            return checksum;
        })).ToArray();

        writer.Wait();
        Task.WaitAll(readers);

        map.Count.Should().Be(n);
        for (int i = 0; i < n; i++)
        {
            map.TryGetValue(Addr(0, 256L * i), out var v).Should().BeTrue($"join 后条目 #{i} 必可见");
            v.Should().Be(i);
        }
    }
}
