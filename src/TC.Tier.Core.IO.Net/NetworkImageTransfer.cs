using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using TC.Tier.Core.IO;
using TC.Tier.Core.IO.Image;
using TC.Tier.Core.Primitives;

namespace TC.Tier.Core.IO.Net;

/// <summary>传送模式（握手帧 mode 字节——TIN1 协议）。</summary>
public enum TransferMode
{
    /// <summary>结构化 TCA1 流（默认——跨介质可转、逐帧 CRC、尾对账）。</summary>
    Structural = 0,

    /// <summary>
    /// 裸字节流（Raw/dd 档——#492 整卷字节镜像）：连续卷介质（能力位 <see cref="FileSystemCapabilities.ContiguousCapture"/>）
    /// 的整卷原始字节顺序收发，逐帧 CRC + 声明长度尾对账 + 回执帧端到端确认；源侧维护租约 =
    /// 传输窗口只读冻结。载荷形态见 <see cref="NetworkImageTransfer"/> 头注。
    /// </summary>
    Raw = 1,
}

/// <summary>传送结果（接收端回执 + 双端一致的摘要对账）。</summary>
/// <param name="EntryCount">传送的根空间条目总数。</param>
/// <param name="FrameCount">传送的 TCA1 帧总数。</param>
/// <param name="RawBytes">传送的原始字节总数。</param>
/// <param name="Verified">true=接收端校验通过（CRC + 对账一致）；false=校验失败。</param>
public sealed record NetworkTransferResult(long EntryCount, long FrameCount, long RawBytes, bool Verified);

/// <summary>
/// 根空间镜像的 TCP 流式收发（raw-medium-and-conversion-design §9——网络发射端）。
/// <para>★ 协议 TIN1：握手（magic "TIN1" | 版本 u16 | mode u8）→ 载荷 → 回执帧
///   （magic "TIN2" | 帧数 u64 | 原始字节 u64 | 聚合 CRC u32 | 状态 u8）。</para>
/// <para>★ 双向：同一 TCP 连接，发起端既可发送（Send）也可接收（ReceiveTo）——方向由调用者角色决定，
///   协议对称（对端另一角色运行互补方法）。</para>
/// <para>★ 校验：TCA1 逐帧 CRC + 尾对账之上，接收端回执回传摘要——发送端可确认对端落盘一致。</para>
/// <para>★ Raw 档（mode=Raw，#492）：连续卷整卷字节镜像——载荷为 [源长 u64] + 数据帧
///   （[len u32][payload][crc32 u32]，1 MiB 分块逐帧 CRC）+ 终结帧（len=0）；
///   回执 CRC 字段 = 聚合载荷 CRC（Structural 档 = 摘要字段 CRC——模式分轨语义）。
///   源侧 EnterMaintenance(WriteOperations) = 传输窗口只读冻结；接收端 EnterMaintenance(AllOperations)
///   + 容量预检（不足写前拒绝，目标零字节受损）+ 长度对账 + <b>OnMirrorCompleted</b> 重建内存元数据。</para>
/// <para>★ v1 边界：单连接单传送（无续传/多路复用——续传为帧级断点的后续增强，台账 RM-06）；
///   Restore 要求 seekable 流的适配在此解决（网络流经 <see cref="RetargetableStream"/> 中转）。</para>
/// </summary>
public static class NetworkImageTransfer
{
    private static ReadOnlySpan<byte> HandshakeMagic => "TIN1"u8;
    private static ReadOnlySpan<byte> ReceiptMagic => "TIN2"u8;
    private const ushort ProtocolVersion = 1;
    private const int RawFrameBytes = 1 << 20;   // Raw 档数据帧载荷上限（1 MiB——尾帧可短）

