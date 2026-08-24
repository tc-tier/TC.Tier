using TC.Tier.Runtime.Tests.Structures.Ring;

namespace TC.Tier.Runtime.Tests.Transactions;

/// <summary>
/// 窗口契约 W（EnsureNoOpenTransaction 统一检查 helper）+ SessionReadScope（聚合 epoch + 覆盖层暴露）
/// 契约测试（session-manager-design.md §5.2/§3.2）。
/// <para>全同步形态——SessionReadScope 是 ref struct（栈生命周期护栏），async 方法体内不可声明。</para>
/// </summary>
public class SessionReadScopeTests
{
    private static SessionManager NewManager(
        params (string Name, ITransactionParticipant Participant)[] participants)
    {
        var m = SessionManager.Create(MemoryFileSystem.New(new MemoryFileSystemOptions()),
            "kv", HangingResolution.ForwardCommit, participants);
        m.Initialize();
        m.WaitForReady();
        return m;
    }

    [Fact]
    public void RuleW_OpenTxPeriod_DirectWriteFailFast_AfterTerminal_Passes()
    {
        using var m = NewManager(("p1", new FakeParticipant()));
        var pass = () => m.EnsureNoOpenTransaction();
        pass.Should().NotThrow("无开放事务——直写放行");

        using var s = m.OpenSession();
        s.Stage(() => { });
        m.OpenTxCount.Should().Be(1, "首个 Stage=开放事务");

        var act = () => m.EnsureNoOpenTransaction();
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*窗口契约 W*")
            .WithMessage("*OpenTxCount=1*", "消息携带开放计数");

        s.CommitAsync().AsTask().GetAwaiter().GetResult();
        m.OpenTxCount.Should().Be(0);
        pass.Should().NotThrow("事务终态后直写恢复");
    }

    [Fact]
    public void ReadScope_AggregatesEpochHolders_ExposesOverlay()
    {
        // 域=Ring（IEpochProtected 参与者）+ FakeParticipant（非 epoch 参与者）——
        // scope 只聚合 IEpochProtected 子集；区内零拷贝读正常；覆盖层经 scope 可达。
        var vol = new TestVolume();
        using var ring = TestRingSettingsFactory.NewRing<long>(vol,
            TestRingSettingsFactory.On(vol, "scope-ring", deleteOnClose: false));
        using var m = SessionManager.Create(vol.Fs, "kv", HangingResolution.ForwardCommit,
            ("ring", ring), ("fake", new FakeParticipant()));
        m.Initialize();
        m.WaitForReady();

        using var s = m.OpenSession();
        var addr = ring.Write(7L, new byte[] { 1, 2, 3 });   // 写在保护区外（Ring 写自进保护）

        s.State = new Dictionary<long, byte[]> { [7] = new byte[] { 9, 9 } };   // 组合层覆盖层挂点
        using (var scope = s.EnterReadScope())
        {
            scope.Session.Should().BeSameAs(s);
            ((Dictionary<long, byte[]>)scope.State!)[7].Should().BeEquivalentTo(new byte[] { 9, 9 },
                "覆盖层经 scope 暴露（RYW 组合层自管内容）");
            ring.GetValueSpan(addr).ToArray().Should().BeEquivalentTo(new byte[] { 1, 2, 3 },
                "区内零拷贝读正常（epoch 聚合护栏）");
        }
        // scope Dispose 后保护区解除（隐式断言：ring.Dispose 不报持保护残留）
        vol.Dispose();
    }

    [Fact]
    public void ReadScope_DisposedSession_Throws()
    {
        using var m = NewManager(("p1", new FakeParticipant()));
        var s = m.OpenSession();
        s.Dispose();
        System.Action act = () => _ = s.EnterReadScope();   // ref struct 返回不可做泛型参数——显式 Action
        act.Should().Throw<ObjectDisposedException>();
    }
}
