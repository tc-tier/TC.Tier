namespace TC.Tier.Runtime.Structures.Mirror;

/// <summary>meta 持久化 partial（对齐 MetadataBase.Meta / LogBase.LogMeta）。</summary>
public abstract partial class MirrorBase
{
    /// <summary>meta 策略（Managed/Transport/Disabled）——构造期装配（构造=配置），永非 null。
    /// <para>装配与 Managed meta 引擎构建在主文件构造（对齐用户范式）；本 partial 只含水位读写与 MetaHost。</para></summary>
    private protected IMetaPolicy<MirrorMetaHeader, MirrorMetaPayload> MetaPolicy { get; }


    /// <summary>★ 登记外部 opaque meta——stage 进策略缓冲，随水位线落盘原子携带（用户裁定：
    /// opaque 搭水位线的车，同一块同一 CRC；无独立提交路径，需确定性持久化点走 Prepare/ConfirmCommitted）。
    /// ⚠️ 写侧拦截：Disabled 抛 InvalidOperationException（禁用即报错）；超 MetaOpaqueBytes 策略抛 ArgumentException。</summary>
    public void SetOpaqueMeta(ReadOnlySpan<byte> data)
    {
        if (_settings.MetaPolicyKind == MetaPolicyKind.Disabled)
            throw new InvalidOperationException(
                "MetaPolicyKind=Disabled——未开启 meta 持久化，opaque 登记被拒（ReadOpaqueMeta 将恒为空）。"
                + "请配置 MetaPolicyKind=Managed/Transport，或移除 opaque 写入。");
        MetaPolicy.WritePayload(data);
    }

    /// <summary>读外部 opaque meta（最近已提交块；Empty = 无数据/未开启——空即答案）。</summary>
    public ReadOnlySpan<byte> ReadOpaqueMeta()
        => MetaPolicy.ReadPayload();

    /// <summary>构建 meta payload（当前水位快照）。</summary>
    private MirrorMetaPayload BuildMetaPayload() => new()
    {
        HighestVersionAddress = _highestVersionAddress,
        LowestVersionAddress = _lowestVersionAddress,
        LastCommittedSeq = _lastCommittedSeq,
        LastPreparedSeq = _lastPreparedSeq,
    };

    /// <summary>写 meta（flush 水位 + 落盘）。</summary>
    private protected void WriteMeta()
    {
        // ★ Create()：ValidEquals 规范字段（Version/Flags）自动填常量
        MetaPolicy.WriteHeader(MirrorMetaHeaderCodec.Create());
        MetaPolicy.WritePayload(BuildMetaPayload());
        MetaPolicy.Commit();
    }

    /// <summary>
    /// MetaLayout 嵌套类（实现 IMetaLayout，供 Managed/Transport meta policy 使用）。
    /// </summary>
    /// <param name="payloadOpaqueSize">不透明的 payload 大小。</param>
    private protected sealed class MetaLayout(int payloadOpaqueSize)
        : IMetaLayout<MirrorMetaHeader, MirrorMetaPayload>
    {
        public int HeaderSize => MirrorMetaHeaderCodec.StructSize;
        public int PayloadSize => MirrorMetaPayloadCodec.StructSize + PayloadOpaqueSize;
        public int PayloadOpaqueSize { get; } = payloadOpaqueSize;
        public uint Magic => MirrorMetaHeader.Magic;
        public ushort CurrentVersion => MirrorMetaHeader.CurrentVersion;
        public ushort DefaultFlags => MirrorMetaHeader.DefaultFlags;

        public void WriteHeader(Span<byte> dst, in MirrorMetaHeader header, bool validate)
            => MirrorMetaHeaderCodec.Write(dst, in header, validate);

        public MirrorMetaHeader ReadHeader(ReadOnlySpan<byte> src) => MirrorMetaHeaderCodec.Read(src);

        public void WritePayload(Span<byte> dst, in MirrorMetaPayload payload) =>
            MirrorMetaPayloadCodec.Write(dst, in payload);

        public MirrorMetaPayload ReadPayload(ReadOnlySpan<byte> src) => MirrorMetaPayloadCodec.Read(src);
        public uint GetMagicValue(in MirrorMetaHeader h) => h.MagicValue;
        public ushort GetVersion(in MirrorMetaHeader h) => h.Version;
        public ushort GetPayloadLength(in MirrorMetaHeader h) => h.PayloadLength;

        public MirrorMetaHeader WithPayloadLength(in MirrorMetaHeader h, ushort len)
        {
            var x = h;
            x.PayloadLength = len;
            return x;
        }

        public MirrorMetaHeader CreateDefaultHeader() => MirrorMetaHeaderCodec.Create();
    }

