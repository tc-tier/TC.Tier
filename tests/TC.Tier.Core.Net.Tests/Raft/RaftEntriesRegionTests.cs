using System.Buffers;
using FluentAssertions;
using Xunit;
using TC.Tier.Core.Primitives;
using TC.Tier.Core.Net.Raft;

namespace TC.Tier.Core.Net.Tests.Raft;

/// <summary>
/// AppendEntries 条目区（线格式 v3）契约测试——布局锁字节（[Count 4B][×N: 头 13B + content]）、
/// 读面防御（截断/负长/负计数 = false）、IBufferWriter 写面往返（PooledBufferWriter 载体）。
/// </summary>
public class RaftEntriesRegionTests
{
    [Fact]
    public void 布局_锁字节()
    {
        var writer = new PooledBufferWriter();
        RaftEntriesRegion.WriteCount(writer, 1);
        RaftEntriesRegion.WriteEntry(writer, 0x0102030405060708, RaftEntryKind.Config, (byte[])[0xAA, 0xBB]);

        var bytes = writer.WrittenSpan.ToArray();
        var headerSize = RaftEntriesRegion.HeaderSize;
        bytes.Length.Should().Be(4 + headerSize + 2);
        // 计数前缀（4B 小端）
        bytes[0].Should().Be(1);
        bytes[1..4].Should().OnlyContain(b => b == 0);
        // 条目头（13B 小端：[Term 8][Kind 1][Len 4]）
        bytes[4].Should().Be(0x08, "term 低字节（小端）");
        bytes[4 + 8].Should().Be(RaftEntryKind.Config);
        bytes[4 + 9].Should().Be(2, "content len 低字节");
        bytes[^2..].Should().Equal(0xAA, 0xBB);   // content 紧随头后
        headerSize.Should().Be(13);
    }

    [Fact]
    public void 往返_多条目_零拷贝切片()
    {
        var writer = new PooledBufferWriter();
        RaftEntriesRegion.WriteCount(writer, 3);
        RaftEntriesRegion.WriteEntry(writer, 1, RaftEntryKind.Command, (byte[])[1, 2, 3]);
        RaftEntriesRegion.WriteEntry(writer, 2, RaftEntryKind.Config, (byte[])[]);
        RaftEntriesRegion.WriteEntry(writer, 9, RaftEntryKind.Command, (byte[])[0xFF]);

        var region = writer.WrittenMemory;
        RaftEntriesRegion.TryReadCount(region, out var count, out var cursor).Should().BeTrue();
        count.Should().Be(3);
        cursor.Should().Be(4);

        RaftEntriesRegion.TryReadEntry(region, ref cursor, out var t1, out var k1, out var c1).Should().BeTrue();
        t1.Should().Be(1);
        k1.Should().Be(RaftEntryKind.Command);
        c1.ToArray().Should().Equal(new byte[] { 1, 2, 3 });
        RaftEntriesRegion.TryReadEntry(region, ref cursor, out var t2, out var k2, out var c2).Should().BeTrue();
        t2.Should().Be(2);
        k2.Should().Be(RaftEntryKind.Config);
        c2.Length.Should().Be(0, "空内容条目合法");
        RaftEntriesRegion.TryReadEntry(region, ref cursor, out var t3, out var k3, out var c3).Should().BeTrue();
        t3.Should().Be(9);
        k3.Should().Be(RaftEntryKind.Command);
        c3.ToArray().Should().Equal(new byte[] { 0xFF });
        cursor.Should().Be(region.Length, "游标恰好在区尾");

        RaftEntriesRegion.TryReadEntry(region, ref cursor, out _, out _, out _).Should().BeFalse("区尾无更多条目");
    }

    [Theory]
    [InlineData(0)]    // 空区——计数前缀都缺
    [InlineData(2)]    // 计数前缀截断
    [InlineData(10)]   // 条目头截断
    [InlineData(20)]   // 条目头完整、内容越界
    public void 读面_截断_防御(int keep)
    {
        var writer = new PooledBufferWriter();
        RaftEntriesRegion.WriteCount(writer, 1);
        RaftEntriesRegion.WriteEntry(writer, 5, RaftEntryKind.Command, new byte[8]);
        var full = writer.WrittenSpan.ToArray();

        var region = full.AsMemory(0, keep);
        if (!RaftEntriesRegion.TryReadCount(region, out _, out var cursor))
            return;   // 计数前缀已不完整——拦截即契约
        RaftEntriesRegion.TryReadEntry(region, ref cursor, out _, out _, out _)
            .Should().BeFalse($"截断区（保留 {keep}B）不得产出条目");
    }

    [Fact]
    public void 读面_负计数_负长_防御()
    {
        // 负计数
        Span<byte> negative = [0xFF, 0xFF, 0xFF, 0xFF];
        RaftEntriesRegion.TryReadCount(negative.ToArray(), out _, out _).Should().BeFalse();

        // 负 content 长度（头 13B 完整、len = -1）
        Span<byte> badLen = stackalloc byte[4 + 13];
        badLen[0] = 1;
        badLen[4 + 9] = 0xFF;
        badLen[4 + 10] = 0xFF;
        badLen[4 + 11] = 0xFF;
        badLen[4 + 12] = 0xFF;
        RaftEntriesRegion.TryReadCount((ReadOnlyMemory<byte>)badLen.ToArray(), out var count, out var cursor).Should().BeTrue();
        count.Should().Be(1);
        RaftEntriesRegion.TryReadEntry((ReadOnlyMemory<byte>)badLen.ToArray(), ref cursor, out _, out _, out _).Should().BeFalse("负长 = 畸形");
    }

    /// <summary>store 直写契约形态（ReplicationProcess 调用同构）：PooledBufferWriter 载体 +
    /// termsOut 回填——写入后 Reset 复用（lane 单写者纪律的载体面验证）。</summary>
    [Fact]
    public void 写面_池化载体_Reset复用()
    {
        using var writer = new PooledBufferWriter();
        RaftEntriesRegion.WriteCount(writer, 1);
        RaftEntriesRegion.WriteEntry(writer, 1, RaftEntryKind.Command, (byte[])[0x01]);
        writer.WrittenCount.Should().Be(4 + 13 + 1);

        writer.Reset();
        RaftEntriesRegion.WriteCount(writer, 1);
        RaftEntriesRegion.WriteEntry(writer, 2, RaftEntryKind.Command, (byte[])[0x02, 0x03]);
        writer.WrittenCount.Should().Be(4 + 13 + 2, "复用背板重写——前批字节数不残留");

        RaftEntriesRegion.TryReadCount(writer.WrittenMemory, out var count, out var cursor).Should().BeTrue();
        count.Should().Be(1);
        RaftEntriesRegion.TryReadEntry(writer.WrittenMemory, ref cursor, out var term, out _, out var content).Should().BeTrue();
        term.Should().Be(2);
        content.ToArray().Should().Equal(new byte[] { 0x02, 0x03 });
    }
}
