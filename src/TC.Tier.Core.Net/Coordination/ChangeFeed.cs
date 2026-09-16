using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Coordination;

/// <summary>
/// CDC 变更导出（二期-E3 NETGAP-029——基于 E2 可恢复流原语）：
/// 订阅 raft 提交事件（EntryCommitted），把新提交的命令条目按序追加进
/// <see cref="ResumableFeedServer"/>（保留窗 + offset 续传）——外部系统按 offset 拉取，
/// 断线续传不丢不重（E2 语义继承）。
/// <para>★ 过滤：锚点 no-op/配置条目由 filter 豁免（缺省仅投递 Command 条目）；
///   消费端可从任意节点拉取（leader/follower 均有已提交日志副本——读面扩展点）。</para>
/// </summary>
public sealed class ChangeFeed : IDisposable
{
    private readonly RaftStateMachine _raft;
    private readonly IRaftStore _store;
    private readonly ResumableFeedServer _feed;
    private readonly Func<long, long, byte, bool>? _filter;   // (index, term, kind) → 是否导出
    private long _nextIndex = 1;                              // 下一个待导出 index（1 起——raft 日志 1 基）
    private long _commitTarget;                               // 最新提交目标（OnCommitted 更新）
    private int _catchingUp;                                  // 单飞追平（防并发 _nextIndex 竞争）
    // ★ 追平循环经 TaskSink 受控提交（TCSG138 存量清扫）——组取消 + drain + 异常观测面一体。
    private readonly TaskSink _catchUpSink = new("change-feed-catchup");
    private readonly ILogger? _logger;

    /// <summary>底层可恢复流（诊断/管理面）。</summary>
    public ResumableFeedServer Feed => _feed;

    /// <summary>构造并挂载（transport 为节点承载传输——管理域随节点生命周期）。</summary>
    /// <param name="raft">raft 状态机（EntryCommitted 事件源）。</param>
    /// <param name="store">存储端口（已提交条目读取——leader/follower 均有副本）。</param>
    /// <param name="transport">节点承载传输（feed 域挂载面）。</param>
    /// <param name="domain">feed 域号（注册区 0x60-0xAF 使用方自管）。</param>
    /// <param name="feedId">流标识。</param>
    /// <param name="retention">保留窗块数（断线续传容错深度）。</param>
    /// <param name="filter">条目过滤（null = 仅 Command 条目——锚点/配置条目豁免）。</param>
    /// <param name="logger">日志（可选）。</param>
    public ChangeFeed(RaftStateMachine raft, IRaftStore store, IProtocolTransport transport,
        byte domain, Opaque16 feedId, int retention = 4096,
        Func<long, long, byte, bool>? filter = null, ILogger? logger = null)
    {
        _raft = raft;
        _store = store;
        _filter = filter;
        _logger = logger;
        _feed = new ResumableFeedServer(transport, domain, feedId, retention, logger);
        raft.EntryCommitted += OnCommitted;
    }

    /// <summary>提交推进 → 更新目标并单飞启动追平（并发提交合并进在途循环——_nextIndex 无竞争）。</summary>
    private void OnCommitted(long commitIndex)
    {
        var current = Interlocked.Read(ref _commitTarget);
        if (commitIndex > current)
            Interlocked.CompareExchange(ref _commitTarget, commitIndex, current);
        TryStartCatchUp();
    }

    private void TryStartCatchUp()
    {
        if (Interlocked.Exchange(ref _catchingUp, 1) != 0) return;   // 已有追平在跑——其循环读最新 target 自续
        // ★ 尽力语义：Dispose 竞窗（退订与事件在途并发）下提交即 ODE——上抛会打断 raft 循环
        //   事件面（fail-fast），吞掉并复位单飞旗标（追平已无意义，随 Dispose 收口）。
        try { _catchUpSink.Submit(async _ => await CatchUpLoopAsync().ConfigureAwait(false)); }
        catch (ObjectDisposedException) { Volatile.Write(ref _catchingUp, 0); }
    }

    /// <summary>追平循环体（读最新 target 自续——退出后新提交经 OnCommitted 重拉，幂等单飞）。</summary>
    private async Task CatchUpLoopAsync()
    {
        try
        {
            while (true)
            {
                var target = Interlocked.Read(ref _commitTarget);
                if (_nextIndex > target) break;   // 已追平
                await CatchUpToAsync(target).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug("CDC 追平异常（等待下次提交重试）：{Message}", ex.Message);
        }
        finally
        {
            Volatile.Write(ref _catchingUp, 0);
        }
        // 追平期间新提交到达 → 重新拉起（TryStartCatchUp 幂等单飞）；Dispose 竞窗 ODE 经 sink 观测面收口
        if (Interlocked.Read(ref _commitTarget) >= _nextIndex)
        {
            try { TryStartCatchUp(); }
            catch (ObjectDisposedException) { }
        }
    }

    private async Task CatchUpToAsync(long target)
    {
        while (_nextIndex <= target)
        {
            (long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content) entry = default;
            var found = false;
            await foreach (var e in _store.ReadEntriesAsync(_nextIndex).ConfigureAwait(false))
            {
                if (e.Index == _nextIndex)
                {
                    entry = e;
                    found = true;
                    break;
                }
                if (e.Index > _nextIndex) break;   // 越过——缺口（不应发生：顺序消费）
            }
            if (!found) return;   // 条目尚不可读——等下一次提交事件驱动

            var exportable = _filter is { } f ? f(entry.Index, entry.Term, entry.Kind) : entry.Kind == RaftEntryKind.Command;
            if (exportable)
                _feed.Append(entry.Content);
            _nextIndex++;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _raft.EntryCommitted -= OnCommitted;
        _catchUpSink.Dispose();   // 有界 drain 在途追平（body 自带 catch-all——正常瞬完）
        _feed.Dispose();
    }
}
