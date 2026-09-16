using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// W1 溢出写分块化测试（docs/design/ring-overflow-chunked-write-design.md §9 单元行）。
/// <para>覆盖：大值分块往返（sync/async）、chunk 门限边界（帧长 ==/＞ 256KB chunk）、
///   pad 四态（N%4=0/1/2/3 末块随写）、多帧混写、并发写者、跨实例恢复（chunked 帧过恢复扫描）。</para>
/// <para>小帧（≤ chunk）路径零改动——既有 <see cref="RingOverflowTests"/> 全绿即格式兼容回归钉。</para>
/// </summary>
public class RingOverflowChunkedWriteTests
{
    /// <summary>chunk 门限（与 RingBase.OverflowChunkSize 同值——帧长 = 18 + AlignUp(N,4) 与 256KB 比较）。</summary>
    private const int ChunkSize = 256 * 1024;

    /// <summary>位置相关模式填充（全零/常量模式会掩盖寻址/覆盖错）。</summary>
    private static byte[] MakePattern(int length, byte seed = 7)
    {
        var buf = new byte[length];
        for (int i = 0; i < length; i++)
            buf[i] = (byte)(i * 31 + seed + (i >> 13));
        return buf;
    }

    private static void AssertRoundtrip(BlittableRing<long> ring, LogicalAddress addr, byte[] expected)
    {
        var dest = new byte[expected.Length];
        ring.GetValue(addr, dest).Should().Be(expected.Length);
        dest.Should().Equal(expected, "大小={0} 分块往返必须逐字节一致", expected.Length);
    }

    // ════════════════════════════════════════════════════════════
    // 大值分块往返
    // ════════════════════════════════════════════════════════════

