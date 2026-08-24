using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.Tests.Primitives;

public sealed class CrcFooterTests
{
    // === Crc32Footer ===

    [Fact]
    public void Crc32Footer_Default_CrcIsZero()
    {
        var footer = new Crc32Footer();
        footer.Crc.Should().Be(0u);
    }

    [Fact]
    public void Crc32Footer_SetCrc_ValueRetained()
    {
        var footer = new Crc32Footer { Crc = 0xDEADBEEF };
        footer.Crc.Should().Be(0xDEADBEEF);
    }

    [Fact]
    public void Crc32Footer_Size_Is4Bytes()
    {
        // [StructLayout(Size = 4)]
        System.Runtime.InteropServices.Marshal.SizeOf<Crc32Footer>().Should().Be(4);
    }

    // === Crc64Footer ===

    [Fact]
    public void Crc64Footer_Default_CrcIsZero()
    {
        var footer = new Crc64Footer();
        footer.Crc.Should().Be(0uL);
    }

    [Fact]
    public void Crc64Footer_SetCrc_ValueRetained()
    {
        var footer = new Crc64Footer { Crc = 0xCAFEBABE12345678 };
        footer.Crc.Should().Be(0xCAFEBABE12345678);
    }
}
