namespace TC.Tier.Runtime.Structures.Snapshot;

/// <summary>meta 持久化 partial（对齐 MetadataBase/MirrorBase.Meta）。</summary>
public abstract partial class SnapshotBase
{
    /// <summary>meta 策略（Managed/Transport/Disabled）——构造期装配（构造=配置），永非 null。</summary>
    private protected IMetaPolicy<SnapshotMetaHeader, SnapshotMetaPayload> MetaPolicy { get; }

    /// <summary>
    /// 默认 meta 策略装配（构造期调用，非虚——签名即 MetaPolicyFactory"按模式构造"委托：
    /// 注入工厂与默认实现统一为同一条 kind → policy 映射）。
    /// </summary>
    private IMetaPolicy<SnapshotMetaHeader, SnapshotMetaPayload> CreateMetaPolicyDefault(MetaPolicyKind kind)
    {
        var layout = new MetaLayout(_settings.MetaOpaqueBytes);
        return kind switch
        {
            MetaPolicyKind.Managed => _metaEngine is not null
                ? new ManagedMetaPolicy<SnapshotMetaHeader, SnapshotMetaPayload>(
                    layout, _metaEngine, Logger)
                : throw new InvalidOperationException("Meta engine is not initialized."),
            // ★ Transport：上层注入传输实例；未注入回落到 MetaHost——meta block 作为 IS_META 完整帧嵌入快照流
            MetaPolicyKind.Transport => new TransportMetaPolicy<SnapshotMetaHeader, SnapshotMetaPayload>(
                layout, _metaTransport ?? new MetaHost(this), Logger),
            _ => new DisabledMetaPolicy<SnapshotMetaHeader, SnapshotMetaPayload>(),
        };
    }


    /// <summary>★ 登记外部 opaque meta——stage 进策略缓冲，随水位线落盘原子携带（设计决策：
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
    private SnapshotMetaPayload BuildMetaPayload() => new()
    {
        WriteAddress = _writeAddress,
        PhysicalWriteAddress = _physicalWriteAddress,
        TruncatedAddress = _truncatedAddress,
        CommittedWriteAddress = _committedWriteAddress,
        LastCommittedSeq = _lastCommittedSeq,
        LastPreparedSeq = _lastPreparedSeq,
    };

    /// <summary>写 meta（水位快照 + 落盘）。</summary>
    private protected void WriteMeta()
    {
        // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量
        MetaPolicy.WriteHeader(SnapshotMetaHeaderCodec.Create());
        MetaPolicy.WritePayload(BuildMetaPayload());
        MetaPolicy.Commit();
    }

    /// <summary>MetaLayout 嵌套类（实现 IMetaLayout）。</summary>
    /// <param name="payloadOpaqueSize">不透明的 payload 大小。</param>
    private protected sealed class MetaLayout(int payloadOpaqueSize)
        : IMetaLayout<SnapshotMetaHeader, SnapshotMetaPayload>
    {
        public int HeaderSize => SnapshotMetaHeaderCodec.StructSize;
        public int PayloadSize => SnapshotMetaPayloadCodec.StructSize + PayloadOpaqueSize;
        public int PayloadOpaqueSize { get; } = payloadOpaqueSize;
        public uint Magic => SnapshotMetaHeader.Magic;
        public ushort CurrentVersion => SnapshotMetaHeader.CurrentVersion;
        public ushort DefaultFlags => SnapshotMetaHeader.DefaultFlags;

        public void WriteHeader(Span<byte> dst, in SnapshotMetaHeader header, bool validate)
            => SnapshotMetaHeaderCodec.Write(dst, in header, validate);

        public SnapshotMetaHeader ReadHeader(ReadOnlySpan<byte> src) => SnapshotMetaHeaderCodec.Read(src);

        public void WritePayload(Span<byte> dst, in SnapshotMetaPayload payload) =>
            SnapshotMetaPayloadCodec.Write(dst, in payload);

        public SnapshotMetaPayload ReadPayload(ReadOnlySpan<byte> src) => SnapshotMetaPayloadCodec.Read(src);
        public uint GetMagicValue(in SnapshotMetaHeader h) => h.MagicValue;
        public ushort GetVersion(in SnapshotMetaHeader h) => h.Version;
        public ushort GetPayloadLength(in SnapshotMetaHeader h) => h.PayloadLength;

        public SnapshotMetaHeader WithPayloadLength(in SnapshotMetaHeader h, ushort len)
        {
            var x = h;
            x.PayloadLength = len;
            return x;
        }

        public SnapshotMetaHeader CreateDefaultHeader() => new()
        {
            MagicValue = SnapshotMetaHeader.Magic,
            Version = SnapshotMetaHeader.CurrentVersion,
            Flags = SnapshotMetaHeader.DefaultFlags,
        };
    }

