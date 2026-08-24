namespace TC.Tier.Core.IO;

/// <summary>
/// 卷几何信息——两个独立探测、职责不同的对齐基准（可不同：512B 逻辑扇区+4K 簇；NTFS 4K 扇区+64K 簇）。
/// <para>★ <see cref="SectorSize"/> = 物理扇区——DIO 读写对齐基准（Win DIO 句柄的 RequiredAlignment 升为 max(sector, 内存页)）。</para>
/// <para>★ <see cref="AllocationUnit"/> = fs 簇/分配单元——空间操作（PunchHole/Collapse/Insert）对齐基准；mem = PageSize。</para>
/// </summary>
public sealed record VolumeInfo
{
    /// <summary>物理扇区大小（字节）——DIO 读写对齐基准。mem 介质 = 1（表达无要求）。</summary>
    public int SectorSize { get; init; }

    /// <summary>分配单元/簇大小（字节）——空间操作对齐基准。mem 介质 = PageSize。</summary>
    public long AllocationUnit { get; init; }

    /// <summary>卷剩余空间（字节）；不可知时为 -1。</summary>
    public long FreeSpace { get; init; } = -1;

    /// <summary>卷总空间（字节）；不可知时为 -1。</summary>
    public long TotalSpace { get; init; } = -1;

    // ═══════ 完整自描述（medium-protocol-and-parity-design §5.4——是什么/怎么挂的/什么状态）═══════

    /// <summary>卷标签（G1 镜像身份——与 spec 协议头同源词汇；null = 未设置）。</summary>
    public string? Label { get; init; }

    /// <summary>介质本性（G4——Local/Memory/Virtual/Network，与 scheme 头逐字相同）。</summary>
    public StorageNature Nature { get; init; }

    /// <summary>二级分类（network → 协议名 "s3"…；virtual → "dev" 或 null=文件载体；local/memory → null）。</summary>
    public string? SubKind { get; init; }

    /// <summary>当前挂载访问三态（G2——总上包络的运行时可见）。</summary>
    public AccessMode Access { get; init; } = AccessMode.ReadWrite;

    /// <summary>当前挂载配额（-1 = 无上限——与 spec quota= 同名往返）。</summary>
    public long QuotaBytes { get; init; } = -1;

    /// <summary>已用字节（配额执法的读侧面）；不可知时为 -1（无配额且未推导）。</summary>
    public long UsedBytes { get; init; } = -1;
}

/// <summary>条目类型（根空间统一条目 FsEntry 的判别）。</summary>
public enum FsEntryType
{
    /// <summary>文件。</summary>
    File,

    /// <summary>目录。</summary>
    Directory,
}

/// <summary>
/// 根空间统一枚举条目（filesystem-root-space-design §3.3）——三族枚举（Files/Directories/Entries）共用。
/// <para>★ 轻量：不含元数据（S3 ListObjectsV2 不返回用户元数据——枚举携带 = 逐键 HeadObject，不可接受）；
///   完整信息（含 ≤2K 元数据）见 <see cref="FsEntryInfo"/>（<c>IFileSystem.Stat</c> 专用）。</para>
/// <para>★ Name = 相对所枚举目录的路径（recursive=true 时多组件 "sub/f"）；
///   Length 文件=字节数、目录恒 0；LastWriteTime 文件三介质必有，S3 目录不可得 = <see cref="DateTimeOffset.MinValue"/>；
///   CreationTime 可空 = 介质不可得（NTFS ✓ / Linux 视 statx / S3 恒 null）。</para>
/// </summary>
public readonly record struct FsEntry(FsEntryType Type, string Name, long Length,
                                      DateTimeOffset LastWriteTime, DateTimeOffset? CreationTime);

/// <summary>
/// 单条目完整信息（<c>IFileSystem.Stat</c> 专用）——<see cref="FsEntry"/> 全字段 + 用户元数据（≤2K）。
/// </summary>
public readonly record struct FsEntryInfo(FsEntryType Type, string Name, long Length,
                                          DateTimeOffset LastWriteTime, DateTimeOffset? CreationTime,
                                          ReadOnlyMemory<byte> FileExtra);
