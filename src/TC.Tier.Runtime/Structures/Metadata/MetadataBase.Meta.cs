namespace TC.Tier.Runtime.Structures.Metadata;

/// <summary>meta 持久化 partial（对齐 LogBase.LogMeta / BlobBase.Meta）。</summary>
public abstract partial class MetadataBase
{
    /// <summary>meta 策略（Managed/Transport/Disabled）——构造期装配（构造=配置），永非 null。</summary>
    private protected IMetaPolicy<MetadataMetaHeader, MetadataMetaPayload> MetaPolicy { get; }

    /// <summary>
    /// 默认 meta 策略装配（构造期调用，非虚——签名即 MetaPolicyFactory"按模式构造"委托：
    /// 注入工厂与默认实现统一为同一条 kind → policy 映射）。
    /// </summary>
    private IMetaPolicy<MetadataMetaHeader, MetadataMetaPayload> CreateMetaPolicyDefault(MetaPolicyKind kind)
    {
        var layout = new MetaLayout(_settings.MetaOpaqueBytes);
        return kind switch
        {
            MetaPolicyKind.Managed => _metaEngine is not null
                ? new ManagedMetaPolicy<MetadataMetaHeader, MetadataMetaPayload>(
                    layout, _metaEngine, Logger)
                : throw new InvalidOperationException("Meta engine is not initialized."),
            // ★ Transport：上层注入传输实例（自定义介质）；未注入回落到 MetaHost——meta block 作为带 IS_META flag
            //   的版本 record 嵌入版本链流（追加流宿主，对齐 Log/Ring）。
            MetaPolicyKind.Transport => new TransportMetaPolicy<MetadataMetaHeader, MetadataMetaPayload>(
                layout, _metaTransport ?? new MetaHost(this), Logger),
            _ => new DisabledMetaPolicy<MetadataMetaHeader, MetadataMetaPayload>(),
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
    private MetadataMetaPayload BuildMetaPayload() => new()
    {
        HighestVersionAddress = _highestVersionAddress,
        LowestVersionAddress = _lowestVersionAddress,
        LastCommittedSeq = _lastCommittedSeq,
        LastPreparedSeq = _lastPreparedSeq,
    };

    /// <summary>写 meta（flush 水位 + 落盘）。</summary>
    private protected void WriteMeta()
    {
        // ★ Create()：ValidEquals 规范字段（Magic/Version/Flags）自动填常量
        MetaPolicy.WriteHeader(MetadataMetaHeaderCodec.Create());
        MetaPolicy.WritePayload(BuildMetaPayload());
        MetaPolicy.Commit();
    }

    /// <summary>
    /// MetaLayout 嵌套类（实现 IMetaLayout，供 Managed/Transport meta policy 使用）。
    /// </summary>
    /// <param name="payloadOpaqueSize">不透明的 payload 大小。</param>
    private protected sealed class MetaLayout(int payloadOpaqueSize)
        : IMetaLayout<MetadataMetaHeader, MetadataMetaPayload>
    {
        public int HeaderSize => MetadataMetaHeaderCodec.StructSize;
        public int PayloadSize => MetadataMetaPayloadCodec.StructSize + PayloadOpaqueSize;
        public int PayloadOpaqueSize { get; } = payloadOpaqueSize;
        public uint Magic => MetadataMetaHeader.Magic;
        public ushort CurrentVersion => MetadataMetaHeader.CurrentVersion;
        public ushort DefaultFlags => MetadataMetaHeader.DefaultFlags;

        public void WriteHeader(Span<byte> dst, in MetadataMetaHeader header, bool validate)
            => MetadataMetaHeaderCodec.Write(dst, in header, validate);

        public MetadataMetaHeader ReadHeader(ReadOnlySpan<byte> src) => MetadataMetaHeaderCodec.Read(src);

        public void WritePayload(Span<byte> dst, in MetadataMetaPayload payload) =>
            MetadataMetaPayloadCodec.Write(dst, in payload);

        public MetadataMetaPayload ReadPayload(ReadOnlySpan<byte> src) => MetadataMetaPayloadCodec.Read(src);
        public uint GetMagicValue(in MetadataMetaHeader h) => h.MagicValue;
        public ushort GetVersion(in MetadataMetaHeader h) => h.Version;
        public ushort GetPayloadLength(in MetadataMetaHeader h) => h.PayloadLength;

        public MetadataMetaHeader WithPayloadLength(in MetadataMetaHeader h, ushort len)
        {
            var x = h;
            x.PayloadLength = len;
            return x;
        }

        public MetadataMetaHeader CreateDefaultHeader() => new()
        {
            MagicValue = MetadataMetaHeader.Magic,
            Version = MetadataMetaHeader.CurrentVersion,
            Flags = MetadataMetaHeader.DefaultFlags,
        };
    }

    /// <summary>起点定位的扫描读页步进（64KB——几何跳进前的 magic 首扫）。</summary>
    private const int ScanProbePageSize = 1 << 16;

    /// <summary>
    /// MetaHost 嵌套类（实现 IMetaTransport，供 TransportMetaPolicy 写入 meta block）。
    /// </summary>
    /// <param name="owner">MetadataBase 实例的引用。</param>
    private protected sealed class MetaHost(MetadataBase owner) : IMetaTransport
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

        /// <summary>把 meta block 当 payload 追加为带 IS_META flag 的版本 record。</summary>
        public void WriteBlock(ReadOnlySpan<byte> block)
        {
            owner._epoch.Resume();
            try
            {
                // record = [VersionedMetadataHeader(IS_META)][block as payload][padding 对齐扇区]
                int payloadLen = block.Length;
                var sectorSize = (int)owner._engine.SectorSize;
                int headerSize = owner._codec.HeaderSize;
                int paddingLen = SectorAlignment.AlignUp(headerSize + payloadLen, sectorSize) - headerSize - payloadLen;
                int recordSize = headerSize + payloadLen + paddingLen;

                using var buf = new AlignedMemoryManager(recordSize, sectorSize);
                var span = buf.GetSpan();
                span.Clear();
                // Header——★ IS_META flag 区分 meta record（不参与数据版本号定位）
                owner._codec.WriteHeader(span, new MetadataRecordFields(
                    Flags: owner._codec.DefaultMetaFlags,
                    PayloadLength: (uint)payloadLen,
                    PaddingLength: (ushort)paddingLen,
                    PreviousVersion: owner._highestVersionAddress,
                    MetadataVersion: owner._currentVersion)); // meta record 不推进版本号（用当前值）
                // Payload = meta block
                block.CopyTo(buf.GetSpan(headerSize, payloadLen + paddingLen));
                // 外层 record CRC
                owner._codec.FillCrc(span, headerSize, payloadLen, paddingLen);

                // Allocate + Write + flush（与数据 record 同模型）
                var addr = owner._engine.Allocate(recordSize).Start;
                owner._engine.Write(addr, span);
                owner._engine.Flush();
                // ★ meta record 不更新 _highestVersionAddress/_lowestVersionAddress（不是数据版本链节点，
                //   只追加在链尾供 ReadLastBlock 找回；ScanForHead 跳过 IS_META）
            }
            finally
            {
                owner._epoch.Suspend();
            }
        }

        /// <summary>异步写 meta block（原生 flush 不支持异步，实质同步执行）。</summary>
        public async ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
        {
            WriteBlock(block.Span);
            await ValueTask.CompletedTask.ConfigureAwait(false);
        }

        /// <summary>正向扫版本链找最后一条 IS_META record，返回其 payload（= meta block）。</summary>
        private byte[]? ScanLastBlockCore()
        {
            // ★ 起点定位：LocateFirstNonZero 已退役（非零语义=格式判断）——按 codec magic 定位
            //   首条 record（Linear 档零富集/前缀洞免疫；magic 命中即候选，头校验裁决）。
            var startLoc = owner._engine.Locate([owner._codec.Magic], MagicDirection.First,
                owner._engine.MinAddress, owner._engine.CommittedTail,
                ScanProbePageSize, magicAlignment: 4, MagicLocateStrategy.Linear);
            var addr = startLoc.Found ? startLoc.MagicAddress : owner._engine.MinAddress;
            var sectorSize = (int)owner._engine.SectorSize;
            int headerSize = owner._codec.HeaderSize;
            byte[]? lastBlock = null;
            long maxScanBytes = 64 * 1024 * 1024;

            for (long scanned = 0; scanned < maxScanBytes;)
            {
                using var hdrBuf = new AlignedMemoryManager(headerSize, sectorSize);
                int got;
                try
                {
                    got = owner._engine.Read(addr, hdrBuf.GetSpan());
                }
                catch
                {
                    break;
                }

                if (got < headerSize) break;
                if (!owner._codec.TryReadHeader(hdrBuf.GetSpanUnsafe(0, headerSize), out var fields))
                    break; // magic 不匹配 = 链结束

                int recTotal = headerSize + (int)fields.PayloadLength + fields.PaddingLength;
                // ★ IS_META record：读出 payload（meta block），记为候选
                if ((fields.Flags & RecordFlags.FLAG_ENTRY_IS_META) != 0)
                {
                    int payloadLen = (int)fields.PayloadLength;
                    using var payBuf = new AlignedMemoryManager(payloadLen, sectorSize);
                    // 用 CalculationAddress 跳过 header，从 payload 起始地址读
                    var payAddr = owner._engine.CalculationAddress(addr, headerSize);
                    owner._engine.Read(payAddr, payBuf.GetSpanUnsafe(0, payloadLen));
                    lastBlock = payBuf.GetSpanUnsafe(0, payloadLen).ToArray();
                }

                scanned += recTotal;
                addr = owner._engine.CalculationAddress(addr, recTotal);
            }

            return lastBlock;
        }

        /// <summary>异步扫找最后一条 meta block（实质同步执行）。</summary>
        private async ValueTask<byte[]?> ScanLastBlockCoreAsync(CancellationToken ct)
        {
            var result = ScanLastBlockCore();
            return await ValueTask.FromResult(result).ConfigureAwait(false);
        }
    }
}