    // ════════════════════════════════════════════════════════════
    // === Transport 嵌入 meta（统一帧 + IS_META——机制归基类，零子类特化）===
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 写 Transport 嵌入 meta 帧（统一帧三拍 + FLAG_ENTRY_IS_META——与数据帧同构，
    /// 宿主流的一等公民）。meta 帧不参与数据版本链（PreviousVersion=Invalid、版本号用当前值
    /// 不推进、链尾水位不推——EndFrame 对 IS_META 跳过 OnRecordAppended），只追加在流尾供找回。
    /// </summary>
    private void WriteEmbeddedMetaBlockCore(ReadOnlySpan<byte> block)
    {
        // ★ Create()：ValidEquals 规范字段（Version）自动填常量——只填变化字段
        var header = MirrorFrameHeaderCodec.Create();
        header.Flags = _codec.DefaultMetaFlags;
        header.PageId = 0;
        header.LogicalAddress = 0;
        header.MirrorVersion = _currentVersion;
        BeginFrame(in header);
        AppendFrameChunk(block);
        var footer = MirrorFrameFooterCodec.Create();
        footer.Flags = _codec.DefaultMetaFlags;
        footer.PreviousVersion = LogicalAddress.Invalid;
        footer.MirrorVersion = _currentVersion;
        EndFrame(in footer);
        _engine.Flush();
    }

    /// <summary>
    /// 扫最后一条嵌入 meta 帧的 payload（前向帧走链单步原语复用——收集 IS_META 帧，
    /// 结构完好即收：payload 交给 meta policy 自校验）。
    /// </summary>
    private byte[]? ScanLastEmbeddedMetaBlockCore()
    {
        var tail = _engine.CommittedTail;
        Span<byte> hdrScratch = stackalloc byte[_codec.HeaderSize];
        Span<byte> ftrScratch = stackalloc byte[_codec.FooterSize];
        byte[]? last = null;
        var cursor = _engine.MinAddress;

        while (cursor.CompareTo(tail) < 0)
        {
            if (!TryWalkNextFrame(ref cursor, tail, hdrScratch, ftrScratch, out var info)) continue;
            if ((info.Footer.Flags & RecordFlags.FLAG_ENTRY_IS_META) == 0) continue;

            long payloadLen = _engine.GetDistance(
                _engine.CalculationAddress(info.Head, _codec.HeaderSize), info.FooterAddress);
            var payload = new byte[payloadLen];
            _engine.Read(_engine.CalculationAddress(info.Head, _codec.HeaderSize), payload);
            last = payload;
        }
        return last;
    }

    /// <summary>
    /// MetaHost 嵌套类（实现 IMetaTransport，供 TransportMetaPolicy 写入 meta block）。
    /// </summary>
    /// <param name="owner">MirrorBase 实例的引用。</param>
    private protected sealed class MetaHost(MirrorBase owner) : IMetaTransport
    {

        /// <summary>最近一次扫描结果（字段持有——ReadLastBlock 返回的视图有效至本传输下一次调用）。</summary>
        private byte[]? _lastBlock;

        public ReadOnlySpan<byte> ReadLastBlock()
        {
            _lastBlock = owner.ScanLastEmbeddedMetaBlockCore();
            return _lastBlock is null ? ReadOnlySpan<byte>.Empty : _lastBlock;
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
        {
            _lastBlock = owner.ScanLastEmbeddedMetaBlockCore();
            return _lastBlock is null ? default : _lastBlock;
        }

        /// <summary>把 meta block 作为 IS_META 帧追加进宿主流（统一帧机制）。</summary>
        public void WriteBlock(ReadOnlySpan<byte> block)
            => owner.WriteEmbeddedMetaBlockCore(block);

        /// <summary>异步写 meta block（引擎写/flush 原生同步，实质等价）。</summary>
        public async ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
        {
            WriteBlock(block.Span);
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }
    }
}
