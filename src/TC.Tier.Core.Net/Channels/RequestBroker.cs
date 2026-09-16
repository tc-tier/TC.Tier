using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using TC.Tier.Core.Execution;

using TC.Tier.Core.Tracing;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 请求回调核心组件（spec-12 §5.2）——介质组合（TCP/InProcess 单源）：
/// <para>★ 发送面：CorrId 生成（随机起始 + 递增——本端在途集合唯一）+ pending 关联表
///   （<b>有界</b>——容量满抛 fail-fast：并发失控立即暴露而非静默排队）+ 等待/超时/取消。</para>
/// <para>★ 接收面：handler 表（注册分流同数据报 §3.5）+ 入站分发（异常隔离——同尽力契约）。</para>
/// <para>★ 防泄漏不变量：每个 BeginRequest 必有终态清理（应答完成 / Abandon 发送失败 /
///   WaitAsync 超时或取消后 Abandon）——关联表无孤儿。</para>
/// </summary>
internal sealed class RequestBroker
{
    /// <summary>pending 关联表容量缺省（介质无显式配置时——与 TransportOptions.RequestPendingCapacity 同值）。</summary>
    public const int DefaultCapacity = AlignmentConst.Alignment4K;

    /// <summary>去重窗口容量缺省。</summary>
    public const int DefaultDedupCapacity = AlignmentConst.Alignment1K;

    /// <summary>去重窗口字节软预算缺省（8 MiB——大响应场景的驻留上界 = 预算 + 最大单条）。</summary>
    public const long DefaultDedupMaxBytes = ResponseDedupWindow.DefaultMaxBytes;

    private readonly ConcurrentDictionary<ulong, PooledValueTaskSource<byte[]>> _pending = new();
    private readonly ConcurrentDictionary<byte, IRequestHandler> _handlers = new();
    private readonly ResponseDedupWindow _dedup;
    private readonly int _capacity;
    private readonly ulong _corrIdOrigin;   // 随机起始（跨实例/重启降低重放混淆窗口）
    private readonly ITracer? _tracer;      // 二期-I4：跨节点 trace 串联（null = 无追踪）

    private long _corrIdCursor;

    /// <summary>重放执行 sink（惰性单建——重放路径仅 at-least-once + 重复到达触发，极低频）。
    /// ★ 不挂 Dispose：broker 生命周期 = 所属传输生命周期，sink 滞留资源（CTS+信号量）由进程
    /// 收尾兜底（对齐 TaskSink FinishDrain 超时滞留先例）——省去传输 Dispose 链耦合。</summary>
    private TaskSink? _replaySink;
    private readonly object _replaySinkLock = new();

    private TaskSink ReplaySink
    {
        get
        {
            var sink = Volatile.Read(ref _replaySink);
            if (sink is not null) return sink;
            lock (_replaySinkLock)
            {
                return _replaySink ??= new TaskSink(name: "request-replay");
            }
        }
    }

    /// <summary>构造。</summary>
    /// <param name="capacity">pending 关联表容量（在途请求数上限——满抛）。</param>
    /// <param name="dedupCapacity">应答端去重窗口容量（已服务请求记忆上限）。</param>
    /// <param name="dedupMaxBytes">应答端去重窗口字节软预算（大响应驻留上界——超预算驱逐最老）。</param>
    /// <param name="queueWait">Queue 背压策略排队等待上限（Zero = 500ms 缺省——超时回落 fail-fast）。</param>
    /// <param name="priorityReserve">Priority 域预留槽位数（共享池满仍可入）。</param>
    /// <param name="slowRequestThreshold">慢请求判定阈值（分发→首次应答耗时；null/非正值 = 关闭计时面）。</param>
    /// <param name="onSlowRequest">慢请求回调（参数 = 协议域 ID, 实际耗时 ms；<paramref name="slowRequestThreshold"/> 关闭时无效）。</param>
    /// <param name="tracer">跨节点 tracer（二期-I4——null = 无追踪）。</param>
    public RequestBroker(int capacity, int dedupCapacity = DefaultDedupCapacity, long dedupMaxBytes = DefaultDedupMaxBytes,
        TimeSpan queueWait = default, int priorityReserve = 0,
        TimeSpan? slowRequestThreshold = null, Action<byte, double>? onSlowRequest = null,
        ITracer? tracer = null)
    {
        _tracer = tracer;
        _queueWait = queueWait == TimeSpan.Zero ? TimeSpan.FromMilliseconds(500) : queueWait;
        _priorityReserve = priorityReserve;
        _slowThresholdTicks = slowRequestThreshold is { } st && st > TimeSpan.Zero
            ? (long)(st.TotalMilliseconds * Stopwatch.Frequency / 1000) : 0;
        _onSlowRequest = onSlowRequest;
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _dedup = new ResponseDedupWindow(dedupCapacity, dedupMaxBytes);
        _corrIdOrigin = BinaryPrimitives.ReadUInt64LittleEndian(RandomNumberGenerator.GetBytes(8));
    }

