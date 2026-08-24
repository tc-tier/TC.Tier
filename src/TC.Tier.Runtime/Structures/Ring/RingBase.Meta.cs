using System.Runtime.CompilerServices;

namespace TC.Tier.Runtime.Structures.Ring;

/// <summary>
/// RingBase meta partial——WriteMeta/WriteMetaAsync + BuildMetaPayload + 嵌套 MetaHost 传输适配器。
/// <para>★ WriteMeta 更新 meta 缓冲（水位 + payload）并 Commit 落盘（对齐 Log 的 AppendMeta 落盘语义）。</para>
/// <para>★ 本 partial 含 await，故不可标 unsafe（CS4004）。RingBase 主 partial 已 unsafe。</para>
/// <para>参见 base.md §2.7。</para>
/// </summary>
public abstract partial class RingBase<TKey>
{
    /// <summary>
    /// 默认 meta 策略装配（构造期经 ??= 收口——签名即 MetaPolicyFactory"按模式构造"委托：
    /// 注入工厂与默认实现是同一条 kind → policy 映射，无匿名 lambda）。
    /// </summary>
    private IMetaPolicy<RingMetaHeader, RingMetaPayload> CreateMetaPolicyDefault(MetaPolicyKind kind)
    {
        var layout = new MetaLayout(_settings.MetaOpaqueBytes);
        return kind switch
        {
            MetaPolicyKind.Managed => _metaEngine is not null
                ? new ManagedMetaPolicy<RingMetaHeader, RingMetaPayload>(layout, _metaEngine, Logger)
                : throw new InvalidOperationException("Meta engine is not initialized."),
            // ★ Transport：上层注入传输实例；未注入回落到 MetaHost——meta block 嵌入 ring 流
            MetaPolicyKind.Transport => new TransportMetaPolicy<RingMetaHeader, RingMetaPayload>(
                layout, _metaTransport ?? new MetaHost(this), Logger),
            _ => new DisabledMetaPolicy<RingMetaHeader, RingMetaPayload>(),
        };
    }

    /// <summary>
    /// ★ 同步更新 meta 缓存（水位 + payload）并 Commit 落盘。
    /// </summary>
    /// <param name="payload">opaque payload（可选，≤策略 PayloadCapacity）。默认空。</param>
    /// <param name="flushedUntilOverride">覆盖 FlushedUntilAddress 水位（&lt;0 表示不覆盖，用当前值）。
    ///   ★ Transport 策略回落 MetaHost（宿主流嵌入）时的 1-fsync Prepare 优化用：先记 dataTail 再随数据同页 flush，
    ///   避免 Managed 那种"先 flush 再记水位"的 2 次 fsync。</param>
    private void WriteMeta(LogicalAddress flushedUntilOverride = default)
    {
        var p = MetaPolicy; // 构造期装配，永非 null
        p.WriteHeader(BuildMetaHeader());
        var mp = BuildMetaPayload(); // 先取局部，再 in 传递（方法返回值不能直接作 in 实参，CS8156）
        if (flushedUntilOverride != default) mp.FlushedUntilAddress = flushedUntilOverride;
        p.WritePayload(in mp);
        p.Commit();
    }

    // ═══ 外部 opaque meta（index 锚点搭车通道——设计稿 §4：锚点与 Ring 水位同 meta 块原子提交）═══

    /// <summary>
    /// 写外部 opaque meta（stage 进策略缓冲——随下一次水位提交原子落盘；结构化水位写保留 opaque 记账）。
    /// <para>★ 唯一消费者形态：TierKV 组合层登记 index 镜像锚点 W（先 Set 锚点、后触发水位提交——
    ///   崩溃窗口内锚点旧于数据，重放一段兜底，绝不锚点新于提交尾）。</para>
    /// </summary>
    public void SetOpaqueMeta(ReadOnlySpan<byte> data)
    {
        if (_settings.MetaPolicyKind == MetaPolicyKind.Disabled)
            throw new InvalidOperationException(
                "MetaPolicyKind=Disabled——未开启 meta 持久化，opaque 登记被拒（ReadOpaqueMeta 将恒为空）。"
                + "请配置 MetaPolicyKind=Managed/Transport，或移除 opaque 写入。");
        MetaPolicy.WritePayload(data);
    }

    /// <summary>读外部 opaque meta（最近已提交块；Empty = 无数据/未开启——空即答案）。</summary>
    public ReadOnlySpan<byte> ReadOpaqueMeta() => MetaPolicy.ReadPayload();

