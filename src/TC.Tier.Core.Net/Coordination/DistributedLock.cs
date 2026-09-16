using System.Collections.Concurrent;
using System.Text;
using TC.Tier.CodeGen;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Logging;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Raft;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Coordination;

// ═══ raft 命令（状态机复制面——经 ReplicateAsync 投递；applied index 即 fencing token）═══

/// <summary>
/// 锁命令族（二期-G4 NETGAP-027——分布式锁/租约原语的 raft 复制命令）。
/// <para>★ 专用 raft 组形态（锁组状态机 = <see cref="LockStateMachine"/>）——组日志只承载锁命令。</para>
/// </summary>
[WireMessage]
public abstract record LockCommand
{
    /// <summary>锁名 blob 防御上限。</summary>
    public const int MaxNameBytes = 256;

    /// <summary>持有者 blob 防御上限。</summary>
    public const int MaxOwnerBytes = 256;
}

/// <summary>获取/续约命令（同 Owner 续约延展租期、token 不变；新授予 = token = apply index）。</summary>
[WireMessageTag(0x01)]
public sealed record LockAcquireCmd : LockCommand
{
    /// <summary>锁名（UTF-8）。</summary>
        /// <summary>锁名（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> NameBytes { get; init; }

    /// <summary>持有者（UTF-8）。</summary>
        /// <summary>持有者标识（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxOwnerBytes)]
    public ReadOnlyMemory<byte> OwnerBytes { get; init; }

    /// <summary>租约时长（ms——apply 节点时钟起算）。</summary>
    public required int LeaseMs { get; init; }
}

/// <summary>释放命令（Owner + fencing token 匹配才生效——旧持有者不可误删新持有者）。</summary>
[WireMessageTag(0x02)]
public sealed record LockReleaseCmd : LockCommand
{
        /// <summary>锁名（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> NameBytes { get; init; }

        /// <summary>持有者标识（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxOwnerBytes)]
    public ReadOnlyMemory<byte> OwnerBytes { get; init; }

    /// <summary>fencing token（单调递增——旧令牌释放被拒）。</summary>
    public required long FencingToken { get; init; }
}

// ═══ 远程服务面 wire（请求回调域——客户端 SDK 形态）═══

/// <summary>锁服务消息族（请求回调形态；CorrId 由传输承载）。</summary>
[WireMessage]
public abstract record LockServiceMessage;

/// <summary>获取/续约请求。</summary>
[WireMessageTag(0x01)]
public sealed record LockAcquireReq : LockServiceMessage
{
        /// <summary>锁名（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> NameBytes { get; init; }

        /// <summary>持有者标识（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxOwnerBytes)]
    public ReadOnlyMemory<byte> OwnerBytes { get; init; }

    /// <summary>租约时长（毫秒）。</summary>
    public required int LeaseMs { get; init; }
}

/// <summary>获取/续约应答。</summary>
[WireMessageTag(0x02)]
public sealed record LockAcquireResp : LockServiceMessage
{
    /// <summary>是否授予。</summary>
    public required bool Granted { get; init; }

    /// <summary>fencing token（Granted 时 = apply index——单调、跨重启单调；denied 时 = 0）。</summary>
    public required long FencingToken { get; init; }

        /// <summary>当前持有者标识（授予失败时回填——竞争观察面）。</summary>
[WireMember(MaxCount = LockCommand.MaxOwnerBytes)]
    public ReadOnlyMemory<byte> HolderBytes { get; init; }
}

/// <summary>释放请求（Owner + fencing token 匹配校验）。</summary>
[WireMessageTag(0x03)]
public sealed record LockReleaseReq : LockServiceMessage
{
        /// <summary>锁名（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxNameBytes)]
    public ReadOnlyMemory<byte> NameBytes { get; init; }

        /// <summary>持有者标识（UTF-8 字节）。</summary>
[WireMember(MaxCount = LockCommand.MaxOwnerBytes)]
    public ReadOnlyMemory<byte> OwnerBytes { get; init; }

    /// <summary>fencing token（单调递增——旧令牌释放被拒）。</summary>
    public required long FencingToken { get; init; }
}

/// <summary>释放应答。</summary>
[WireMessageTag(0x04)]
public sealed record LockReleaseResp : LockServiceMessage
{
    /// <summary>是否释放成功。</summary>
    public required bool Released { get; init; }
}

