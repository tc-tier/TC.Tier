using System.Buffers.Binary;
using System.Net.Quic;
using System.Runtime.Versioning;
using TC.Tier.Core.Net;
using TC.Tier.Core.Net.Channels;
using TC.Tier.Core.Net.Wire;

namespace TC.Tier.Core.Net.Transport.Quic;

/// <summary>
/// QUIC 流式会话（spec-11 W5——<see cref="IWireStream"/> 到 <see cref="QuicStream"/> 的直映射）：
/// QUIC 流控 = 背压天然传导（接收侧消费慢 → 对端 WriteAsync await——零机制帧）；
/// 帧边界 = [4B len] 长度前缀（IWireStream 消息边界语义承载）。
/// <para>★ 生命周期：<see cref="CompleteAsync"/> = CompleteWrites（对端枚举自然结束）；
/// <see cref="DisposeAsync"/> = Abort 双向（Reset 语义——双方读写立即终止）。</para>
/// </summary>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
internal sealed class QuicWireStream : IWireStream
{
    private readonly QuicStream _stream;
    private int _disposed;

    /// <summary>构造（发起侧与 acceptor 侧共用——stream 头已由传输分发面消费）。</summary>
    /// <param name="stream">QUIC 双向流（所有权转移至本件——Dispose 随本件）。</param>
    /// <param name="protocolId">协议域 ID。</param>
    /// <param name="peer">对端节点。</param>
    /// <param name="openPayload">开流载荷（随流建立透传——<see cref="OpenPayload"/> 暴露给使用方；默认空）。</param>
    public QuicWireStream(QuicStream stream, byte protocolId, NodeId peer, ReadOnlyMemory<byte> openPayload = default)
    {
        _stream = stream;
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

    /// <summary>写一帧（[4B len][data]——QUIC 写 await = 流控背压传导）。</summary>
    /// <param name="buffer">帧数据（字节；≤ <see cref="FrameCodec.MaxPayloadLength"/>——大块分帧推进）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>完成时帧已写入 QUIC stream（对端 Reset/链路异常抛 <see cref="NetIOException"/>）。</returns>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length > FrameCodec.MaxPayloadLength)
            throw new ArgumentOutOfRangeException(nameof(buffer), buffer.Length, "帧数据超上限（大块分帧推进）。");
        try
        {
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, buffer.Length);
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            if (buffer.Length > 0)
                await _stream.WriteAsync(buffer, ct).ConfigureAwait(false);
        }
        catch (QuicException ex)
        {
            // 对端 Reset/链路异常——介质异常转消费面异常（同构语义：写立即终止，不跨恢复）
            throw new NetIOException("QUIC 流写终止（对端 Reset 或链路关闭）。", ex);
        }
    }

    /// <summary>本端写完（CompleteWrites——对端枚举自然结束）。</summary>
    /// <param name="ct">取消令牌（本实现同步完成——形态面签名同构）。</param>
    /// <returns>完成时本端写侧已收尾（对端读序列随之自然结束）。</returns>
    public ValueTask CompleteAsync(CancellationToken ct = default)
    {
        _stream.CompleteWrites();
        return ValueTask.CompletedTask;
    }

    /// <summary>读全部帧（[4B len][data] 逐帧产出；对端 Complete 后 EOF 枚举自然结束）。</summary>
    /// <param name="ct">取消令牌（传播到读取——取消时停止枚举）。</param>
    /// <returns>逐帧数据块异步序列（帧长非法抛 InvalidOperationException；截断抛 EndOfStreamException）。</returns>
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var header = new byte[4];
        while (true)
        {
            if (!await TryReadExactlyAsync(_stream, header, ct).ConfigureAwait(false))
                yield break;   // EOF——对端已 Complete/关闭
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 0 || length > FrameCodec.MaxPayloadLength)
                throw new InvalidOperationException($"QUIC 流帧长度非法：{length}。");
            if (length == 0)
            {
                yield return ReadOnlyMemory<byte>.Empty;
                continue;
            }
            var frame = new byte[length];
            if (!await TryReadExactlyAsync(_stream, frame, ct).ConfigureAwait(false))
                throw new EndOfStreamException("QUIC 流帧截断——对端提前关闭。");
            yield return frame;
        }
    }

    /// <summary>中止（Reset 语义——双方读写立即终止）。</summary>
    /// <returns>完成时底层 QUIC stream 已释放（幂等）。</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _stream.DisposeAsync().ConfigureAwait(false);
    }

    private static async ValueTask<bool> TryReadExactlyAsync(QuicStream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }
}
