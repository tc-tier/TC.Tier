namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// multipart 上传会话——桥层按 part 粒度编排、厂商映射在适配器内（§4.4）。
/// <para>★ 禁止跳 part：对象 = 各 part <b>顺序拼接的连续字节流</b>，跳过中段 part = 对象缩短 +
///   偏移整体错位（正确性级错误）——桥全量上传（含全零 part）。</para>
/// <para>★ 碎片回收契约：未 Complete/Abort 的会话（崩溃/异常路径）由桥登记回收（AbortAsync）；
///   DisposeAsync ≡ AbortAsync（异常安全兜底）。</para>
/// </summary>
public interface IMultipartUpload : IAsyncDisposable
{
    /// <summary>上传一个 part（partNumber ≥1，最终对象按 partNumber 升序拼接——允许乱序上传）。</summary>
    ValueTask<UploadPartResult> UploadPartAsync(int partNumber, ReadOnlyMemory<byte> data,
                                                CancellationToken ct = default);

    /// <summary>服务端拷贝一个 part（源对象区间 → part；零出口流量——未触区间回填的首选路径）。</summary>
    ValueTask<UploadPartResult> UploadPartCopyAsync(int partNumber, string sourceKey,
                                                    long sourceOffset, long length,
                                                    CancellationToken ct = default);

    /// <summary>
    /// 完成上传——原子替换旧对象版本（S3 PUT 语义）；崩溃在 complete 之前 → 旧对象完全不受影响。
    /// ★ 非完全幂等：重试遇 NoSuchUpload（upload-id 已失效）实现抛 <see cref="FileIOException"/>
    ///   (<see cref="IOError.NotFound"/>)——桥层视为"已 complete 过"，回读校验。
    /// </summary>
    ValueTask CompleteAsync(IReadOnlyList<UploadPartResult> parts, CancellationToken ct = default);

    /// <summary>放弃会话（碎片回收——已上传 part 的存储回收）。</summary>
    ValueTask AbortAsync(CancellationToken ct = default);
}

/// <summary>已上传 part 的句柄结果（CompleteMultipartUpload 的输入件）。</summary>
/// <param name="PartNumber">part 序号（≥1，升序参与拼接）。</param>
/// <param name="ETag">服务端返回的 part ETag（complete 必需）。</param>
public readonly record struct UploadPartResult(int PartNumber, string ETag);

/// <summary>进行中的 multipart 会话描述（孤儿判定基准——会话治理原语的返回件）。</summary>
/// <param name="Key">对象键。</param>
/// <param name="UploadId">会话 id（AbortMultipartUploadAsync 的句柄）。</param>
/// <param name="InitiatedUtc">发起时间（UTC；不可知的实现为 null——孤儿扫描按"不误杀"跳过）。</param>
public sealed record MultipartUploadSession(string Key, string UploadId, DateTimeOffset? InitiatedUtc);
