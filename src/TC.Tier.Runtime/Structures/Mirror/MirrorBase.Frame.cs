using System.Buffers.Binary;
using System.IO.Hashing;
using TC.Tier.Runtime.Structures.Mirror.Contracts;

namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>
/// 帧机制 partial——统一帧的三拍写入原语 / 帧几何账面 / 读与验证（机制归基类，子类只填 codec）。
/// <para>★ 写模型（引擎模式 A：Allocate 随写随留 + Write 复写）：每段圈完即写满 →
///   AllocatedTail 恒贴已写尾，帧间零间隙、帧长任意字节边界（下一帧起点 = 本帧尾末）。</para>
/// <para>★ CRC 覆盖：头（全部）+ payload + 尾前缀 [0,<see cref="MirrorFrameFooter.CrcPrefixSize"/>)——
///   写侧三拍边写边累积收官落尾，全程不需要知道总长；算法按 flags 算法位分派（CRC64 增量/CRC32C 硬件）。</para>
/// </summary>
public abstract partial class MirrorBase
{
    private const int FrameIoBufSize = 1 << 16;      // Verify 分段读缓冲
    private const int FrameScanPageSize = 1 << 16;   // 帧现场定位（Locate）扫描页

    // === 帧写入状态（单线程契约：BeginFrame → AppendFrameChunk × N → EndFrame）===
    private LogicalAddress _frameHead = LogicalAddress.Empty;
    private LogicalAddress _frameWriteEnd;
    private IMirrorFrameCrc? _frameCrc;              // 非 null = 帧写入中（三拍互斥标志）

    /// <summary>pending 帧账目（EndFrame 后待 Confirm/Abort 裁决——子类会话钩子消费）。</summary>
    private protected LogicalAddress _pendingFrameHead = LogicalAddress.Empty;

    private protected LogicalAddress _pendingFrameFooter = LogicalAddress.Empty;
    private protected bool _hasPendingFrame;

    /// <summary>帧几何账面（head→footer——GetFrameInfo 快路径；写入/恢复时填充，回收时清理）。</summary>
    private readonly Dictionary<LogicalAddress, LogicalAddress> _frameFooters = new();

    /// <summary>验证/分段读缓冲（基类持随，Dispose 释放）。</summary>
    private readonly AlignedMemoryManager _frameIoBuf;

    // ════════════════════════════════════════════════════════════
    // === 帧写入三拍（子类门面直通：WholeMirror 会话 / PagedMirror 页帧 / MetaHost 嵌入）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 帧三拍之一：圈定头区 + 写帧头（CRC 从头起算）。不预留、不知尺寸。
    /// <para>★ 不触碰 pending 账目（最近未裁决<b>数据帧</b>的几何——Confirm/Abort 消费）：
    ///   meta 帧（非会话写入）与本拍正交，清 pending 会偷换 Confirm 的链头裁决依据
    ///   （实测实案：meta 帧地址被当链头 → N=2 keepAddr 错位 → 数据帧被整段回收）。</para>
    /// </summary>
    /// <returns>帧头地址。</returns>
    private protected LogicalAddress BeginFrame(in MirrorFrameHeader header)
    {
        if (_frameCrc is not null)
            throw new InvalidOperationException("上一帧未收官——先 EndFrame。");

        var head = _engine.Allocate(_codec.HeaderSize).Start;
        Span<byte> hdr = stackalloc byte[_codec.HeaderSize];
        _codec.WriteHeader(hdr, in header);
        _engine.Write(head, hdr);

        _frameCrc = CreateFrameCrc(header.Flags);
        _frameCrc.Append(hdr);
        _frameHead = head;
        _frameWriteEnd = _engine.CalculationAddress(head, _codec.HeaderSize);
        return head;
    }

    /// <summary>
    /// 帧三拍之二：顺序追加 payload chunk（圈地+复写+CRC 边写边累积——零缓冲不驻内存）。
    /// </summary>
    private protected void AppendFrameChunk(ReadOnlySpan<byte> chunk)
    {
        if (_frameCrc is null)
            throw new InvalidOperationException("帧未开始（先 BeginFrame）。");
        if (chunk.IsEmpty) return;

        _engine.Allocate(chunk.Length);
        _engine.Write(_frameWriteEnd, chunk);
        _frameCrc.Append(chunk);
        _frameWriteEnd = _engine.CalculationAddress(_frameWriteEnd, chunk.Length);
    }

