
using TC.Tier.Contracts.Structures;

[assembly: TC.Tier.CodeGen.RingKey(typeof(TC.Tier.Runtime.Tests.Structures.Ring.RingClosedSpecializationTests.OrderId))]

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// [RingKey] 生成器封闭特化测试（设计稿 §2）——编译通过本身即生成器契约（RingOfLong/RingOfOrderId
/// 是生成物，不存在则本文件编译失败）；行为面冒烟封闭类型与泛型内核全等。
/// <para>★ 两条路径都覆盖：①内建 long（Runtime 程序集声明）②自定义 unmanaged struct（消费程序集声明）。</para>
/// </summary>
public class RingClosedSpecializationTests
{
    /// <summary>自定义 Key：unmanaged struct + IEquatable（生成器对不满足 unmanaged 者编译期报错 TCSG020）。</summary>
    public readonly record struct OrderId(long Id);

    [Fact]
    public void RingOfLong_BuiltIn_WritesAndReads()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = new RingOfLong(settings, vol.Fs);
            ring.Initialize();
            ring.WaitForReady();

            var addr = ring.Write(42L, new byte[] { 1, 2, 3 });

            ring.TryGetKey(addr, out var key).Should().BeTrue();
            key.Should().Be(42L);

            var buf = new byte[3];
            ring.GetValue(addr, buf).Should().Be(3);
            buf.Should().BeEquivalentTo(new byte[] { 1, 2, 3 });
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void RingOfLong_CreateFactory_OneStepLifecycle()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = RingOfLong.Create(settings, vol.Fs);

            ring.IsReady.Should().BeTrue("Create = 构造 + Initialize + WaitForReady 一步");
            ring.Write(7L, new byte[] { 9 }).Should().NotBe(LogicalAddress.Empty);
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void RingOfLong_IsClosedFormOf_GenericKernel()
    {
        // 封闭类型 = 泛型内核的编译期封闭：赋值面兼容 + IKeyResolver 契约（判等闭环数据面）成立
        // （内联强转证明契约——避免接口型局部/参数触发 CA1859 表演性抑制）
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using RingOfLong closed = RingOfLong.Create(settings, vol.Fs);
            var addr = closed.Write(100L, new byte[] { 5 });

            ((IKeyResolver<long>)closed).TryGetKey(addr, out var key).Should().BeTrue();
            key.Should().Be(100L);
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void RingOfOrderId_CustomKey_InConsumingAssembly_WritesAndReads()
    {
        // 消费程序集自定义 Key：[assembly: RingKey(typeof(OrderId))] → RingOfOrderId 生成于本程序集
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = RingOfOrderId.Create(settings, vol.Fs);

            var key = new OrderId(12345L);
            var addr = ring.Write(key, new byte[] { 7, 7, 7, 7 });

            ring.TryGetKey(addr, out var read).Should().BeTrue();
            read.Should().Be(key);

            var buf = new byte[4];
            ring.GetValue(addr, buf).Should().Be(4);
            buf.Should().BeEquivalentTo(new byte[] { 7, 7, 7, 7 });
        }
        finally
        {
            vol.Dispose();
        }
    }

    // ═══ 索引封闭形态（同一 [RingKey] 声明产出全套——开放泛型不落消费面三索引同闸门）═══

    [Fact]
    public void HashOfLong_ClosedForm_PutFindWithRingResolver()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = RingOfLong.Create(settings, vol.Fs);
            using var index = HashOfLong.Create(vol.Fs,
                new HashIndexSettings(new StorageEngineOptions("closed-hash", 1L << 24, true, true, true)), ring);

            index.EntryCount.Should().Be(0);
            var addr = ring.Write(42L, new byte[] { 9 });
            index.Insert(42L, addr, LogicalAddress.Empty).Should().NotBe(LogicalAddress.Empty);
            index.Find(42L).Should().NotBe(LogicalAddress.Empty);
            index.EntryCount.Should().Be(1);
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void BTreeOfLong_ClosedForm_InsertFindScan()
    {
        var vol = new TestVolume();
        try
        {
            using var index = BTreeOfLong.Create(vol.Fs,
                new BTreeIndexSettings(new StorageEngineOptions("closed-bt", 1L << 24, true, true, true)));

            for (long k = 5; k >= 0; k--)
                index.Insert(k, new LogicalAddress(0, k * 10), LogicalAddress.Empty);

            index.Find(3).Should().Be(new LogicalAddress(0, 30));
            using var cursor = index.CreateScanCursor(ReadDirection.Forward);
            long expected = 0;
            while (cursor.MoveNext())
                cursor.CurrentKey.Should().Be(expected++);
            expected.Should().Be(6, "封闭形态保留比较族有序遍历能力");
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void SkipListOfLong_ClosedForm_InsertFind()
    {
        var vol = new TestVolume();
        try
        {
            using var index = SkipListOfLong.Create(vol.Fs,
                new SkipListIndexSettings(new StorageEngineOptions("closed-sl", 1L << 24, true, true, true)));

            index.Insert(7L, new LogicalAddress(0, 70), LogicalAddress.Empty);
            index.Find(7L).Should().Be(new LogicalAddress(0, 70));
            index.Find(8L).Should().Be(LogicalAddress.Empty);
        }
        finally
        {
            vol.Dispose();
        }
    }

    [Fact]
    public void HashOfOrderId_ClosedForm_CustomKey()
    {
        // 自定义 Key 的全套封闭：同一 [assembly: RingKey(typeof(OrderId))] 声明产出索引封闭形态
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = RingOfOrderId.Create(settings, vol.Fs);
            using var index = HashOfOrderId.Create(vol.Fs,
                new HashIndexSettings(new StorageEngineOptions("closed-hash-o", 1L << 24, true, true, true)), ring);

            var key = new OrderId(7L);
            var addr = ring.Write(key, new byte[] { 1 });
            index.Insert(key, addr, LogicalAddress.Empty);
            index.Find(key).Should().NotBe(LogicalAddress.Empty, "自定义 Key 封闭索引判等闭环经真 Ring 成立");
            index.EntryCount.Should().Be(1);
        }
        finally
        {
            vol.Dispose();
        }
    }
}
