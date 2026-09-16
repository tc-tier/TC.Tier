using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Transport;
using TC.Tier.Core.Net.Wire;
using Xunit;

namespace TC.Tier.Core.Net.Tests;

/// <summary>
/// 保活计票契约测试（spec-11 §2.3——"3 次未答断连"计票语义：连续性/归零/边界）。
/// </summary>
public class KeepaliveTrackerTests
{
    [Fact]
    public void 连续未答_达上限后断连()
    {
        var tracker = new KeepaliveTracker(maxUnanswered: 3);
        tracker.OnSent().Should().BeFalse("第 1 次未答——给一个周期应答机会");
        tracker.OnSent().Should().BeFalse("第 2 次未答");
        tracker.OnSent().Should().BeFalse("第 3 次未答");
        tracker.OnSent().Should().BeTrue("第 4 个周期仍无应答 = 此前 3 次全部未答——断连");
    }

    [Fact]
    public void 入站保活归零_不误断活跃链路()
    {
        var tracker = new KeepaliveTracker(maxUnanswered: 3);
        tracker.OnSent().Should().BeFalse();
        tracker.OnSent().Should().BeFalse();
        tracker.OnReceived();                       // 对端保活到达——连续性中断
        tracker.OnSent().Should().BeFalse();
        tracker.OnSent().Should().BeFalse();
        tracker.OnSent().Should().BeFalse();
        tracker.OnSent().Should().BeTrue();
    }

    [Fact]
    public void 上限为1_单次未答即断()
    {
        var tracker = new KeepaliveTracker(maxUnanswered: 1);
        tracker.OnSent().Should().BeFalse();
        tracker.OnSent().Should().BeTrue();
    }
}
