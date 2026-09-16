using System.Collections.Concurrent;
using System.Net;
using TC.Tier.CodeGen;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 可恢复流消息族（二期-E2 NETGAP-028——offset/resume 令牌原语）：
/// 拉取式按 offset 取块——断线/超时以同一 offset 重试即续传（按 offset 幂等，天然不丢不重）。
/// <para>★ 线格式（生成物：[Tag 1B][字段声明序]，小端）：</para>
/// <code>
/// FeedChunkReq   [Tag][FeedId 16B][Offset 8B]
/// FeedChunkResp  [Tag][Offset 8B][HasChunk 1B][HasMore 1B][Len 4B][Data]
/// </code>
/// </summary>
[WireMessage]
public abstract record ResumableFeedMessage
{
    /// <summary>单块字节防御上限（1 MiB——畸形报文拦截界）。</summary>
    public const int MaxChunkBytes = 1 << 20;
}

/// <summary>按 offset 取块请求（同 offset 重试幂等——续传语义基石）。</summary>
[WireMessageTag(0x01)]
public sealed record FeedChunkReq : ResumableFeedMessage
{
    /// <summary>流标识。</summary>
    public required Opaque16 FeedId { get; init; }

    /// <summary>请求的块 offset（0 起，单调递增）。</summary>
    public required long Offset { get; init; }
}

/// <summary>取块应答。</summary>
[WireMessageTag(0x02)]
public sealed record FeedChunkResp : ResumableFeedMessage
{
    /// <summary>块 offset（回显——请求配对校验）。</summary>
    public required long Offset { get; init; }

    /// <summary>是否有此块（false = 已越过尾部=完成，或保留窗外缺口——按 HasMore 区分）。</summary>
    public required bool HasChunk { get; init; }

    /// <summary>HasChunk=false 时：true = 保留窗内缺口（稍后重试同 offset）；false = 已到尾部（完成）。</summary>
    public required bool HasMore { get; init; }

    /// <summary>块数据（HasChunk=false 时为空）。</summary>
    [WireMember(MaxCount = ResumableFeedMessage.MaxChunkBytes)]
    public ReadOnlyMemory<byte> Data { get; init; }
}

/// <summary>
/// 可恢复流服务端（二期-E2）：有界保留窗 + 单调 offset 分配——
/// <see cref="Append"/> 产出块 offset，<see cref="IRequestHandler.OnRequest"/> 按 offset 供块；
/// 保留窗外缺口 = HasChunk=false + HasMore=true（客户端稍后重试），越过尾部 = 完成。
/// </summary>
public sealed class ResumableFeedServer : IRequestHandler, IDisposable
{
    private readonly ConcurrentDictionary<long, ReadOnlyMemory<byte>> _chunks = new();

    /// <summary>★ 测试访问器：保留窗内块内容（#418 复制留存回归门用）。</summary>
    internal ReadOnlyMemory<byte> ChunkForTest(long offset) => _chunks[offset];
    private readonly Opaque16 _feedId;
    private readonly int _retention;
    private long _tail = -1;   // 最后一个已分配 offset（-1 = 空）
    private long _oldest;      // 保留窗最老 offset
    private readonly object _lock = new();
    // ★ 供块回执经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面（计数/日志）。
    //   不挂 Dispose 链（对齐 request-replay 先例——收尾兜底语义，省 Dispose 耦合）。
    private readonly TaskSink _replySink = new("feed-reply");

    /// <summary>构造并按域挂载（RegisterRequestHandler——域号由使用方在注册区 0x60-0xAF 自管）。</summary>
    /// <param name="transport">承载传输。</param>
    /// <param name="domain">协议域 ID（注册区）。</param>
    /// <param name="feedId">流标识（客户端请求须匹配）。</param>
    /// <param name="retention">保留窗块数（断线续传的容错深度——窗外缺口由上层重置）。</param>
    /// <param name="logger">日志（可选）。</param>
    public ResumableFeedServer(IProtocolTransport transport, byte domain, Opaque16 feedId,
        int retention = 1024, ILogger? logger = null)
    {
        _feedId = feedId;
        _retention = retention;
        transport.RegisterRequestHandler(domain, this);
    }

    /// <summary>在途/保留块数（诊断）。</summary>
    public int RetainedCount { get { lock (_lock) return _chunks.Count; } }

    /// <summary>追加一块（返回分配的 offset——单调递增）。
    /// <para>★ #418：块数据在锁内<b>复制</b>后留存——调用方传入池化/滚动缓冲切片（ring/pipe
    /// 复用是常见形态）时，缓冲复用不再静默污染保留窗内已存块（曾为引用留存，污染窗口 =
    /// 保留窗生命周期，全程无错误信号）。上限 <see cref="ResumableFeedMessage.MaxChunkBytes"/>
    /// （1 MiB）内复制代价可接受，契约安全零心智负担。</para></summary>
    /// <param name="chunk">块数据（内部复制——调用方缓冲可立即复用）。</param>
    /// <returns>分配给该块的 offset（0 起单调递增；供客户端按 offset 拉取）。</returns>
    public long Append(ReadOnlyMemory<byte> chunk)
    {
        lock (_lock)
        {
            var offset = ++_tail;
            _chunks[offset] = chunk.ToArray();
            var oldest = offset - _retention + 1;
            while (_oldest < oldest)
            {
                _chunks.TryRemove(_oldest, out _);
                _oldest++;
            }
            return offset;
        }
    }

