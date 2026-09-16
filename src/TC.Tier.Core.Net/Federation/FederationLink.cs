using TC.Tier.Core.Execution;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Federation;

/// <summary>
/// 联邦链路源侧处理器（二期-F6——DDR-F6）：挂源集群 leader 的 0x06 核心域——
/// 应答对端集群的拉取请求。ClusterTag 门（<see cref="FederationWire.ClusterTagMatches"/>）
/// 不匹配 = 拒链（Granted=0 + 本端标签回显供诊断）。
/// <para>★ 跨集群部署：handler 经传输的请求回调域挂载（RegisterCoreRequestHandler）；
/// 单进程/测试形态可直接以委托接入 <see cref="FederationLink"/>。</para>
/// </summary>
public sealed class FederationSourceHandler
{
    private readonly uint _localClusterTag;
    private readonly IRaftStore _store;

    /// <param name="localClusterTag">源集群标签（握手同源——错配即拒链）。</param>
    /// <param name="store">源组日志读面（已提交条目——联邦只投已提交事实）。</param>
    public FederationSourceHandler(uint localClusterTag, IRaftStore store)
    {
        _localClusterTag = localClusterTag;
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// 处理拉取请求：返回批次应答帧（Granted=0 拒链 / 空批次无增量 / 条目增量）。
    /// </summary>
    /// <param name="pullRequest">拉取请求帧（<see cref="FederationWire.TryDecodePull"/> 形态）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>批次应答帧（<see cref="FederationWire.TryDecodeBatch"/> 形态）：请求畸形/ClusterTag 错配 = 拒链帧（Granted=0 + 本端标签）；
    /// 正常 = 自请求水位 + 1 起最多 max 条已提交条目（无增量 = 空批次）。</returns>
    public async ValueTask<byte[]> HandlePullAsync(ReadOnlyMemory<byte> pullRequest, CancellationToken ct = default)
    {
        if (!FederationWire.TryDecodePull(pullRequest.Span, out var remoteTag, out var group, out var watermark, out var max)
            || max <= 0 || !FederationWire.ClusterTagMatches(_localClusterTag, remoteTag))
            return FederationWire.EncodeReject(_localClusterTag);   // 畸形/拒链——Granted=0

        var from = watermark + 1;
        var entries = new List<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)>();
        var last = _store.LastLogIndex;
        if (from <= last)
        {
            await foreach (var e in _store.ReadEntriesAsync(from, ct).ConfigureAwait(false))
            {
                entries.Add(e);
                if (entries.Count >= max) break;
            }
        }
        return FederationWire.EncodeBatch(_localClusterTag, entries);
    }
}

/// <summary>
/// 联邦链路目标侧拉取循环（二期-F6——DDR-F6）：周期向源拉取增量、目标组引擎幂等重放
/// （<see cref="RaftStateMachine.ReplicateAsync"/>——本地多数派确认，无跨集群特权）、
/// 重放确认后推进去重水位（持久化——断链/重启从水位续传，不丢不重）。
/// <para>★ 重放范围：Command 条目 only（Config/Noop 是集群本地语义——成员/空操作不跨域）；
/// Config 类条目照常推进水位（源集群已提交事实，仅不重放内容）。</para>
/// <para>★ 断链续传：传输失败/批次缺失即停推（水位不动），下一 poll 从持久化水位续拉。</para>
/// </summary>
public sealed class FederationLink : IAsyncDisposable
{
    private readonly uint _sourceClusterTag;   // 被请求方（源集群）标签——pull 声明 + 水位键
    private readonly RaftGroupId _group;
    private readonly RaftStateMachine _target;
    private readonly IFederationWatermarkStore _watermarks;
    private readonly TimeSpan _pollInterval;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> _pull;
    private readonly int _maxEntriesPerPoll;
    // ★ poll 拍次经 TaskSink 受控提交（TCSG138 存量清扫）——组取消（Dispose 传播进在途拍的
    //   ct）+ 有界 drain + 异常观测面一体（PollOnceAsync 自带 catch-all，观测面双保险）。
    private readonly TaskSink _pollSink = new("federation-link");
    private System.Threading.Timer? _timer;
    private readonly SemaphoreSlim _pollGate = new(1, 1);
    private int _started;
    private int _polling;

    /// <summary>链路观测：最近一次 poll 的（源水位, 本地已消费水位）。</summary>
    public (long SourceIndex, long ConsumedIndex) LastPollResult;

    /// <summary>poll 异常观测口（断链/换届瞬态——链路自愈语义不变，供调用方计数/告警）。</summary>
    public Action<Exception>? OnPollError;

