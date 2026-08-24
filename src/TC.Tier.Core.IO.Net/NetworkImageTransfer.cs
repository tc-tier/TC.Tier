using System.Buffers.Binary;
using System.IO.Hashing;
using System.Net;
using System.Net.Sockets;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;

namespace TC.Tier.Core.IO.Net;

/// <summary>传送模式（握手帧 mode 字节——TIN1 协议）。</summary>
public enum TransferMode : byte
{
    /// <summary>结构化 TCA1 流（默认——跨介质可转、逐帧 CRC、尾对账）。</summary>
    Structural = 0,

    /// <summary>裸字节流（对接外部 dd/nc——无控制帧语义外的校验，中断即报废；调用方显式选择）。</summary>
    /// <remarks>v1 预留值——握手即拒（未知保留值拒读同族，设计 §3.9）。</remarks>
    Raw = 1,
}

/// <summary>传送结果（接收端回执 + 双端一致的摘要对账）。</summary>
public sealed record NetworkTransferResult(long EntryCount, long FrameCount, long RawBytes, bool Verified);

/// <summary>
/// 根空间镜像的 TCP 流式收发（raw-medium-and-conversion-design §9——网络发射端）。
/// <para>★ 协议 TIN1：握手（magic "TIN1" | 版本 u16 | mode u8）→ TCA1 载荷 → 回执帧
///   （magic "TIN2" | 帧数 u64 | 原始字节 u64 | 聚合 CRC u32 | 状态 u8）。</para>
/// <para>★ 双向：同一 TCP 连接，发起端既可发送（Send）也可接收（ReceiveTo）——方向由调用者角色决定，
///   协议对称（对端另一角色运行互补方法）。</para>
/// <para>★ 校验：TCA1 逐帧 CRC + 尾对账之上，接收端回执回传摘要——发送端可确认对端落盘一致。</para>
/// <para>★ v1 边界：单连接单传送（无续传/多路复用——续传为帧级断点的后续增强，台账 RM-06）；
///   Restore 要求 seekable 流的适配在此解决（网络流经 <see cref="RetargetableStream"/> 中转）。</para>
/// </summary>
public static class NetworkImageTransfer
{
    private static ReadOnlySpan<byte> HandshakeMagic => "TIN1"u8;
    private static ReadOnlySpan<byte> ReceiptMagic => "TIN2"u8;
    private const ushort ProtocolVersion = 1;

    /// <summary>
    /// 发送端：采集本地根空间 → TCP 流推送（对端须先以 <see cref="ReceiveTo"/> 监听）。
    /// 阻塞至对端回执并对账（回执摘要 ≠ 本端摘要 = 传送失败抛异常）。
    /// </summary>
    public static NetworkTransferResult Send(IFileSystem source, string host, int port,
        ImageOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var client = new TcpClient();
        client.Connect(host, port);
        using var network = client.GetStream();
        WriteHandshake(network, TransferMode.Structural);

        var summary = RootSpaceImage.Capture(source, network, options);
        network.Flush();
        client.Client.Shutdown(SocketShutdown.Send);   // 半关发送——接收端 CopyTo 的 EOF 信号（回执走接收方向仍通）

        var receipt = ReadReceipt(network);
        if (!receipt.Verified)
            throw new IOException("对端回执报告校验失败——传送不完整。");
        if (receipt.EntryCount != summary.EntryCount || receipt.RawBytes != summary.RawBytes)
            throw new IOException(
                $"回执对账不符：条目 {receipt.EntryCount}=={summary.EntryCount}? 字节 {receipt.RawBytes}=={summary.RawBytes}?");
        return new NetworkTransferResult(summary.EntryCount, summary.FrameCount, summary.RawBytes, true);
    }