    /// <summary>
    /// 发送端：采集本地根空间 → TCP 流推送（对端须先以 ReceiveTo 监听）。
    /// 阻塞至对端回执并对账（回执摘要 ≠ 本端摘要 = 传送失败抛异常）。
    /// </summary>
    /// <param name="source">本地根空间文件系统（被采集方）。</param>
    /// <param name="host">对端监听地址。</param>
    /// <param name="port">对端监听端口。</param>
    /// <param name="options">采集选项（过滤/进度回调）；null=默认全量。</param>
    /// <param name="ct">取消令牌（取消时中断发送——已发送部分不可恢复）。</param>
    /// <returns>发送端视角的传送摘要（Verified=true=对端回执确认一致）。</returns>
    /// <exception cref="IOException">对端回执报告校验失败，或回执对账不符（条目/字节数不匹配）。</exception>
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

        var receipt = ReadReceipt(network, out _);
        if (!receipt.Verified)
            throw new IOException("对端回执报告校验失败——传送不完整。");
        if (receipt.EntryCount != summary.EntryCount || receipt.RawBytes != summary.RawBytes)
            throw new IOException(
                $"回执对账不符：条目 {receipt.EntryCount}=={summary.EntryCount}? 字节 {receipt.RawBytes}=={summary.RawBytes}?");
        return new NetworkTransferResult(summary.EntryCount, summary.FrameCount, summary.RawBytes, true);
    }

    /// <summary>
    /// Raw 档发送端（#492——整卷字节镜像）：连续卷 [0, Length) 原始字节分帧推送，
    /// 阻塞至对端回执并端到端对账（Verified + 字节数 + 聚合 CRC 三对账，不符抛异常）。
    /// <para>★ 传输窗口源侧只读冻结（EnterMaintenance WriteOperations——采集期间源域静默，租约随本方法进出）。</para>
    /// </summary>
    /// <param name="source">源根空间——须连续卷介质（能力位 <see cref="FileSystemCapabilities.ContiguousCapture"/>
    ///   且实现 <see cref="IContiguousVolume"/>，即 TierVolume 族；否则抛 <see cref="FileIOException"/> Unsupported）。</param>
    /// <param name="host">对端监听地址。</param>
    /// <param name="port">对端监听端口。</param>
    /// <param name="ct">取消令牌（取消时中断发送——已发送部分不可恢复）。</param>
    /// <returns>发送端视角的传送摘要（EntryCount 恒 0；FrameCount=数据帧数；Verified=true=对端回执确认一致）。</returns>
    /// <exception cref="FileIOException">源介质非连续卷（Unsupported）。</exception>
    /// <exception cref="IOException">对端回执报告校验失败，或端到端对账不符（字节数/聚合 CRC 不匹配）。</exception>
    public static NetworkTransferResult SendRaw(IFileSystem source, string host, int port, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var volume = RequireContiguousVolume(source, nameof(SendRaw));

        using var lease = source.EnterMaintenance("network-image-raw-src", MaintenanceScope.WriteOperations, ct);
        using var client = new TcpClient();
        client.Connect(host, port);
        using var network = client.GetStream();
        WriteHandshake(network, TransferMode.Raw);

        using var backing = volume.OpenVolumeBacking(writable: false);
        long total = backing.Length;
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(header, (ulong)total);
        network.Write(header);   // 声明帧——尾对账基准 + 接收端容量预检

        var buffer = new byte[RawFrameBytes];
        uint aggregateCrc = 0;
        long frames = 0, sent = 0;
        Span<byte> frameTail = stackalloc byte[8];
        while (sent < total)
        {
            ct.ThrowIfCancellationRequested();
            var n = backing.Read(buffer.AsSpan(0, (int)Math.Min(RawFrameBytes, total - sent)));
            if (n <= 0)
                throw new IOException($"源卷读取中断：位置 {sent} 处提前 EOF（声明 {total} 字节）。");
            aggregateCrc = UnifiedCrc.ComputeCrc32(aggregateCrc, buffer.AsSpan(0, n));
            BinaryPrimitives.WriteUInt32LittleEndian(frameTail, (uint)n);
            BinaryPrimitives.WriteUInt32LittleEndian(frameTail[4..], UnifiedCrc.ComputeCrc32(buffer.AsSpan(0, n)));
            network.Write(frameTail);   // [len u32][crc32 u32]
            network.Write(buffer.AsSpan(0, n));
            sent += n;
            frames++;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(frameTail, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frameTail[4..], 0);
        network.Write(frameTail);   // 终结帧（len=0）——显式收尾
        network.Flush();
        client.Client.Shutdown(SocketShutdown.Send);   // 半关发送——回执走接收方向仍通

        var receipt = ReadReceipt(network, out var receiptCrcField);
        if (!receipt.Verified)
            throw new IOException("对端回执报告校验失败——Raw 传送不完整。");
        if (receipt.EntryCount != 0 || receipt.RawBytes != total)
            throw new IOException(
                $"Raw 回执对账不符：条目 {receipt.EntryCount}（Raw 恒 0）? 字节 {receipt.RawBytes}=={total}?");
        if (receiptCrcField != aggregateCrc)
            throw new IOException(
                $"Raw 回执聚合 CRC 不符：对端 0x{receiptCrcField:X8} == 本端 0x{aggregateCrc:X8}?——载荷端到端不一致。");
        return new NetworkTransferResult(0, frames, total, true);
    }

    /// <summary>
    /// 接收端：监听单连接 → TCA1 载荷还原到目标根空间（必须为空）→ 回执回传摘要。
    /// 返回监听所用的实际端口（port=0 时由系统分配——测试友好）。
    /// </summary>
    /// <param name="destination">目标根空间文件系统（必须为空——还原方）。</param>
    /// <param name="port">监听端口；0=系统分配（返回实际端口）。</param>
    /// <param name="options">还原选项（过滤/进度回调）；null=默认全量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>接收端视角的传送摘要（Verified=true=本地校验通过，回执已回传）。</returns>
    /// <exception cref="IOException">握手 magic 不符、协议版本不支持、传送模式非 Structural（Raw 预留值拒读）。</exception>
    public static NetworkTransferResult ReceiveTo(IFileSystem destination, int port,
        ImageOptions? options = null, CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();   // 绑定同步完成——调用方先取 LocalEndPoint 端口再启动发送方，无绑定竞速
        return ReceiveTo(destination, listener, options, ct);
    }

    /// <summary>
    /// 接收端（预绑定形态）：调用方自建并 <c>Start()</c> 的监听器——绑定先于发送方存在，
    /// 结构性消除"接收端未就绪"竞速（测试与多监听编排的确定性形态；慢 runner 上 Sleep 赌绑定必输）。
    /// </summary>
    /// <param name="destination">目标根空间文件系统（必须为空——还原方）。</param>
    /// <param name="listener">已 <c>Start()</c> 的监听器（本方法结束即 Stop——生命周期由本方法收口）。</param>
    /// <param name="options">还原选项（过滤/进度回调）；null=默认全量。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>接收端视角的传送摘要（Verified=true=本地校验通过，回执已回传）。</returns>
    /// <exception cref="IOException">握手 magic 不符、协议版本不支持、传送模式非 Structural（Raw 预留值拒读）。</exception>
    public static NetworkTransferResult ReceiveTo(IFileSystem destination, TcpListener listener,
        ImageOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(listener);
        try
        {
            using var client = listener.AcceptTcpClient();
            using var network = client.GetStream();
            var mode = ReadHandshake(network);
            if (mode != TransferMode.Structural)
                throw new IOException($"传送模式不支持：{mode}（本入口仅 Structural——Raw 档走 ReceiveRawTo）");

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

    // ═══════════════ Raw 档接收端（#492——整卷字节镜像落盘）═══════════════

    /// <summary>
    /// Raw 档接收端（端口形态）：自建监听 → 整卷字节镜像写入目标连续卷 → 回执回传。
    /// <para>★ 整卷覆盖为破坏性操作（dd 语义）：容量预检先于首字节写入；镜像完成后目标实例内存元数据
    ///   经 <see cref="IContiguousVolume.OnMirrorCompleted"/> 从盘重建——镜像目标不应有活跃句柄（文档化契约）。</para>
    /// </summary>
    /// <param name="destination">目标根空间——须连续卷介质（同 <see cref="SendRaw"/> 源要求）。</param>
    /// <param name="port">监听端口；0=系统分配。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>接收端视角的传送摘要（Verified=true=逐帧 CRC + 长度对账 + 聚合 CRC 全部通过，回执已回传）。</returns>
    public static NetworkTransferResult ReceiveRawTo(IFileSystem destination, int port, CancellationToken ct = default)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return ReceiveRawTo(destination, listener, ct);
    }

    /// <summary>Raw 档接收端（预绑定形态）——绑定先于发送方存在，结构性消除就绪竞速（同 Structural 档形态）。</summary>
    /// <param name="destination">目标根空间——须连续卷介质（同 <see cref="SendRaw"/> 源要求）。</param>
    /// <param name="listener">已 <c>Start()</c> 的监听器（本方法结束即 Stop）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>接收端视角的传送摘要（Verified=true=全部校验通过）。</returns>
    /// <exception cref="IOException">握手违约、帧 CRC 不符、长度对账不符、或对端声明长度越容量。</exception>
    public static NetworkTransferResult ReceiveRawTo(IFileSystem destination, TcpListener listener,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(listener);
        var volume = RequireContiguousVolume(destination, nameof(ReceiveRawTo));
        try
        {
            using var client = listener.AcceptTcpClient();
            using var network = client.GetStream();
            var mode = ReadHandshake(network);
            if (mode != TransferMode.Raw)
                throw new IOException($"传送模式不支持：{mode}（本入口仅 Raw——Structural 档走 ReceiveTo）");

            Span<byte> header = stackalloc byte[8];
            ReadExactly(network, header);
            var declared = (long)BinaryPrimitives.ReadUInt64LittleEndian(header);

            // 写入端完全隔离 + 容量预检（FastPath D6 同款——不足写前拒绝，目标零字节受损）
            using var lease = destination.EnterMaintenance("network-image-raw-dst", MaintenanceScope.AllOperations, ct);
            using var backing = volume.OpenVolumeBacking(writable: true);
            if (backing.Length < declared)
                throw new IOException(
                    $"目标卷容量不足：{backing.Length} < 对端声明 {declared} 字节——整卷覆盖已预检拒绝（目标未受任何写入）。");

            var buffer = new byte[RawFrameBytes];
            uint aggregateCrc = 0;
            long frames = 0, received = 0;
            Span<byte> frameTail = stackalloc byte[8];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                ReadExactly(network, frameTail);
                var len = (int)BinaryPrimitives.ReadUInt32LittleEndian(frameTail);   // ≤ RawFrameBytes（越界守卫下行）
                if (len == 0) break;   // 终结帧
                var frameCrc = BinaryPrimitives.ReadUInt32LittleEndian(frameTail[4..]);
                if ((uint)len > RawFrameBytes || received + len > declared)
                    throw new IOException($"Raw 帧越界：len={len}（received={received}/declared={declared}）——流完整性违约。");
                ReadExactly(network, buffer.AsSpan(0, len));
                if (UnifiedCrc.ComputeCrc32(buffer.AsSpan(0, len)) != frameCrc)
                    throw new IOException($"Raw 帧 CRC 不符：帧 #{frames}（{len} 字节）——传输损坏，镜像报废。");
                aggregateCrc = UnifiedCrc.ComputeCrc32(aggregateCrc, buffer.AsSpan(0, len));
                backing.Write(buffer.AsSpan(0, len));   // 顺序写（容量预检后从 0 覆盖——dd 语义）
                received += len;
                frames++;
            }

            if (received != declared)
                throw new IOException($"Raw 长度对账不符：实收 {received} == 声明 {declared}?——流提前终结，镜像报废。");

            volume.OnMirrorCompleted();   // 字节镜像覆盖盘上状态——实例内存元数据从盘重建（租约内）

            WriteReceiptFrame(network, entryCount: 0, rawBytes: received,
                crcField: aggregateCrc, verified: true);   // Raw 档 CRC 字段 = 聚合载荷 CRC（发送端端到端对账）
            network.Flush();
            return new NetworkTransferResult(0, frames, received, true);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>连续卷介质守卫——Raw 档双端共用（能力位 + 接口双验，任一缺席即 Unsupported）。</summary>
    private static IContiguousVolume RequireContiguousVolume(IFileSystem fs, string operation)
        => fs is IContiguousVolume v && fs.Capabilities.HasFlag(FileSystemCapabilities.ContiguousCapture)
            ? v
            : throw new FileIOException(IOError.Unsupported,
                $"Raw 档要求连续卷介质（能力位 ContiguousCapture + IContiguousVolume）——当前 {fs.GetType().Name} 不满足",
                null, operation);

    // ═══════════════ 帧编解码 ═══════════════

    /// <summary>写入 TIN1 握手帧（magic | 版本 u16 | mode u8 = 7 字节）。</summary>
    /// <param name="s">网络流。</param>
    /// <param name="mode">传送模式（v1 仅 Structural）。</param>
    private static void WriteHandshake(Stream s, TransferMode mode)
    {
        Span<byte> frame = stackalloc byte[7];
        HandshakeMagic.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame[4..], ProtocolVersion);
        frame[6] = (byte)mode;
        s.Write(frame);
    }

    /// <summary>读取并校验 TIN1 握手帧。</summary>
    /// <param name="s">网络流。</param>
    /// <returns>对端声明的传送模式。</returns>
    /// <exception cref="IOException">magic 不符、协议版本不支持、或未知传送模式（保留值拒读）。</exception>
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

    /// <summary>写入 TIN2 回执帧（magic | EntryCount u64 | RawBytes u64 | 聚合CRC u32 | 状态 u8 = 25 字节）。</summary>
    /// <param name="s">网络流。</param>
    /// <param name="r">接收端校验摘要（FrameCount 不传输——对账用 RawBytes）。</param>
    private static void WriteReceipt(Stream s, NetworkTransferResult r)
        => WriteReceiptFrame(s, r.EntryCount, r.RawBytes,
            UnifiedCrc.ComputeCrc32([.. BitConverter.GetBytes(r.FrameCount), .. BitConverter.GetBytes(r.RawBytes)]),
            r.Verified);

    /// <summary>回执帧写体（CRC 字段语义按档分轨：Structural = 摘要字段 CRC；Raw = 聚合载荷 CRC）。</summary>
    private static void WriteReceiptFrame(Stream s, long entryCount, long rawBytes, uint crcField, bool verified)
    {
        Span<byte> frame = stackalloc byte[25];
        ReceiptMagic.CopyTo(frame);
        BinaryPrimitives.WriteInt64LittleEndian(frame[4..], entryCount);
        BinaryPrimitives.WriteInt64LittleEndian(frame[12..], rawBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], crcField);
        frame[24] = verified ? (byte)1 : (byte)0;
        s.Write(frame);
    }

    /// <summary>读取并校验 TIN2 回执帧。</summary>
    /// <param name="s">网络流。</param>
    /// <param name="crcField">回执 CRC 字段原文（Structural 档 = 摘要字段 CRC；Raw 档 = 对端聚合载荷 CRC）。</param>
    /// <returns>对端回执摘要（FrameCount 固定 0——回执不携带，对账用 RawBytes）。</returns>
    /// <exception cref="IOException">回执 magic 不符（流不完整）。</exception>
    private static NetworkTransferResult ReadReceipt(Stream s, out uint crcField)
    {
        Span<byte> frame = stackalloc byte[25];
        ReadExactly(s, frame);
        if (!frame[..4].SequenceEqual(ReceiptMagic))
            throw new IOException("回执 magic 不符（流不完整）。");
        crcField = BinaryPrimitives.ReadUInt32LittleEndian(frame[20..]);
        return new NetworkTransferResult(
            BinaryPrimitives.ReadInt64LittleEndian(frame[4..]),    // EntryCount
            0,                                                         // FrameCount（回执不携带——对账用 RawBytes）
            BinaryPrimitives.ReadInt64LittleEndian(frame[12..]),    // RawBytes
            frame[24] != 0);                                         // Verified
    }

    /// <summary>从流精确读取指定字节数（不足抛异常——帧完整性守卫）。</summary>
    /// <param name="s">网络流。</param>
    /// <param name="buffer">填充目标缓冲区（读满为止）。</param>
    /// <exception cref="IOException">连接中断（流提前结束，帧不完整）。</exception>
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