    /// <param name="sourceClusterTag">源集群标签（pull 声明——源侧据此核对，错配即拒链）。</param>
    /// <param name="group">联邦承载的目标组。</param>
    /// <param name="target">目标组引擎（重放面——本集群多数派确认）。</param>
    /// <param name="watermarks">去重水位持久化面（键 = (源标签, 组)——消费域是源集群）。</param>
    /// <param name="pull">拉取委托：请求帧 → 应答帧（跨进程 = 传输 0x06 域请求回调；单进程 = 委托直连）。</param>
    /// <param name="pollInterval">拉取周期（缺省 200ms）。</param>
    /// <param name="maxEntriesPerPoll">单次拉取条数上限（缺省 256——背压由周期天然形成）。</param>
    public FederationLink(uint sourceClusterTag, RaftGroupId group, RaftStateMachine target,
        IFederationWatermarkStore watermarks,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<ReadOnlyMemory<byte>>> pull,
        TimeSpan? pollInterval = null, int maxEntriesPerPoll = 256)
    {
        _sourceClusterTag = sourceClusterTag;
        _group = group;
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _watermarks = watermarks ?? throw new ArgumentNullException(nameof(watermarks));
        _pull = pull ?? throw new ArgumentNullException(nameof(pull));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(200);
        _maxEntriesPerPoll = maxEntriesPerPoll;
    }

    /// <summary>本地已消费的源日志高水位（去重依据——0 = 尚未消费）。</summary>
    public long Watermark => _watermarks.Get(_sourceClusterTag, _group);

    /// <summary>启动拉取循环。</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // ★ Timer 回调不许抛（线程池线程未观测异常 = 进程崩）——Dispose 竞窗下提交即 ODE，吞掉
        //   （链路已收口，本拍作废）。
        _timer = new System.Threading.Timer(
            static s => ((FederationLink)s!).PollTick(), this,
            _pollInterval, _pollInterval);
    }

    /// <summary>拍次提交（Timer 回调面——ODE 收口在此，回调零外抛）。</summary>
    private void PollTick()
    {
        try { _pollSink.SubmitFast(ct => PollOnceAsync(ct)); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>
    /// 单次拉取-重放（循环体；亦可手动驱动——测试/维护形态）：拉批次 → Command 条目
    /// 逐条重放（committed 即确认）→ 推进持久化水位。拒链（Granted=0）= 静默跳过
    /// （错配是配置错误——重试无益也不致命，观测面由调用方读 LastPollResult）。
    /// </summary>
    /// <param name="ct">取消令牌（本拍传输等待阶段生效——取消即中断本拍，水位不动）。</param>
    /// <returns>本拍完成即完成；拉取/重放异常不外抛（经 <see cref="OnPollError"/> 观测——水位不动，下一拍自持久化水位续拉）；
    /// 入口排队前 ct 已取消则上抛 <see cref="OperationCanceledException"/>。</returns>
    public async ValueTask PollOnceAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _polling) != 0) return;   // 上一拍未归——跳过本拍（无并发重放）
        if (!await _pollGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        try
        {
            Interlocked.Exchange(ref _polling, 1);
            var tag = _sourceClusterTag;
            var watermark = _watermarks.Get(tag, _group);
            var request = FederationWire.EncodePull(tag, _group, watermark, _maxEntriesPerPoll);
            var response = await _pull(request, ct).ConfigureAwait(false);
            // ★ 拒链由 Granted=0 表达（源侧 ClusterTag 门）；批次标签是源的（跨集群本不同域），
            //   源侧只对正确 pull 标签发数据——目标侧不再比对（跨集群比对恒假——实锤修正）
            if (!FederationWire.TryDecodeBatch(response.Span, out var granted, out _, out var entries) || !granted)
                return;   // 畸形/拒链——水位不动（断链续传语义）

            var lastIndex = watermark;
            foreach (var (index, _, kind, content) in entries)
            {
                if (index <= watermark) continue;   // 幂等去重（重复投递跳过）
                if (kind == RaftEntryKind.Command)
                    await _target.ReplicateAsync(content, ct).ConfigureAwait(false);   // 本地多数派确认 = 重放完成
                lastIndex = index;   // Config/Noop 照常推进水位（仅不重放内容）
            }
            if (lastIndex > watermark)
                _watermarks.Set(tag, _group, lastIndex);   // 持久化先于确认返回
            LastPollResult = (lastIndex, _watermarks.Get(tag, _group));
        }
        catch (Exception ex)
        {
            // 断链/换届（NotLeader）/取消——水位不动，下一拍从持久化水位续拉（DDR-F6）
            OnPollError?.Invoke(ex);
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
            _pollGate.Release();
        }
    }

    /// <summary>收口拉取循环。</summary>
    /// <returns>定时器已停、在途 poll 排空（组取消 + drain——门随拍尾 finally 归位）后完成。</returns>
    public async ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        await _pollSink.DisposeAsync().ConfigureAwait(false);
        _pollGate.Dispose();
    }
}
