using System.Buffers.Binary;
using FluentAssertions;
using TC.Tier.Contracts.Storage;
using TC.Tier.Core.Net;
using TC.Tier.Products.Net.Queue;
using TC.Tier.Products.Net.Routing;
using Xunit;

namespace TC.Tier.Products.Tests.Queue;

/// <summary>
/// 复制命令/封套线格式字节金样（[WireMessage] 迁移的格式钉）：
/// 期望字节按布局声明手工推导（[Tag 1B][CorrId 8B][子类字段声明序]；bool/byte = 1B；
/// 整型 = 小端（enum 成员走 byte 载体 1B）；blob = [Len 4B][bytes]；列表 = [Count 4B][item×N]；
/// 嵌套 [BinaryLayout] struct = 按声明偏移整帧）——生成器输出与推导不符 = 格式漂移，红。
/// </summary>
public class QueueCommandGoldenTests
{
    private static byte[] Hex(string hex)
    {
        hex = hex.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        var buf = new byte[hex.Length / 2];
        for (var i = 0; i < buf.Length; i++)
            buf[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return buf;
    }

    private static void AssertGolden(byte[] actual, string expectedHex)
    {
        actual.Should().BeEquivalentTo(Hex(expectedHex),
            o => o.WithStrictOrdering(),
            "线格式字节金样——与布局声明推导不符即格式漂移。actual=" + Convert.ToHexString(actual));
    }

    private static void Corr(ulong v, Span<byte> dest) => BinaryPrimitives.WriteUInt64LittleEndian(dest, v);
    private static byte[] CorrBytes(ulong v)
    {
        var b = new byte[8];
        Corr(v, b);
        return b;
    }

    // ── Enqueue：[01][CorrId 8B][Delayed 1B][DueTime 8B][Idempotent 1B][ProducerId 8B][Seq 8B][Len 4B][payload] ──

    [Fact]
    public void Enqueue_Golden()
    {
        var bytes = QueueCommandCodec.Encode(new QueueEnqueueCommand
        {
            Correlation = 0x0102030405060708,
            Delayed = true,
            DueTime = 42,
            Idempotent = false,
            ProducerId = 7,
            Seq = 9,
            Payload = new byte[] { 0xAA, 0xBB },
        });

        var expected = """
            01
            0807060504030201
            01
            2A00000000000000
            00
            0700000000000000
            0900000000000000
            02000000
            AABB
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var e = decoded.Should().BeOfType<QueueEnqueueCommand>().Subject;
        e.Correlation.Should().Be(0x0102030405060708);
        e.Delayed.Should().BeTrue();
        e.DueTime.Should().Be(42);
        e.Idempotent.Should().BeFalse();
        e.ProducerId.Should().Be(7);
        e.Seq.Should().Be(9);
        e.Payload.ToArray().Should().BeEquivalentTo(new byte[] { 0xAA, 0xBB });
    }

    // ── Ack：[02][CorrId 8B][Len 4B][name][Epoch 8B][Count 4B][addr 16B ×N] ──

    [Fact]
    public void Ack_Golden()
    {
        var addr = new LogicalAddress(1, 2, 3);
        var bytes = QueueCommandCodec.Encode(new QueueAckCommand
        {
            Correlation = 1,
            Name = "g"u8.ToArray(),
            Epoch = 5,
            Addresses = [addr],
        });

        var expected = """
            02
            0100000000000000
            01000000 67
            0500000000000000
            01000000
            01000000 02000000 0300000000000000
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var a = decoded.Should().BeOfType<QueueAckCommand>().Subject;
        System.Text.Encoding.UTF8.GetString(a.Name.Span).Should().Be("g");
        a.Epoch.Should().Be(5);
        a.Addresses.Should().ContainSingle().Which.Should().Be(addr);
    }

    // ── Group Create：[03][CorrId 8B][Op 1B][Len 4B][name][StartAt 4B][Addr 16B][VisMs 8B][MaxRedeliveries 4B][Home 16B] ──

    [Fact]
    public void GroupCreate_Golden()
    {
        var bytes = QueueCommandCodec.Encode(new QueueGroupCommand
        {
            Correlation = 2,
            Op = (byte)QueueGroupOp.Create,
            Name = "g"u8.ToArray(),
            StartAt = (byte)GroupStartAt.Earliest,
            StartAddress = LogicalAddress.Empty,
            VisibilityTimeoutMs = 60000,
            MaxRedeliveries = 16,
            Home = NodeId.Empty,
        });

        var expected = """
            03
            0200000000000000
            01
            01000000 67
            00
            00000000 00000000 0000000000000000
            60EA000000000000
            10000000
            00000000000000000000000000000000
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var g = decoded.Should().BeOfType<QueueGroupCommand>().Subject;
        g.Operation().Should().Be(QueueGroupOp.Create);
        System.Text.Encoding.UTF8.GetString(g.Name.Span).Should().Be("g");
        g.StartPoint().Should().Be(GroupStartAt.Earliest);
        g.VisibilityTimeoutMs.Should().Be(60000);
        g.MaxRedeliveries.Should().Be(16);
    }

    // ── Expire Claim：[04][CorrId 8B][Op 1B][Len 4B][name][Count 4B=0] ──

    [Fact]
    public void ExpireClaim_Golden()
    {
        var bytes = QueueCommandCodec.Encode(new QueueExpireCommand
        {
            Correlation = 3,
            Op = (byte)QueueExpireOp.Claim,
            Name = "g"u8.ToArray(),
        });

        var expected = """
            04
            0300000000000000
            02
            01000000 67
            00000000
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var x = decoded.Should().BeOfType<QueueExpireCommand>().Subject;
        x.Operation().Should().Be(QueueExpireOp.Claim);
        x.Entries.Should().BeEmpty();
    }

    // ── Expire UpdateEntries：条目帧 = [Addr 16B][Count 4B][Dead 1B] ──

    [Fact]
    public void ExpireUpdateEntries_Golden()
    {
        var bytes = QueueCommandCodec.Encode(new QueueExpireCommand
        {
            Correlation = 4,
            Op = (byte)QueueExpireOp.UpdateEntries,
            Name = "g"u8.ToArray(),
            Entries = [new QueueExpireEntryFrame(new LogicalAddress(9, 0, 11), 2, true)],
        });

        var expected = """
            04
            0400000000000000
            01
            01000000 67
            01000000
            09000000 00000000 0B00000000000000
            02000000
            01
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var x = decoded.Should().BeOfType<QueueExpireCommand>().Subject;
        var entry = x.Entries.Should().ContainSingle().Subject;
        entry.Address.Should().Be(new LogicalAddress(9, 0, 11));
        entry.RedeliveryCount.Should().Be(2);
        entry.DeadLettered.Should().Be(1);
    }

    // ── Home：[05][CorrId 8B][Len 4B][name][home 16B] ──

    [Fact]
    public void Home_Golden()
    {
        var homeBytes = Enumerable.Range(0, 16).Select(i => (byte)(i + 1)).ToArray();
        var bytes = QueueCommandCodec.Encode(new QueueHomeCommand
        {
            Correlation = 5,
            Name = "g"u8.ToArray(),
            NewHome = new NodeId(homeBytes),
        });

        var expected = """
            05
            0500000000000000
            01000000 67
            0102030405060708090A0B0C0D0E0F10
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var h = decoded.Should().BeOfType<QueueHomeCommand>().Subject;
        h.NewHome.Should().Be(new NodeId(homeBytes));
    }

    // ── Retention：[06][CorrId 8B][HasTarget 1B][Target 16B] ──

    [Fact]
    public void Retention_Golden()
    {
        var bytes = QueueCommandCodec.Encode(new QueueRetentionCommand
        {
            Correlation = 6,
            HasTarget = true,
            Target = new LogicalAddress(3, 0, 100),
        });

        var expected = """
            06
            0600000000000000
            01
            03000000 00000000 6400000000000000
            """;
        AssertGolden(bytes, expected);

        QueueCommandCodec.TryDecode(bytes, out var decoded).Should().BeTrue();
        var r = decoded.Should().BeOfType<QueueRetentionCommand>().Subject;
        r.HasTarget.Should().BeTrue();
        r.Target.Should().Be(new LogicalAddress(3, 0, 100));
    }

    // ── 畸形防御：未知 tag / 截断 = false（确定性拒绝的入口）──

    [Fact]
    public void TryDecode_Malformed_ReturnsFalse()
    {
        QueueCommandCodec.TryDecode([0xFF], out _).Should().BeFalse("未知 tag");
        QueueCommandCodec.TryDecode([], out _).Should().BeFalse("空帧");
        QueueCommandCodec.TryDecode([0x01, 0x00], out _).Should().BeFalse("截断");
    }

    // ── 封套族金样：Ok 全帧 / Leader 提示 / 拒绝文本 ──

    [Fact]
    public void Reply_Golden()
    {
        var ok = GroupApplyReplyCodec.Encode(
            new GroupApplyResult(GroupApplyStatus.Ok, new LogicalAddress(1, 2, 3), 77, null).ToReply());
        AssertGolden(ok, """
            00
            01
            01000000 02000000 0300000000000000
            4D00000000000000
            """);
        GroupApplyReplyCodec.TryDecode(ok, out var okReply).Should().BeTrue();
        okReply!.ToResult().Should().Be(new GroupApplyResult(GroupApplyStatus.Ok, new LogicalAddress(1, 2, 3), 77, null));

        var stale = GroupApplyReplyCodec.Encode(
            new GroupApplyResult(GroupApplyStatus.StaleEpoch, default, 0, "epoch").ToReply());
        AssertGolden(stale, """
            01
            05000000 65706F6368
            """);

        var hint = GroupApplyReplyCodec.Encode(
            new GroupApplyResult(GroupApplyStatus.NotLeaderHint, default, 0, NodeId.Empty.ToString()).ToReply());
        hint.Length.Should().Be(1 + NodeId.Size, "NotLeaderHint = [tag][leader 16B]");
        hint[0].Should().Be(0x03);
        GroupApplyReplyCodec.TryDecode(hint, out var hintReply).Should().BeTrue();
        hintReply.Should().BeOfType<GroupApplyLeaderReply>();
    }
}
