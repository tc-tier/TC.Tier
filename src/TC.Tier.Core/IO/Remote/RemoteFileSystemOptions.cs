using TC.Tier.Core.IO.Shared;
using TC.Tier.CodeGen;

namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// RemoteFileSystem 桥配置——staging/编排/缓存/隔离的全部数值旋钮（调参事实源，io.md 调参指南对应）。
/// <para>★ 默认值面向"段级对象"负载（日志段 8MB~1GB 量级）；S3 物理约束：
///   PartSize ∈ [5MB, 5GB]、总 part 数 ≤ <see cref="MaxParts"/>（S3 上限 10000）。</para>
/// </summary>
[MediumOptions("network", Verbs = "New,Open,OpenOrCreate")]
public sealed class RemoteFileSystemOptions : FileSystemOptions
{
    /// <summary>staging 内存页预算（超出即 spill 到 <see cref="Spill"/>；默认 64MB）。</summary>
    public long StagingMemoryLimit { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// spill 位置（G7 收编：单一概念两形态——磁盘目录 / 内存私有卷）。null = 超限抛
    /// <see cref="IOError.DiskFull"/>（不配置即无中转的既有语义）；spec 对应 spill=local:///… / spill=memory:。
    /// </summary>
    public RemoteSpill? Spill { get; init; }

    /// <summary>staging 页大小（字节，2 的幂，[4KiB, 1MiB]）——延迟加载/回填的最小粒度。默认 64KiB。</summary>
    public int StagingPageSize { get; init; } = 64 * 1024;

    /// <summary>multipart 阈值——小于此值的 Flush 走单次 PUT（默认 8MB；≥1 才有意义）。</summary>
    public long MultipartThreshold { get; init; } = 8L * 1024 * 1024;

    /// <summary>multipart part 大小（[5MB, 5GB]——S3 物理约束；默认 8MB）。</summary>
    public long PartSize { get; init; } = 8L * 1024 * 1024;

    /// <summary>单对象 part 数上限（S3 上限 10000——超出时 Flush 自动上调 part 尺寸补齐）。</summary>
    public int MaxParts { get; init; } = 10_000;

    /// <summary>multipart 并发上传度（默认 4）。</summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>读句柄页缓存预算（LRU；默认 4MB；0 = 关闭读缓存直通 Range GET）。</summary>
    public long ReadCacheBytes { get; init; } = 4L * 1024 * 1024;

    /// <summary>顺序读预取页数（Advise(Sequential) 时窗口放大 4×；默认 4 页）。</summary>
    public int PrefetchPages { get; init; } = 4;

    /// <summary>
    /// 命名空间隔离前缀——对象键 = <c>{KeyPrefix}{path}</c>（多引擎共桶的标准隔离姿势；路径穿越防线，
    /// PathValidator 保证规范化后必在前缀内）。
    /// </summary>
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>二级协议身份（G4 观测用——协议构建器填充，如 "s3"；VolumeInfo.SubKind 的来源）。</summary>
    public string? SubKind { get; init; }

    /// <summary>fencing 租约超时（心跳停止超此即视为持有者死亡可接管；默认 60s——按 1.5× 心跳间隔安全裕度）。</summary>
    public TimeSpan LeaseTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 孤儿 multipart 会话启动扫描阈值（§4.4 碎片回收配套）：null=关闭（默认）；非 null = 构造时扫描
    /// KeyPrefix 内进行中的会话，发起时间早于该阈值的全部 Abort（跨进程/崩溃残留清理）。
    /// </summary>
    public TimeSpan? OrphanUploadCleanup { get; init; }

    /// <summary>fencing 心跳间隔（默认 = <see cref="LeaseTimeout"/>/3）。</summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>构造校验（S3 物理约束 + 幂性）。</summary>
    public void Validate()
    {
        if (StagingPageSize is < 4 * 1024 or > 1024 * 1024
            || (StagingPageSize & (StagingPageSize - 1)) != 0)
            throw new ArgumentException($"StagingPageSize 须为 2 的幂且在 [4KiB, 1MiB]: {StagingPageSize}");
        if (StagingMemoryLimit < StagingPageSize)
            throw new ArgumentException("StagingMemoryLimit 不得小于 StagingPageSize。");
        if (MultipartThreshold < 1)
            throw new ArgumentException("MultipartThreshold ≥ 1。");
        if (PartSize is < 5L * 1024 * 1024 or > 5L * 1024 * 1024 * 1024)
            throw new ArgumentException($"PartSize 须在 [5MB, 5GB]（S3 物理约束）: {PartSize}");
        if (MaxParts is < 1 or > 10_000)
            throw new ArgumentException($"MaxParts 须在 [1, 10000]（S3 上限）: {MaxParts}");
        if (MaxConcurrency < 1)
            throw new ArgumentException("MaxConcurrency ≥ 1。");
        if (ReadCacheBytes < 0)
            throw new ArgumentException("ReadCacheBytes ≥ 0。");
        if (PrefetchPages < 0)
            throw new ArgumentException("PrefetchPages ≥ 0。");
        if (LeaseTimeout <= TimeSpan.Zero)
            throw new ArgumentException("LeaseTimeout 须为正。");
        var prefixBytes = System.Text.Encoding.UTF8.GetByteCount(KeyPrefix);
        if (prefixBytes + 1 > ObjectKeyValidator.MaxKeyBytes - PathValidator.MaxComponentLength)
            throw new ArgumentException(
                $"KeyPrefix 过长（{prefixBytes} 字节——加最长文件名将超 S3 键上限 {ObjectKeyValidator.MaxKeyBytes}）。");
    }
}
