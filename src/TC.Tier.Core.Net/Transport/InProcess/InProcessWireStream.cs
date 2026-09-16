using System.Threading.Channels;
using TC.Tier.Core.Net.Channels;

namespace TC.Tier.Core.Net.Transport.InProcess;

/// <summary>
/// 进程内流式会话（spec-12 §5.3 <see cref="IWireStream"/> 内存实现——同构基准）：
/// 一对有界 Channel 直连两端——无帧/无会话号（内存对象直连，语义与 TCP 会话同构）。
/// <para>★ 背压 = 写 await 传导：对端不读 → 出站 Channel 满 → 本端 <see cref="WriteAsync"/> await。</para>
/// <para>★ 不走注入矩阵——流式对抗由 Dispose/Reset 驱动（帧级注入是数据报/请求回调面）。</para>
/// </summary>
internal sealed class InProcessWireStream : IWireStream
{
    private readonly Channel<ReadOnlyMemory<byte>> _outChannel;   // 本端出站（writer——对端读）
    private readonly Channel<ReadOnlyMemory<byte>> _inChannel;    // 本端入站（reader——对端写）
    private int _completed;

    private InProcessWireStream(Channel<ReadOnlyMemory<byte>> outChannel, Channel<ReadOnlyMemory<byte>> inChannel,
        byte protocolId, NodeId peer, ReadOnlyMemory<byte> openPayload)
    {
        _outChannel = outChannel;
        _inChannel = inChannel;
        ProtocolId = protocolId;
        Peer = peer;
        OpenPayload = openPayload;
    }

    /// <inheritdoc/>
    public byte ProtocolId { get; }

    /// <inheritdoc/>
    public NodeId Peer { get; }

    /// <inheritdoc/>
    public ReadOnlyMemory<byte> OpenPayload { get; }

    /// <summary>创建会话对（发起端流 + 接受端流——同对 Channel 的两端视图）。</summary>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="initiator">发起节点。</param>
    /// <param name="acceptor">接受节点。</param>
    /// <param name="windowFrames">每方向缓冲帧数（背压窗口）。</param>
    /// <param name="openPayload">开流载荷（随会话对透传——发起端与接受端 <see cref="OpenPayload"/> 同值；默认空）。</param>
    /// <returns>（发起端流，接受端流）。</returns>
    public static (InProcessWireStream Initiator, InProcessWireStream Acceptor) CreatePair(
        byte protocolId, NodeId initiator, NodeId acceptor, int windowFrames, ReadOnlyMemory<byte> openPayload = default)
    {
        var toAcceptor = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(windowFrames)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var toInitiator = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(windowFrames)
        {
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        return (new InProcessWireStream(toAcceptor, toInitiator, protocolId, acceptor, openPayload),
                new InProcessWireStream(toInitiator, toAcceptor, protocolId, initiator, openPayload));
    }

    /// <inheritdoc/>
    /// <param name="buffer">待写数据块（字节）。</param>
    /// <param name="ct">取消令牌（等待写入时可取消）。</param>
    /// <returns>完成时数据块已入站（出站缓冲满 = await——背压传导；会话中止抛 <see cref="NetIOException"/>）。</returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            await _outChannel.Writer.WriteAsync(buffer, ct).ConfigureAwait(false);   // 缓冲满 = await（对端消费慢——背压传导）
        }
        catch (ChannelClosedException ex)
        {
            throw new NetIOException("会话已中止（Reset）。", ex);   // 与 TCP 会话 Reset 语义同构
        }
    }

    /// <inheritdoc/>
    /// <param name="ct">取消令牌（形态面签名同构——本实现同步完成）。</param>
    /// <returns>完成时本端写侧已正常收尾（对端读序列随之结束；幂等）。</returns>
    public ValueTask CompleteAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) _outChannel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    /// <param name="ct">取消令牌（传播到枚举——取消时停止读取）。</param>
    /// <returns>入站数据块异步序列（按写入序；对端 Reset/Dispose = 序列异常终止）。</returns>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(CancellationToken ct = default)
        => _inChannel.Reader.ReadAllAsync(ct);

    /// <inheritdoc/>
    /// <returns>完成时双向通道已异常终止（对端读序列异常、对端写抛 <see cref="NetIOException"/>——TCP Reset 同构）。</returns>
    public ValueTask DisposeAsync()
    {
        // 对端中止语义（与 TCP Reset 同构）：双向通道异常终止——本端读枚举异常、对端后续写抛 NetIOException
        var reset = new NetIOException("会话被对端 Reset。");
        _outChannel.Writer.TryComplete(reset);   // 对端读异常终止
        _inChannel.Writer.TryComplete(reset);    // 本端读异常终止 + 对端写抛
        return ValueTask.CompletedTask;
    }
}
