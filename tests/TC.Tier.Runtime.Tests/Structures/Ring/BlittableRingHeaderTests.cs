using System.Runtime.CompilerServices;
using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// BlittableRingHeader codec 往返测试（v2.0：KeyLength 退役，Reserved 4B）。
/// </summary>
public class BlittableRingHeaderTests
{
    [Fact]
    public void HeaderCodec_WriteRead_RoundTrip()
    {
        var hdr = new BlittableRingHeader
        {
            MagicValue = BlittableRingHeader.Magic,
            Version = BlittableRingHeader.CurrentVersion,
            Flags = BlittableRingHeader.DefaultFlags,
            PayloadLength = 128,
            PaddingLength = 0,
            PreviousAddress = new LogicalAddress(0, 0x1000),
        };
        Span<byte> buf = stackalloc byte[BlittableRingHeaderCodec.StructSize];
        BlittableRingHeaderCodec.Write(buf, in hdr);

        var read = BlittableRingHeaderCodec.Read(buf);
        read.MagicValue.Should().Be(BlittableRingHeader.Magic);
        read.Version.Should().Be(BlittableRingHeader.CurrentVersion);
        read.PayloadLength.Should().Be(128u);
        read.PreviousAddress.Should().Be(new LogicalAddress(0, 0x1000));
    }

    [Fact]
    public void HeaderSize_Is_40()
    {
        BlittableRingHeaderCodec.StructSize.Should().Be(40);
        Unsafe.SizeOf<BlittableRingHeader>().Should().Be(40);
    }

    [Fact]
    public void Version_IsV2_GenericRefactor()
    {
        // v2.0（泛型改版）：KeyLength 退役（key 长度=类型事实），旧盘（1.x）TryReadHeader 直接拒
        BlittableRingHeader.CurrentVersion.Should().Be((ushort)((2 << 8) | 0));
    }
}
