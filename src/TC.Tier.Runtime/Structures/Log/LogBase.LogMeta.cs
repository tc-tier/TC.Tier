namespace TC.Tier.Runtime.Structures.Log;

public abstract partial class LogBase
{
    // ═══════════════════════════════════════════════════════════════════
    // ★ AppendMeta — 写元数据公共入口（委托注入的 ILogMetaPolicy）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 写元数据（水位提交链统一入口）：水位 + 已登记 opaque 同块原子落盘。
    /// <para>opaque 来自 SetOpaqueMeta 的 stage（策略缓冲保留——结构化水位写不清除），
    ///   内部提交链不再有独立 opaque 通道（设计决策：opaque 跟随水位线，无第二条提交路径）。</para>
    /// </summary>
    /// <param name="committedOffset">本次提交边界（写入 Payload 的 CommittedOffset 字段）。</param>
    private protected void AppendMeta(LogicalAddress committedOffset)
    {
        var p = MetaPolicy;
        lock (p)
        {
            p.WriteHeader(BuildMetaHeader());
            p.WritePayload(BuildMetaPayload(committedOffset));
            p.Commit();
            _opaqueDirty = false;   // opaque 已随本块落盘
        }
    }

    /// <summary>★ 写元数据（异步轨，对等同步版）。</summary>
    private protected async ValueTask AppendMetaAsync(LogicalAddress committedOffset, CancellationToken ct = default)
    {
        var p = MetaPolicy;
        lock (p)
        {
            p.WriteHeader(BuildMetaHeader());
            p.WritePayload(BuildMetaPayload(committedOffset));
        }
        await p.CommitAsync(ct).ConfigureAwait(false);
        _opaqueDirty = false;   // opaque 已随本块落盘
    }

    /// <summary>构造 meta header（纯规范字段 12B，水位不在 Header）。</summary>
    /// <para>★ P0 修复：必须填正确的 Magic/Version/Flags——返回 default（全零）会导致</para>
    /// <para>  Managed/Transport 策略 Load 时 MagicValue 校验必然失败（0 ≠ Magic），</para>
    /// <para>  meta 永久加载失败、崩溃后水位丢失。</para>
    /// <para>PayloadLength/PaddingLength 由策略 WritePayload 时按实际数据长度填。</para>
    private static LogMetaHeader BuildMetaHeader() => new()
    {
        MagicValue = LogMetaHeader.Magic,
        Version = LogMetaHeader.CurrentVersion,
        Flags = LogMetaHeader.DefaultFlags,
    };

    /// <summary>★ 构造 meta payload（四水位：BeginAddress/TailAddress/CommittedOffset/PreparedTailAddress）。</summary>
    private LogMetaPayload BuildMetaPayload(LogicalAddress committedOffset) => new()
    {
        BeginAddress = BeginAddress,
        TailAddress = TailAddress,
        CommittedOffset = committedOffset,
        LastCommittedSeq = LastCommittedSeq,
        LastPreparedSeq = LastPreparedSeq,
        PreparedTailAddress = _txRollbackTail,   // ★ Abort 回退点（Empty = 无待回滚窗口）
    };

    // ═══════════════════════════════════════════════════════════════════
    // WriteMetaPayload — 嵌入式 meta 写 log 流原语
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>★ 嵌入式 meta 写 log 流——复用 AppendCore（isMeta=true）。</summary>
    private protected LogicalAddress WriteMetaPayload(ReadOnlySpan<byte> payload)
        => AppendCore(payload, isMeta: true);

    private protected async ValueTask<LogicalAddress> WriteMetaPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => await AppendCoreAsync(payload, isMeta: true, ct).ConfigureAwait(false);

