namespace TC.Tier.Core.IO.Remote;

/// <summary>对象元数据探测结果（HeadObject 语义归一）。</summary>
/// <param name="Key">对象键。</param>
/// <param name="Size">对象字节长度。</param>
/// <param name="ETag">对象 ETag（条件写 IfMatch 的取值源；不可知为 null）。</param>
/// <param name="Metadata">用户元数据（无元数据时为 <see cref="ObjectMetadata.Empty"/>，恒非 null）。</param>
/// <param name="LastModified">最后修改时间（S3=LastModified；内存替身=写入时间；不可得 null——桥据此接 FsEntry/FsEntryInfo 时间戳）。</param>
public sealed record ObjectInfo(string Key, long Size, string? ETag, ObjectMetadata Metadata,
                                DateTimeOffset? LastModified = null);

/// <summary>枚举条目——(Key, Size, LastModified) 融合（ListObjectsV2 天然携带；不可得 null）。</summary>
public readonly record struct ObjectEntry(string Key, long Size, DateTimeOffset? LastModified = null);

/// <summary>
/// 分隔符列举结果（IObjectStore.ListDelimitedAsync 返回）——对象（未截断整键）+
/// 公共前缀（截断聚合：prefix 起至首个 delimiter 含分隔符整段、去重、Ordinal 有序）。
/// </summary>
public readonly record struct ObjectListing(IReadOnlyList<ObjectEntry> Objects, IReadOnlyList<string> CommonPrefixes);
