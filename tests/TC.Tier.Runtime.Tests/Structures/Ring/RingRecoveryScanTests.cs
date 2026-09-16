using FluentAssertions;
using TC.Tier.Runtime.Structures.Ring;
using TC.Tier.Runtime.Structures.Ring.Contracts;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// ScanPageForRecords 撕裂帧重同步单测（STORAGE-082 / #302）——合成页缓冲直测扫描器。
/// <para>★ 契约：撕裂帧（CRC 失败/坏 header/长度越页）按对齐粒度同页重同步续扫——撕裂点之后
///   的同页有效帧不再连带丢失；全垃圾页返回 null（跨页保守止步由上层 ForwardScan 承担）。</para>
/// </summary>
public class RingRecoveryScanTests
{
    private const int HeaderSize = 40;   // BlittableRingHeader v2.0
    private const int KeySize = 8;       // TKey = long
    private const int PayloadLen = 16;   // key 8 + value 8
    private const int FrameSize = 56;    // (40+16+7) & ~7 = 56

    private static (byte[] page, IRingCodec codec) BuildPage(int frameCount, int pageSize = 4096, int tornIndex = -1)
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            var codec = ring.CodecForTest;
            var page = new byte[pageSize];

            for (var i = 0; i < frameCount; i++)
            {
                var off = i * FrameSize;
                var fields = new RingRecordFields(BlittableRingHeader.DefaultFlags, PayloadLen, 0, LogicalAddress.Empty);
                codec.WriteHeader(page.AsSpan(off, HeaderSize), in fields);
                BitConverter.GetBytes(0x1000L + i).CopyTo(page.AsSpan(off + HeaderSize, KeySize));
                for (var j = 0; j < 8; j++) page[off + HeaderSize + KeySize + j] = (byte)(i * 10 + j);
                codec.FillCrc(page.AsSpan(off, HeaderSize + PayloadLen), HeaderSize, PayloadLen);
            }

            if (tornIndex >= 0)
                page[tornIndex * FrameSize + HeaderSize + KeySize] ^= 0xFF;   // 撕裂：payload 单字节翻转（CRC 必败）

            return (page, codec);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void AllValid_Frames_ReturnsLastFrameEnd()
    {
        var (page, codec) = BuildPage(4);
        var end = RingBase<long>.DefaultRingRecovery.ScanPageForRecords(
            page, new LogicalAddress(0, 0), page.Length, page.Length - 1, HeaderSize, 8, codec);
        end.Should().Be(new LogicalAddress(0, 4 * FrameSize), "全有效帧 → 尾 = 末帧结束地址");
    }

    [Fact]
    public void TornMiddleFrame_Resyncs_RecoversSamePageLaterFrames()
    {
        // ★ #302 主诉求：B 帧 CRC 失败（index=1）不再连带丢弃同页后续的 C/D 帧——
        //   旧实现返回 A 尾（56），恢复丢 C/D 数据
        var (page, codec) = BuildPage(4, tornIndex: 1);
        var end = RingBase<long>.DefaultRingRecovery.ScanPageForRecords(
            page, new LogicalAddress(0, 0), page.Length, page.Length - 1, HeaderSize, 8, codec);
        end.Should().Be(new LogicalAddress(0, 4 * FrameSize), "撕裂帧重同步后尾 = 末帧结束地址（C/D 不丢）");
    }

    [Fact]
    public void TornFirstFrame_Resyncs_RecoversRest()
    {
        var (page, codec) = BuildPage(4, tornIndex: 0);
        var end = RingBase<long>.DefaultRingRecovery.ScanPageForRecords(
            page, new LogicalAddress(0, 0), page.Length, page.Length - 1, HeaderSize, 8, codec);
        end.Should().Be(new LogicalAddress(0, 4 * FrameSize));
    }

    [Fact]
    public void AllGarbagePage_ReturnsNull()
    {
        var page = new byte[4096];
        Array.Fill(page, (byte)0x5A);   // 非零垃圾（不可解析且非空魔）
        var (codec, _) = (BuildPage(0).codec, 0);
        var end = RingBase<long>.DefaultRingRecovery.ScanPageForRecords(
            page, new LogicalAddress(0, 0), page.Length, page.Length - 1, HeaderSize, 8, codec);
        end.Should().BeNull("全垃圾页 → null（上层保守止步）");
    }

    [Fact]
    public void ZeroPage_ReturnsNull()
    {
        var (page, codec) = BuildPage(0);
        var end = RingBase<long>.DefaultRingRecovery.ScanPageForRecords(
            page, new LogicalAddress(0, 0), page.Length, page.Length - 1, HeaderSize, 8, codec);
        end.Should().BeNull("全零页（预分配未写区）→ null");
    }

    [Fact]
    public void TornTail_AfterLastGoodFrame_TailStaysAtLastGood()
    {
        // 撕裂的是末帧（index=3）——重同步后无后续有效帧，尾 = 前一有效帧结束（56×3）
        var (page, codec) = BuildPage(4, tornIndex: 3);
        var end = RingBase<long>.DefaultRingRecovery.ScanPageForRecords(
            page, new LogicalAddress(0, 0), page.Length, page.Length - 1, HeaderSize, 8, codec);
        end.Should().Be(new LogicalAddress(0, 3 * FrameSize), "撕裂末帧不可计入尾（内容不可信）");
    }
}
