using FluentAssertions;
using TC.Tier.Runtime.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// 内容自愈读契约测试（read-protection-tiering v2）——1:1 于 RingBase.GetRecords 的
/// GetValueSpan/TryGetValue/GetValue 读内核。
/// <para>★ 契约面：热/冷切片内容 == 拷贝口径；溢出 record 回退拷贝交付；内容自愈
/// （人为损坏页池记录 → 设备权威数据或明确 NotFound，不产生假数据）；恢复后空壳页池读设备权威；
/// 墓碑在 value 交付面视为不存在、GetKey 保留墓碑旗标。</para>
/// </summary>
public class RingGetValueSpanTests
{
    [Fact]
    public void HotSpan_EqualsCopiedValue()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[64];
            new Random(7).NextBytes(value);
            var addr = ring.Write(42L, value);

            var span = ring.GetValueSpan(addr);
            span.Length.Should().Be(64);
            span.ToArray().Should().Equal(value, "零拷贝切片须与拷贝口径同字节");

            var copied = new byte[64];
            ring.GetValue(addr, copied).Should().Be(64);
            copied.Should().Equal(value);

            var tried = new byte[64];
            ring.TryGetValue(addr, tried, out var written).Should().BeTrue("热区有效记录 TryGetValue 必命中");
            written.Should().Be(64);
            tried.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void ColdSpan_AfterRecovery_EqualsCopiedValue()
    {
        // ★ 恢复后页池空壳（safe=dataStart）——全冷读设备权威（read-protection-tiering §3.2 行 1；
        //   旧 FlushedUntil 判据在恢复形态误判热区直读空池——本用例即该缺陷的回归门）
        var vol = new TestVolume();
        try
        {
            var value = new byte[64];
            new Random(11).NextBytes(value);
            LogicalAddress addr;
            var settings = TestRingSettingsFactory.On(vol, "ring-heal-cold", deleteOnClose: false);
            using (var ring = TestRingSettingsFactory.NewRing<long>(vol, settings))
            {
                addr = ring.Write(42L, value);
                ring.FlushUntil(ring.TailAddress);   // 设备留全量副本后关闭
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol,
                TestRingSettingsFactory.On(vol, "ring-heal-cold", deleteOnClose: false));

            var span = ring2.GetValueSpan(addr);
            span.Length.Should().Be(64);
            span.ToArray().Should().Equal(value, "恢复后冷区经设备回源须与拷贝口径同字节");

            var copied = new byte[64];
            ring2.GetValue(addr, copied).Should().Be(64);
            copied.Should().Equal(value);

            var tried = new byte[64];
            ring2.TryGetValue(addr, tried, out var written).Should().BeTrue();
            written.Should().Be(64);
            tried.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void HotCorrupted_PayloadBitflip_FallsBackToDeviceAuthority()
    {
        // ★ 内容自愈主用例：页池记录 payload 单字节翻转（CRC 必检）→ 设备回退读到写穿权威数据
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[64];
            new Random(13).NextBytes(value);
            var addr = ring.Write(42L, value);
            ring.FlushUntil(ring.TailAddress);   // 设备留权威副本

            // 翻转页池 payload 中间字节（header 完整——TryReadHeader 过、CRC 拦截）
            const int valueOffset = 48;          // header(40) + key(8) = value 起点
            ring.GetSpan(addr, valueOffset + 64)[valueOffset + 32] ^= 0xFF;

            ring.GetValueSpan(addr).ToArray().Should().Equal(value, "热读 CRC 失败须自愈回退设备权威");
            var copied = new byte[64];
            ring.GetValue(addr, copied).Should().Be(64);
            copied.Should().Equal(value);
            var tried = new byte[64];
            ring.TryGetValue(addr, tried, out var written).Should().BeTrue();
            written.Should().Be(64);
            tried.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void HotCorrupted_MagicDestroyed_FallsBackToDeviceAuthority()
    {
        // ★ header 魔数摧毁（复用残留形态）→ TryReadHeader 拦截 → 设备回退
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[64];
            new Random(17).NextBytes(value);
            var addr = ring.Write(42L, value);
            ring.FlushUntil(ring.TailAddress);

            ring.GetSpan(addr, 8).Fill(0xFF);    // 摧毁 header 头部（magic 位段）

            ring.GetValueSpan(addr).ToArray().Should().Equal(value, "magic 无效须自愈回退设备权威");
            var copied = new byte[64];
            ring.GetValue(addr, copied).Should().Be(64);
            copied.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void HotCorrupted_NotFlushed_NoFalseData()
    {
        // ★ 设备无副本时自愈必须收敛到明确 NotFound——不产生假数据（§7.3 验收）
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var addr = ring.Write(42L, new byte[64]);
            // 不 FlushUntil——设备无此数据

            ring.GetSpan(addr, 112).Fill(0x5A);  // 摧毁整条记录

            ring.GetValueSpan(addr).Length.Should().Be(0, "设备无权威副本须返回 NotFound 语义（空 span）");
            ring.GetValue(addr, new byte[64]).Should().Be(0, "NotFound 语义（0 字节）");
            ring.TryGetValue(addr, new byte[64], out _).Should().BeFalse("NotFound 语义（false）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void Tombstone_ValueApisTreatAsAbsent_KeyRetainsFlag()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var addr = ring.WriteTombstone(42L);

            ring.GetValueSpan(addr).Length.Should().Be(0, "墓碑在 value 交付面 = 不存在（空 span）");
            ring.GetValue(addr, new byte[16]).Should().Be(0);
            ring.TryGetValue(addr, new byte[16], out var written).Should().BeFalse("墓碑 TryGetValue = false");
            written.Should().Be(0);

            var record = ring.GetKey(addr);
            record.IsTombstone.Should().BeTrue("GetKey 保留墓碑旗标（队列消费方依赖）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void WrapReuse_SamePageRecord_ReadsDeviceHistorical_NotAliasedNewRecord()
    {
        // ★ head 守卫回归门（I5 补强）：环形回绕后旧地址被判热直读时，若新记录恰好落在同页同偏移
        //   （同形记录下必然成批出现），magic/CRC 校验全部通过——读到的是别人的记录（假数据）。
        //   守卫语义：addr < head ⇒ 页已回收 ⇒ 强制设备读 → 历史权威值。
        var epoch = new LightEpoch();
        var (settings, vol) = TestRingSettingsFactory.Create(pageSize: 4096, memorySize: 64 * 1024);   // 16 页 × 4KB
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings, epoch: epoch);
            var original = new byte[64];
            new Random(23).NextBytes(original);
            var addrA = ring.Write(1L, original);
            for (int i = 0; i < 40; i++)
                ring.Write(100L + i, new byte[64]);   // 填满 page 0 余量 + 分配 page 1（_pageLogicalBySlot[1] 就绪）
            ring.FlushUntil(ring.TailAddress);   // 设备留权威副本 + 解除 head 的 flushed 钳制

            // 显式驱逐 page 0（head 越过 addrA——page 0 排队释放，照 RingEvictionTests 驱逐形态）
            var page1Start = ring._pageLogicalBySlot[1];
            epoch.Resume();
            try { ring.ShiftHeadAddress(page1Start); }
            finally { epoch.Suspend(); }
            ring.HeadAddress.Should().BeGreaterThan(addrA, "测试前提：addrA 所在页已回收");

            // 写满一圈进 page 16（slot = 16 & 15 = 0——addrA 同槽被重写）；
            // 同形记录（112B）在页内偏移 0x70（= addrA 页内偏移）处落一条内容合法的新记录 = 别名源
            for (long k = 0; k < 620; k++)
                ring.Write(1000L + k, BitConverter.GetBytes(k));

            ring.GetValueSpan(addrA).ToArray().Should().Equal(original,
                "回绕后读旧地址必须读到设备历史权威值，而非复用槽上的新记录（别名假数据）");
            var copied = new byte[64];
            ring.GetValue(addrA, copied).Should().Be(64);
            copied.Should().Equal(original);
            var tried = new byte[64];
            ring.TryGetValue(addrA, tried, out var written).Should().BeTrue();
            written.Should().Be(64);
            tried.Should().Equal(original);
        }
        finally { vol.Dispose(); epoch.Dispose(); }
    }

    [Fact]
    public void OverflowRecord_SpanFallsBackToCopy()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = new byte[512];
            new Random(13).NextBytes(value);
            var addr = ring.Write(1L, value);

            var span = ring.GetValueSpan(addr);
            span.Length.Should().Be(512);
            span.ToArray().Should().Equal(value, "溢出 record 回退拷贝交付——内容同字节");

            var tried = new byte[512];
            ring.TryGetValue(addr, tried, out var written).Should().BeTrue();
            written.Should().Be(512);
            tried.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void MultipleRecords_SpansAllCorrect()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            const int n = 100;
            var values = new byte[n][];
            var addrs = new LogicalAddress[n];
            for (long k = 0; k < n; k++)
            {
                values[k] = new byte[16 + (int)k];
                new Random((int)k).NextBytes(values[k]);
                addrs[k] = ring.Write(k, values[k]);
            }

            for (int k = 0; k < n; k++)
                ring.GetValueSpan(addrs[k]).ToArray().Should().Equal(values[k]);
        }
        finally { vol.Dispose(); }
    }
}