    /// <summary>按 offset 供块（HasChunk=false + HasMore=true = 保留窗内缺口）。</summary>
    /// <param name="from">请求来源节点（本实现未用）。</param>
    /// <param name="payload">FeedChunkReq 编码帧（畸形/非本消息族 = 静默丢弃——对端超时自愈）。</param>
    /// <param name="reply">应答上下文（同步编码 FeedChunkResp 回程）。</param>
    public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (!ResumableFeedMessageCodec.TryDecode(payload.Span, out var msg) || msg is not FeedChunkReq req)
            return;   // 畸形——对端超时自愈（尽力语义）

        lock (_lock)
        {
            var tail = _tail;
            if (req.Offset > tail)
            {
                _replySink.SubmitFast(_ => reply.ReplyAsync(ResumableFeedMessageCodec.Encode(new FeedChunkResp
                {
                    Offset = req.Offset, HasChunk = false, HasMore = false,   // 越过尾部 = 完成
                }), CancellationToken.None));
                return;
            }
            if (_chunks.TryGetValue(req.Offset, out var chunk))
            {
                _replySink.SubmitFast(_ => reply.ReplyAsync(ResumableFeedMessageCodec.Encode(new FeedChunkResp
                {
                    Offset = req.Offset, HasChunk = true, HasMore = true, Data = chunk,
                }), CancellationToken.None));
                return;
            }
            // 保留窗外缺口（被淘汰）——客户端须从重置面重入（超出本原语保留窗契约）
            _replySink.SubmitFast(_ => reply.ReplyAsync(ResumableFeedMessageCodec.Encode(new FeedChunkResp
            {
                Offset = req.Offset, HasChunk = false, HasMore = true,
            }), CancellationToken.None));
        }
    }

    /// <inheritdoc/>
    public void Dispose() { _chunks.Clear(); }
}

/// <summary>
/// 可恢复流客户端（二期-E2——拉取式消费）：从 startOffset 起按 offset 顺序拉取，每次成功才
/// 前进——断链/超时以同一 offset 重试即续传（不丢不重）；HasChunk=false+HasMore=false = 完成。
/// </summary>
public static class ResumableFeedClient
{
    /// <summary>顺序消费全量块（断线续传——同 offset 重试直至成功）。</summary>
    /// <param name="transport">发起端传输。</param>
    /// <param name="server">feed 服务端节点。</param>
    /// <param name="domain">服务端挂载域。</param>
    /// <param name="feedId">流标识。</param>
    /// <param name="startOffset">起始 offset。</param>
    /// <param name="onChunk">块消费回调（offset 单调递增、恰一次）。</param>
    /// <param name="pullTimeout">单次拉取等待（缺省 3s——重试窗口；断线续传由重试循环承担）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="clock">时钟供给源（时钟缝 件一 P1——重试退避等待；缺省 <see cref="TimeProvider.System"/>）。</param>
    /// <returns>完成 = 已越过尾部（HasChunk=false + HasMore=false）且全部块经 <paramref name="onChunk"/> 消费成功；
    /// 取消/应答畸形/offset 失配 = 抛（断线与保留窗缺口不外泄——内部重试续传）。</returns>
    public static async Task ConsumeAsync(IProtocolTransport transport, NodeId server, byte domain, Opaque16 feedId,
        long startOffset, Func<long, ReadOnlyMemory<byte>, ValueTask> onChunk,
        TimeSpan? pullTimeout = null, CancellationToken ct = default, TimeProvider? clock = null)
    {
        var offset = startOffset;
        var wait = pullTimeout ?? TimeSpan.FromSeconds(3);
        var delayClock = clock ?? TimeProvider.System;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            byte[] respBytes;
            try
            {
                var bytes = ResumableFeedMessageCodec.Encode(new FeedChunkReq { FeedId = feedId, Offset = offset });
                respBytes = await transport.SendRequestAsync(server, domain, bytes, ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NetIOException or TimeoutException)
            {
                await delayClock.Delay(50, ct).ConfigureAwait(false);
                continue;   // 断线/超时——同 offset 重试（续传）
            }
            if (!ResumableFeedMessageCodec.TryDecode(respBytes, out var msg) || msg is not FeedChunkResp resp)
                throw new InvalidOperationException("feed 应答畸形（解码失败）。");
            if (!resp.HasChunk)
            {
                if (resp.HasMore) { await delayClock.Delay(50, ct).ConfigureAwait(false); continue; }   // 保留窗缺口——重试
                return;   // 越过尾部 = 完成
            }
            if (resp.Offset != offset)
                throw new InvalidOperationException($"feed offset 失配：期望 {offset} 收到 {resp.Offset}。");
            await onChunk(offset, resp.Data).ConfigureAwait(false);
            offset++;
        }
    }
}
