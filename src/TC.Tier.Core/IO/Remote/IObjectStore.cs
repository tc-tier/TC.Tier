namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 对象存储开放契约——远程对象介质的厂商中立抽象（S3 事实标准为通用货币）。
/// <para>★ Core 只定义契约；实现注入：TC.Tier.Core.IO.S3（SigV4 自写，覆盖 S3/OSS/MinIO/R2/B2 等
///   S3 兼容云）/ <see cref="Testing.MemoryObjectStore"/>（内存替身）/ 任意第三方实现。</para>
/// <para>★ 接口风格 = <b>异步族为主实现路径</b>（HttpClient 本质异步——同步全量阻塞等待会线程池饥饿）；
///   同步便捷包装见 <see cref="ObjectStoreExtensions"/>（低频路径专用）。</para>
/// <para>★ 对象键编码（契约冻结项，§9.5）：UTF-8 / 键长 ≤1024 字节（S3 上限，超限抛错）/
///   禁 '\0' 与 CR/LF（破坏签名 canonical request）；统一经 <see cref="ObjectKeyValidator"/> 校验。</para>
/// <para>★ 错误出口：<see cref="FileIOException"/>（Core/IO 唯一异常类型）；键不存在 = NotFound；
///   条件失配 = <see cref="IOError.PreconditionFailed"/>；能力缺失路径 = Unsupported。</para>
/// <para>★ GetAsync EOF 语义（对齐 IFileHandle.Read 的 pread-EOF 契约）：offset ≥ 对象长度 → 返回 0
///   （S3 416 RangeNotSatisfiable 映射为 0，不抛）——桥层 Read 直通无特判。</para>
/// </summary>
public interface IObjectStore : IDisposable
{
    /// <summary>能力协商位——构造时一次性声明/探测。</summary>
    ObjectStoreCapabilities Capabilities { get; }

    // ═══════════════════════════════════════════════════════════════
    //  基本六件套（S3 语义 1:1）—— 异步族为主实现路径
    // ═══════════════════════════════════════════════════════════════

    /// <summary>整对象 PUT（幂等原子替换——S3 PUT 语义）；元数据随 PUT 原子提交。</summary>
    /// <param name="key">对象键（经 <see cref="ObjectKeyValidator"/> 校验）。</param>
    /// <param name="data">对象内容。</param>
    /// <param name="metadata">用户元数据（null = 无；超限抛 <see cref="ArgumentException"/>，不静默截断）。</param>
    /// <param name="condition">条件前置（null = 无条件）。</param>
    ValueTask PutAsync(string key, ReadOnlyMemory<byte> data, ObjectMetadata? metadata = null,
                       PutCondition? condition = null, CancellationToken ct = default);

    /// <summary>
    /// 整对象 PUT（流式）。三形态：①可寻+长度已知 = 单段签名上传；②不可寻+长度已知 =
    /// chunked 流式签名直传（STREAMING-AWS4-HMAC-SHA256-PAYLOAD 链式签名——免整驻免双遍哈希）；
    /// ③<b>长度未知（length &lt; 0）= spool 后上传</b>（实现中转不整驻内存——S3 侧临时文件）。
    /// </summary>
    ValueTask PutAsync(string key, Stream data, long length, ObjectMetadata? metadata = null,
                       PutCondition? condition = null, CancellationToken ct = default);

    /// <summary>Range GET——读 [offset, offset+destination.Length) 命中对象数据的部分；
    /// 返回实际读取数（EOF 处可能小于请求；offset ≥ 长度 → 0，416 映射 0 不抛）。</summary>
    ValueTask<int> GetAsync(string key, long offset, Memory<byte> destination, CancellationToken ct = default);

    /// <summary>元数据/存在性探测（HeadObject 语义）；不存在返回 null。</summary>
    ValueTask<ObjectInfo?> HeadAsync(string key, CancellationToken ct = default);

    /// <summary>删除（幂等——POSIX unlink 对齐：不存在仍成功）。</summary>
    ValueTask DeleteAsync(string key, DeleteCondition? condition = null, CancellationToken ct = default);

    /// <summary>前缀枚举（ListObjectsV2 语义；分页归一在实现内）。★ 全量加载——段目录千级键适用，
    /// 十万级键大桶见 io.md 规模边界声明。</summary>
    ValueTask<IReadOnlyList<ObjectEntry>> ListAsync(string? prefix = null, CancellationToken ct = default);

    /// <summary>整对象服务端拷贝（CopyObject 语义——dest 新建/替换，与源独立；缺 ServerSideCopy 能力时下载重传）。</summary>
    ValueTask CopyAsync(string sourceKey, string destKey, CopyMetadata? metadata = null,
                        CancellationToken ct = default);