    /// <summary>在途请求数（诊断/测试）。</summary>
    public int PendingCount => _pending.Count;

    // ══ 注册面（§3.5 分流——同数据报规则）══

    /// <summary>注册·公开口（注册区 0x60-0xAF）。</summary>
    /// <param name="protocolId">协议域 ID（注册区 0x60-0xAF）。</param>
    /// <param name="handler">入站请求处理器。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> 不在注册区。</exception>
    /// <exception cref="InvalidOperationException">同 ID 重复注册。</exception>
    public void RegisterUserHandler(byte protocolId, IRequestHandler handler)
    {
        ProtocolRegistration.ValidateUserPort(protocolId);
        AddHandler(protocolId, handler);
    }

    /// <summary>注册·内部口（核心区 0x00-0x4F——机制面专用）。</summary>
    /// <param name="protocolId">协议域 ID（内部核心区 0x00-0x4F）。</param>
    /// <param name="handler">入站请求处理器。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="protocolId"/> 不在内部核心区。</exception>
    /// <exception cref="InvalidOperationException">同 ID 重复注册。</exception>
    public void RegisterCoreHandler(byte protocolId, IRequestHandler handler)
    {
        ProtocolRegistration.ValidateCorePort(protocolId);
        AddHandler(protocolId, handler);
    }

    private void AddHandler(byte protocolId, IRequestHandler handler)
        => ProtocolRegistration.Add(_handlers, protocolId, handler);   // 重复注册抛（分流规则单源）

    // ══ 发送面 ══

    /// <summary>
    /// 在途请求句柄（<see cref="BeginRequest"/> 产出）——完成通道随句柄走：
    /// 应答可能在调用方进入 <see cref="WaitAsync"/> 前到达（直排介质同步完成整个回程），
    /// 句柄持有池化完成源，关联表只是 CorrId → 完成通道的路由索引。
    /// </summary>
    public readonly struct PendingRequest
    {
        /// <summary>关联 ID（请求帧携带——与 <see cref="IReplyContext.CorrelationId"/> 同源）。</summary>
        public ulong CorrelationId { get; }

        /// <summary>池化完成通道（应答到达 / 未到达即超时或取消）。</summary>
        internal readonly PooledValueTaskSource<byte[]> Source;

        internal PendingRequest(ulong correlationId, PooledValueTaskSource<byte[]> source)
        {
            CorrelationId = correlationId;
            Source = source;
        }
    }



    // ══ 背压策略（二期-D5 NETGAP-014——域级声明；缺省 FailFast 保持既有语义）══

    private readonly ConcurrentDictionary<byte, BackpressurePolicy> _policies = new();
    private readonly TimeSpan _queueWait;       // Queue 策略的排队等待上限（Zero = 500ms 缺省）
    private readonly int _priorityReserve;      // Priority 策略的预留槽位（共享池满仍可入）
    // ★ 二期-I3：慢请求面——分发至应答耗时超阈值的域级计数回调（缺省无 = 关闭）
    private readonly long _slowThresholdTicks;
    private readonly Action<byte, double>? _onSlowRequest;

    /// <summary>域级背压策略声明（产品按域声明；重复设置 = 更新）。从未声明的域查询 = FailFast。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="policy">域背压策略（重复设置 = 覆盖）。</param>
    public void SetBackpressurePolicy(byte protocolId, BackpressurePolicy policy)
        => _policies[protocolId] = policy;