    /// <summary>
    /// 帧三拍之三：圈定尾区 → CRC 收官落尾（覆盖头+体+尾前缀）。flush 由调用方门面决定
    /// （持久化时机是使用方语义——WholeMirror 会话收官 flush；PagedMirror 逐页不 flush）。
    /// </summary>
    /// <returns>帧尾末地址。</returns>
    private protected LogicalAddress EndFrame(in MirrorFrameFooter footer)
    {
        if (_frameCrc is null)
            throw new InvalidOperationException("帧未开始（先 BeginFrame）。");

        Span<byte> ftr = stackalloc byte[_codec.FooterSize];
        _codec.WriteFooter(ftr, in footer);
        _frameCrc.Append(ftr[..MirrorFrameFooter.CrcPrefixSize]);
        BinaryPrimitives.WriteUInt64LittleEndian(
            ftr.Slice(MirrorFrameFooterCodec.Offset_Crc), _frameCrc.Finalize());

        var footerAddr = _engine.Allocate(_codec.FooterSize).Start;   // == _frameWriteEnd（连续圈地）
        _engine.Write(footerAddr, ftr);
        var frameEnd = _engine.CalculationAddress(footerAddr, _codec.FooterSize);

        // 链尾水位/pending 账目只记数据帧（IS_META 嵌入帧不是版本链节点：不推 OnRecordAppended、
        // 不占 pending——pending=最近未裁决数据帧，Confirm/Abort 的链头裁决依据）
        if ((footer.Flags & RecordFlags.FLAG_ENTRY_IS_META) == 0)
        {
            OnRecordAppended(_frameHead, _engine.GetDistance(_frameHead, frameEnd));
            _pendingFrameHead = _frameHead;
            _pendingFrameFooter = footerAddr;
            _hasPendingFrame = true;
        }

        _frameFooters[_frameHead] = footerAddr;
        _frameCrc = null;
        return frameEnd;
    }

    /// <summary>Confirm/Abort 裁决后清理 pending 账目（子类会话钩子调用）。</summary>
    private protected void ClearPendingFrame()
    {
        _hasPendingFrame = false;
        _pendingFrameHead = LogicalAddress.Empty;
        _pendingFrameFooter = LogicalAddress.Empty;
    }

    // ════════════════════════════════════════════════════════════
    // === 帧几何（账面快路径 + 现场定位兜底）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 帧几何（payload 长 = 尾位−头−头结构——v2 零长度字段，长度是推导的事实）。
    /// 快路径走内存账面；miss 时现场定位（先验头，再从头之后找第一个尾 magic，尾归属=头尾版本一致）。
    /// </summary>
    /// <returns>有效帧返回 true 并给出几何；头损毁/无尾返回 false。</returns>
    private protected bool TryGetFrameInfo(LogicalAddress head, out MirrorFrameInfo info)
    {
        Span<byte> hdrScratch = stackalloc byte[_codec.HeaderSize];
        Span<byte> ftrScratch = stackalloc byte[_codec.FooterSize];

        if (_frameFooters.TryGetValue(head, out var footerAddr))
        {
            if (TryReadFrameHeaderAt(head, hdrScratch, out var h)
                && TryReadFrameFooterAt(footerAddr, ftrScratch, out var f)
                && f.MirrorVersion == h.MirrorVersion)
            {
                info = new MirrorFrameInfo(head, footerAddr, h, f);
                return true;
            }
        }

        // 现场定位：先验头（回收洞/垃圾头直接否），再从头之后找第一个尾 magic 命中
        // （真尾不缺席；尾归属校验=头尾版本一致，假尾由调用方 CRC 裁决）
        if (!TryReadFrameHeaderAt(head, hdrScratch, out var header)) { info = default; return false; }
        var from = _engine.CalculationAddress(head, _codec.HeaderSize);
        var loc = _engine.Locate([_codec.FooterMagic], MagicDirection.First,
            from, _engine.CommittedTail, FrameScanPageSize, magicAlignment: 1, MagicLocateStrategy.Linear);
        if (!loc.Found
            || !TryReadFrameFooterAt(loc.MagicAddress, ftrScratch, out var footer)
            || footer.MirrorVersion != header.MirrorVersion)
        {
            info = default;
            return false;
        }
        _frameFooters[head] = loc.MagicAddress;
        info = new MirrorFrameInfo(head, loc.MagicAddress, header, footer);
        return true;
    }

    /// <summary>帧 payload 长度（尾位−头−头结构）。无有效帧返回 0。</summary>
    private protected long GetFramePayloadLength(LogicalAddress head)
        => TryGetFrameInfo(head, out var info)
            ? _engine.GetDistance(info.Head, info.FooterAddress) - _codec.HeaderSize
            : 0;

