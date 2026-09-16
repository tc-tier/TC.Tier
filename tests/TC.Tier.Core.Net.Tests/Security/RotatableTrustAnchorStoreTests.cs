using FluentAssertions;
using TC.Tier.Core.Net.Security;
using Xunit;

namespace TC.Tier.Core.Net.Tests.Security;

/// <summary>
/// 可轮换信任锚（二期-H1——多锚并存过渡期验收）：
/// ① 轮换过渡窗内新旧公钥都被接受（轮换非全网原子——先/后升级节点互通）；
/// ② 过渡窗过期后旧钥拒绝（收敛回单锚）+ Prune 主动清理；
/// ③ 纪律保持：Save 不覆盖、无绑定首绑、未轮换行为与单锚一致；轮换幂等/无绑定拒绝。
/// </summary>
public class RotatableTrustAnchorStoreTests
{
    private static readonly byte[] KeyA = Enumerable.Repeat((byte)0xAA, NodeKeyPair.PublicKeySize).ToArray();
    private static readonly byte[] KeyB = Enumerable.Repeat((byte)0xBB, NodeKeyPair.PublicKeySize).ToArray();
    private static readonly byte[] KeyC = Enumerable.Repeat((byte)0xCC, NodeKeyPair.PublicKeySize).ToArray();

    private static RotatableTrustAnchorStore NewStore(out DateTimeMutator clock, DateTime? start = null)
    {
        var now = new DateTimeMutator(start ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        clock = now;
        return new RotatableTrustAnchorStore(now.Get);
    }

    /// <summary>测试时钟（旋钮——grace 过期确定性）。</summary>
    private sealed class DateTimeMutator(DateTime value)
    {
        public Func<DateTime> Get => () => _value;
        private DateTime _value = value;
        public void Advance(TimeSpan d) => _value += d;
    }

    /// <summary>验收①：过渡窗内新旧并存。</summary>
    [Fact]
    public void BeginRotation_BothKeysTrusted_DuringGraceWindow()
    {
        var store = NewStore(out var clock);
        var node = NodeId.NewRandom();
        store.Save(node, KeyA);

        store.BeginRotation(node, KeyB, TimeSpan.FromMinutes(10));

        store.IsTrusted(node, KeyA).Should().BeTrue("旧钥在过渡窗内仍被接受（后升级节点）");
        store.IsTrusted(node, KeyB).Should().BeTrue("新钥立即生效（先升级节点）");
        store.IsTrusted(node, KeyC).Should().BeFalse("无关钥不受信");
        store.TryGet(node, out var current).Should().BeTrue();
        current.ToArray().Should().Equal(KeyB, "单钥契约只暴露 current（新钥）");
    }

    /// <summary>验收②：过期即拒 + Prune 清理。</summary>
    [Fact]
    public void GraceWindowExpiry_OldKeyRejected_PruneCollapsesToSingleAnchor()
    {
        var store = NewStore(out var clock);
        var node = NodeId.NewRandom();
        store.Save(node, KeyA);
        store.BeginRotation(node, KeyB, TimeSpan.FromMinutes(10));

        clock.Advance(TimeSpan.FromMinutes(10));

        store.IsTrusted(node, KeyA).Should().BeFalse("窗过旧钥拒绝——收敛回单锚");
        store.IsTrusted(node, KeyB).Should().BeTrue("current 不受影响");
        store.PruneExpired().Should().Be(1, "清理一条过期 previous");
        store.PruneExpired().Should().Be(0, "幂等——已清理不再计");
    }

    /// <summary>验收③：纪律保持（不覆盖/幂等/无绑定拒绝/错误参数拒绝）。</summary>
    [Fact]
    public void Discipline_SaveNoOverwrite_RotationGuards()
    {
        var store = NewStore(out var clock);
        var node = NodeId.NewRandom();

        store.Save(node, KeyA);
        var act = () => store.Save(node, KeyB);
        act.Should().Throw<InvalidOperationException>("已有绑定拒绝静默覆盖（换钥走显式轮换）");

        store.Invoking(s => s.BeginRotation(node, KeyB, TimeSpan.FromMinutes(5)))
            .Should().NotThrow("轮换登记——KeyA 降为过渡 previous、KeyB 即时生效");
        store.IsTrusted(node, KeyA).Should().BeTrue("过渡窗内旧钥仍受信");

        clock.Advance(TimeSpan.FromMinutes(5));   // KeyA 过渡窗到期
        store.IsTrusted(node, KeyA).Should().BeFalse("窗过旧钥拒绝");
        store.Invoking(s => s.BeginRotation(node, KeyB, TimeSpan.FromMinutes(5)))
            .Should().NotThrow("同钥重入幂等——current==newKey 直返");
        store.IsTrusted(node, KeyA).Should().BeFalse("幂等重入不复活旧钥过渡窗");

        var orphan = () => store.BeginRotation(NodeId.NewRandom(), KeyC, TimeSpan.FromMinutes(5));
        orphan.Should().Throw<InvalidOperationException>("无既有绑定——首绑走 Save");

        var badKey = () => store.Save(NodeId.NewRandom(), new byte[8]);
        badKey.Should().Throw<ArgumentException>("公钥长度校验");
        var zeroGrace = () => store.BeginRotation(node, KeyC, TimeSpan.Zero);
        zeroGrace.Should().Throw<ArgumentOutOfRangeException>("零窗 = 无并存语义，显式拒绝");
    }
}
