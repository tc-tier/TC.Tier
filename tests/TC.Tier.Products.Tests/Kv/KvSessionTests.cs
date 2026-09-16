using System.Threading.Tasks;
using FluentAssertions;
using TC.Tier.Core.Epochs;
using TC.Tier.Products.Kv;
using Xunit;

namespace TC.Tier.Products.Tests.Kv;

/// <summary>
/// KvSession 会话契约测试（W2 验收——逐档位）：None/ReadMyWrites[缺省]/Serializable 三档读可见性 +
/// 原子批（F2 stage 隔离/全或无/提交点/同 key 串行）+ pending 收口 + EPVS epoch pin + 释放契约。
/// <para>★ mem 介质（TestVolume）；封闭形态 TierKvOfLongLong 作会话宿主。</para>
/// </summary>
public class KvSessionTests
{
    private static TierKvOptions Opts(string suffix)
        => TierKvOptions.Default.WithKvName("tier-kv-sess-" + suffix);

    // ═══ 逐档位读可见性（None vs ReadMyWrites——写集语义分水岭）═══

    [Fact]
    public async Task ReadMyWrites_OwnWriteSurvives_ConcurrentOverwrite()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("rmw"));
        using var s1 = kv.CreateSession();                       // 缺省 = ReadMyWrites（D3）
        using var s2 = kv.CreateSession(KvSessionConditions.None);

        await s1.PutFormattedAsync(1, 100L);
        (await s2.TryGetFormattedAsync(1)).Value.Should().Be(100L, "写立即应用——他人立即可见");

        // 他人并发覆盖同 key
        await s2.PutFormattedAsync(1, 200L);

        (await s1.TryGetFormattedAsync(1)).Value.Should().Be(100L, "RMW：本会话最后一次写胜出他人覆盖");
        (await s2.TryGetFormattedAsync(1)).Value.Should().Be(200L, "None：读恒走索引看最新");
        using var s3 = kv.CreateSession(KvSessionConditions.None);
        (await s3.TryGetFormattedAsync(1)).Value.Should().Be(200L, "新会话见最新已应用值");
    }

    [Fact]
    public async Task None_NoWriteSet_ReadsAlwaysLatest()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("none"));
        using var s = kv.CreateSession(KvSessionConditions.None);
        s.Conditions.Should().Be(KvSessionConditions.None);

        await s.PutFormattedAsync(1, 1L);
        // 他人覆盖后本会话读立即见新值（None 无写集遮蔽）
        using var other = kv.CreateSession();
        await other.PutFormattedAsync(1, 2L);
        (await s.TryGetFormattedAsync(1)).Value.Should().Be(2L, "None 读恒走索引——无 RMW 遮蔽");
    }

    [Fact]
    public async Task ReadMyWrites_TombstoneInWriteSet_OwnReadMisses()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("rmw-tomb"));
        using var s1 = kv.CreateSession();
        using var s2 = kv.CreateSession();

        await s1.PutFormattedAsync(1, 100L);
        (await s1.DeleteAsync(1)).Should().BeTrue();
        (await s1.TryGetFormattedAsync(1)).Found.Should().BeFalse("写集墓碑——本会话读未命中");
        (await s2.TryGetFormattedAsync(1)).Found.Should().BeFalse("删除已应用——他人也未命中");
    }

    [Fact]
    public async Task Serializable_SeesOwnWrites_TransitionsCompleteBetweenOps()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("serial"));
        using var session = kv.CreateSession(KvSessionConditions.Serializable);

        await session.PutFormattedAsync(1, 7L);
        (await session.TryGetFormattedAsync(1)).Value.Should().Be(7L, "Serializable 含 RMW 全集");

        // async-first 契约：pin 粒度 = 同步临界区——操作之间版本过渡可完成（无生命周期 pin、
        // 无线程亲和契约，异步续体自由跳转）
        var startVersion = kv.SessionEpvs.CurrentState().Version;
        kv.SessionEpvs.ExecuteStateMachine(
            new SimpleVersionSchemeStateMachine((oldV, newV) => { }, -1), spin: true)
            .Should().BeTrue("会话存活但无进行中临界区——版本过渡应完成");
        kv.SessionEpvs.CurrentState().Version.Should().BeGreaterThan(startVersion);

        // 过渡完成后会话继续可用（可见性语义不受版本推进影响）
        (await session.TryGetFormattedAsync(1)).Value.Should().Be(7L);
        await session.PutFormattedAsync(1, 8L);
        (await session.TryGetFormattedAsync(1)).Value.Should().Be(8L);
    }

    // ═══ 原子批（F2 多 key 全或无——stage 可见性隔离）═══

    [Fact]
    public async Task AtomicBatch_Abort_DropsEverything()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("batch-abort"));
        using var s1 = kv.CreateSession();
        using var s2 = kv.CreateSession(KvSessionConditions.None);

        s1.BeginAtomicBatch();
        await s1.PutFormattedAsync(10, 999L);
        (await s1.TryGetFormattedAsync(10)).Value.Should().Be(999L, "stage 隔离——本会话批内读可见");
        (await s2.TryGetFormattedAsync(10)).Found.Should().BeFalse("批写未提交——他人与索引不可见");

        s1.AbortBatch();
        (await s1.TryGetFormattedAsync(10)).Found.Should().BeFalse("回滚全丢弃——零应用");
        (await s2.TryGetFormattedAsync(10)).Found.Should().BeFalse();
    }

    [Fact]
    public async Task AtomicBatch_Commit_AllOrNothing_VisibleAndDurable()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("batch-commit"));
        using var s1 = kv.CreateSession();
        using var s2 = kv.CreateSession(KvSessionConditions.None);

        s1.BeginAtomicBatch();
        await s1.PutFormattedAsync(10, 111L);
        await s1.PutFormattedAsync(11, 222L);
        await s1.CommitBatchAsync();

        (await s2.TryGetFormattedAsync(10)).Value.Should().Be(111L, "提交后全应用");
        (await s2.TryGetFormattedAsync(11)).Value.Should().Be(222L);
        s1.IsBatchActive.Should().BeFalse();

        // 2PC 提交点已推进 + 批数据已落盘（Prepare 落盘语义）
        kv.RingParticipant.LastCommittedSeq.Should().BeGreaterThan(0, "ConfirmCommitted 已推进");
        kv.Ring.FlushedUntilAddress.Should().Be(kv.Ring.TailAddress, "批提交 Prepare 即刷盘");
    }

    [Fact]
    public async Task AtomicBatch_SameKey_LastWriteWins_AndPutThenDeleteStaysDeleted()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("batch-order"));
        using var s = kv.CreateSession();
        using var observer = kv.CreateSession(KvSessionConditions.None);

        s.BeginAtomicBatch();
        await s.PutFormattedAsync(1, 1L);
        await s.PutFormattedAsync(1, 2L);        // 同 key 两写——批内序 = 提交序
        await s.PutFormattedAsync(5, 50L);
        await s.DeleteAsync(5);                   // 批内写后删——终态删除
        await s.CommitBatchAsync();

        (await observer.TryGetFormattedAsync(1)).Value.Should().Be(2L, "同 key 批内最后一条胜出");
        (await observer.TryGetFormattedAsync(5)).Found.Should().BeFalse("批内 put+delete 终态删除");
    }

    [Fact]
    public async Task AtomicBatch_StateMachine_Guards()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("batch-guard"));
        using var s = kv.CreateSession();

        await FluentActions.Awaiting(() => s.CommitBatchAsync().AsTask())
            .Should().ThrowAsync<InvalidOperationException>("无批可提交");
        FluentActions.Invoking(s.AbortBatch).Should().Throw<InvalidOperationException>("无批可回滚");

        s.BeginAtomicBatch();
        FluentActions.Invoking(s.BeginAtomicBatch).Should().Throw<InvalidOperationException>("禁止嵌套");
        s.AbortBatch();
    }

    // ═══ pending 收口（CompletePendingAsync）═══

    [Fact]
    public async Task Pending_CommittedPolicy_FlushesImmediately()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("committed"));
        using var s = kv.CreateSession();

        var addr = await s.PutFormattedAsync(1, 1L, policy: KvCommitPolicy.Committed);
        kv.Ring.FlushedUntilAddress.Should().Be(addr, "Committed 缺省档——写即刷");
    }

    [Fact]
    public async Task Pending_FireAndForget_CompletePendingFlushes()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("pending"));
        using var s = kv.CreateSession();

        var addr = await s.PutFormattedAsync(1, 1L, policy: KvCommitPolicy.FireAndForget);
        kv.Ring.FlushedUntilAddress.Should().NotBe(addr, "FireAndForget 不即时刷");
        kv.Ring.FlushedUntilAddress.Should().BeLessThan(addr);

        await s.CompletePendingAsync();
        kv.Ring.FlushedUntilAddress.Should().BeGreaterOrEqualTo(addr, "CompletePending 刷至高水位");
    }

    // ═══ 释放契约 ═══

    [Fact]
    public async Task Dispose_Contract_CountsDown_AndRejectsOps()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("dispose"));
        var s = kv.CreateSession();
        kv.ActiveSessionCount.Should().Be(1);

        s.Dispose();
        kv.ActiveSessionCount.Should().Be(0, "会话释放回收计数");
        s.Dispose();   // 幂等

        await FluentActions.Awaiting(() => s.TryGetFormattedAsync(1).AsTask())
            .Should().ThrowAsync<ObjectDisposedException>("释放后操作拒绝");
    }

    [Fact]
    public async Task Dispose_DuringOpenBatch_TreatsAsAbort()
    {
        using var vol = new TestVolume();
        await using var kv = await TierKvOfLongLong.CreateAsync(vol.Fs, Opts("dispose-batch"));
        var s = kv.CreateSession();

        s.BeginAtomicBatch();
        await s.PutFormattedAsync(1, 1L);
        s.Dispose();   // 未提交批 = 全丢弃

        using var observer = kv.CreateSession(KvSessionConditions.None);
        (await observer.TryGetFormattedAsync(1)).Found.Should().BeFalse("未提交批随释放丢弃");
    }
}
