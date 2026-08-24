namespace TC.Tier.Core.IO.Remote;

/// <summary>
/// 拷贝元数据指令——null（不传本类型）= 复制源元数据（S3 CopyObject 默认）；
/// 非 null = 以 <see cref="Metadata"/> 替换目标元数据。
/// </summary>
/// <param name="Metadata">目标对象的替换元数据（构造期已校验）。</param>
public sealed record CopyMetadata(ObjectMetadata Metadata);