    /// <summary>
    /// 元数据更新（服务端 CopyObject 自拷贝 + REPLACE 指令的语义归一）——不改对象内容只换元数据。
    /// <paramref name="replace"/> = null → 保留现有元数据；非 null → 以此替换。返回更新后的元数据。
    /// </summary>
    ValueTask<ObjectMetadata> CopyMetadataAsync(string sourceKey, ObjectMetadata? replace = null,
                                                CancellationToken ct = default);

    // ═══════════════════════════════════════════════════════════════
    //  multipart 原语族（桥层厂商无关编排的底座）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>开启 multipart 会话（会话 = upload-id 的厂商中立句柄；part 粒度编排归桥层）。</summary>
    IMultipartUpload CreateMultipartUpload(string key, ObjectMetadata? metadata = null);

    /// <summary>
    /// 范围拷贝原语——新建 dest（若已存在则替换）= 源 [sourceOffset, sourceOffset+length) 的内容；
    /// 返回实际拷贝长度。S3 映射 UploadPartCopy / GetRange+Put。★ 不支持"对已有对象部分覆写"（S3 无此能力）。
    /// </summary>
    ValueTask<long> CopyRangeAsync(string sourceKey, string destKey, long sourceOffset, long length,
                                   CopyMetadata? metadata = null, CancellationToken ct = default);

    // ═════════════════════════════ 会话治理原语（增补设计 §2）═════════════════════════════

    /// <summary>
    /// 枚举进行中的 multipart 会话（ListMultipartUploads 语义归一）——孤儿清理与运维面专用，
    /// 不进桥的日常路径（大桶会话枚举本身是重操作）。
    /// </summary>
    ValueTask<IReadOnlyList<MultipartUploadSession>> ListMultipartUploadsAsync(CancellationToken ct = default);

    /// <summary>
    /// 定向放弃会话（uploadId 粒度——供非本进程创建会话的清理路径）；幂等（NoSuchUpload 视为成功）。
    /// 与 <see cref="IMultipartUpload.AbortAsync"/> 语义等价，共享底层调用。
    /// </summary>
    ValueTask AbortMultipartUploadAsync(string key, string uploadId, CancellationToken ct = default);

    /// <summary>
    /// 流式枚举（IAsyncEnumerable——十万级键大桶消费者；分页在实现内推进）。
    /// 默认实现 = <see cref="ListAsync"/> 整量包装（小桶零损失；S3 实现覆写真流式）。
    /// </summary>
    IAsyncEnumerable<ObjectEntry> ListStreamingAsync(string? prefix = null, CancellationToken ct = default)
        => ListStreamingCoreAsync(this, prefix, ct);

    private static async IAsyncEnumerable<ObjectEntry> ListStreamingCoreAsync(
        IObjectStore store, string? prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var e in await store.ListAsync(prefix, ct).ConfigureAwait(false))
            yield return e;
    }

    /// <summary>
    /// 分隔符列举（ListObjectsV2 + delimiter 语义——目录模拟的底座，filesystem-root-space-design §7）：
    /// 键在 prefix 后首个 <paramref name="delimiter"/> 处截断聚合成 <see cref="ObjectListing.CommonPrefixes"/>
    /// （去重），未截断的整键入 <see cref="ObjectListing.Objects"/>。
    /// <para>默认实现 = <see cref="ListAsync"/> 全量 + 客户端切分聚合（小桶零损失；S3 实现覆写为
    /// 原生 delimiter——服务端聚合省流量）。delimiter=null ≡ ListAsync（无聚合）。</para>
    /// </summary>
    ValueTask<ObjectListing> ListDelimitedAsync(string? prefix = null, string? delimiter = null,
                                                CancellationToken ct = default)
        => ListDelimitedCoreAsync(this, prefix, delimiter, ct);

    private static async ValueTask<ObjectListing> ListDelimitedCoreAsync(
        IObjectStore store, string? prefix, string? delimiter, CancellationToken ct)
    {
        var entries = await store.ListAsync(prefix, ct).ConfigureAwait(false);
        if (delimiter is null)
            return new ObjectListing(entries, Array.Empty<string>());
        var prefixLen = prefix?.Length ?? 0;
        var objects = new List<ObjectEntry>();
        var prefixes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var rest = e.Key[prefixLen..];
            var cut = rest.IndexOf(delimiter, StringComparison.Ordinal);
            if (cut < 0)
                objects.Add(e);
            else
                prefixes.Add(e.Key[..(prefixLen + cut + delimiter.Length)]);   // 截断点含分隔符整段（S3 CommonPrefix 语义）
        }
        return new ObjectListing(objects, prefixes.ToList());
    }
}