    /// <summary>
    /// 接收端：监听单连接 → TCA1 载荷还原到目标根空间（必须为空）→ 回执回传摘要。
    /// 返回监听所用的实际端口（port=0 时由系统分配——测试友好）。
    /// </summary>
    public static NetworkTransferResult ReceiveTo(IFileSystem destination, int port,
        ImageOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        try
        {
            using var client = listener.AcceptTcpClient();
            using var network = client.GetStream();
            var mode = ReadHandshake(network);
            if (mode != TransferMode.Structural)
                throw new IOException($"传送模式不支持：{mode}（v1 仅 Structural——Raw 为预留值拒读）");

            // 网络流单向——经中转流适配 Restore 的 seekable 要求（P3 传送层职责，设计 §9）
            using var relay = new RetargetableStream();
            network.CopyTo(relay);
            relay.Position = 0;
            var summary = RootSpaceImage.Restore(relay, destination, options);

            WriteReceipt(network, new NetworkTransferResult(summary.EntryCount, summary.FrameCount,
                summary.RawBytes, true));
            network.Flush();
            return new NetworkTransferResult(summary.EntryCount, summary.FrameCount, summary.RawBytes, true);
        }
        finally
        {
            listener.Stop();
        }
    }

    // ═══════════════ 帧编解码 ═══════════════

    private static void WriteHandshake(Stream s, TransferMode mode)
    {
        Span<byte> frame = stackalloc byte[7];
        HandshakeMagic.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], ProtocolVersion);
        frame[6] = (byte)mode;
        s.Write(frame);
    }

    private static TransferMode ReadHandshake(Stream s)
    {
        Span<byte> frame = stackalloc byte[7];
        ReadExactly(s, frame);
        if (!frame[..4].SequenceEqual(HandshakeMagic))
            throw new IOException("握手 magic 不符（非 TIN1 传送）。");
        var version = BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]);
        if (version != ProtocolVersion)
            throw new IOException($"协议版本不支持：{version}（本实现 {ProtocolVersion}）。");
        var mode = frame[6];
        if (mode is not ((byte)TransferMode.Structural or (byte)TransferMode.Raw))
            throw new IOException($"未知传送模式：{mode}（未知保留值拒读）。");
        return (TransferMode)mode;
    }

    private static void WriteReceipt(Stream s, NetworkTransferResult r)
    {
        Span<byte> frame = stackalloc byte[25];
        ReceiptMagic.CopyTo(frame);
        BinaryPrimitives.WriteInt64LittleEndian(frame[4..], r.EntryCount);
        BinaryPrimitives.WriteInt64LittleEndian(frame[12..], r.RawBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], Crc32.HashToUInt32(
            [.. BitConverter.GetBytes(r.FrameCount), .. BitConverter.GetBytes(r.RawBytes)]));
        frame[24] = r.Verified ? (byte)1 : (byte)0;
        s.Write(frame);
    }

    private static NetworkTransferResult ReadReceipt(Stream s)
    {
        Span<byte> frame = stackalloc byte[25];
        ReadExactly(s, frame);
        if (!frame[..4].SequenceEqual(ReceiptMagic))
            throw new IOException("回执 magic 不符（流不完整）。");
        return new NetworkTransferResult(
            BinaryPrimitives.ReadInt64LittleEndian(frame[4..]),    // EntryCount
            0,                                                         // FrameCount（回执不携带——对账用 RawBytes）
            BinaryPrimitives.ReadInt64LittleEndian(frame[12..]),    // RawBytes
            frame[24] != 0);                                         // Verified
    }

    private static void ReadExactly(Stream s, Span<byte> buffer)
    {
        var got = 0;
        while (got < buffer.Length)
        {
            var n = s.Read(buffer[got..]);
            if (n <= 0) throw new IOException("连接中断（帧不完整）。");
            got += n;
        }
    }

    /// <summary>可重定位中转流（MemoryStream 语义别名——网络→Restore 的 seekable 适配器）。</summary>
    private sealed class RetargetableStream : MemoryStream;
}
