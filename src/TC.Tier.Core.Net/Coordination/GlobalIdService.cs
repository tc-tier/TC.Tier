using System.Collections.Concurrent;
using TC.Tier.CodeGen;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Coordination;

// ═══ raft 命令（状态机复制面——经 ReplicateAsync 投递；段区间经 raft 序全局不重叠）═══

/// <summary>ID 段分配命令族（二期-G2 NETGAP-025——raft 复制的号段分配）。</summary>
[WireMessage]
public abstract record IdSegmentCommand
{
    /// <summary>段键 blob 防御上限。</summary>
    public const int MaxKeyBytes = 256;
}

/// <summary>段授予命令（Key 的可分配尾推进 Count——apply 后 Start..End 归申请方独占）。</summary>
[WireMessageTag(0x01)]
public sealed record IdSegmentGrantCmd : IdSegmentCommand
{
    /// <summary>号段键（业务命名空间，UTF-8 字节）。</summary>
    [WireMember(MaxCount = IdSegmentCommand.MaxKeyBytes)]
    public ReadOnlyMemory<byte> KeyBytes { get; init; }

    /// <summary>段长度（≥ 1）。</summary>
    public required long Count { get; init; }
}

// ═══ 远程服务面 wire（请求回调域——客户端 SDK 形态）═══

/// <summary>ID 段服务消息族。</summary>
[WireMessage]
public abstract record IdSegmentServiceMessage;

/// <summary>段分配请求。</summary>
[WireMessageTag(0x01)]
public sealed record IdSegmentReq : IdSegmentServiceMessage
{
    /// <summary>号段键（与请求回显一致）。</summary>
    [WireMember(MaxCount = IdSegmentCommand.MaxKeyBytes)]
    public ReadOnlyMemory<byte> KeyBytes { get; init; }

    /// <summary>请求段长（≥ 1）。</summary>
    public required long Count { get; init; }
}

/// <summary>段分配应答（Granted=true 时 [Start..End] 归申请方独占使用）。</summary>
[WireMessageTag(0x02)]
public sealed record IdSegmentResp : IdSegmentServiceMessage
{
    /// <summary>是否授予该号段</summary>
    public required bool Granted { get; init; }

    /// <summary>段起始（含）。</summary>
    public required long Start { get; init; }

    /// <summary>段结束（含）。</summary>
    public required long End { get; init; }
}

/// <summary>
/// ID 段状态机（二期-G2——raft 复制的全局单调分配）：每 Key 维护已分配尾，
/// apply 推进 Count——**段区间经 raft 全序天然不重叠**（重启后尾随日志/快照恢复，单调保持）。
/// </summary>
public sealed class IdSegmentStateMachine : IStateMachine
{
    private readonly ConcurrentDictionary<string, long> _allocatedTail = new();
    private readonly ConcurrentDictionary<long, long> _results = new();   // index → 新尾

    /// <summary>读命令结果（新分配尾——ReplicateAsync 返回 index 后读取，无竞态）。</summary>
    /// <returns>该命令 apply 后 Key 的新已分配段尾（含端点）；-1 = 结果不在保留窗（未应用或已被裁剪）。</returns>
    public long ResultOf(long index) => _results.TryGetValue(index, out var tail) ? tail : -1;

    /// <summary>读 Key 当前已分配尾（无分配 = 0）。</summary>
    /// <returns>该 Key 已分配的段尾（含——下一段自尾 + 1 起）；0 = 该 Key 从未分配（发号自 1 起）。</returns>
    public long AllocatedTail(string key) => _allocatedTail.TryGetValue(key, out var t) ? t : 0;

