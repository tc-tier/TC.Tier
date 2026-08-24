using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 头截断测试——验证 TruncatePrefix 推进 BeginAddress + 可选物理段回收 + 截断后扫描跳过。
/// <para>★ 截断是快照的必要前提——快照后截断旧前缀释放空间。</para>
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt;。</para>
/// </summary>
public class RingTruncateTests
{
    /// <summary>截断推进 BeginAddress——TruncatePrefix 后 BeginAddress 单调推进到截断点。</summary>
    [Fact]
    public void TruncatePrefix_AdvancesBeginAddress()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 64));
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 64));
            ring.Write(3L, TestRingSettingsFactory.MakePattern(0xCC, 64));
            ring.FlushUntil(ring.TailAddress);   // 全部落盘（截断前置条件）

            LogicalAddress oldBegin = ring.BeginAddress;
            LogicalAddress truncateAt = ring.FlushedUntilAddress;   // 截到末尾

            ring.TruncatePrefix(truncateAt);

            ring.BeginAddress.Should().Be(truncateAt, "TruncatePrefix 应推进 BeginAddress");
            ring.BeginAddress.Should().BeGreaterThan(oldBegin, "BeginAddress 应单调推进");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>截断点 beyond FlushedUntilAddress 抛异常——防丢未落盘数据。</summary>
    [Fact]
    public void TruncatePrefix_BeyondFlushedThrows()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 64));
            ring.FlushUntil(ring.TailAddress);
            // ★ 再写一条但不 flush——TailAddress 超过 FlushedUntilAddress（即未落盘区域）
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 64));
            LogicalAddress beyondFlushed = ring.TailAddress;   // 超过已落盘边界

            Action act = () => ring.TruncatePrefix(beyondFlushed);
            act.Should().Throw<InvalidOperationException>(
                "截断点 beyond FlushedUntilAddress 应抛异常（防丢未落盘数据）");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>截断点 <= BeginAddress 不回退（单调）。</summary>
    [Fact]
    public void TruncatePrefix_BelowBegin_NoOp()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            LogicalAddress originalBegin = ring.BeginAddress;

            // 新 Ring 的 BeginAddress = 引擎 MinAddress（空盘 = LogicalAddress.Empty，已是最小地址）。
            //   故用 BeginAddress 本身作为截断点——等价于"不推进"，应 no-op（单调不回退/不前进）。
            ring.TruncatePrefix(originalBegin);

            ring.BeginAddress.Should().Be(originalBegin, "截断点 <= BeginAddress 应 no-op（单调不回退）");
        }
        finally { vol.Dispose(); }
    }

    /// <summary>截断后扫描游标跳过截断区——OpenScanCursor 默认从新 BeginAddress 起。</summary>
    [Fact]
    public void TruncatePrefix_ScanSkipsTruncated()
    {
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);

            ring.Write(1L, TestRingSettingsFactory.MakePattern(0xAA, 64));
            LogicalAddress firstRecordAddr = ring.TailAddress;
            ring.Write(2L, TestRingSettingsFactory.MakePattern(0xBB, 64));
            ring.Write(3L, TestRingSettingsFactory.MakePattern(0xCC, 64));
            ring.FlushUntil(ring.TailAddress);

            int countBeforeTruncate;
            using (var cursorBefore = ring.OpenScanCursor())
            {
                countBeforeTruncate = 0;
                while (cursorBefore.MoveNext()) countBeforeTruncate++;
            }
            countBeforeTruncate.Should().Be(3, "截断前应扫到 3 条 record");

            // 截断掉第一条 record
            ring.TruncatePrefix(firstRecordAddr);

            // 截断后扫描——BeginAddress 已推进，第一条被跳过
            using var cursorAfter = ring.OpenScanCursor();
            int countAfterTruncate = 0;
            while (cursorAfter.MoveNext()) countAfterTruncate++;

            countAfterTruncate.Should().Be(2, "截断后应只扫到 2 条 record（第一条被截断跳过）");
        }
        finally { vol.Dispose(); }
    }
}
