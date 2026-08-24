using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring CRC 验证测试——RecordCodec.VerifyCrc 对正常 record 返回 true、损坏 record 返回 false。
/// 覆盖 final review 缺口：CRC 验证路径（scan/recovery 调）此前零覆盖。
/// <para>★ 经 RecordCodec.VerifyCrc（internal static + public method）直接调——与 nested Codec.VerifyCrc
///   委托的代码路径完全一致（相同参数），等价验证。</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt;；BlittableRingHeader 已是命名空间顶级。</para>
/// </summary>
public class BlittableRingCrcTests
{
    [Fact]
    public void VerifyCrc_ReturnsTrue_ForValidRecord()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(0x0102L, new byte[] { 10, 20, 30 });

            var fields = ring.GetFields(addr);
            int payloadLen = (int)fields.PayloadLength;
            int total = BlittableRingHeaderCodec.StructSize + payloadLen;
            Span<byte> record = ring.GetSpan(addr, total);

            bool ok = RecordCodec.VerifyCrc(record, BlittableRingHeader.DefaultFlags, total, BlittableRingHeaderCodec.Offset_Crc32C);
            ok.Should().BeTrue("正常 record 的 CRC 应校验通过");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void VerifyCrc_ReturnsFalse_ForCorruptedRecord()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress addr = ring.Write(0x0102L, new byte[] { 10, 20, 30 });

            var fields = ring.GetFields(addr);
            int payloadLen = (int)fields.PayloadLength;
            int total = BlittableRingHeaderCodec.StructSize + payloadLen;
            Span<byte> record = ring.GetSpan(addr, total);

            // ★ 翻转 payload 区 1 字节（避开 offset 36 的 CRC 字段，翻转 HeaderSize+1 即 key 第 2 字节）
            record[BlittableRingHeaderCodec.StructSize + 1] ^= 0xFF;

            bool ok = RecordCodec.VerifyCrc(record, BlittableRingHeader.DefaultFlags, total, BlittableRingHeaderCodec.Offset_Crc32C);
            ok.Should().BeFalse("损坏 payload 后 CRC 应校验失败");
        }
        finally { vol.Dispose(); }
    }
}
