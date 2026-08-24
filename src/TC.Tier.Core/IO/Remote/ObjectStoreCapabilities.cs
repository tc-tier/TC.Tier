namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 对象存储能力协商位——<see cref="IObjectStore"/> 实现的介质异构显式表达（构造时一次性声明/探测）。
/// <para>★ 契约纪律（对齐 <see cref="FileSystemCapabilities"/>）：每个能力位对应的操作在未置位的实现上有
///   文档化回退（如 <see cref="ServerSideCopy"/> 缺 → 下载重传）或抛 <see cref="IOError.Unsupported"/>；
///   缺 <see cref="ConditionalPut"/> → fencing 锁降级（remote-storage-s3 设计 §4.6/§6）。</para>
/// <para>★ 表面 = S3 六件套语义超集，不加任何厂商专有方法——专有能力走能力位 + 消费者自决，
///   永不出现在接口上（§5 设计纪律）。</para>
/// </summary>
[Flags]
public enum ObjectStoreCapabilities
{
    /// <summary>无能力。</summary>
    None = 0,

    /// <summary>条件 PUT（If-Match/If-NoneMatch——fencing 依赖；缺 → 锁降级或 Unsupported）。</summary>
    ConditionalPut = 1 << 0,

    /// <summary>Copy 服务端零流量（缺 → 下载重传——结果正确性不变，仅流量/延迟代价）。</summary>
    ServerSideCopy = 1 << 1,

    /// <summary>写后立即可见（S3 2020.12+/MinIO ✓；老 OSS 最终一致——适配器读后短重试吸收）。</summary>
    StrongList = 1 << 2,

    /// <summary>分片上传 multipart（全支持；part 数量/大小限制由实现参数校验表达）。</summary>
    Multipart = 1 << 3,

    /// <summary>Range GET（全支持——保留位防 exotic 实现）。</summary>
    RangeGet = 1 << 4,

    /// <summary>原生 AppendObject（可选增强——桥不用；与桥层 Partial flush 是两回事，§9.2）。</summary>
    Appendable = 1 << 5,

    /// <summary>厂商 WORM 语义（合规场景，非本层 fencing——永不置位，预留）。</summary>
    ObjectLock = 1 << 6,

    /// <summary>条件 DELETE（If-Match——锁释放防误删依赖；缺 → Head 校验 + 无条件删降级，§6）。</summary>
    ConditionalDelete = 1 << 7,
}

/// <summary>
/// PUT 条件前置（对象层条件写底座——fencing 锁的实现原语）。
/// <para>★ 语义对齐 S3 If-Match / If-None-Match：与实现当前状态失配 → <see cref="FileIOException"/>
///   (<see cref="IOError.PreconditionFailed"/>)，对象不被修改。</para>
/// <para>值语义：ETag 或 "<c>*</c>"（任意）。<see cref="IfNoneMatch"/> = "*" 表"对象必须不存在"（抢建）。</para>
/// </summary>
/// <param name="IfMatch">仅当对象存在且 ETag 匹配时写入（CAS 替换/锁接管）。</param>
/// <param name="IfNoneMatch">仅当条件满足时写入；"*" = 对象必须不存在（抢建语义）。</param>
public readonly record struct PutCondition(string? IfMatch, string? IfNoneMatch);

/// <summary>
/// DELETE 条件前置（锁释放防误删他人锁——§4.6）。
/// 失配 → <see cref="IOError.PreconditionFailed"/>，对象不被删除。
/// </summary>
/// <param name="IfMatch">仅当对象存在且 ETag 匹配时删除（token 校验释放）。</param>
public readonly record struct DeleteCondition(string? IfMatch);
