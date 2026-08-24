namespace TC.Tier.Runtime.Meta;

/// <summary>
/// 外部 meta 介质适配器——把 meta 块托管到 <see cref="VersionedMetadata"/>（版本链稳定存储）。
/// <para>★ 3a（外部隔离）语义的<b>推荐实现</b>（用户裁定）：需要外部独有 meta 存储时，不再自写
///   单槽文件/KV——独自写盘要自己解决 torn write 原子性、落盘顺序（data 先 meta 后）、
///   崩溃恢复一致性，还得自搭 2PC 提交链路。托管到 VersionedMetadata 后：</para>
/// <para> - 每次写块 = 版本链追加新版本（写到一半崩 = 旧版本完好，CRC/magic 断链天然容错）；</para>
/// <para> - 持久化点即 <see cref="WriteBlock"/>（Write + Persist + flush）——对齐
///   TransportMetaPolicy.Commit 的 meta fsync 语义（调用链保证 data 先落盘）；</para>
/// <para> - N=2 轮转自动回收（每次写后 <see cref="VersionedMetadata.ReclaimOldVersions"/>），空间有界；</para>
/// <para> - 读 = 内存工作副本零 IO；需要跨结构原子提交时底层已实现 ITransactionParticipant（
///   经 <see cref="Storage"/> 注册进 TransactionLog）。</para>
/// <para>配置约束：<see cref="VersionedMetadataSettings.PayloadSize"/> 必须 ≥ meta 块上界
/// （12B 头 + 水位 struct + MetaOpaqueBytes + 4B 尾）；超限写抛 <see cref="ArgumentException"/>
/// （fail-fast，不静默截断）。引擎名须与主结构不同（如 "xxx.meta"）。</para>
/// </summary>
public sealed class MetadataMetaTransport : IMetaTransport, IDisposable
{
    private readonly VersionedMetadata _metadata;
    private readonly int _payloadSize;
    private byte[]? _lastBlock;

    /// <summary>
    /// 构造 + 启动（Initialize 非阻塞；首次读写会 WaitForReady 就绪——外部介质归调用方组合根）。
    /// </summary>
    /// <param name="fs">组合根文件系统（与主结构同 fs 即可，引擎名隔离子目录）。</param>
    /// <param name="settings">VersionedMetadata 配置（PayloadSize ≥ meta 块上界；EngineName 与主结构不同）。</param>
    /// <param name="epoch">可选共享 epoch。</param>
    /// <param name="logger">可选日志。</param>
    public MetadataMetaTransport(
        IFileSystem fs,
        VersionedMetadataSettings settings,
        LightEpoch? epoch = null,
        ILogger? logger = null)
    {
        _payloadSize = settings.PayloadSize;
        _metadata = new VersionedMetadata(fs, settings, epoch: epoch);
        _metadata.Initialize();
    }

    /// <summary>底层 VersionedMetadata——高级场景（注册 ITransactionParticipant 进 TransactionLog / 手动 ReclaimOldVersions）。</summary>
    public VersionedMetadata Storage => _metadata;

    public ReadOnlySpan<byte> ReadLastBlock()
    {
        _metadata.WaitForReady();
        _lastBlock = ReadCore();
        return _lastBlock is null ? ReadOnlySpan<byte>.Empty : _lastBlock;
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReadLastBlockAsync(CancellationToken ct)
    {
        await _metadata.WaitForReadyAsync(ct).ConfigureAwait(false);
        _lastBlock = ReadCore();
        return _lastBlock is null ? default : _lastBlock;
    }

    /// <summary>写完整块 = 持久化点：内存镜像推进版本 → 追加版本链 + flush → N=2 轮转回收。</summary>
    public void WriteBlock(ReadOnlySpan<byte> block)
    {
        if (block.Length > _payloadSize)
            throw new ArgumentException(
                $"meta block {block.Length}B exceeds hosting PayloadSize {_payloadSize}B——" +
                "请把 VersionedMetadataSettings.PayloadSize 配到 ≥ 12+水位struct+MetaOpaqueBytes+4");
        _metadata.WaitForReady();
        _metadata.Write(block);
        _metadata.Persist();              // ★ 本调用即 meta fsync 点（data 已在调用链先行落盘）
        _metadata.ReclaimOldVersions();   // N=2 轮转——版本链空间有界
    }

    public async ValueTask WriteBlockAsync(ReadOnlyMemory<byte> block, CancellationToken ct)
    {
        WriteBlock(block.Span);   // flush 原生仅同步（引擎写/截断同步语义）
        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>读当前版本 payload（= 最后一次写块），按统一布局自述裁剪为<b>变长精确块</b>；
    /// 无版本（CurrentVersion=0）返回 null = 无数据。
    /// <para>★ 传输契约是精确块（Header 12B + PayloadLength@8 + Footer 4B 自述定长）——
    ///   托管结构按 PayloadSize 定长读出（尾部补零），必须裁剪；自述字段越界视为垃圾，
    ///   返回原样由策略 magic 校验拒绝（fail-safe）。</para></summary>
    private byte[]? ReadCore()
    {
        if (_metadata.CurrentVersion == 0) return null;   // 空链 = 无块（空即答案）
        var buf = new byte[_payloadSize];
        _metadata.Read(buf);
        // 统一 meta 头规范：PayloadLength(ushort) @ 8；块长 = 12 + PayloadLength + 4
        int payloadLen = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(8, 2));
        int blockLen = 12 + payloadLen + 4;
        if (payloadLen >= 0 && blockLen <= _payloadSize)
            return buf[..blockLen];   // 精确块（策略缓冲按实际块长拷贝）
        return buf;   // 自述越界 = 垃圾——原样交策略 magic 校验拒绝
    }

    public void Dispose() => _metadata.Dispose();
}