    /// <inheritdoc/>
    public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
    {
        if (!IdSegmentCommandCodec.TryDecode(command.Span, out var cmd))
            throw new InvalidOperationException($"ID 段命令解码失败（ID 组日志只承载段命令）：index={index}。");
        if (cmd is not IdSegmentGrantCmd grant)
            throw new InvalidOperationException($"ID 段命令种类未知：index={index}。");

        var key = System.Text.Encoding.UTF8.GetString(grant.KeyBytes.Span);
        ArgumentOutOfRangeException.ThrowIfLessThan(grant.Count, 1);
        var newTail = _allocatedTail.AddOrUpdate(key, grant.Count, (_, tail) => tail + grant.Count);
        _results[index] = newTail;

        if (_results.Count > 4096)
            foreach (var k in _results.Keys.Where(k => k < index - 2048))
                _results.TryRemove(k, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 全局 ID 段服务（二期-G2——挂载请求回调域，远程段分配）：命令经 raft 复制应用后回读
/// 新分配尾——**段区间经 raft 全序全局不重叠**；非 leader 节点挂载 = NotLeader 快速失败。
/// </summary>
public sealed class GlobalIdService : IRequestHandler, IDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly RaftStateMachine _raft;
    private readonly IdSegmentStateMachine _machine;
    // ★ 远程段分配回执经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面 + Dispose drain。
    private readonly TaskSink _replySink;
    private readonly ILogger? _logger;

    /// <summary>构造并按域挂载（须挂于 ID 组 raft 成员节点）。</summary>
    public GlobalIdService(IProtocolTransport transport, byte domain,
        RaftStateMachine raft, IdSegmentStateMachine machine, ILogger? logger = null)
    {
        _transport = transport;
        _raft = raft;
        _machine = machine;
        _logger = logger;
        _replySink = new TaskSink("global-id-service", logger: logger);
        transport.RegisterRequestHandler(domain, this);
    }

    /// <summary>本地段分配（同节点快路径——与远程请求同经 raft 复制，语义一致）。</summary>
    public async ValueTask<(long Start, long End)> AllocateAsync(string key, long count, CancellationToken ct = default)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        var index = await _raft.ReplicateAsync(
            IdSegmentCommandCodec.Encode(new IdSegmentGrantCmd
            {
                KeyBytes = keyBytes, Count = count,
            }), ct).AsTask().ConfigureAwait(false);
        var tail = _machine.ResultOf(index);
        return (tail - count + 1, tail);
    }

    /// <inheritdoc/>
    public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (!IdSegmentServiceMessageCodec.TryDecode(payload.Span, out var req))
            return;   // 畸形——对端超时自愈
        if (req is not IdSegmentReq r)
            return;

        _replySink.Submit(async _ =>
        {
            try
            {
                var (start, end) = await AllocateAsync(
                    System.Text.Encoding.UTF8.GetString(r.KeyBytes.Span), r.Count, CancellationToken.None).ConfigureAwait(false);   // 尽力应答——不受入站取消传播
                await reply.ReplyAsync(IdSegmentServiceMessageCodec.Encode(new IdSegmentResp
                {
                    Granted = true, Start = start, End = end,
                }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (NotLeaderException)
            {
                await reply.ReplyAsync(IdSegmentServiceMessageCodec.Encode(new IdSegmentResp
                {
                    Granted = false, Start = 0, End = 0,
                }), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("ID 段分配失败（对端重试）：{Message}", ex.Message);
            }
        });
    }

    /// <inheritdoc/>
    public void Dispose() => _replySink.Dispose();   // 有界 drain 在途回执（body 自带 catch-all——正常瞬完）
}

/// <summary>
/// 段客户端分发器（二期-G2——leaf-segment 形态）：本地持有 [Start..End] 段内发号，
/// 耗尽自动向服务端补段——raft 往返按段长摊薄（10 万 ID 仅 10 次段请求 @ 段长 1 万）。
/// </summary>
public sealed class IdSegmentDispenser
{
    private readonly IProtocolTransport _transport;
    private readonly NodeId _server;
    private readonly byte _domain;
    private readonly byte[] _keyBytes;
    private readonly long _segmentSize;

    private long _next = 1;   // ID 从 1 起（0 = 初始"无段"哨兵态）
    private long _end;

    /// <summary>构造。</summary>
    /// <param name="transport">发起端传输。</param>
    /// <param name="server">ID 服务节点。</param>
    /// <param name="domain">服务域。</param>
    /// <param name="key">段键（业务自定——如 "order-id"）。</param>
    /// <param name="segmentSize">段长（每次 raft 补段获得的 ID 数）。</param>
    public IdSegmentDispenser(IProtocolTransport transport, NodeId server, byte domain,
        string key, long segmentSize = 10_000)
    {
        _transport = transport;
        _server = server;
        _domain = domain;
        _keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
        _segmentSize = segmentSize;
    }

    /// <summary>取一个全局单调 ID（本地段耗尽 → 自动补段；NotLeader 由调用方重试/重路由）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下一全局单调 ID（段内顺序发放；段耗尽经 raft 补段后从新段续发）。</returns>
    public async ValueTask<long> NextAsync(CancellationToken ct = default)
        => await NextAsync(_segmentSize, ct).ConfigureAwait(false);

    /// <summary>取一个全局单调 ID（指定补段长）。</summary>
    /// <param name="refetchSegment">补段长度（≥ 1——仅本地段耗尽向服务端申请时使用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>下一全局单调 ID（补段时为新段 [Start..End] 起始）；非 leader 应答 = 上抛 <see cref="NotLeaderException"/>，应答畸形抛 <see cref="InvalidOperationException"/>。</returns>
    public async ValueTask<long> NextAsync(long refetchSegment, CancellationToken ct = default)
    {
        if (_next <= _end) return _next++;
        var bytes = IdSegmentServiceMessageCodec.Encode(new IdSegmentReq
        {
            KeyBytes = _keyBytes, Count = refetchSegment,
        });
        var respBytes = await _transport.SendRequestAsync(_server, _domain, bytes, ct: ct).ConfigureAwait(false);
        if (!IdSegmentServiceMessageCodec.TryDecode(respBytes, out var msg) || msg is not IdSegmentResp resp)
            throw new InvalidOperationException("ID 段应答畸形。");
        if (!resp.Granted)
            throw new NotLeaderException(null);
        _next = resp.Start;
        _end = resp.End;
        return _next++;
    }
}