    /// <summary>大值（4 chunk）分块写 sync 往返逐字节一致。</summary>
    [Fact]
    public void Write_LargeValue_Chunked_RoundtripsSync()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = MakePattern(4 * ChunkSize);   // 1MB = 4 整块
            LogicalAddress addr = ring.Write(1L, value);
            AssertRoundtrip(ring, addr, value);
            ring.GetKey(addr).IsOverflow.Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }

    /// <summary>大值分块写 async 写 + async 读往返逐字节一致。</summary>
    [Fact]
    public async Task WriteAsync_LargeValue_Chunked_RoundtripsAsync()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = MakePattern(3 * ChunkSize + 12345, seed: 9);
            LogicalAddress addr = await ring.WriteAsync(2L, value);

            var dest = new byte[value.Length];
            (await ring.GetValueAsync(addr, dest)).Should().Be(value.Length);
            dest.Should().Equal(value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>非整块末块（最后一块 7B + pad）往返逐字节一致。</summary>
    [Fact]
    public void Write_LargeValue_PartialLastChunk_Roundtrips()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = MakePattern(3 * ChunkSize + 7);
            LogicalAddress addr = ring.Write(3L, value);
            AssertRoundtrip(ring, addr, value);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // chunk 门限边界（帧长 == ChunkSize 走小帧；帧长 > ChunkSize 走分块）
    // ════════════════════════════════════════════════════════════

    /// <summary>帧长恰等 chunk（18 + AlignUp(N,4) = 256KB）→ 小帧单写路径，往返一致。</summary>
    [Fact]
    public void ChunkBoundary_FrameLenEqualsChunk_SmallPath_Roundtrips()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            int n = ChunkSize - 18 - 3;   // paddedLen = ChunkSize-18 → frameLen = ChunkSize（pad=3）
            n.Should().BeGreaterThan(0);
            var value = MakePattern(n);
            LogicalAddress addr = ring.Write(4L, value);
            AssertRoundtrip(ring, addr, value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>帧长 = chunk+1（最小分块形态：单 chunk + 1B 尾块）→ 分块路径，往返一致。</summary>
    [Fact]
    public void ChunkBoundary_FrameLenJustAboveGate_ChunkedSingleTail_Roundtrips()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            int n = ChunkSize - 18 - 2;   // paddedLen = ChunkSize-17 → frameLen = ChunkSize+1
            var value = MakePattern(n);
            LogicalAddress addr = ring.Write(5L, value);
            AssertRoundtrip(ring, addr, value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>N = chunk±1（payload 恰跨整块边界 ±1B）两个方向都往返一致。</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void ChunkBoundary_PayloadAroundChunkSize_Roundtrips(int delta)
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = MakePattern(ChunkSize + delta);
            LogicalAddress addr = ring.Write(6L, value);
            AssertRoundtrip(ring, addr, value);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // pad 四态（N%4 = 0/1/2/3 → pad = 0/3/2/1，末块随写）
    // ════════════════════════════════════════════════════════════

    /// <summary>大值 pad 四态：N%4 = 0/1/2/3 全部往返一致（pad 随末块同缓冲写入并入 CRC 链）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PadStates_LargeValue_AllRemainders_Roundtrip(int remainder)
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var value = MakePattern(2 * ChunkSize + 11 + remainder);
            LogicalAddress addr = ring.Write(7L, value);
            AssertRoundtrip(ring, addr, value);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // 更新翻转（大值 → 大值）
    // ════════════════════════════════════════════════════════════

    /// <summary>大值→大值 sync 更新：新帧分块写，指针更新，读回新值。</summary>
    [Fact]
    public void UpdateValue_LargeToLarge_Chunked_Roundtrips()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(8L, MakePattern(ChunkSize + 100));
            var updated = MakePattern(2 * ChunkSize + 555, seed: 21);
            ring.UpdateValue(addr, updated);
            AssertRoundtrip(ring, addr, updated);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>大值→大值 async 更新：新帧分块写，读回新值。</summary>
    [Fact]
    public async Task UpdateValueAsync_LargeToLarge_Chunked_Roundtrips()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = await ring.WriteAsync(9L, MakePattern(ChunkSize + 1));
            var updated = MakePattern(ChunkSize + 999, seed: 33);
            await ring.UpdateValueAsync(addr, updated);
            AssertRoundtrip(ring, addr, updated);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // 多帧混写 + 并发
    // ════════════════════════════════════════════════════════════

    /// <summary>小值/大值交错多帧：每帧独立地址，全部读回一致。</summary>
    [Fact]
    public void MixedSmallAndLarge_MultipleFrames_AllRoundtrip()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var expected = new Dictionary<long, byte[]>();
            for (long k = 0; k < 4; k++)
            {
                var big = MakePattern((int)(ChunkSize * (k + 1) + k), seed: (byte)(40 + k));
                var small = MakePattern(512, seed: (byte)(50 + k));
                expected[100 + k * 2] = big;
                expected[101 + k * 2] = small;
            }
            var addrs = new Dictionary<long, LogicalAddress>();
            foreach (var (key, value) in expected)
                addrs[key] = ring.Write(key, value);
            foreach (var (key, value) in expected)
                AssertRoundtrip(ring, addrs[key], value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>并发大值写者（各持各自 chunk 缓冲）：全部读回一致。</summary>
    [Fact]
    public async Task ConcurrentLargeValueWriters_AllRoundtrip()
    {
        var (settings, vol) = TestRingSettingsFactory.CreateOverflow();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var values = Enumerable.Range(0, 4)
                .Select(w => MakePattern(ChunkSize + w * 1024 + 3, seed: (byte)(60 + w)))
                .ToArray();

            var addrs = new LogicalAddress[values.Length];
            var tasks = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
            {
                addrs[w] = await ring.WriteAsync(200 + w, values[w]);
            }));
            await Task.WhenAll(tasks);

            for (int w = 0; w < 4; w++)
                AssertRoundtrip(ring, addrs[w], values[w]);
        }
        finally { vol.Dispose(); }
    }

    // ════════════════════════════════════════════════════════════
    // 跨实例恢复（chunked 帧过 FlushUntil/恢复）
    // ════════════════════════════════════════════════════════════

    /// <summary>大值分块帧 FlushUntil 落盘，跨实例重开读回一致（恢复链：溢出尾水位 → chunked 帧布局）。</summary>
    [Fact]
    public void Chunked_FlushUntil_DataDurable_CrossInstance()
    {
        var vol = new TestVolume();
        try
        {
            var settings = TestRingSettingsFactory.On(vol, "ring.chunked",
                deleteOnClose: false, metaKind: MetaPolicyKind.Managed,
                overflowPolicy: OverflowPolicy.Enabled, minOverflowSize: 32);

            var value = MakePattern(ChunkSize + 4096, seed: 77);
            LogicalAddress addr;
            using (var ring = TestRingSettingsFactory.NewRing<long>(vol, settings))
            {
                addr = ring.Write(300L, value);
                ring.FlushUntil(ring.TailAddress);
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var rk = ring2.GetKey(addr);
            rk.IsOverflow.Should().BeTrue();
            rk.ValueLength.Should().Be(value.Length);
            AssertRoundtrip(ring2, addr, value);
        }
        finally { vol.Dispose(); }
    }

    /// <summary>meta Disabled 形态：恢复走溢出引擎扫描（ScanOverflowTail 按 magic+帧长步进 chunked 帧）。</summary>
    [Fact]
    public void Chunked_MetaDisabled_RecoveryScansChunkedFrames()
    {
        var vol = new TestVolume();
        try
        {
            var settings = TestRingSettingsFactory.On(vol, "ring.chunked.scan",
                deleteOnClose: false, metaKind: MetaPolicyKind.Disabled,
                overflowPolicy: OverflowPolicy.Enabled, minOverflowSize: 32);

            var value = MakePattern(2 * ChunkSize + 77, seed: 88);
            LogicalAddress scanAddr = LogicalAddress.Empty;
            using (var ring = TestRingSettingsFactory.NewRing<long>(vol, settings))
            {
                LogicalAddress addr = ring.Write(400L, value);
                ring.FlushUntil(ring.TailAddress);
                scanAddr = addr;
            }

            using var ring2 = TestRingSettingsFactory.NewRing<long>(vol, settings);
            AssertRoundtrip(ring2, scanAddr, value);
        }
        finally { vol.Dispose(); }
    }
}