    /// <summary>
    /// MetaHost 嵌套类（实现 IMetaTransport）——meta block 作为带 IS_META flag 的完整帧追加进快照流。
    /// 读回 = Backward 扫描找最后一个帧尾，校验 header IS_META 后取 payload。
    /// </summary>
    /// <param name="owner">SnapshotBase 实例的引用。</param>
    private protected sealed class MetaHost(SnapshotBase owner) : IMetaTransport
    {
        private byte[]? _lastBlock;

        public ReadOnlySpan<byte> ReadLastBlock()
        {
            _lastBlock = ScanLastMetaFrame();
            return _lastBlock;
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
        {
            _lastBlock = await ValueTask.FromResult(ScanLastMetaFrame()).ConfigureAwait(false);
            return _lastBlock is null ? default : _lastBlock;
        }

        /// <summary>把 meta block 作为 payload 追加为带 IS_META flag 的完整帧。
        /// 布局 [Header][block][Footer][padding]（padding 在 Footer 后——帧头位置 = Footer 前移 HeaderSize+TotalLength，确定可算）。</summary>
        public void WriteBlock(ReadOnlySpan<byte> block)
        {
            var sectorSize = owner._sectorSize;
            int payloadLen = block.Length;
            int frameLen = owner._codec.HeaderSize + payloadLen + owner._codec.FooterSize;
            int aligned = frameLen.AlignUp(sectorSize);
            int paddingLen = aligned - frameLen;

            using var buf = new AlignedMemoryManager(aligned, sectorSize);
            var span = buf.GetSpan();
            span.Clear();
            owner._codec.WriteHeader(span, new SnapshotRecordFields(
                Flags: owner._codec.DefaultMetaFlags,
                PayloadLength: (uint)payloadLen,
                PaddingLength: (ushort)paddingLen,
                TotalLength: (ulong)payloadLen,
                EntryCount: 0));
            block.CopyTo(span.Slice(owner._codec.HeaderSize, payloadLen));
            owner._codec.WriteFooter(span.Slice(owner._codec.HeaderSize + payloadLen),
                new SnapshotRecordFields(Flags: 0, PayloadLength: 0, PaddingLength: 0,
                    TotalLength: (ulong)payloadLen, EntryCount: 0));
            var addr = owner._engine.Allocate(aligned).Start;
            owner._engine.Write(addr, span);
            owner._engine.Flush();
        }

        /// <summary>异步写 meta block（引擎写/flush 原生同步，实质等价）。</summary>
        public async ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
        {
            WriteBlock(block.Span);
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>Backward 扫描找最后一个帧尾，校验 IS_META 后读 payload（= meta block）。
        /// 帧 = [Header][payload][Footer][padding]——帧头 = Footer 前移 HeaderSize+TotalLength（确定可算）。</summary>
        private byte[]? ScanLastMetaFrame()
        {
            var tail = owner.LocateLastFrameEnd();
            if (tail is not { } frameEnd) return null;
            var footerAddr = owner.RetreatFrom(frameEnd, owner._codec.FooterSize); // Footer magic 位置
            using var footerBuf = new AlignedMemoryManager(owner._codec.FooterSize, owner._sectorSize);
            int got = owner._engine.Read(footerAddr, footerBuf.GetSpan());
            if (got < owner._codec.FooterSize) return null;
            var footer = owner._codec.ReadFooter(footerBuf.GetSpan());
            long payloadLen = (long)footer.TotalLength;

            var hdrAddr = owner.RetreatFrom(footerAddr, owner._codec.HeaderSize + payloadLen);
            using var hdrBuf = new AlignedMemoryManager(owner._codec.HeaderSize, owner._sectorSize);
            got = owner._engine.Read(hdrAddr, hdrBuf.GetSpan());
            if (got < owner._codec.HeaderSize) return null;
            if (!owner._codec.TryReadHeader(hdrBuf.GetSpan(), out var fields)) return null;
            if ((fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) == 0) return null;

            using var payBuf = new AlignedMemoryManager((int)payloadLen, owner._sectorSize);
            var payAddr = owner._engine.CalculationAddress(hdrAddr, owner._codec.HeaderSize);
            owner._engine.Read(payAddr, payBuf.GetSpan());
            return payBuf.GetSpan().ToArray();
        }
    }
}