    /// <summary>
    /// 默认 meta 策略装配（构造期经 ??= 收口——签名即 MetaPolicyFactory"按模式构造"委托：
    /// 注入工厂与默认实现是同一条 kind → policy 映射，无匿名 lambda）。
    /// </summary>
    private IMetaPolicy<LogMetaHeader, LogMetaPayload> CreateMetaPolicyDefault(MetaPolicyKind kind)
    {
        var layout = new MetaLayout(_settings.MetaOpaqueBytes);
        return kind switch
        {
            MetaPolicyKind.Managed => _metaEngine is not null
                ? new ManagedMetaPolicy<LogMetaHeader, LogMetaPayload>(layout, _metaEngine, Logger)
                : throw new InvalidOperationException("Meta engine is not initialized."),
            // ★ Transport：上层注入传输实例；未注入回落到 MetaHost——meta entry 嵌入 log 流
            MetaPolicyKind.Transport => new TransportMetaPolicy<LogMetaHeader, LogMetaPayload>(
                layout, _metaTransport ?? new MetaHost(this), Logger),
            _ => new DisabledMetaPolicy<LogMetaHeader, LogMetaPayload>(),
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MetaLayout / MetaHost——Log 的 IMetaLayout/IMetaTransport 实现（泛型策略用）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Log meta 布局描述（供泛型策略操作 LogMetaHeader/LogMetaPayload codec）。</summary>
    /// <param name="payloadOpaqueSize">opaque 插槽字节数（settings.MetaOpaqueBytes）。</param>
    protected sealed class MetaLayout(int payloadOpaqueSize) : IMetaLayout<LogMetaHeader, LogMetaPayload>
    {
        /// <summary>规范 header 字节大小（LogMetaHeaderCodec 定长 12B）。</summary>
        public int HeaderSize => LogMetaHeaderCodec.StructSize;
        /// <summary>结构化 payload 字节大小（LogMetaPayloadCodec 定长 + opaque 插槽）。</summary>
        public int PayloadSize => LogMetaPayloadCodec.StructSize + PayloadOpaqueSize;
        /// <summary>opaque 插槽字节大小（构造注入，settings.MetaOpaqueBytes）。</summary>
        public int PayloadOpaqueSize { get; } = payloadOpaqueSize;
        /// <summary>Magic 常量（块身份校验）。</summary>
        public uint Magic => LogMetaHeader.Magic;
        /// <summary>当前版本号。</summary>
        public ushort CurrentVersion => LogMetaHeader.CurrentVersion;
        /// <summary>默认 Flags（含 CRC 模式等）。</summary>
        public ushort DefaultFlags => LogMetaHeader.DefaultFlags;

        /// <summary>序列化 header 到 Span（validate=true 时校验 Magic/Version）。</summary>
        /// <param name="dst">目标缓冲。</param>
        /// <param name="header">header 值。</param>
        /// <param name="validate">是否校验 Magic/Version。</param>
        public void WriteHeader(Span<byte> dst, in LogMetaHeader header, bool validate)
            => LogMetaHeaderCodec.Write(dst, in header, validate);
        /// <summary>从 Span 反序列化 header。</summary>
        /// <param name="src">源缓冲。</param>
        /// <returns>解析出的 LogMetaHeader。</returns>
        public LogMetaHeader ReadHeader(ReadOnlySpan<byte> src) => LogMetaHeaderCodec.Read(src);
        /// <summary>序列化 payload 到 Span。</summary>
        /// <param name="dst">目标缓冲。</param>
        /// <param name="payload">payload 值。</param>
        public void WritePayload(Span<byte> dst, in LogMetaPayload payload)
            => LogMetaPayloadCodec.Write(dst, in payload);
        /// <summary>从 Span 反序列化 payload（旧块短于当前布局时零扩展解读——新字段读默认值 Empty，优雅降级不抛）。</summary>
        /// <param name="src">源缓冲。</param>
        /// <returns>解析出的 LogMetaPayload。</returns>
        public LogMetaPayload ReadPayload(ReadOnlySpan<byte> src)
        {
            // ★ 旧块容错：盘上 payload 短于当前布局（字段追加前写入的块）→ 零扩展后解读，
            //   超出旧布局的新字段（如 PreparedTailAddress）读默认值 Empty——优雅降级不抛。
            if (src.Length >= LogMetaPayloadCodec.StructSize) return LogMetaPayloadCodec.Read(src);
            Span<byte> buf = stackalloc byte[LogMetaPayloadCodec.StructSize];
            src.CopyTo(buf);
            return LogMetaPayloadCodec.Read(buf);
        }

        /// <summary>读 header 的 MagicValue（用于校验）。</summary>
        /// <param name="h">header 值。</param>
        /// <returns>MagicValue 字段值。</returns>
        public uint GetMagicValue(in LogMetaHeader h) => h.MagicValue;
        /// <summary>读 header 的 Version（用于校验）。</summary>
        /// <param name="h">header 值。</param>
        /// <returns>Version 字段值。</returns>
        public ushort GetVersion(in LogMetaHeader h) => h.Version;
        /// <summary>读 header 的 PayloadLength（用于计算 opaque 长度）。</summary>
        /// <param name="h">header 值。</param>
        /// <returns>PayloadLength 字段值。</returns>
        public ushort GetPayloadLength(in LogMetaHeader h) => h.PayloadLength;
        /// <summary>设置 header 的 PayloadLength（WritePayload 后更新）。</summary>
        /// <param name="h">header 值。</param>
        /// <param name="len">新的 PayloadLength。</param>
        /// <returns>更新后的 header。</returns>
        public LogMetaHeader WithPayloadLength(in LogMetaHeader h, ushort len)
        { var x = h; x.PayloadLength = len; return x; }
        /// <summary>创建一个填好规范字段（Magic/Version/Flags）的默认 header。</summary>
        /// <returns>默认 LogMetaHeader（规范字段常量填好，PayloadLength/PaddingLength 为零）。</returns>
        public LogMetaHeader CreateDefaultHeader() => new()
        {
            MagicValue = LogMetaHeader.Magic,
            Version = LogMetaHeader.CurrentVersion,
            Flags = LogMetaHeader.DefaultFlags,
        };
    }

    /// <summary>Log meta 传输宿主（Transport 策略未注入传输时的回落）——WriteBlock 走 AppendCore(isMeta)，ReadLastBlock 走 cursor 找最后 IsMeta。</summary>
    /// <param name="owner">LogBase 实例的引用。</param>
    protected sealed class MetaHost(LogBase owner) : IMetaTransport
    {

        /// <summary>最近一次扫描结果（字段持有——ReadLastBlock 返回的视图有效至本传输下一次调用）。</summary>
        private byte[]? _lastBlock;

        /// <summary>读回最后一条 meta block——游标正扫 log 流取最后一条 IsMeta entry 的 payload。</summary>
        /// <returns>meta block 字节视图；无 meta entry 时为空 Span。</returns>
        public ReadOnlySpan<byte> ReadLastBlock()
        {
            _lastBlock = ScanLastBlockCore();
            return _lastBlock;
        }

        /// <summary>读回最后一条 meta block（异步对等版；空 Memory = 无）。</summary>
        /// <param name="ct">取消令牌。</param>
        /// <returns>meta block 字节视图；无 meta entry 时为空 Memory。</returns>
        public async ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
        {
            _lastBlock = await ScanLastBlockCoreAsync(ct).ConfigureAwait(false);
            return _lastBlock is null ? default : _lastBlock;
        }

        /// <summary>写完整 meta block——作为 IsMeta entry 嵌入 log 流（WriteMetaPayload）。</summary>
        /// <param name="block">完整 meta 块字节。</param>
        public void WriteBlock(ReadOnlySpan<byte> block)
            => owner.WriteMetaPayload(block);

        /// <summary>写完整 meta block（异步对等版）。</summary>
        /// <param name="block">完整 meta 块字节。</param>
        /// <param name="ct">取消令牌。</param>
        public async ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
            => await owner.WriteMetaPayloadAsync(block, ct).ConfigureAwait(false);

        private byte[]? ScanLastBlockCore()
        {
            // ★ 显式 end = 引擎 AllocatedTail：无参 OpenCursor 的 end 缺省 Empty → 游标立即 EOF；
            //   且 LoadAsync 在恢复核心尾部裁决前调用（TailAddress 未就绪）——引擎尾是恢复期
            //   与运行期都可靠的物理边界（依赖 join 已保证引擎就绪）。
            using var cursor = owner.OpenCursor(endAddress: owner._engine.AllocatedTail);
            if (cursor is null) return null;
            byte[]? last = null;
            while (cursor.MoveNext())
                if (cursor.CurrentIsMeta)
                    last = cursor.CurrentPayload.ToArray();
            return last;
        }

        private async ValueTask<byte[]?> ScanLastBlockCoreAsync(CancellationToken ct)
        {
            // ★ 同同步轨：显式 end = 引擎 AllocatedTail（无参缺省 Empty = 立即 EOF）
            await using var cursor = owner.OpenCursor(endAddress: owner._engine.AllocatedTail);
            if (cursor is null) return null;
            byte[]? last = null;
            while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
                if (cursor.CurrentIsMeta)
                    last = cursor.CurrentPayload.ToArray();
            return last;
        }
    }
}