    /// <summary>域级背压策略读面（未声明 = FailFast）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <returns>域背压策略（未声明域 = <see cref="BackpressurePolicy.FailFast"/>）。</returns>
    public BackpressurePolicy GetBackpressurePolicy(byte protocolId)
        => _policies.TryGetValue(protocolId, out var p) ? p : BackpressurePolicy.FailFast;

    /// <summary>
    /// 背压策略执行（二期-D5——发送前置门）：按域策略处理满载——
    /// Queue = 有界等待槽位释放（≤ 队列等待上限，超时回落 fail-fast）；
    /// Degrade = 满载立即抛 <see cref="RequestDegradeException"/>；
    /// FailFast/Priority = 直通（Priority 的预留预算在 BeginRequest 准入判定）。
    /// </summary>
    /// <param name="protocolId">协议域 ID（null = 无域上下文——按 FailFast 直通）。</param>
    /// <param name="ct">取消令牌（Queue 排队等待期间可取消）。</param>
    /// <returns>完成 = 门已放行（未满载或 Queue 排队等到槽位）；Queue 排队超时静默返回（回落 fail-fast——由后续 <see cref="BeginRequest"/> 容量判定拒绝）；Degrade 满载 = 抛 <see cref="RequestDegradeException"/>。</returns>
    public async ValueTask EnforceBackpressureAsync(byte? protocolId, CancellationToken ct)
    {
        var policy = protocolId is { } pid ? GetBackpressurePolicy(pid) : BackpressurePolicy.FailFast;
        if (policy is not (BackpressurePolicy.Queue or BackpressurePolicy.Degrade)) return;
        if (_pending.Count < _capacity) return;

        if (policy == BackpressurePolicy.Degrade)
            throw new RequestDegradeException(
                $"请求关联表满载降级（在途 {_pending.Count}/{_capacity}）——Degrade 策略。");

        // Queue：有界等待槽位释放（轮询 10ms——低频满载形态，无唤醒原语依赖）
        var deadline = Environment.TickCount64 + (long)_queueWait.TotalMilliseconds;
        while (_pending.Count >= _capacity)
        {
            if (Environment.TickCount64 >= deadline) return;   // 排队超时——回落 fail-fast
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 占位 pending 并返回句柄（介质随后发送请求帧）。容量满抛
    /// <see cref="InvalidOperationException"/>（在途并发超限——fail-fast）。
    /// </summary>
    /// <returns>包含关联 ID 和池化完成源的 <see cref="PendingRequest"/> 句柄。</returns>
    /// <exception cref="InvalidOperationException">在途请求数达到容量上限（<see cref="PendingCount"/> ≥ <see cref="DefaultCapacity"/>）。</exception>
    /// <param name="protocolId">协议域 ID（Priority 域共享池满仍可占用预留槽位；null = 无预留预算）。</param>
    public PendingRequest BeginRequest(byte? protocolId = null)
    {
        // ★ 二期-D5：Priority 域——共享池满仍可入预留预算槽位（容量 + reserve）；超预留回落 fail-fast
        var reserve = protocolId is { } pid && GetBackpressurePolicy(pid) == BackpressurePolicy.Priority
            ? _priorityReserve : 0;
        if (_pending.Count >= _capacity + reserve)
            throw new InvalidOperationException($"请求关联表已满（在途 {_pending.Count}/{_capacity + reserve}）——调用方并发失控或应答缺失。");
        ulong corrId;
        var source = PooledValueTaskSource<byte[]>.Rent();   // 池化完成源（RunContinuationsAsynchronously=true 内建）
        do
        {
            corrId = _corrIdOrigin + (ulong)Interlocked.Increment(ref _corrIdCursor);
        }
        while (!_pending.TryAdd(corrId, source));   // 起始随机 + 单调递增——理论撞号防御
        return new PendingRequest(corrId, source);
    }

    /// <summary>等待应答完成（应答早于等待到达也成立——句柄持完成通道）。超时抛 <see cref="TimeoutException"/>；取消传播。
    /// 经 AsTask 桥接 + BCL Task.WaitAsync（桥接单对象成本 &lt; 原 TCS+Task 双对象；BCL 超时机器池化）。</summary>
    /// <param name="pending">待等待的请求句柄（由 <see cref="BeginRequest"/> 产出）。</param>
    /// <param name="timeout">等待超时。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>应答载荷字节数组。</returns>
    /// <exception cref="TimeoutException">等待超时未收到应答。</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> 已取消。</exception>
    public Task<byte[]> WaitAsync(PendingRequest pending, TimeSpan timeout, CancellationToken ct)
        => new ValueTask<byte[]>(pending.Source, pending.Source.Version).AsTask().WaitAsync(timeout, ct);

    /// <summary>
    /// 清理关联项（发送失败/等待终态后）——★ 归还所有权仲裁：摘除关联表的赢家负责归还完成源。
    /// <para>本方摘除（超时/取消/发送失败——源仍武装）→ <see cref="PooledValueTaskSource{T}.TryRelease"/>
    /// 归还；<see cref="OnResponse"/> 已摘除（应答已到达）→ 等待方 GetResult 消费后显式
    /// <see cref="PooledValueTaskSource{T}.Return"/>。每源恰一次归还，无泄漏无双重入池。</para>
    /// </summary>
    /// <param name="pending">待清理的请求句柄。</param>
    public void Abandon(in PendingRequest pending)
    {
        if (_pending.TryRemove(pending.CorrelationId, out var source))
            source.TryRelease();
    }

    /// <summary>
    /// 发送协调（spec-12 §5.2 投递语义分层）：<paramref name="retry"/> null = at-most-once
    /// （单发 + <paramref name="timeout"/> 等待——热路径手写池化等待态，零状态机箱）；否则 at-least-once
    /// ——同 CorrId 重发（每发等待 <c>Retry.Backoff</c>，次数/总时限由策略约束——"确认" = 响应即确认，
    /// 不发明独立 ack 帧）。介质发送失败（<see cref="NetIOException"/> 链路断）直接外泄——
    /// 重试只针对"应答未达"。
    /// </summary>
    /// <param name="send">介质发送（参数 = CorrId——重发同号）。</param>
    /// <param name="timeout">at-most-once 单发等待。</param>
    /// <param name="retry">at-least-once 策略（null = 不重发）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="protocolId">协议域 ID（域背压前置门与 Priority 预留判定依据；null = 不做域级背压；默认 null）。</param>
    /// <returns>完成时返回应答载荷。</returns>
    public ValueTask<byte[]> SendAsync(Func<ulong, CancellationToken, ValueTask> send, TimeSpan timeout, RetryPolicy? retry, CancellationToken ct, byte? protocolId = null)
    {
        if (retry is null)
            return new ValueTask<byte[]>(SendAtMostOnceAsync(send, timeout, ct, protocolId));
        return new ValueTask<byte[]>(SendWithRetryAsync(send, timeout, retry, ct, protocolId));
    }

    /// <summary>at-most-once 单发等待（异步形态——e4507539 手写等待态在 InProcess 全管线 800 并发
    /// 形态下引发 9× 吞吐回归（契约/微基准均绿未拦截），回退为已知健康形态，见设计稿判例⑥）。</summary>
    private async Task<byte[]> SendAtMostOnceAsync(Func<ulong, CancellationToken, ValueTask> send, TimeSpan timeout, CancellationToken ct, byte? protocolId = null)
    {
        await EnforceBackpressureAsync(protocolId, ct).ConfigureAwait(false);   // ★ 二期-D5：域背压前置门
        var pending = BeginRequest(protocolId);
        try
        {
            await send(pending.CorrelationId, ct).ConfigureAwait(false);
            var result = await WaitAsync(pending, timeout, ct).ConfigureAwait(false);
            PooledValueTaskSource<byte[]>.Return(pending.Source);   // 应答已消费（GetResult 完成）——显式归还
            return result;
        }
        finally
        {
            Abandon(pending);           // 未完成路径（超时/取消/发送失败）——摘除即武装归还
        }
    }

    /// <summary>at-least-once 重发循环（冷路径 async 形态——语义与原实现逐项等价）。</summary>
    private async Task<byte[]> SendWithRetryAsync(Func<ulong, CancellationToken, ValueTask> send, TimeSpan timeout, RetryPolicy retry, CancellationToken ct, byte? protocolId = null)
    {
        retry.Validate();
        await EnforceBackpressureAsync(protocolId, ct).ConfigureAwait(false);   // ★ 二期-D5：域背压前置门
        var pending = BeginRequest(protocolId);
        var completed = false;
        try
        {
            var budget = retry.TotalBudget ?? TimeSpan.FromTicks(retry.EffectiveBackoff.Ticks * retry.MaxAttempts);
            var deadline = Stopwatch.GetTimestamp() + budget.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            for (var attempt = 1; ; attempt++)
            {
                await send(pending.CorrelationId, ct).ConfigureAwait(false);
                var remaining = TimeSpan.FromTicks((deadline - Stopwatch.GetTimestamp()) * TimeSpan.TicksPerSecond / Stopwatch.Frequency);
                var window = remaining < retry.EffectiveBackoff ? remaining : retry.EffectiveBackoff;
                if (window <= TimeSpan.Zero) throw new TimeoutException($"请求总时限耗尽（at-least-once {retry.MaxAttempts} 次额度内无应答）。");
                try
                {
                    var result = await WaitAsync(pending, window, ct).ConfigureAwait(false);
                    completed = true;
                    return result;
                }
                catch (TimeoutException) when (attempt < retry.MaxAttempts)
                {
                    // 该发等待窗口耗尽——同 CorrId 重发（应答端去重窗口保证不重复执行）。
                    // ★ 换代源：ManualResetValueTaskSourceCore 每代只容一次 OnCompleted——旧桥超时
                    //   弃置后同源二次注册非法，重试须租新源续挂同 CorrId（旧源 TryRelease 归还池）。
                    RenewPending(ref pending);
                }
            }
        }
        finally
        {
            Abandon(pending);           // 未完成路径（超时/取消/发送失败）——摘除即武装归还
            if (completed)
                PooledValueTaskSource<byte[]>.Return(pending.Source);   // 应答已消费（GetResult 完成）——显式归还
        }
    }

    /// <summary>重试换代：摘除旧源（武装归还池）→ 租新源续挂同 CorrId。corrId 由本端唯一游标持有，
    /// TryAdd 无并发竞争面。</summary>
    private void RenewPending(ref PendingRequest pending)
    {
        if (_pending.TryRemove(pending.CorrelationId, out var old))
            old.TryRelease();
        var source = PooledValueTaskSource<byte[]>.Rent();
        while (!_pending.TryAdd(pending.CorrelationId, source))
        {
        }
        pending = new PendingRequest(pending.CorrelationId, source);
    }

    /// <summary>应答到达（介质调用——完成 pending；未知 CorrId = 迟到应答，忽略）。</summary>
    /// <param name="corrId">关联 ID（与请求帧携带的 CorrId 同源）。</param>
    /// <param name="payload">应答载荷。</param>
    public void OnResponse(ulong corrId, ReadOnlyMemory<byte> payload)
    {
        if (_pending.TryRemove(corrId, out var source))
            source.SetResult(payload.ToArray());
    }

    // ══ 接收面 ══

    /// <summary>
    /// 入站请求分发（介质调用——<paramref name="reply"/> 回程上下文由介质构造）：
    /// 未注册协议静默丢弃；handler 异常隔离（不外泄不致命——同数据报契约）。
    /// <para>★ at-least-once 去重（§5.2）：同（来源, CorrId）重发到达——已服务有缓存响应 =
    ///   重放（不重复执行）；已见未回复 = 不执行不回复（对端继续超时重试）。</para>
    /// </summary>
    /// <param name="from">请求来源节点。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="corrId">关联 ID（传输内核生成——回带定位发起端 pending 表）。</param>
    /// <param name="payload">请求载荷。</param>
    /// <param name="reply">应答上下文（回程路由知识在介质——handler 只见 ReplyAsync）。</param>
    /// <param name="traceWireContext">线上 trace 上下文（二期-I4——恢复为服务端 span；null = 无追踪；默认 null）。</param>
    public void DispatchRequest(NodeId from, byte protocolId, ulong corrId, ReadOnlyMemory<byte> payload, IReplyContext reply,
        byte[]? traceWireContext = null)
    {
        if (!_handlers.TryGetValue(protocolId, out var handler)) return;   // 未注册协议 = 丢弃（尽力语义）

        if (!_dedup.TryBeginServe(from, corrId, out var cached))
        {
            // 已服务——缓存响应重放（不重复执行）。★ 受控提交（TCSG138 存量清扫）：裸 `_ =` 丢弃
            //   改经 TaskSink——异常进观测面（计数/日志），尽力重放语义不变（失败 = 对端超时重试
            //   再次命中重放）。重放极低频（at-least-once + 重复到达才触发），sink 惰性建。
            ReplaySink.SubmitFast(ct => reply.ReplyAsync(cached, ct));
            return;
        }

        // ★ 二期-I3：慢请求计时——分发起算、首次 ReplyAsync 收口（超阈值即回调 _onSlowRequest）
        var startTicks = _slowThresholdTicks > 0 ? Stopwatch.GetTimestamp() : 0;
        try
        {
            IReplyContext replyCtx = _slowThresholdTicks > 0
                ? new TimedReplyContext(reply, startTicks, _slowThresholdTicks, protocolId, _onSlowRequest)
                : reply;
            // ★ 二期-I4：跨节点 trace 串联——线上上下文恢复为服务端 span（handler 执行期）
            var span = traceWireContext is not null && _tracer is { IsEnabled: true } t
                ? t.BeginSpan("net.request", SpanKind.Server, traceWireContext)
                : null;
            try
            {
                handler.OnRequest(from, payload, new RecordingReplyContext(this, replyCtx, from, corrId));
                span?.SetStatus(SpanStatus.Ok);
            }
            catch (Exception ex)
            {
                span?.RecordException(ex);
                throw;
            }
            finally
            {
                span?.Dispose();
            }
        }
        catch (Exception)
        {
            // handler（使用方代码）异常不外泄、不致命——不回复 = 对端超时（§5.2）
        }
    }

    /// <summary>计时应答装饰（二期-I3——首次 ReplyAsync 收口慢判定；透传全部成员）。</summary>
    private sealed class TimedReplyContext(IReplyContext inner, long startTicks, long thresholdTicks,
        byte protocolId, Action<byte, double>? onSlow) : IReplyContext
    {
        public NodeId Peer => inner.Peer;
        public byte ProtocolId => inner.ProtocolId;
        public ulong CorrelationId => inner.CorrelationId;

        /// <summary>回发应答并收口慢请求判定（分发起算→首次应答耗时超阈值即回调慢请求计数）。</summary>
        /// <param name="payload">应答载荷。</param>
        /// <param name="ct">取消令牌（透传内层回程）。</param>
        /// <returns>完成时应答已经内层上下文回程发送。</returns>
        public async ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            var elapsedTicks = Stopwatch.GetTimestamp() - startTicks;
            if (elapsedTicks >= thresholdTicks)
            {
                var elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;
                onSlow?.Invoke(protocolId, elapsedMs);
            }
            await inner.ReplyAsync(payload, ct).ConfigureAwait(false);
        }
    }

    /// <summary>应答回程装饰——登记去重窗口缓存（重发到达即重放此响应）。</summary>
    private sealed class RecordingReplyContext(RequestBroker broker, IReplyContext inner, NodeId from, ulong corrId) : IReplyContext
    {
        public NodeId Peer => inner.Peer;
        public byte ProtocolId => inner.ProtocolId;
        public ulong CorrelationId => corrId;

        /// <summary>回发请求应答（先登记去重窗口缓存，再经内层上下文回程——重发到达即重放此响应）。</summary>
        /// <param name="payload">应答载荷（空应答合法）。</param>
        /// <param name="ct">取消令牌（传播到回程发送）。</param>
        /// <returns>完成时应答已登记缓存并交由内层回程发送。</returns>
        public async ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            broker._dedup.RecordResponse(from, corrId, payload.ToArray());
            await inner.ReplyAsync(payload, ct).ConfigureAwait(false);
        }
    }
}
