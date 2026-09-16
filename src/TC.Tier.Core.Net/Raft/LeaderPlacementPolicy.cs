using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 优先 Leader 放置策略（二期-F7——DDR-F7）：偏好序声明 + 观测循环 + 既有
/// <see cref="RaftStateMachine.TransferLeadershipAsync"/>（编排面——选举协议零改动）。
/// <para>★ 迁移三要素（齐备才发起，防抖）：① 当前 leader 偏好位置劣于某存活 voter；
/// ② 该成员已追平（leader 视角 matchIndex == commitIndex——数据安全搬迁）；
/// ③ 无在途转让（同目标未完成不重复发起——转让超时由引擎自愈回退）。</para>
/// <para>★ 偏好序语义：列表顺序即优先级（越前越优）；不在列表中的 voter 视为最低优先；
/// 成员宕机 = 追平判定自然跳过。witness 因 IsFullVoter 门天然不可承接（F2）。</para>
/// </summary>
public sealed class LeaderPlacementPolicy : IAsyncDisposable
{
    private readonly RaftStateMachine _raft;
    private readonly IReadOnlyList<NodeId> _preferenceOrder;
    private readonly TimeSpan _interval;
    private System.Threading.Timer? _timer;
    // ★ 转让发起经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面 + Dispose 有界 drain。
    private readonly TaskSink _transferSink = new("leader-placement");
    private int _inFlight;   // 防抖旗标（同一时刻至多一个在途转让——tick 串行天然单候选）
    private int _started;

    /// <param name="raft">目标组引擎（本节点视角——非 leader 时策略静默空转）。</param>
    /// <param name="preferenceOrder">偏好序（越前越优——leader 应尽量落在靠前成员上）。</param>
    /// <param name="interval">观测周期（缺省 1s）。</param>
    public LeaderPlacementPolicy(RaftStateMachine raft, IReadOnlyList<NodeId> preferenceOrder,
        TimeSpan? interval = null)
    {
        _raft = raft ?? throw new ArgumentNullException(nameof(raft));
        _preferenceOrder = preferenceOrder ?? throw new ArgumentNullException(nameof(preferenceOrder));
        _interval = interval ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>启动观测循环。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _timer = new System.Threading.Timer(static s => ((LeaderPlacementPolicy)s!).Tick(), this,
            _interval, _interval);
    }

    private void Tick()
    {
        try
        {
            var snapshot = _raft.GetStateSnapshot();
            if (snapshot.Role != RaftRole.Leader || snapshot.LeaderId is null) return;
            var leaderPos = PositionOf(snapshot.LeaderId.Value);

            // 偏好序中比当前 leader 更靠前的成员：找到第一个已追平者 → 迁移
            for (var pos = 0; pos < _preferenceOrder.Count && pos < leaderPos; pos++)
            {
                var candidate = _preferenceOrder[pos];
                if (!snapshot.Members.TryGetMatch(candidate, out var match)) continue;
                if (match != snapshot.CommitIndex) continue;   // 未追平——数据安全搬迁条件
                if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0) continue;   // 已有在途转让——下一 tick 重估
                // ★ 尽力语义：Dispose 竞窗下提交即 ODE——Tick 自带 catch-all 不崩但旗标会卡死，此处
                //   显式复位（转让作废，下一 tick 重估）。
                try { _transferSink.SubmitFast(_ => TransferAsync(candidate)); }   // 首段内联——原形态同步段即在 Timer 线程发起
                catch (ObjectDisposedException) { Volatile.Write(ref _inFlight, 0); }
                return;   // 每 tick 至多一次发起（防抖）
            }
        }
        catch
        {
            // 观测面尽力语义：快照竞态/换届瞬态——下一 tick 重估
        }
    }

    private async Task TransferAsync(NodeId target)
    {
        try
        {
            await _raft.TransferLeadershipAsync(target).ConfigureAwait(false);
        }
        catch
        {
            // 转让失败（换届/目标失格/超时）——静默，下一 tick 重估（pending 已清）
        }
        finally
        {
            Volatile.Write(ref _inFlight, 0);
        }
    }

    private int PositionOf(NodeId id)
    {
        for (var i = 0; i < _preferenceOrder.Count; i++)
            if (_preferenceOrder[i] == id) return i;
        return int.MaxValue;   // 不在偏好序 = 最低优先
    }

    /// <summary>收口观测循环。</summary>
    /// <returns>计时器已停、在途转让排空（有界 drain——转让超时由引擎自愈，超时仅告警）后完成。</returns>
    public async ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        await _transferSink.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>成员 matchIndex 查询扩展（leader 复制进度视图）。</summary>
internal static class RaftMemberStateExtensions
{
    public static bool TryGetMatch(this IReadOnlyList<RaftMemberState> members, NodeId id, out long match)
    {
        foreach (var m in members)
        {
            if (m.Id == id)
            {
                match = m.MatchIndex;
                return true;
            }
        }
        match = 0;
        return false;
    }
}
