using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring Flush 测试——FlushUntil/FlushUntilAsync 整页落盘 + FlushedUntilAddress 推进。
/// 覆盖 final review 缺口：Flush 路径此前零直接测试。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt; 一步生命周期。</para>
/// </summary>
public class RingFlushTests
{
    [Fact]
    public void FlushUntil_Advances_FlushedUntilAddress()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[] { 2, 3 });
            LogicalAddress tail = ring.TailAddress;
            // ★ 新文件初始水位 = 引擎 MinAddress（= BeginAddress）——未 flush 时 FlushedUntilAddress 仍停在初始水位
            ring.FlushedUntilAddress.Should().Be(ring.BeginAddress, "初始未 flush（FlushedUntilAddress 仍是引擎 MinAddress 初始值）");

            ring.FlushUntil(tail);
            ring.FlushedUntilAddress.Should().Be(tail, "FlushUntil(tail) 应推进 FlushedUntilAddress 到 tail");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void FlushUntil_NoOp_IfAlreadyFlushed_BeyondTarget()
    {
        // 先 FlushUntil 到高位，再 FlushUntil 较低值 → MonotonicUpdate 拒绝回退
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[100]);
            LogicalAddress tail = ring.TailAddress;
            ring.FlushUntil(tail);
            LogicalAddress flushedHigh = ring.FlushedUntilAddress;

            ring.FlushUntil(ring.BeginAddress);   // 较低值（初始水位），应被忽略
            ring.FlushedUntilAddress.Should().Be(flushedHigh, "FlushedUntilAddress 不应回退（MonotonicUpdate 拒绝）");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task FlushUntilAsync_RoundTrip()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[] { 2, 3, 4 });
            LogicalAddress tail = ring.TailAddress;

            await ring.FlushUntilAsync(tail);
            (ring.FlushedUntilAddress >= tail).Should().BeTrue();
        }
        finally { vol.Dispose(); }
    }
}
