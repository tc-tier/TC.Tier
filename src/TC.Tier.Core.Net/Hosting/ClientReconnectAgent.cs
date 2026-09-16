using System.Net;
using TC.Tier.Core.Execution;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Ports;

namespace TC.Tier.Core.Net.Hosting;

/// <summary>
/// 地址制客户端自动重连代理（二期-D7 NETGAP-015——Net 装配面）：
/// 跟踪初始连接的对端，<see cref="ITransport.PeerGone"/> 触发即按退避（初值 ×2 封顶）
/// 经 <see cref="IProtocol.OpenStreamAsync"/> 同源的 <see cref="IProtocol.SendRequestAsync"/>
/// 地址制拨号（ConnectAsync）重连，成功后重新武装（下次断链再入——单飞防并发重拨）。
/// <para>★ 生命周期：INodeMechanism 形态挂载——随 NodeEndpoint 释放；装配面 =
/// <see cref="NetClientBuilder.WithAutoReconnect"/>。</para>
/// </summary>
internal sealed class ClientReconnectAgent : INodeMechanism
{
    private readonly TimeProvider _clock;   // 时钟供给源（时钟缝 件一 P1）
    private readonly IProtocolTransport _transport;   // 事件订阅面（PeerGone）
    private readonly Func<IPEndPoint, CancellationToken, ValueTask<NodeId>> _redial;
    private readonly IPEndPoint _endpoint;
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maxDelay;
    // ★ 重拨经 TaskSink 受控提交（TCSG138 存量清扫）：组取消 + drain + 异常观测面一体
    //   （取消归一——重拨循环被 Dispose 取消时 Task.Delay 的 OCE 不再是无观测 fault）。
    private readonly TaskSink _redialSink = new("client-reconnect");
    private object? _remoteBox;   // NodeId? 装箱（null = 未武装）
    private int _redialing;

    public ClientReconnectAgent(IProtocolTransport transport,
        Func<IPEndPoint, CancellationToken, ValueTask<NodeId>> redial,
        IPEndPoint endpoint, TimeSpan initialDelay, TimeSpan maxDelay, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;   // 时钟供给源（时钟缝 件一 P1——重连退避）
        _transport = transport;
        _redial = redial;   // 地址制拨号（介质具体面——由装配方以具体传输绑定）
        _endpoint = endpoint;
        _initialDelay = initialDelay;
        _maxDelay = maxDelay;
    }

    public string Name => "client-reconnect";

    /// <summary>武装：跟踪当前对端（初始连接成功后调用——此后断链即自动重拨）。</summary>
    public void Arm(NodeId remote)
    {
        _remoteBox = remote;
        _transport.PeerGone += OnPeerGone;
    }

    /// <inheritdoc/>
    public ValueTask MountAsync(IProtocolTransport transport, CancellationToken ct = default)
        => ValueTask.CompletedTask;   // 无物理挂载——武装面在 Arm；接口兼容（机制形态统一收口）

    private void OnPeerGone(NodeId id)
    {
        if (_remoteBox is not NodeId tracked || id != tracked) return;
        if (Interlocked.Exchange(ref _redialing, 1) != 0) return;   // 单飞——重拨已在途
        // ★ 尽力语义：Dispose 期间对端断链事件仍会到达（传输收尾关链触发）——已释放/释放竞窗
        //   提交即 ODE 上抛事件源（PeerLink.Close 拆链路径），吞掉（重拨已无意义，随组收尾）。
        try { _redialSink.Submit(ct => RedialAsync(ct)); }
        catch (ObjectDisposedException) { Volatile.Write(ref _redialing, 0); }
    }

    private async Task RedialAsync(CancellationToken ct)
    {
        try
        {
            var delay = _initialDelay;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var remote = await _redial(_endpoint, ct).ConfigureAwait(false);
                    _remoteBox = remote;   // 重连成功——重新武装（下次断链再入）
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    // 尽力重连（对端未恢复/网络未收敛）——退避续试
                }
                await _clock.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _maxDelay.Ticks));
            }
        }
        finally
        {
            Volatile.Write(ref _redialing, 0);   // 重新武装单飞——下次断链可再入
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
        => await _redialSink.DisposeAsync().ConfigureAwait(false);   // 取消传播 + 有界 drain（重拨循环协作收尾）
}
