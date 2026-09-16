using FluentAssertions;
using TC.Tier.Core.Net.Channels;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Channels;

/// <summary>
/// 请求背压策略（二期-D5 NETGAP-014——验证矩阵 D5 行）：FailFast 缺省保持、
/// Queue 有界排队（槽位释放后通过/超时回落）、Degrade 满载降级专用异常、
/// Priority 预留预算准入（共享池满仍可入、超预留回落）。
/// </summary>
public class RequestBrokerBackpressureTests
{
    /// <summary>占满容量（回填 pending；返回句柄供释放）。</summary>
    private static RequestBroker.PendingRequest[] Fill(RequestBroker broker, int n)
    {
        var handles = new RequestBroker.PendingRequest[n];
        for (var i = 0; i < n; i++) handles[i] = broker.BeginRequest();
        return handles;
    }

    /// <summary>D5：FailFast 缺省——满载立即抛（既有语义保持，未声明域零行为变化）。</summary>
    [Fact]
    public void FailFast_Default_ThrowsWhenFull()
    {
        var broker = new RequestBroker(capacity: 2);
        var handles = Fill(broker, 2);

        var act = () => broker.BeginRequest();
        act.Should().Throw<InvalidOperationException>("满载 fail-fast——缺省语义保持");

        foreach (var h in handles) broker.Abandon(h);
    }

    /// <summary>D5：Degrade——满载抛专用降级异常（区别于失控 fail-fast 的语义面）。</summary>
    [Fact]
    public async Task Degrade_Full_ThrowsDegradeException()
    {
        var broker = new RequestBroker(capacity: 2);
        broker.SetBackpressurePolicy(0x70, BackpressurePolicy.Degrade);
        var handles = Fill(broker, 2);

        var act = async () => await broker.EnforceBackpressureAsync(0x70, CancellationToken.None);
        (await act.Should().ThrowAsync<RequestDegradeException>().WaitAsync(TimeSpan.FromSeconds(2)))
            .Which.Message.Should().Contain("满载降级", "降级失败面——产品据此 fallback/限流告警");

        foreach (var h in handles) broker.Abandon(h);
    }

    /// <summary>D5：Queue——满载排队等待槽位释放（Abandon 释放 → 通过；不抛）。</summary>
    [Fact]
    public async Task Queue_WaitsForSlot_ThenProceeds()
    {
        var broker = new RequestBroker(capacity: 2, queueWait: TimeSpan.FromSeconds(2));
        broker.SetBackpressurePolicy(0x71, BackpressurePolicy.Queue);
        var handles = Fill(broker, 2);

        var enforce = broker.EnforceBackpressureAsync(0x71, CancellationToken.None).AsTask();
        await Task.Delay(100);
        enforce.IsCompleted.Should().BeFalse("满载——Queue 排队等待");

        broker.Abandon(handles[0]);   // 槽位释放
        await enforce.WaitAsync(TimeSpan.FromSeconds(2));
        enforce.IsCompleted.Should().BeTrue("槽位释放后通过——排队有效");
    }

    /// <summary>D5：Queue 超时——排队上限耗尽回落 fail-fast（Enforce 直通，BeginRequest 抛）。</summary>
    [Fact]
    public async Task Queue_Timeout_FallsBackToFailFast()
    {
        var broker = new RequestBroker(capacity: 1, queueWait: TimeSpan.FromMilliseconds(150));
        broker.SetBackpressurePolicy(0x72, BackpressurePolicy.Queue);
        var handles = Fill(broker, 1);

        await broker.EnforceBackpressureAsync(0x72, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        // 排队超时直通——BeginRequest 侧仍 fail-fast
        var act = () => broker.BeginRequest(0x72);
        act.Should().Throw<InvalidOperationException>("排队超时回落 fail-fast");

        broker.Abandon(handles[0]);
    }

    /// <summary>D5：Priority——共享池满仍可入（预留预算），未声明域满载照抛；超预留回落。</summary>
    [Fact]
    public void Priority_ReserveBudget_AdmitsWhenFull()
    {
        var broker = new RequestBroker(capacity: 2, priorityReserve: 3);
        broker.SetBackpressurePolicy(0x73, BackpressurePolicy.Priority);
        var handles = Fill(broker, 2);

        // 未声明域——满载照抛（FailFast 缺省）
        var actPlain = () => broker.BeginRequest();
        actPlain.Should().Throw<InvalidOperationException>();

        // Priority 域——预留预算内准入（3 个额外槽位）
        var priorityHandles = new RequestBroker.PendingRequest[3];
        for (var i = 0; i < 3; i++)
            priorityHandles[i] = broker.BeginRequest(0x73);   // Priority 预留预算内准入（2 容量 + 3 预留 = 5）

        // 超预留（2 容量 + 3 预留 = 5 满）——第 6 个回落 fail-fast
        var actOver = () => broker.BeginRequest(0x73);
        actOver.Should().Throw<InvalidOperationException>("预留预算耗尽——回落 fail-fast");

        foreach (var h in handles) broker.Abandon(h);
        foreach (var h in priorityHandles) broker.Abandon(h);
    }

    /// <summary>D5：策略读面——未声明 = FailFast；声明后可读回。</summary>
    [Fact]
    public void Policy_DeclareAndRead()
    {
        var broker = new RequestBroker(capacity: 2);
        broker.GetBackpressurePolicy(0x70).Should().Be(BackpressurePolicy.FailFast, "未声明 = FailFast 缺省");
        broker.SetBackpressurePolicy(0x70, BackpressurePolicy.Queue);
        broker.GetBackpressurePolicy(0x70).Should().Be(BackpressurePolicy.Queue, "声明后读回");
    }
}
