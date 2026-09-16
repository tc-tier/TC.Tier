using FluentAssertions;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// ResponseDedupWindow 契约测试（spec-12 §5.2 at-least-once——识别已服务请求不重复执行、
/// 缓存响应重放、窗口有界淘汰最老）。
/// </summary>
public class ResponseDedupWindowTests
{
    [Fact]
    public void 首见_返回true执行()
    {
        var window = new ResponseDedupWindow(4);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 100, out var cached).Should().BeTrue();
        cached.Should().BeNull();
    }

    [Fact]
    public void 重发到达_未回复_不执行无缓存()
    {
        var window = new ResponseDedupWindow(4);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 100, out _);

        window.TryBeginServe(from, 100, out var cached).Should().BeFalse("已服务——不重复执行");
        cached.Should().BeNull("handler 未回复过——重放面无响应（对端继续超时重试）");
    }

    [Fact]
    public void 重发到达_已回复_缓存响应重放()
    {
        var window = new ResponseDedupWindow(4);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 100, out _);
        window.RecordResponse(from, 100, new byte[] { 9, 9 });

        window.TryBeginServe(from, 100, out var cached).Should().BeFalse();
        cached.Should().Equal(new byte[] { 9, 9 }, "缓存响应重放——不重复执行");
    }

    [Fact]
    public void 唯一域_来源节点隔离()
    {
        var window = new ResponseDedupWindow(4);
        var a = NodeId.NewRandom();
        var b = NodeId.NewRandom();
        window.TryBeginServe(a, 100, out _);

        window.TryBeginServe(b, 100, out _).Should().BeTrue("CorrId 唯一域在发起端——不同来源同号互不相干");
    }

    [Fact]
    public void 窗口有界_淘汰最老()
    {
        var window = new ResponseDedupWindow(2);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 1, out _);
        window.TryBeginServe(from, 2, out _);
        window.TryBeginServe(from, 3, out _);   // 容量 2——淘汰最老（1）

        window.TryBeginServe(from, 1, out _).Should().BeTrue("已淘汰条目重发到达 = 视为首见重新执行（窗口有界语义）");
        window.Count.Should().Be(2, "容量恒定——重进 1 又淘汰当前最老（FIFO）");
    }

    [Fact]
    public void 未占位的RecordResponse_无效果()
    {
        var window = new ResponseDedupWindow(4);
        var from = NodeId.NewRandom();
        var act = () => window.RecordResponse(from, 42, new byte[] { 1 });
        act.Should().NotThrow();
        window.Count.Should().Be(0);
    }

    // ═══ 字节软预算（大响应驻留上界——驱逐目标非准入门槛）═══

    [Fact]
    public void ByteBudget_OverBudget_EvictsOldestUntilWithinBudget()
    {
        var window = new ResponseDedupWindow(capacity: 8, maxBytes: 100);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 1, out _);
        window.RecordResponse(from, 1, new byte[60]);
        window.TryBeginServe(from, 2, out _);
        window.RecordResponse(from, 2, new byte[60]);   // 累计 120 > 100——驱逐最老（1）

        window.TryBeginServe(from, 1, out var cached1).Should().BeTrue("最老条目被字节预算驱逐——重发 = 首见重执行");
        window.CachedBytes.Should().Be(60, "回落预算内");
        window.TryBeginServe(from, 2, out var cached2).Should().BeFalse();
        cached2.Should().NotBeNull("预算内存活条目——缓存响应重放不重复执行");
    }

    [Fact]
    public void ByteBudget_SingleOversizeEntry_Retained_SoftBudgetNotAdmissionGate()
    {
        var window = new ResponseDedupWindow(capacity: 8, maxBytes: 64);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 1, out _);
        window.RecordResponse(from, 1, new byte[256]);   // 单条超预算——独占驻留（软预算=驱逐目标）

        window.CachedBytes.Should().Be(256, "超预算单条保留——'不缓存'会让重发永不获回复（破坏 at-least-once）");
        window.TryBeginServe(from, 1, out var cached).Should().BeFalse();
        cached.Should().NotBeNull().And.HaveCount(256, "重发放缓存重放——确认契约完整");
    }

    [Fact]
    public void ByteBudget_Replacement_BytesAccounted()
    {
        var window = new ResponseDedupWindow(capacity: 8, maxBytes: 1024);
        var from = NodeId.NewRandom();
        window.TryBeginServe(from, 1, out _);
        window.RecordResponse(from, 1, new byte[100]);
        window.RecordResponse(from, 1, new byte[30]);    // 重复应答（罕见）——旧缓存先减再记

        window.CachedBytes.Should().Be(30, "替换字节核算");
        window.TryBeginServe(from, 1, out var cached).Should().BeFalse();
        cached.Should().HaveCount(30);
    }

    [Fact]
    public void ByteBudget_UnplacedRecord_NoByteEffect()
    {
        var window = new ResponseDedupWindow(capacity: 4, maxBytes: 64);
        var from = NodeId.NewRandom();
        window.RecordResponse(from, 42, new byte[32]);   // 未占位——不缓存不记账

        window.CachedBytes.Should().Be(0);
    }
}
