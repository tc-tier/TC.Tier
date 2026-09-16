using System.Buffers.Binary;
using System.Text;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 协议级批量/管道（二期-E5 NETGAP-033）：把 N 个单项请求封装为**一个**请求帧
/// （一次往返摊薄 per-request 开销），应答同样批量回程（逐项 status + 载荷）。
/// <para>★ 信封（自描述、帧头不动）：</para>
/// <code>
/// 请求  [count 4B][×N: len 4B + bytes]
/// 应答  [count 4B][×N: status 1B + len 4B + bytes]   status: 0=ok, 1=error
/// </code>
/// <para>★ 管道：批量请求之间天然并发（传输 CorrId 独立配对）——同链多批在途即管道。</para>
/// <para>★ 约束：批量域 handler 须同步应答（快进快出——deferred 应答不兼容批量聚合）；
/// 单项上限/总量遵循帧 16MB 上界（Encode 超限抛）。</para>
/// </summary>
public static class RequestBatch
{
    /// <summary>单项载荷上限（保守——批量总帧仍受传输 16MB 帧界约束）。</summary>
    public const int MaxItemBytes = 1 << 20;

    /// <summary>批量项数上限（防御——畸形计数拦截）。</summary>
    public const int MaxItems = 1024;

    /// <summary>编码批量请求。</summary>
    /// <param name="items">批量单项载荷列表（非空；项数 ≤ <see cref="MaxItems"/>；单项 ≤ <see cref="MaxItemBytes"/>）。</param>
    /// <returns>批量请求帧字节（[count 4B][×N: len 4B + bytes]，小端）。</returns>
    public static byte[] EncodeRequest(IReadOnlyList<ReadOnlyMemory<byte>> items)
    {
        ValidateItems(items);
        var buffer = new byte[4 + items.Count * 4 + items.Sum(i => i.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, items.Count);
        var cursor = 4;
        foreach (var item in items)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(cursor), item.Length);
            cursor += 4;
            item.Span.CopyTo(buffer.AsSpan(cursor));
            cursor += item.Length;
        }
        return buffer;
    }

    /// <summary>解码批量请求（畸形 = 抛）。</summary>
    /// <param name="payload">批量请求帧（由 <see cref="EncodeRequest"/> 产出——计数/长度/尾部长度须自洽）。</param>
    /// <returns>解码出的单项载荷列表（顺序与帧内一致——切片引用原载荷）。</returns>
    public static IReadOnlyList<ReadOnlyMemory<byte>> DecodeRequest(ReadOnlyMemory<byte> payload)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxItems);
        var cursor = 4;
        var items = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++)
        {
            var len = BinaryPrimitives.ReadInt32LittleEndian(payload.Span[cursor..]);
            ArgumentOutOfRangeException.ThrowIfNegative(len);
            cursor += 4;
            if (cursor + len > payload.Length)
                throw new InvalidOperationException($"批量请求截断：item {i} 期望 {len}B。");
            items[i] = payload.Slice(cursor, len);
            cursor += len;
        }
        if (cursor != payload.Length)
            throw new InvalidOperationException($"批量请求尾部长度失配：余 {payload.Length - cursor}B。");
        return items;
    }

    /// <summary>编码批量应答（带逐项状态——0=ok, 1=error）。</summary>
    /// <param name="results">逐项（status, 载荷）结果（status 由调用方定——本方法不校验取值）。</param>
    /// <returns>批量应答帧字节（[count 4B][×N: status 1B + len 4B + bytes]，小端）。</returns>
    public static byte[] EncodeResponseWithStatus(IReadOnlyList<(byte Status, ReadOnlyMemory<byte> Payload)> results)
    {
        var buffer = new byte[4 + results.Count * 5 + results.Sum(r => r.Payload.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, results.Count);
        var cursor = 4;
        foreach (var (status, item) in results)
        {
            buffer[cursor] = status;
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(cursor + 1), item.Length);
            cursor += 5;
            item.Span.CopyTo(buffer.AsSpan(cursor));
            cursor += item.Length;
        }
        return buffer;
    }

    /// <summary>编码批量应答（全部成功形态）。</summary>
    /// <param name="results">逐项成功载荷（项数 ≤ <see cref="MaxItems"/>；单项 ≤ <see cref="MaxItemBytes"/>）。</param>
    /// <returns>批量应答帧字节（逐项 status 固定 0=ok）。</returns>
    public static byte[] EncodeResponse(IReadOnlyList<ReadOnlyMemory<byte>> results)
    {
        ValidateItems(results);
        var buffer = new byte[4 + results.Count * 5 + results.Sum(r => r.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, results.Count);
        var cursor = 4;
        foreach (var item in results)
        {
            buffer[cursor] = 0;   // status ok
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(cursor + 1), item.Length);
            cursor += 5;
            item.Span.CopyTo(buffer.AsSpan(cursor));
            cursor += item.Length;
        }
        return buffer;
    }

    /// <summary>解码批量应答。</summary>
    /// <param name="payload">批量应答帧（由 <see cref="EncodeResponseWithStatus"/>/<see cref="EncodeResponse"/> 产出）。</param>
    /// <returns>解码出的逐项载荷列表（顺序与帧内一致；任一项 status ≠ 0 = 抛，不产出错误项）。</returns>
    public static IReadOnlyList<ReadOnlyMemory<byte>> DecodeResponse(ReadOnlyMemory<byte> payload)
    {
        var count = BinaryPrimitives.ReadInt32LittleEndian(payload.Span);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, MaxItems);
        var cursor = 4;
        var items = new ReadOnlyMemory<byte>[count];
        for (var i = 0; i < count; i++)
        {
            var status = payload.Span[cursor];
            var len = BinaryPrimitives.ReadInt32LittleEndian(payload.Span[(cursor + 1)..]);
            ArgumentOutOfRangeException.ThrowIfNegative(len);
            cursor += 5;
            if (cursor + len > payload.Length)
                throw new InvalidOperationException($"批量应答截断：item {i} 期望 {len}B。");
            if (status != 0)
                throw new InvalidOperationException($"批量应答 item {i} 错误（status={status}）。");
            items[i] = payload.Slice(cursor, len);
            cursor += len;
        }
        return items;
    }

    private static void ValidateItems(IReadOnlyList<ReadOnlyMemory<byte>> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(items.Count, MaxItems);
        foreach (var item in items)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(item.Length, MaxItemBytes);
        }
    }
}