/// <summary>
/// 锁状态机（二期-G4——raft 复制的锁表）：ApplyAsync 按序应用 Acquire/Release——
/// <para>★ fencing token = 命令的 raft applied index：单调（raft 日志序）、跨重启单调（日志持久化）、
/// 可校验（受保护资源比对 token 排除旧 epoch 持有者写入）。</para>
/// <para>★ 租约 = apply 节点时钟的到期判定（时钟偏移影响租期精度；安全性由 fencing token 保证）。</para>
/// </summary>
public sealed class LockStateMachine : IStateMachine
{
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一——租约到期判定；缺省 System）

    /// <summary>构造。</summary>
    /// <param name="clock">时钟供给源（缺省 <see cref="TimeProvider.System"/> 行为不变；假钟下租约到期由快进确定性驱动）。</param>
    public LockStateMachine(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
    }

    private sealed class LockEntry(string owner, long fencingToken, long expiresAtTicks)
    {
        public string Owner { get; set; } = owner;
        public long FencingToken { get; set; } = fencingToken;
        public long ExpiresAtTicks { get; set; } = expiresAtTicks;
    }

    /// <summary>apply 结果（index → 结果——服务端读面；保留窗裁剪）。</summary>
    private readonly ConcurrentDictionary<long, (bool Granted, string Holder, long Token)> _results = new();

    private readonly Dictionary<string, LockEntry> _locks = new();

    /// <summary>读命令结果（Granted/Holder/Token——ReplicateAsync 返回 index 后读取，无竞态：
    /// index 的结果在 apply 该命令时写入，先于调用方观察到 applied 水位）。</summary>
    public (bool Granted, string Holder, long Token) ResultOf(long index)
        => _results.TryGetValue(index, out var r) ? r : (false, "?", 0);

    /// <summary>查询锁（诊断/管理面——存在与持有者）。</summary>
    public (bool Exists, string Owner) Query(string name)
    {
        if (_locks.TryGetValue(name, out var e) && e.ExpiresAtTicks > _clock.GetMsTimestamp())
            return (true, e.Owner);
        return (false, string.Empty);
    }

