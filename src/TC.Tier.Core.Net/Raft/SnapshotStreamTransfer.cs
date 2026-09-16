using System.Collections.Concurrent;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 快照流式传输（spec-03 §7 × spec-12 §6 单源基线——快照数据面走流式会话 0x04；
/// ISnapshotTransfer 的机制面内建实现——存储经 IRaftStore 快照读/导入面，传输零格式知识）。
/// <para>★ 时序（spec-03 §2）：leader 先导出（开流 → 逐条帧写 → End）→ 再发 InstallSnapshotReq
/// 握手；★ follower 在 acceptor 回调即启动消费循环<b>边收边导</b>（流式背压契约：读端不读 =
/// 写端挂起——IStreamAcceptor"重活自起消费循环"），<see cref="ImportSnapshotAsync"/> 等待导入完成
/// （"快照已经注入传输面送达，本 RPC 只做同步点握手"语义）。</para>
/// <para>★ 挂载：StartAsync 经 <see cref="ICoreProtocolPort"/> 内部口注册 0x04 acceptor。
/// 每 peer 至多一个在途导入（引擎快照安装防重入保证）。</para>
/// <para>★ 流式纪律：导出/导入均逐帧 O(单帧) 驻留（GB 级快照不驻内存）。</para>
/// </summary>
public sealed class SnapshotStreamTransfer : ISnapshotTransfer, IAsyncDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly IRaftStore _store;
    private readonly ILogger? _logger;
    private readonly TaskSink _imports;   // 入站导入任务组（结构化并发——消费循环受控）
    private readonly ConcurrentDictionary<NodeId, TaskCompletionSource> _incoming = [];   // follower 侧：来源 → 导入完成信号
    private readonly StreamAcceptor _acceptor;

    /// <summary>构造（零 IO——挂载经 <see cref="StartAsync"/>；传输/存储生命周期归装配层）。</summary>
    /// <param name="transport">节点端点完整面（流式会话开流/接受）。</param>
    /// <param name="store">存储端口（快照条目源 / 导入目标）。</param>
    /// <param name="logger">日志（可选）。</param>
    public SnapshotStreamTransfer(IProtocolTransport transport, IRaftStore store, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(store);
        _transport = transport;
        _store = store;
        _logger = logger;
        _imports = new TaskSink($"snap-stream-{transport.Self}", logger: logger);
        _acceptor = new StreamAcceptor(this);
    }

    /// <summary>启动：经内部口注册 0x04 快照流会话 acceptor。只可一次。</summary>
    /// <returns>完成时 acceptor 已注册（同步完成——重复启动抛）。</returns>
    /// <exception cref="InvalidOperationException">传输未实现内部挂载口。</exception>
    public Task StartAsync()
    {
        if (_transport is not ICoreProtocolPort core)
            throw new InvalidOperationException(
                $"机制挂载须内部注册口（ICoreProtocolPort）——介质 {_transport.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");
        core.RegisterCoreStreamAcceptor(ProtocolIds.SnapshotStream, _acceptor);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <returns>完成时导入任务组已有界排空（幂等）。</returns>
    public async ValueTask DisposeAsync() => await _imports.DisposeAsync().ConfigureAwait(false);

    // ═══ 入站（follower 侧——acceptor 即起消费循环边收边导）═══

    private sealed class StreamAcceptor(SnapshotStreamTransfer owner) : IStreamAcceptor
    {
        /// <summary>入站快照流转交（直通 owner.<see cref="OnIncomingStream"/>——消费归传输件）。</summary>
        /// <param name="from">发起方节点。</param>
        /// <param name="stream">快照流式会话。</param>
        /// <param name="openPayload">开流载荷（直挂 = 空；组路由 = host 剥前缀后的内层——二期-C1）。</param>
        // ★ 二期-C1：openPayload 内层透传——引擎零感知
        public void OnStream(NodeId from, IWireStream stream, ReadOnlyMemory<byte> openPayload)
            => owner.OnIncomingStream(from, stream);
    }

    private void OnIncomingStream(NodeId from, IWireStream stream)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_incoming.TryAdd(from, tcs))
        {
            _logger?.LogWarning("快照流会话重复（旧导入未完成被替换）：from={From}", from);
            _incoming[from] = tcs;   // 新会话覆盖（旧会话 Dispose 由消费循环兜底）
        }
        _imports.SubmitFast((Func<CancellationToken, ValueTask>)(async cancellationToken =>
        {
            await using (stream)
            {
                try
                {
                    await ConsumeAsync(stream, cancellationToken).ConfigureAwait(false);
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);   // 畸形帧/存储失败——导入等待面外泄（engine 应答失败重试）
                }
            }
        }));
    }

    /// <summary>消费循环（follower——两段式：preamble N₀ → 条目帧流解码导入）。</summary>
    /// <param name="stream">快照流式会话。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async ValueTask ConsumeAsync(IWireStream stream, CancellationToken cancellationToken=default)
    {
        var frames = stream.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (frames)
        {
            if (!await frames.MoveNextAsync().ConfigureAwait(false))
                throw new InvalidOperationException("快照流空（缺首帧 preamble）。");
            var preamble = SnapshotPreambleCodec.Read(Wire.PayloadCompression.Inflate(frames.Current).AsSpan());
            await _store.ImportSnapshotAsync(preamble.SnapshotIndex, DecodeEntriesAsync(frames, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>条目帧解码流（preamble 之后的剩余帧——逐帧解 (index, term, kind, content)）。</summary>
    /// <param name="frames">帧枚举器（preamble 之后）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>条目流（(Index, Term, Kind, Content) 四元组）。</returns>
    private static async IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> DecodeEntriesAsync(
        IAsyncEnumerator<ReadOnlyMemory<byte>> frames, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await frames.MoveNextAsync().ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = Wire.PayloadCompression.Inflate(frames.Current);   // ★ 二期-E1：解包络（原始条目帧）
            if (frame.Length < SnapshotEntryHeaderCodec.StructSize)
                throw new InvalidOperationException($"快照条目帧截断：len={frame.Length} < 头 {SnapshotEntryHeaderCodec.StructSize}。");
            var header = SnapshotEntryHeaderCodec.Read(frame.AsSpan());
            if (frame.Length != SnapshotEntryHeaderCodec.StructSize + header.PayloadLength)
                throw new InvalidOperationException($"快照条目帧长度失配：len={frame.Length} 期望 {SnapshotEntryHeaderCodec.StructSize + header.PayloadLength}。");
            yield return (header.Index, header.Term, header.Kind, frame.AsMemory(SnapshotEntryHeaderCodec.StructSize));
        }
    }

    // ═══ ISnapshotTransfer（spec-03 §2 数据面——单源流式形态：无握手协调数据，数据面自带一切）═══

    /// <inheritdoc/>
    public async ValueTask<SnapshotCoordination?> ExportSnapshotAsync(NodeId target, CancellationToken cancellationToken = default)
    {
        // ★ 快照创建 = 本实现策略：导出前把快照点推进到已持久化尾（快照 = 已持久化日志前缀——
        //   只含已提交数据）；"仅新数据重拍"判定 = TruncatePrefix 幂等（≤ 快照点 no-op）
        await _store.TruncatePrefixToAsync(_store.PersistedIndex, cancellationToken).ConfigureAwait(false);
        var n0 = _store.SnapshotIndex;

        // ★ 二期-C1：内层载荷为空（组前缀由组通道边界写入——引擎零感知）
        await using var stream = await _transport.OpenStreamAsync(target, ProtocolIds.SnapshotStream, ReadOnlyMemory<byte>.Empty, cancellationToken)
            .ConfigureAwait(false);
        // ★ 二期-E1：快照流逐帧压缩包络（流内编码——帧头不动；小帧 raw 直通，可压帧 DEFLATE）
        await stream.WriteAsync(Wire.PayloadCompression.Deflate(FramePreamble(n0)), cancellationToken)
            .ConfigureAwait(false);
        await foreach (var entry in _store.ReadSnapshotEntriesAsync(n0, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(Wire.PayloadCompression.Deflate(FrameEntry(entry)), cancellationToken)
                .ConfigureAwait(false);
        }
        await stream.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return null;   // 单源流式——无握手协调数据（swarm 形态见 SnapshotSwarmTransfer）
    }

    /// <inheritdoc/>
    /// <param name="source">导出方节点。</param>
    /// <param name="coordination">握手协调数据（单源流式形态忽略——数据面自带一切）。</param>
    /// <param name="cancellationToken">取消令牌（等待导入完成可取消）。</param>
    /// <returns>完成时快照流已接收并导入存储（无对应会话/导入失败抛——engine 应答失败重试）。</returns>
    public async ValueTask ImportSnapshotAsync(NodeId source, SnapshotCoordination? coordination = null, CancellationToken cancellationToken = default)
    {
        // 单源流式——协调数据不适用（swarm 装配混装由对端握手面拒绝；本形态忽略）
        if (!_incoming.TryRemove(source, out var tcs))
            throw new InvalidOperationException(
                $"无来自 {source} 的快照流会话（顺序违规——leader 应先导出再发 InstallSnapshot 握手）。");
        await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);   // 导入完成信号（消费循环并发推进——边收边导）
    }

    /// <summary>preamble 帧（8B 快照覆盖点）。</summary>
    /// <param name="n0">快照覆盖点 N₀。</param>
    /// <returns>preamble 帧字节数组。</returns>
    private static byte[] FramePreamble(long n0)
    {
        var frame = new byte[SnapshotPreambleCodec.StructSize];
        SnapshotPreambleCodec.Write(frame, new SnapshotPreamble(n0));
        return frame;
    }

    /// <summary>条目帧（[头][content] 拼装——长度全派生，零偏移字面量）。</summary>
    /// <param name="entry">条目四元组（Index, Term, Kind, Content）。</param>
    /// <returns>拼装的条目帧字节数组。</returns>
    private static byte[] FrameEntry((long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content) entry)
    {
        var headerSize = SnapshotEntryHeaderCodec.StructSize;
        var frame = new byte[headerSize + entry.Content.Length];
        SnapshotEntryHeaderCodec.Write(frame, new SnapshotEntryHeader(entry.Index, entry.Term, entry.Kind, entry.Content.Length));
        entry.Content.Span.CopyTo(frame.AsSpan(headerSize));
        return frame;
    }
}