    /// <summary>读帧 payload chunk（offset 相对 payload 起始）。</summary>
    private protected int ReadFramePayload(LogicalAddress head, long offsetInPayload, Span<byte> dst)
        => _engine.Read(_engine.CalculationAddress(head, _codec.HeaderSize + offsetInPayload), dst);

    // ════════════════════════════════════════════════════════════
    // === 帧验证（流式分段重读重算——大帧不驻内存）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 验证整个帧的 CRC（头 + 体 + 尾前缀 对 尾 Crc 字段）——流式分段重读重算。
    /// </summary>
    private protected bool VerifyFrame(LogicalAddress head, LogicalAddress footerAddr)
    {
        Span<byte> hdrScratch = stackalloc byte[_codec.HeaderSize];
        if (!TryReadFrameHeaderAt(head, hdrScratch, out var header)) return false;
        var crc = CreateFrameCrc(header.Flags);
        crc.Append(hdrScratch);

        long payloadLen = _engine.GetDistance(
            _engine.CalculationAddress(head, _codec.HeaderSize), footerAddr);
        var off = _engine.CalculationAddress(head, _codec.HeaderSize);
        Span<byte> buf = _frameIoBuf.GetSpan();
        while (payloadLen > 0)
        {
            int n = (int)Math.Min(payloadLen, buf.Length);
            int got = _engine.Read(off, buf[..n]);
            if (got <= 0) return false;
            crc.Append(buf[..got]);
            off = _engine.CalculationAddress(off, got);
            payloadLen -= got;
        }

        Span<byte> ftrScratch = stackalloc byte[_codec.FooterSize];
        if (!TryReadFrameFooterAt(footerAddr, ftrScratch, out var footer)) return false;
        crc.Append(ftrScratch[..MirrorFrameFooter.CrcPrefixSize]);
        return crc.Finalize() == footer.Crc;
    }

    /// <summary>读并结构校验帧头（读点 helper——恢复/定位共用）。</summary>
    private protected bool TryReadFrameHeaderAt(LogicalAddress addr, Span<byte> scratch, out MirrorFrameHeader header)
    {
        header = default;
        if (_engine.Read(addr, scratch) < _codec.HeaderSize) return false;
        return _codec.TryReadHeader(scratch, out header);
    }

    /// <summary>读并结构校验帧尾（读点 helper——恢复/定位共用）。</summary>
    private protected bool TryReadFrameFooterAt(LogicalAddress addr, Span<byte> scratch, out MirrorFrameFooter footer)
    {
        footer = default;
        if (_engine.Read(addr, scratch) < _codec.FooterSize) return false;
        return _codec.TryReadFooter(scratch, out footer);
    }

    /// <summary>N=2 头截断后清理被回收帧的账面条目（ReclaimOldVersions 调用）。</summary>
    private protected void PruneFrameBook(LogicalAddress keepAddr)
    {
        if (_frameFooters.Count == 0) return;
        var dead = new List<LogicalAddress>();
        foreach (var (head, _) in _frameFooters)
            if (head.CompareTo(keepAddr) < 0) dead.Add(head);
        foreach (var h in dead) _frameFooters.Remove(h);
    }

    // ════════════════════════════════════════════════════════════
    // === CRC 工厂（算法位归 flags——机制内分派，非子类类型判断）===
    // ════════════════════════════════════════════════════════════

    private static IMirrorFrameCrc CreateFrameCrc(ushort flags)
        => (flags & RecordFlags.FLAG_CRC_MASK) switch
        {
            RecordFlags.FLAG_CRC32C => new Crc32CFrameCrc(),
            _ => new Crc64FrameCrc(),   // FLAG_CRC64
        };

    /// <summary>帧 CRC 增量计算器（头+体+尾前缀分段累积 → Finalize 对尾 Crc 字段）。</summary>
    private interface IMirrorFrameCrc
    {
        void Append(ReadOnlySpan<byte> data);

        ulong Finalize();
    }

    private sealed class Crc64FrameCrc : IMirrorFrameCrc
    {
        private readonly Crc64 _crc = UnifiedCrc.CreateCrc64();

        public void Append(ReadOnlySpan<byte> data) => _crc.Append(data);

        public ulong Finalize() => UnifiedCrc.FinalizeCrc64(_crc);
    }

    private sealed class Crc32CFrameCrc : IMirrorFrameCrc
    {
        private uint _crc;

        public void Append(ReadOnlySpan<byte> data) => _crc = UnifiedCrc.ComputeCrc32C(_crc, data);

        public ulong Finalize() => _crc;
    }
}
