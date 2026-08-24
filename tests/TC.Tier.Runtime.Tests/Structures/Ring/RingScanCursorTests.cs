using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 扫描游标测试。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt; 一步生命周期。</para>
/// </summary>
public class RingScanCursorTests
{
    [Fact]
    public void OpenScanCursor_Sequential_ReadsAllRecords()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            foreach (var k in new[] { 1L, 2L, 3L }) ring.Write(k, new byte[] { 0xFF });
            // ★ 游标从 device 读（冷热统一，对齐 LogBase.Cursor）——须先 flush 让数据落盘可见
            ring.FlushUntil(ring.TailAddress);

            using var cursor = ring.OpenScanCursor();
            int count = 0;
            while (cursor.MoveNext())
            {
                cursor.CurrentAddress.Should().BeGreaterThanOrEqualTo(LogicalAddress.Empty);
                cursor.CurrentRecordSize.Should().BeGreaterThan(0);
                count++;
            }
            count.Should().Be(3);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void OpenScanCursor_EmptyLog_ReturnsFalseImmediately()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            using var cursor = ring.OpenScanCursor();
            cursor.MoveNext().Should().BeFalse();
        }
        finally { vol.Dispose(); }
    }
}