    /// <summary>
    /// ★ 异步更新 meta 缓冲（水位 + payload）并 CommitAsync 落盘。
    /// </summary>
    /// <param name="payload">opaque payload（可选，≤策略 PayloadCapacity）。默认空。</param>
    /// <param name="ct">取消令牌。</param>
    /// <param name="flushedUntilOverride">覆盖 FlushedUntilAddress 水位（&lt;0 表示不覆盖）。见 <see cref="WriteMeta"/>。</param>
    private async ValueTask WriteMetaAsync(LogicalAddress flushedUntilOverride = default, CancellationToken ct = default)
    {
        MetaPolicy.WriteHeader(BuildMetaHeader());
        var mp = BuildMetaPayload();
        if (flushedUntilOverride != default) mp.FlushedUntilAddress = flushedUntilOverride;
        MetaPolicy.WritePayload(in mp);
        await MetaPolicy.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>构造 meta header（规范字段填默认，水位进 Payload 不在 Header）。</summary>
    private static RingMetaHeader BuildMetaHeader()
        => new()
        {
            MagicValue = RingMetaHeader.Magic,
            Version = RingMetaHeader.CurrentVersion,
            Flags = RingMetaHeader.DefaultFlags,
        };

    /// <summary>★ 构造 meta payload（持久化层 6 指针 + LastCommittedSeq + OverflowTail + 回退点）。</summary>
    private RingMetaPayload BuildMetaPayload() => new()
    {
        BeginAddress = BeginAddress,
        FlushedUntilAddress = FlushedUntilAddress,
        SafeReadOnlyAddress = SafeReadOnlyAddress,
        ReadOnlyAddress = ReadOnlyAddress,
        TailAddress = TailAddress,
        LastCommittedSeq = LastCommittedSeq,
        LastPreparedSeq = LastPreparedSeq,
        OverflowTailAddress = _overflowTailAddress,
        KeySize = Unsafe.SizeOf<TKey>(),
        CommittedTailAddress = _txRollbackTail,   // ★ D2 Abort 回退点（Empty = 无待回滚窗口）
    };


    protected sealed class MetaLayout: IMetaLayout<RingMetaHeader,RingMetaPayload>
    {
        public MetaLayout(int payloadOpaqueSize)
        {
            PayloadOpaqueSize = payloadOpaqueSize;
        }
        public int HeaderSize => RingMetaHeaderCodec.StructSize;
        public int PayloadSize => RingMetaPayloadCodec.StructSize + PayloadOpaqueSize;
        public int PayloadOpaqueSize { get; }
        public uint Magic => RingMetaHeader.Magic;
        public ushort CurrentVersion => RingMetaHeader.CurrentVersion;
        public ushort DefaultFlags => RingMetaHeader.DefaultFlags;

        public void WriteHeader(Span<byte> dst, in RingMetaHeader header, bool validate)
            => RingMetaHeaderCodec.Write(dst, in header, validate);

        public RingMetaHeader ReadHeader(ReadOnlySpan<byte> src)
            => RingMetaHeaderCodec.Read(src);

        public void WritePayload(Span<byte> dst, in RingMetaPayload payload)
            => RingMetaPayloadCodec.Write(dst, in payload);

        public RingMetaPayload ReadPayload(ReadOnlySpan<byte> src)
        {
            // ★ 旧块容错：盘上 payload 短于当前布局（字段追加前写入的块）→ 零扩展后解读，
            //   超出旧布局的新字段（如 CommittedTailAddress）读默认值 Empty——优雅降级不抛。
            if (src.Length >= RingMetaPayloadCodec.StructSize) return RingMetaPayloadCodec.Read(src);
            Span<byte> buf = stackalloc byte[RingMetaPayloadCodec.StructSize];
            src.CopyTo(buf);
            return RingMetaPayloadCodec.Read(buf);
        }

        // === Header 规范字段访问（泛型策略通过 layout 读写 header 字段）===
        public uint GetMagicValue(in RingMetaHeader header) => header.MagicValue;
        public ushort GetVersion(in RingMetaHeader header) => header.Version;
        public ushort GetPayloadLength(in RingMetaHeader header) => header.PayloadLength;
        public RingMetaHeader WithPayloadLength(in RingMetaHeader header, ushort payloadLength)
        {
            var h = header;
            h.PayloadLength = payloadLength;
            return h;
        }
        public RingMetaHeader CreateDefaultHeader() => new()
        {
            MagicValue = RingMetaHeader.Magic,
            Version = RingMetaHeader.CurrentVersion,
            Flags = RingMetaHeader.DefaultFlags,
        };
    }
    protected sealed class MetaHost(RingBase<TKey> owner) : IMetaTransport
    {

        /// <summary>最近一次扫描结果（字段持有——ReadLastBlock 返回的视图有效至本传输下一次调用）。</summary>
        private byte[]? _lastBlock;

        public ReadOnlySpan<byte> ReadLastBlock()
        {
            _lastBlock = ScanLastBlockCore();
            return _lastBlock;
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
        {
            _lastBlock = await ScanLastBlockCoreAsync(ct).ConfigureAwait(false);
            return _lastBlock is null ? default : _lastBlock;
        }

        public void WriteBlock(ReadOnlySpan<byte> block)
            => owner.WriteMetaRecord(block);

        public async ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
            => await owner.WriteMetaRecordAsync(block, ct).ConfigureAwait(false);

        /// <summary>★ 倒序扫 ring 流找最后一条 IS_META record 的 payload（= 内层 meta block）。</summary>
        private byte[]? ScanLastBlockCore()
        {
            int pageSize = owner.PageSize;
            int pageMask = owner.PageSizeMask;
            int headerSize = owner.RingCodec.HeaderSize;
            int alignment = owner.RingCodec.Alignment;
            var codec = owner.RingCodec;
            var frame = new AlignedMemoryManager(pageSize, (int)owner.SectorSize);
            try
            {
                var engine = owner._engine;
                long totalDist = engine.GetDistance(engine.MinAddress, engine.AllocatedTail);
                long pageCount = (totalDist + pageSize - 1) / pageSize;
                for (long p = pageCount - 1; p >= 0; p--)
                {
                    LogicalAddress pageAddr = engine.CalculationAddress(engine.MinAddress, p * pageSize);
                    int got = engine.Read(pageAddr, frame.GetSpan(0, pageSize));
                    if (got <= 0) continue;
                    var block = ScanPageForLastMeta(frame.GetSpan(0, got), pageAddr, pageSize, pageMask, headerSize, alignment, codec);
                    if (block != null) return block;
                }
            }
            finally { frame.Dispose(); }
            return null;
        }

        /// <summary>★ 异步倒序扫（对等同步版）。</summary>
        private async ValueTask<byte[]?> ScanLastBlockCoreAsync(CancellationToken ct)
        {
            int pageSize = owner.PageSize;
            int pageMask = owner.PageSizeMask;
            int headerSize = owner.RingCodec.HeaderSize;
            int alignment = owner.RingCodec.Alignment;
            var codec = owner.RingCodec;
            var frame = new AlignedMemoryManager(pageSize, (int)owner.SectorSize);
            try
            {
                var engine = owner._engine;
                long totalDist = engine.GetDistance(engine.MinAddress, engine.AllocatedTail);
                long pageCount = (totalDist + pageSize - 1) / pageSize;
                for (long p = pageCount - 1; p >= 0; p--)
                {
                    ct.ThrowIfCancellationRequested();
                    LogicalAddress pageAddr = engine.CalculationAddress(engine.MinAddress, p * pageSize);
                    int got = await engine.ReadAsync(pageAddr, frame.Memory, ct).ConfigureAwait(false);
                    if (got <= 0) continue;
                    var block = ScanPageForLastMeta(frame.GetSpan(0, got), pageAddr, pageSize, pageMask, headerSize, alignment, codec);
                    if (block != null) return block;
                }
            }
            finally { frame.Dispose(); }
            return null;
        }

        /// <summary>★ 页内正向扫描，只识别 IS_META 候选并做外层 CRC，返回最后一条有效 meta 的 payload。</summary>
        private static byte[]? ScanPageForLastMeta(Span<byte> pageData, LogicalAddress pageAddr, int pageSize,
            int pageMask, int headerSize, int alignment, IRingCodec codec)
        {
            byte[]? found = null;
            long pageOff = pageAddr.Offset;
            long pageEndOff = pageOff + pageSize;
            for (long addrOff = pageOff; addrOff + headerSize <= pageEndOff;)
            {
                int off = (int)(addrOff - pageOff);
                if (!codec.TryReadHeader(pageData.Slice(off, headerSize), out var fields))
                {
                    if (codec.IsEmptyRecord(pageData.Slice(off, headerSize))) { addrOff += alignment; continue; }
                    break;
                }
                if (off + headerSize + (int)fields.PayloadLength > pageSize) break;
                if ((fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0)
                {
                    if (codec.VerifyCrc(pageData.Slice(off, headerSize + (int)fields.PayloadLength), headerSize, (int)fields.PayloadLength))
                        found = pageData.Slice(off + headerSize, (int)fields.PayloadLength).ToArray();
                }
                int total = headerSize + (int)fields.PayloadLength + fields.PaddingLength;
                addrOff += (total + alignment - 1) & ~(alignment - 1);
            }
            return found;
        }
    }
}