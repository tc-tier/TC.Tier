using System;
using System.Collections.Concurrent;
using System.Threading.Channels;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;
using TC.Tier.Core.Observability;
using TC.Tier.Core.Execution;

namespace TC.Tier.Core.Net.Swarm;

/// <summary>
/// 多源并行同步（spec-12 §6.1——BitTorrent 同构：块清单 manifest + 并行度 K 多源拉取 +
/// 块级换源重试 + 块校验和防错源）。
/// <para>★ 形态映射（§6）：取块 = 请求回调（at-most-once 缺省——块级换源重试是投递语义的
/// 换源变体）；持有者 = 显式列表（gossip 捎带集成随 T5 快照安装消费时定义）。</para>
/// <para>★ 挂载（§3.5）：StartAsync 经 <see cref="ICoreProtocolPort"/> 内部口注册
/// <see cref="ProtocolIds.SwarmSync"/>（0x03）请求 handler；本地块源经 <see cref="SetSource"/>
/// 装配（holder 侧内容知识归使用方）。</para>
/// <para>★ 边界（§6.1）：raft 日志复制不多源（线性一致）——本机制仅内容寻址的基线数据；
/// 装配点幂等 = 同一内容同一 Id 重下载结果一致（manifest 校验和保证）。</para>
/// <para>★ 下载语义：逐块流式产出（O(并行度 × 块) 驻留——消费者逐块落盘，GB 级不驻内存）；
/// 单块全源耗尽 = 整体失败（<see cref="SwarmDownloadException"/>——部分装配失败重来，
/// 装配点幂等）；错块拦截（校验失败 = 换源 + 计数 + Net 视图 CrcFailure 钩子）。</para>
/// </summary>
public sealed class SwarmSync : IAsyncDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly SwarmOptions _options;
    private readonly ILogger? _logger;
    private readonly ObservabilityHub.NetView? _netView;
    private readonly TaskSink _replies;   // 应答回写任务组（fire-and-forget 纪律——handler 同步回调内的受控丢弃）
    private ISwarmBlockSource? _source;   // 本地块源（晚绑定——快照安装每次装配新 manifest 切换；raft 域单槽快捷形态）
    private readonly object _attachedLock = new();
    private readonly List<ISwarmBlockSource> _attachedSources = [];   // 附加块源（#436 件四——多内容源注册，按注册序探测）
    private readonly RpcHandler _handler;
    private int _started;
    private long _badBlocks;   // 错块拦截计数（跨线程观测——校验失败换源）
    // leader 端 manifest 代际持有表（spec-12 §6.1 增量 S2——Announce 到达即登记，重复幂等；
    // 持有关系 = (节点, manifestId) 二元组——快照换代 = 新 Id，旧代表 Announce 不污染新代表）
    // #436 件四：键带内容域——raft 换届清表只清 Raft 域，与 Blob 域共存互不误伤
    private readonly ConcurrentDictionary<(SwarmContentDomain Domain, Opaque16 Id), ConcurrentDictionary<NodeId, byte>> _manifestHolders = new();

    /// <summary>错块拦截累计数（校验和失败被拒的块）。</summary>
    public long BadBlockCount => Interlocked.Read(ref _badBlocks);

    /// <summary>节点端点完整面（反熵对账的探测发送面——装配面接线用）。</summary>
    public IProtocolTransport Transport => _transport;

    /// <summary>构造（零 IO——挂载经 <see cref="StartAsync"/>；传输端点生命周期归装配层）。</summary>
    /// <param name="transport">节点端点完整面（取块请求回调收发）。</param>
    /// <param name="options">参数（null = 缺省）。</param>
    /// <param name="logger">日志（可选）。</param>
    /// <param name="hub">可观测中心（可选——null = Disabled；错块拦截计数归 Net 视图 CrcFailure）。</param>
    public SwarmSync(IProtocolTransport transport, SwarmOptions? options = null,
        ILogger? logger = null, ObservabilityHub? hub = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        _options = options ?? SwarmOptions.Default;
        _logger = logger;
        _netView = hub?.Net;
        _replies = new TaskSink($"swarm-{transport.Self}", logger: logger);
        _handler = new RpcHandler(this);
    }

    /// <inheritdoc/>
    /// <returns>完成时出站应答任务组已有界排空（幂等）。</returns>
    public async ValueTask DisposeAsync() => await _replies.DisposeAsync().ConfigureAwait(false);

    /// <summary>装配本地块源（晚绑定/切换——holder 侧内容随使用方装配变动）。</summary>
    /// <param name="source">本地块源（null = 清空——本端不再持有任何内容）。</param>
    public void SetSource(ISwarmBlockSource? source) => _source = source;

    /// <summary>注册附加块源（#436 件四——多内容源；入站取块按 manifestId 顺序探测 HasManifest 命中者）。
    /// raft 单槽 <see cref="SetSource"/> 优先——既有行为不变。</summary>
    public void AttachSource(ISwarmBlockSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_attachedLock)
        {
            _attachedSources.Add(source);
        }
    }

    /// <summary>按内容标识路由本地块源（raft 单槽优先，附加源按注册序探测 HasManifest 命中者）。</summary>
    private ISwarmBlockSource? RouteSource(Opaque16 manifestId)
    {
        var raft = _source;
        if (raft is not null && raft.HasManifest(manifestId)) return raft;
        lock (_attachedLock)
        {
            foreach (var attached in _attachedSources)
            {
                if (attached.HasManifest(manifestId)) return attached;
            }
        }

        return null;
    }

    // ═══ 持有表（spec-12 §6.1 增量 S2——leader 端 manifest 代际持有关系）═══

    /// <summary>登记持有者（Announce 到达/本端发布——幂等；跨线程安全）。</summary>
    /// <param name="manifestId">基线内容标识（代）。</param>
    /// <param name="holder">持有节点。</param>
    public void RegisterSource(Opaque16 manifestId, NodeId holder)
        => RegisterSource(SwarmContentDomain.Raft, manifestId, holder);

    /// <summary>登记持有者（域化——#436 件四；幂等，跨线程安全）。</summary>
    public void RegisterSource(SwarmContentDomain domain, Opaque16 manifestId, NodeId holder)
    {
        var holders = _manifestHolders.GetOrAdd((domain, manifestId), _ => new ConcurrentDictionary<NodeId, byte>());
        holders.TryAdd(holder, 0);
    }

    /// <summary>持有者列表（空表兜底 [本端]——v1 行为，无上报时退化为单源，永不比现在差）。</summary>
    /// <param name="manifestId">基线内容标识（代）。</param>
    /// <returns>持有该代内容的节点列表（无上报记录时返回仅含本端的单元素表）。</returns>
    public IReadOnlyList<NodeId> GetHolders(Opaque16 manifestId)
        => GetHolders(SwarmContentDomain.Raft, manifestId);

    /// <summary>持有者列表（域化；空表兜底 [本端]——v1 行为，无上报时退化为单源，永不比现在差）。</summary>
    public IReadOnlyList<NodeId> GetHolders(SwarmContentDomain domain, Opaque16 manifestId)
    {
        var holders = _manifestHolders.TryGetValue((domain, manifestId), out var h) ? h : null;
        if (holders is null || holders.IsEmpty)
            return [_transport.Self];
        return [.. holders.Keys];
    }

    /// <summary>换代清表（快照重导出 = 新 manifest——旧代 Announce 迟到不污染新代表）：
    /// 清该代全部持有者并登记本端（导出方必持有刚发布的内容）。</summary>
    /// <param name="manifestId">新代内容标识。</param>
    public void ResetHolders(Opaque16 manifestId)
        => ResetHolders(SwarmContentDomain.Raft, manifestId);

    /// <summary>换代清表（域化——清该域该代全部持有者并登记本端）。</summary>
    public void ResetHolders(SwarmContentDomain domain, Opaque16 manifestId)
    {
        _manifestHolders[(domain, manifestId)] = new ConcurrentDictionary<NodeId, byte>();
        RegisterSource(domain, manifestId, _transport.Self);
    }

    /// <summary>换届清表（leader 卸任即丢表——本进程状态；新 leader 当选后由全体持有者重报覆盖）。</summary>
    public void OnLeaderLost()
    {
        foreach (var key in _manifestHolders.Keys)
        {
            if (key.Domain == SwarmContentDomain.Raft)
                _manifestHolders.TryRemove(key, out _);
        }
    }

    /// <summary>内容域持有上报（#436 件四——Blob 域无 leader，目标 = 消费方配置的种子集；尽力送达）。</summary>
    public async Task AnnounceContentAsync(IEnumerable<NodeId> seeds, Opaque16 manifestId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        if (!ShouldAnnounce) return;
        foreach (var seed in seeds)
        {
            if (ct.IsCancellationRequested) return;
            await AnnounceSourceAsync(seed, manifestId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>清单发现（#436 件三——向种子拉取内容清单；未持有/种子不可达 = null）。</summary>
    public async Task<SwarmManifest?> GetManifestAsync(NodeId seed, Opaque16 manifestId,
        CancellationToken ct = default)
    {
        var req = new SwarmManifestReq { ManifestId = manifestId };
        var perCall = _options.RequestTimeout is { } t ? new RequestOptions { Timeout = t } : null;
        var reqLen = SwarmMessageCodec.EncodePooled(req, out var reqBuffer);
        try
        {
            var respBytes = await _transport.SendRequestAsync(seed, ProtocolIds.SwarmSync,
                reqBuffer.AsMemory(0, reqLen), perCall, ct).ConfigureAwait(false);
            if (!SwarmMessageCodec.TryDecode(respBytes, out var msg) || msg is not SwarmManifestResp resp)
                return null;
            if (!resp.HasManifest)
                return null;
            return new SwarmManifest
            {
                Id = resp.Id,
                TotalBytes = resp.TotalBytes,
                BlockSize = resp.BlockSize,
                Checksums = resp.Checksums,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException)
        {
            return null;   // 种子不可达——调用方换下一种子
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(reqBuffer);
        }
    }

    /// <summary>是否广播持有上报（ServeBlocks 与 AnnounceOnStart 同 true——纯消费者/退出广播面都不发）。</summary>
    public bool ShouldAnnounce => _options.ServeBlocks && _options.AnnounceOnStart;

    /// <summary>
    /// 持有上报（尽力送达——单边声明；失败静默：丢失由下次上报/换届重报覆盖）。
    /// </summary>
    /// <param name="leader">上报目标（当前已知 leader）。</param>
    /// <param name="manifestId">持有的基线内容标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时上报已尽力送达（失败静默——丢失由下次上报/换届重报覆盖）。</returns>
    public async Task AnnounceSourceAsync(NodeId leader, Opaque16 manifestId, CancellationToken ct = default)
    {
        if (!ShouldAnnounce) return;   // 纯消费者 / 退出广播面——不发
        try
        {
            var len = SwarmMessageCodec.EncodePooled(new SourceAnnounceMsg { ManifestId = manifestId }, out var buffer);
            try
            {
                await _transport.SendRequestAsync(leader, ProtocolIds.SwarmSync,
                    buffer.AsMemory(0, len), null, ct)
                    .ConfigureAwait(false);   // 应答 = 传输闭环空应答（非语义确认——内容不读）
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception)
        {
            // 尽力送达最后一步——leader 不可达/超时，换届重报自愈
        }
    }

    /// <summary>启动：经内部口注册 SwarmSync 协议域（取块请求 handler）。只可一次。</summary>
    /// <returns>完成时注册已提交（同步完成——<see cref="SwarmOptions.ServeBlocks"/>=false 时不注册域）。</returns>
    /// <exception cref="InvalidOperationException">重复启动或传输未实现内部挂载口。</exception>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("SwarmSync 已启动（StartAsync 只可一次）。");
        if (_transport is not ICoreProtocolPort core)
            throw new InvalidOperationException(
                $"机制挂载须内部注册口（ICoreProtocolPort）——介质 {_transport.GetType().Name} 未实现；机制宿主归 Core.Net（spec-12 §3.5）。");
        if (_options.ServeBlocks)   // 纯消费者（ServeBlocks=false）：不注册域——不服务取块、不收持有上报
            core.RegisterCoreRequestHandler(ProtocolIds.SwarmSync, _handler);
        return Task.CompletedTask;
    }

    // ═══ 入站（取块 handler + 持有上报——holder 侧）═══

    private sealed class RpcHandler(SwarmSync owner) : IRequestHandler
    {
        /// <summary>入站请求转发（直通 owner 内部 OnRequest——解码/路由归 SwarmSync）。</summary>
        /// <param name="from">请求来源节点。</param>
        /// <param name="payload">线格式载荷。</param>
        /// <param name="reply">应答回程上下文。</param>
        public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
            => owner.OnRequest(from, payload, reply);
    }

    private void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (!SwarmMessageCodec.TryDecode(payload.Span, out var msg))
        {
            // 畸形——不答（对端超时换源）
            _logger?.LogWarning("SwarmSync 请求解码失败（丢弃）：from={From} len={Length}", from, payload.Length);
            return;
        }
        if (msg is SourceAnnounceMsg announce)
        {
            // 持有上报（尽力面——重复幂等；错报由块校验和防线兜底）+ 空应答闭环传输
            RegisterSource(announce.ManifestId, from);
            _logger?.LogDebug("SwarmSync 持有上报：manifest={Id} holder={From}", announce.ManifestId, from);
            _replies.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
            {
                try { await reply.ReplyAsync(ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false); }
                catch (Exception) { /* 回程失败——上报方尽力语义 */ }
            }));
            return;
        }
        if (msg is SwarmManifestReq manifestReq)
        {
            // 清单发现应答（#436 件三——路由命中源实现 ISwarmManifestSource 即可应答；未持有 = 空应答）
            SwarmManifest? manifest = null;
            var manifestRoute = RouteSource(manifestReq.ManifestId);
            if (manifestRoute is ISwarmManifestSource manifestSource
                && manifestSource.TryGetManifest(manifestReq.ManifestId, out var m))
                manifest = m;
            _replies.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                await ReplyAsync(reply, ToManifestResp(manifest)).ConfigureAwait(false)));
            return;
        }
        if (msg is EntropyProbeReq probe)
        {
            // 反熵对账应答（只读比对——零写路径；未持内容 = 空集，发起方跳过）
            var hashes = Array.Empty<uint>();
            var src = RouteSource(probe.ManifestId) ?? _source;
            if (src is { } srcN && srcN.TryGetChecksums(probe.ManifestId, out var checksums))
            {
                if (probe.Level == 0)
                    hashes = [SwarmMerkle.ComputeGlobalRoot(checksums)];
                else if (probe.Level == 1)
                {
                    var start = Math.Clamp(probe.RangeStart, 0, checksums.Length);
                    var count = Math.Clamp(probe.RangeCount, 0, checksums.Length - start);
                    hashes = checksums.AsSpan(start, count).ToArray();
                }
            }
            _replies.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
                await ReplyAsync(reply, new EntropyProbeResp { Hashes = hashes }).ConfigureAwait(false)));
            return;
        }
        if (msg is GetBlockReq req)
        {
            OnGetBlock(from, req, reply);
            return;
        }
        // Resp 不应入站（畸形）——不答
    }

    private static SwarmManifestResp ToManifestResp(SwarmManifest? manifest)
        => manifest is null
            ? new SwarmManifestResp
            {
                HasManifest = false,
                Id = Opaque16.Empty,
                TotalBytes = 0,
                BlockSize = 0,
                Checksums = Array.Empty<uint>(),
            }
            : new SwarmManifestResp
            {
                HasManifest = true,
                Id = manifest.Id,
                TotalBytes = manifest.TotalBytes,
                BlockSize = manifest.BlockSize,
                Checksums = manifest.Checksums,
            };

    private void OnGetBlock(NodeId from, GetBlockReq req, IReplyContext reply)
    {
        bool has = false;
        var data = Array.Empty<byte>();
        var source = RouteSource(req.ManifestId);
        if (source is not null && source.HasManifest(req.ManifestId)
            && source.TryGetBlock(req.ManifestId, req.BlockIndex, out var block))
        {
            has = true;
            data = block.ToArray();   // 线载荷拷贝（源缓冲跨调用稳定不承诺）
        }
        // ★ 应答回写受控提交（fire-and-forget 纪律——handler 同步回调内不裸丢弃，任务组观测）
        _replies.SubmitFast((Func<CancellationToken, ValueTask>)(async ct =>
            await ReplyAsync(reply, new GetBlockResp
            {
                ManifestId = req.ManifestId,
                BlockIndex = req.BlockIndex,
                HasBlock = has,
                Data = data,
            }).ConfigureAwait(false)));
    }

    private static async ValueTask ReplyAsync(IReplyContext reply, SwarmMessage resp)
    {
        var len = SwarmMessageCodec.EncodePooled(resp, out var buffer);
        try { await reply.ReplyAsync(buffer.AsMemory(0, len)).ConfigureAwait(false); }
        catch (Exception)
        {
            // 回程失败 = 尽力送达最后一步——对端换源
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ═══ 下载（多源并行——spec-12 §6.1）═══

    /// <summary>
    /// 下载内容（并行度 K 多源拉取——逐块流式产出，消费者逐块落盘）。
    /// <para>★ 换源：每块从轮转起点遍历持有者（源超时/无块/畸形/错块 = 换下一源）——
    ///   单块全源耗尽 = 整体失败（迭代收尾抛 <see cref="SwarmDownloadException"/>）。</para>
    /// </summary>
    /// <param name="manifest">块清单（权威方构建）。</param>
    /// <param name="holders">持有者列表（可含本端）。</param>
    /// <param name="onBlockCompleted">块完成回调（块号 + 来源——可观测/测试计数；null = 无）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="parallelismOverride">并行度覆写（null = Options.Parallelism；1 = 顺序交付——顺序落盘导入用）。</param>
    /// <returns>块流（(块号, 块字节)——无序产出，消费者按块号定位落盘）。</returns>
    public async IAsyncEnumerable<(long Index, byte[] Block)> DownloadAsync(
        SwarmManifest manifest, IReadOnlyList<NodeId> holders,
        Action<long, NodeId>? onBlockCompleted = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default,
        int? parallelismOverride = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(holders);
        if (holders.Count == 0)
            throw new ArgumentException("持有者列表不能为空。", nameof(holders));
        var parallelism = Math.Max(1, Math.Min(parallelismOverride ?? _options.Parallelism, manifest.BlockCount));
        var failures = new ConcurrentDictionary<long, byte>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateUnbounded<(long, byte[])>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        long next = -1;
        var workers = new Task[parallelism];
        for (var i = 0; i < parallelism; i++)
        {
            workers[i] = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    var index = Interlocked.Increment(ref next);
                    if (index >= manifest.BlockCount) break;
                    var block = await FetchBlockAsync(manifest, holders, index, onBlockCompleted, stop.Token).ConfigureAwait(false);
                    if (block is null)
                    {
                        failures.TryAdd(index, 0);   // 单块全源耗尽——首败即停新取（在途块自然收敛）
                        await stop.CancelAsync();
                        break;
                    }
                    await channel.Writer.WriteAsync((index, block), stop.Token).ConfigureAwait(false);
                }
            }, stop.Token);   // CA2016：停止令牌转发（任务未启动即取消的窗口封闭）
        }

        var wait = Task.WhenAll(workers).ContinueWith(_ => channel.Writer.TryComplete(), TaskScheduler.Default);
        await foreach (var item in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return item;
        }
        await wait.ConfigureAwait(false);   // 迭代器收尾——worker 全退（含停止令牌触发的在途收敛）

        if (!failures.IsEmpty)
            throw new SwarmDownloadException(failures.Keys.ToArray(), manifest);
    }

    /// <summary>定向块拉取（反熵"只传差异块"——仅指定块号，换源/校验和全复用；单块全源耗尽
    /// = 整体失败 <see cref="SwarmDownloadException"/>）。</summary>
    /// <param name="manifest">块清单。</param>
    /// <param name="holders">持有者列表。</param>
    /// <param name="blockIndexes">目标块号（按序逐块取回）。</param>
    /// <param name="onBlockCompleted">块完成回调。</param>
    /// <param name="ct">取消令牌。</param>
    public async IAsyncEnumerable<(long Index, byte[] Block)> DownloadBlocksAsync(
        SwarmManifest manifest, IReadOnlyList<NodeId> holders, IEnumerable<long> blockIndexes,
        Action<long, NodeId>? onBlockCompleted = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(holders);
        if (holders.Count == 0)
            throw new ArgumentException("持有者列表不能为空。", nameof(holders));
        foreach (var index in blockIndexes)
        {
            if ((ulong)index >= (ulong)manifest.BlockCount)
                throw new ArgumentOutOfRangeException(nameof(blockIndexes), index, "块号超清单范围。");
            var block = await FetchBlockAsync(manifest, holders, index, onBlockCompleted, ct).ConfigureAwait(false);
            if (block is null)
                throw new SwarmDownloadException([index], manifest);
            yield return (index, block);
        }
    }

    /// <summary>单块取回（换源循环——轮转起点确定性：(块号 + 尝试序) % 源数）。</summary>
    private async Task<byte[]?> FetchBlockAsync(SwarmManifest manifest, IReadOnlyList<NodeId> holders,
        long blockIndex, Action<long, NodeId>? onBlockCompleted, CancellationToken ct)
    {
        var req = new GetBlockReq { ManifestId = manifest.Id, BlockIndex = blockIndex };
        var perCall = _options.RequestTimeout is { } t ? new RequestOptions { Timeout = t } : null;
        for (var a = 0; a < holders.Count; a++)
        {
            var holder = holders[(int)((blockIndex + a) % holders.Count)];
            var reqLen = SwarmMessageCodec.EncodePooled(req, out var reqBuffer);
            try
            {
                var respBytes = await _transport.SendRequestAsync(holder, ProtocolIds.SwarmSync,
                    reqBuffer.AsMemory(0, reqLen), perCall, ct).ConfigureAwait(false);
                if (!SwarmMessageCodec.TryDecode(respBytes, out var msg) || msg is not GetBlockResp resp)
                    continue;   // 畸形应答——换源
                if (resp.ManifestId != manifest.Id || resp.BlockIndex != blockIndex)
                    continue;   // 回显失配——畸形，换源
                if (!resp.HasBlock)
                    continue;   // holder 无此块（正常形态）——换源
                if (!manifest.VerifyBlock(blockIndex, resp.Data))
                {
                    // ★ 错块拦截（校验和失败——防错源）：换源 + 计数 + Net 视图钩子
                    Interlocked.Increment(ref _badBlocks);
                    _netView?.OnCrcFailure();
                    _logger?.LogWarning("SwarmSync 错块拦截：manifest={Id} block={Index} from={Holder}",
                        manifest.Id, blockIndex, holder);
                    continue;
                }
                onBlockCompleted?.Invoke(blockIndex, holder);
                return resp.Data;
            }
            catch (OperationCanceledException)
            {
                throw;   // 取消传播（迭代器终止——不换源、不记失败）
            }
            catch (Exception ex) when (ex is IOException or TimeoutException)
            {
                // 源失败/超时——换下一源
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(reqBuffer);   // 0-Copy：应答裁决后归还租借载荷
            }
        }
        return null;   // 全源耗尽
    }
}

/// <summary>
/// Swarm 下载失败（spec-12 §6.1——单块全源耗尽 = 整体失败；装配点幂等：重下载一致）。
/// </summary>
public sealed class SwarmDownloadException : NetIOException
{
    /// <summary>失败的块号集合。</summary>
    public IReadOnlyList<long> FailedBlocks { get; }

    /// <summary>清单（诊断——重试输入）。</summary>
    public SwarmManifest Manifest { get; }

    /// <summary>构造。</summary>
    /// <param name="failedBlocks">失败块号。</param>
    /// <param name="manifest">清单。</param>
    public SwarmDownloadException(long[] failedBlocks, SwarmManifest manifest)
        : base($"Swarm 下载失败：{failedBlocks.Length} 块全源耗尽（manifest={manifest.Id}）。")
    {
        FailedBlocks = failedBlocks;
        Manifest = manifest;
    }
}
