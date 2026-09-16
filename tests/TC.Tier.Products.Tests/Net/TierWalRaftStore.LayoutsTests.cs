using System.Buffers.Binary;
using TC.Tier.Core.Net;
using TC.Tier.Products.Net.Node;
using TC.Tier.Products.Net.Host;
using TC.Tier.Products.Net.Admin;
using TC.Tier.Products.Net.RaftStore;

namespace TC.Tier.Products.Tests.Net;

/// <summary>
/// TierWAL raft 布局 codec 字节金样（<see cref="RaftWalFrameHeader"/> / <see cref="RaftStoreMeta"/>）：
/// 生成物读写必须与既有磁盘/线格式逐字节一致（金样 = 适配器旧手写偏移形态的复刻）——
/// 布局声明改写不改 wire/disk 格式。
/// </summary>
public class TierWalRaftStore_LayoutsTests
{
    [Fact]
    public void FrameHeader_ByteGolden_AndRoundTrip()
    {
        RaftWalFrameHeaderCodec.StructSize.Should().Be(9);
        var header = new RaftWalFrameHeader(0x0102030405060708L, 0x7F);

        Span<byte> buf = stackalloc byte[RaftWalFrameHeaderCodec.StructSize];
        RaftWalFrameHeaderCodec.Write(buf, header);

        // 金样：[Term 8B LE][Kind 1B]（content 紧随——帧前缀）
        buf[0].Should().Be(0x08); buf[1].Should().Be(0x07); buf[2].Should().Be(0x06); buf[3].Should().Be(0x05);
        buf[4].Should().Be(0x04); buf[5].Should().Be(0x03); buf[6].Should().Be(0x02); buf[7].Should().Be(0x01);
        buf[8].Should().Be((byte)0x7F);

        var parsed = RaftWalFrameHeaderCodec.Read(buf);
        parsed.Term.Should().Be(0x0102030405060708L);
        parsed.Kind.Should().Be((byte)0x7F);
    }

    [Fact]
    public void Meta_ByteGolden_AndRoundTrip()
    {
        RaftStoreMetaCodec.StructSize.Should().Be(41);
        Span<byte> idBytes = stackalloc byte[16];
        for (var i = 0; i < 16; i++) idBytes[i] = (byte)(0x10 + i);
        var votedFor = new NodeId(idBytes);
        var meta = new RaftStoreMeta(version: 1, term: 0x0102030405060708L, votedFor, appliedIndex: 42, snapshotIndex: 99);

        var blob = new byte[RaftStoreMetaCodec.StructSize];
        RaftStoreMetaCodec.Write(blob, meta);

        // 金样：[ver 1B][term 8B LE][votedFor 16B 原序][applied 8B LE][snapshotIndex 8B LE]
        //（= 旧手写 BuildMeta 的逐字节形态——旧版写入的存量 blob 必须可被新读面解析）
        var expected = new byte[41];
        expected[0] = 1;
        BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(1, 8), 0x0102030405060708L);
        votedFor.CopyTo(expected.AsSpan(9, 16));
        BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(25, 8), 42);
        BinaryPrimitives.WriteInt64LittleEndian(expected.AsSpan(33, 8), 99);
        blob.Should().Equal(expected);

        var parsed = RaftStoreMetaCodec.Read(blob);
        parsed.Version.Should().Be((byte)1);
        parsed.Term.Should().Be(0x0102030405060708L);
        parsed.VotedFor.Should().Be(votedFor);
        parsed.AppliedIndex.Should().Be(42);
        parsed.SnapshotIndex.Should().Be(99);
    }
}