/// <summary>
/// 批量服务端适配器（二期-E5）：包装单项 handler——解码批量请求 → 逐项调用 → 聚合批量应答。
/// 逐项异常 = 该项 Error 标记（其余项不受影响）。**批量域 handler 须同步应答**。
/// </summary>
public sealed class RequestBatchHandler : IRequestHandler, IDisposable
{
    private readonly IRequestHandler _itemHandler;

    /// <summary>释放聚合应答回执任务组（生命周期归创建方——宿主收口时调用）。</summary>
    public void Dispose() => _replySink.Dispose();
    // ★ 聚合应答回执经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面（计数/日志）。
    //   不挂 Dispose 链（对齐 request-replay 惰性 sink 先例——收尾兜底语义，省 Dispose 耦合）。
    private readonly TaskSink _replySink = new("batch-reply");

    /// <summary>构造。</summary>
    /// <param name="itemHandler">单项处理器（批量域内每项调用；须同步应答）。</param>
    public RequestBatchHandler(IRequestHandler itemHandler)
    {
        ArgumentNullException.ThrowIfNull(itemHandler);
        _itemHandler = itemHandler;
    }

    /// <inheritdoc/>
    public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        var items = RequestBatch.DecodeRequest(payload);
        var results = new (byte Status, ReadOnlyMemory<byte> Payload)[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var captured = new BatchItemContext(reply);
            try
            {
                _itemHandler.OnRequest(from, items[i], captured);
                results[i] = (0, captured.Reply ?? throw new InvalidOperationException(
                    "批量域 item 未同步应答——批量域 handler 须同步应答（快进快出）。"));
            }
            catch (Exception ex)
            {
                results[i] = (1, Encoding.UTF8.GetBytes($"item {i} error: {ex.Message}"));   // 逐项错误标记
            }
        }
        _replySink.SubmitFast(_ => reply.ReplyAsync(RequestBatch.EncodeResponseWithStatus(results), CancellationToken.None));
    }

    /// <summary>单项应答捕获上下文（批量聚合用）。</summary>
    private sealed class BatchItemContext(IReplyContext inner) : IReplyContext
    {
        public byte[]? Reply;

        public NodeId Peer => inner.Peer;
        public byte ProtocolId => inner.ProtocolId;
        public ulong CorrelationId => inner.CorrelationId;

        /// <summary>捕获单项应答（不回程——由批量聚合统一编码应答帧）。</summary>
        /// <param name="payload">单项应答载荷（复制为副本留存）。</param>
        /// <param name="ct">取消令牌（本实现同步捕获，忽略）。</param>
        /// <returns>完成即应答已捕获。</returns>
        public ValueTask ReplyAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        {
            Reply = payload.ToArray();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>批量客户端助手（管道 = 多批并发在途——传输 CorrId 独立配对）。</summary>
public static class RequestBatchClient
{
    /// <summary>发送批量请求并取回逐项应答（一次往返）。</summary>
    /// <param name="transport">发起端传输（扩展方法接收者）。</param>
    /// <param name="target">批量服务端节点。</param>
    /// <param name="domain">服务端挂载域（handler 须同步应答——批量聚合契约）。</param>
    /// <param name="items">批量单项载荷（项数 ≤ <see cref="RequestBatch.MaxItems"/>；单项 ≤ <see cref="RequestBatch.MaxItemBytes"/>）。</param>
    /// <param name="timeout">整体往返超时（null = 3s 缺省；默认 null）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时返回逐项应答载荷（顺序与 <paramref name="items"/> 一致；任一项错误/帧畸形 = 抛）。</returns>
    public static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> SendBatchAsync(
        this IProtocolTransport transport, NodeId target, byte domain,
        IReadOnlyList<ReadOnlyMemory<byte>> items, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var batch = RequestBatch.EncodeRequest(items);
        var respBytes = await transport.SendRequestAsync(target, domain, batch,
            new RequestOptions { Timeout = timeout ?? TimeSpan.FromSeconds(3) }, ct).ConfigureAwait(false);
        return RequestBatch.DecodeResponse(respBytes);
    }
}