    /// <inheritdoc/>
    public ValueTask ApplyAsync(long index, ReadOnlyMemory<byte> command, CancellationToken ct = default)
    {
        if (!LockCommandCodec.TryDecode(command.Span, out var cmd))
            throw new InvalidOperationException($"锁命令解码失败（锁组日志只承载锁命令）：index={index}。");

        var now = _clock.GetMsTimestamp();
        switch (cmd)
        {
            case LockAcquireCmd a:
            {
                var owner = Encoding.UTF8.GetString(a.OwnerBytes.Span);
                var name = Encoding.UTF8.GetString(a.NameBytes.Span);
                var granted = false;
                var token = index;   // 新授予 = apply index
                var holder = owner;
                if (_locks.TryGetValue(name, out var e))
                {
                    if (e.Owner == owner && e.ExpiresAtTicks > now)
                    {
                        e.ExpiresAtTicks = now + a.LeaseMs;   // 续约——token 不变
                        granted = true;
                        token = e.FencingToken;
                    }
                    else if (e.ExpiresAtTicks <= now)
                    {
                        _locks[name] = new LockEntry(owner, index, now + a.LeaseMs);   // 租约过期——新授予新 token
                        granted = true;
                    }
                    else holder = e.Owner;   // 在租期内被他人持有——拒绝
                }
                else
                {
                    _locks[name] = new LockEntry(owner, index, now + a.LeaseMs);
                    granted = true;
                }
                _results[index] = (granted, holder, granted ? token : 0);
                break;
            }
            case LockReleaseCmd r:
            {
                var owner = Encoding.UTF8.GetString(r.OwnerBytes.Span);
                var name = Encoding.UTF8.GetString(r.NameBytes.Span);
                var released = _locks.TryGetValue(name, out var e)
                    && e.Owner == owner && e.FencingToken == r.FencingToken;
                if (released) _locks.Remove(name);
                _results[index] = (released, owner, 0);
                break;
            }
        }

        // 结果保留窗裁剪（读面已消费/超窗淘汰）
        if (_results.Count > 4096)
            foreach (var k in _results.Keys.Where(k => k < index - 2048))
                _results.TryRemove(k, out _);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// 分布式锁服务（二期-G4——挂载请求回调域，远程 Acquire/Renew/Release）：
/// 命令经 raft 复制（ReplicateAsync）应用后回读结果——线性化授予 + fencing token。
/// </summary>
public sealed class DistributedLockService : IRequestHandler, IDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly RaftStateMachine _raft;
    private readonly LockStateMachine _machine;
    // ★ 远程授予/释放回执经 TaskSink 受控提交（TCSG138 存量清扫）——异常观测面 + Dispose drain。
    private readonly TaskSink _replySink;
    private readonly ILogger? _logger;

    /// <summary>构造并按域挂载（域号使用方在注册区 0x60-0xAF 自管；须挂于锁组 raft 成员节点）。</summary>
    public DistributedLockService(IProtocolTransport transport, byte domain,
        RaftStateMachine raft, LockStateMachine machine, ILogger? logger = null)
    {
        _transport = transport;
        _raft = raft;
        _machine = machine;
        _logger = logger;
        _replySink = new TaskSink("lock-service", logger: logger);
        transport.RegisterRequestHandler(domain, this);
    }

    /// <inheritdoc/>
    public void OnRequest(NodeId from, ReadOnlyMemory<byte> payload, IReplyContext reply)
    {
        if (!LockServiceMessageCodec.TryDecode(payload.Span, out var req))
            return;   // 畸形——对端超时自愈

        switch (req)
        {
            case LockAcquireReq a:
                _replySink.Submit(async _ =>
                {
                    try
                    {
                        var index = await _raft.ReplicateAsync(
                            LockCommandCodec.Encode(new LockAcquireCmd
                            {
                                NameBytes = a.NameBytes, OwnerBytes = a.OwnerBytes, LeaseMs = a.LeaseMs,
                            }), CancellationToken.None).AsTask().ConfigureAwait(false);   // 应答回写不受入站取消传播（尽力应答）
                        var (granted, holder, token) = _machine.ResultOf(index);
                        await reply.ReplyAsync(LockServiceMessageCodec.Encode(new LockAcquireResp
                        {
                            Granted = granted, FencingToken = granted ? token : 0,
                            HolderBytes = Encoding.UTF8.GetBytes(holder),
                        }), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("锁授予失败（对端重试）：{Message}", ex.Message);
                    }
                });
                return;

            case LockReleaseReq r:
                _replySink.Submit(async _ =>
                {
                    try
                    {
                        var index = await _raft.ReplicateAsync(
                            LockCommandCodec.Encode(new LockReleaseCmd
                            {
                                NameBytes = r.NameBytes, OwnerBytes = r.OwnerBytes, FencingToken = r.FencingToken,
                            }), CancellationToken.None).AsTask().ConfigureAwait(false);   // 应答回写不受入站取消传播（尽力应答）
                        var (released, _, _) = _machine.ResultOf(index);
                        await reply.ReplyAsync(LockServiceMessageCodec.Encode(new LockReleaseResp
                        {
                            Released = released,
                        }), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug("锁释放失败（对端重试）：{Message}", ex.Message);
                    }
                });
                return;
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _replySink.Dispose();   // 有界 drain 在途回执（body 自带 catch-all——正常瞬完）
}

/// <summary>
/// 分布式锁客户端（二期-G4——SDK 形态）：Acquire/Keepalive(Renew)/Release + fencing token 读面。
/// </summary>
public sealed class DistributedLockClient : IAsyncDisposable
{
    private readonly IProtocolTransport _transport;
    private readonly NodeId _server;
    private readonly byte _domain;
    private readonly byte[] _name;
    private readonly byte[] _owner;
    private readonly int _leaseMs;
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一——续约周期等待；缺省 System）

    /// <summary>fencing token（持有期间有效——受保护资源写入须携带并比对单调性）。</summary>
    public long FencingToken { get; private set; }

    /// <summary>当前是否持有。</summary>
    public bool Holds => Volatile.Read(ref _holding) != 0;
    private int _holding;

    /// <summary>构造。</summary>
    /// <param name="transport">传输端口。</param>
    /// <param name="server">锁服务节点。</param>
    /// <param name="domain">协议域。</param>
    /// <param name="name">锁名。</param>
    /// <param name="owner">持有者标识。</param>
    /// <param name="leaseMs">租约时长（毫秒）。</param>
    /// <param name="clock">时钟供给源（时钟缝 件一——续约周期等待；缺省 <see cref="TimeProvider.System"/>）。</param>
    public DistributedLockClient(IProtocolTransport transport, NodeId server, byte domain,
        string name, string owner, int leaseMs, TimeProvider? clock = null)
    {
        _transport = transport;
        _server = server;
        _domain = domain;
        _name = Encoding.UTF8.GetBytes(name);
        _owner = Encoding.UTF8.GetBytes(owner);
        _leaseMs = leaseMs;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>获取/续约（Granted = token 就位；false = 被持有——Holder 可经 AcquireResp 观测）。</summary>
    /// <param name="ct">取消令牌（请求发送/应答等待阶段生效——取消不改变服务端锁状态）。</param>
    /// <returns>true = 授予/续约成功（<see cref="FencingToken"/> 就位、Holds 置位）；false = 锁在租期内被其他持有者占用（未授予，token 不变）。</returns>
    public async Task<bool> AcquireAsync(CancellationToken ct = default)
    {
        var bytes = LockServiceMessageCodec.Encode(new LockAcquireReq
        {
            NameBytes = _name, OwnerBytes = _owner, LeaseMs = _leaseMs,
        });
        var respBytes = await _transport.SendRequestAsync(_server, _domain, bytes, ct: ct).ConfigureAwait(false);
        if (!LockServiceMessageCodec.TryDecode(respBytes, out var msg) || msg is not LockAcquireResp resp)
            throw new InvalidOperationException("锁服务应答畸形。");
        if (!resp.Granted) return false;
        FencingToken = resp.FencingToken;
        Volatile.Write(ref _holding, 1);
        return true;
    }

    /// <summary>续约（同 Owner——token 不变、租期延展）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 续约成功（同 Owner 租期延展、token 不变；租约已过期则新授予、token 更新）；false = 租期内被其他持有者占用（被拒）。</returns>
    public Task<bool> RenewAsync(CancellationToken ct = default) => AcquireAsync(ct);

    /// <summary>释放（Owner + 当前 token 匹配才生效）。</summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 释放成功（Owner 与 fencing token 均匹配）；false = 释放未生效（token/Owner 失配或应答异常——服务端锁保持）。</returns>
    public Task<bool> ReleaseAsync(CancellationToken ct = default) => ReleaseWithTokenAsync(FencingToken, ct);

    /// <summary>以显式 token 释放（测试/诊断面——错 token = 服务端拒绝，锁保持）。</summary>
    /// <param name="token">释放校验用 fencing token（须与授予时的 apply index 一致——旧持有者不可误删新持有者）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 释放成功；false = 释放未生效（token/Owner 失配或应答畸形——服务端锁状态保持）。</returns>
    public async Task<bool> ReleaseWithTokenAsync(long token, CancellationToken ct = default)
    {
        Volatile.Write(ref _holding, 0);
        var bytes = LockServiceMessageCodec.Encode(new LockReleaseReq
        {
            NameBytes = _name, OwnerBytes = _owner, FencingToken = token,
        });
        var respBytes = await _transport.SendRequestAsync(_server, _domain, bytes, ct: ct).ConfigureAwait(false);
        if (!LockServiceMessageCodec.TryDecode(respBytes, out var msg) || msg is not LockReleaseResp resp)
            return false;
        return resp.Released;
    }

    /// <summary>keepalive 循环（每 renewalPeriod 续约；失锁即回调 onLost 并返回）。</summary>
    /// <param name="renewalPeriod">续约周期（应显著小于租约时长——续约被拒即失锁，循环内无重试）。</param>
    /// <param name="onLost">失锁回调（续约被拒即触发一次并退出循环；可为 null）。</param>
    /// <param name="ct">取消令牌（取消 = 循环静默退出——不触发 onLost）。</param>
    /// <returns>循环退出（失锁或取消）后完成。</returns>
    public async Task RunKeepaliveAsync(TimeSpan renewalPeriod, Action? onLost, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _clock.Delay(renewalPeriod, ct).ConfigureAwait(false);
                if (!await AcquireAsync(ct).ConfigureAwait(false))
                {
                    onLost?.Invoke();
                    return;
                }
            }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Volatile.Write(ref _holding, 0);
        await Task.CompletedTask;
    }
}
