using TC.Tier.Core.Primitives;
using TC.Tier.Runtime.Structures.Ring;
using FluentAssertions;

namespace TC.Tier.Runtime.Tests.Structures.Ring;

/// <summary>
/// Ring 2PC 事务测试——ITransactionParticipant 的 Prepare/ConfirmCommitted/OnCommitted。
/// 覆盖 final review 缺口：Transaction partial 此前零测试覆盖。
/// <para>★ 接入形态（当前 API）：TestVolume 组合根 + NewRing&lt;long&gt; 一步生命周期。</para>
/// </summary>
public class RingTransactionTests
{
    [Fact]
    public void ConfirmCommitted_Advances_LastCommittedSeq_Monotonically()
    {
        // ConfirmCommitted(5) 后 ConfirmCommitted(3)（旧 seq）应被忽略——CAS 单调拒绝回退
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.LastCommittedSeq.Should().Be(-1, "初始未参与事务");

            ring.ConfirmCommitted(5);
            ring.LastCommittedSeq.Should().Be(5);

            ring.ConfirmCommitted(3);   // 旧 seq，应被忽略
            ring.LastCommittedSeq.Should().Be(5, "旧 seq 不应回退 LastCommittedSeq");

            ring.ConfirmCommitted(10);
            ring.LastCommittedSeq.Should().Be(10);
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void OnCommitted_FiresImmediately_IfAlreadyCommitted()
    {
        // 先 ConfirmCommitted(10)，再 OnCommitted(5, callback) → callback 同步触发（5 <= 10）
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.ConfirmCommitted(10);

            bool fired = false;
            ring.OnCommitted(5, () => fired = true);
            fired.Should().BeTrue("已提交到更高 seq(10)，OnCommitted(5) 应立即同步触发");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public void OnCommitted_RegistersAndFires_OnLaterConfirm()
    {
        // OnCommitted(20, callback)（20 > 当前），callback 暂不触发；ConfirmCommitted(20) 后触发
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            bool fired = false;
            ring.OnCommitted(20, () => fired = true);
            fired.Should().BeFalse("未提交到 seq 20，callback 不应触发");

            ring.ConfirmCommitted(20);
            fired.Should().BeTrue("ConfirmCommitted(20) 后 callback 应触发");
        }
        finally { vol.Dispose(); }
    }

    [Fact]
    public async Task Prepare_FlushesData_AdvancesFlushedUntil()
    {
        // Prepare(seq) 内部 FlushUntil(TailAddress) + WriteMeta → FlushedUntilAddress 应推进到 TailAddress
        var (settings, vol) = TestRingSettingsFactory.Create();
        try
        {
            using var ring = TestRingSettingsFactory.NewRing<long>(vol, settings);
            ring.Write(1L, new byte[] { 2, 3 });
            LogicalAddress tail = ring.TailAddress;
            tail.Should().BeGreaterThan(LogicalAddress.Empty);

            await ring.PrepareAsync(seq: 1, CancellationToken.None);
            ring.FlushedUntilAddress.Should().BeGreaterThanOrEqualTo(tail,
                "Prepare 内部 FlushUntil(TailAddress) 应把数据落盘，推进 FlushedUntilAddress");
        }
        finally { vol.Dispose(); }
    }
}
