namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// GetFileInformationByHandleEx 信息类枚举（文件信息查询用）。
/// </summary>
internal enum FileInfoByHandleClass
{
    /// <summary>
    /// 文件基本信息（创建时间/修改时间/访问时间/属性/标记）。
    /// </summary>
    FileBasicInfo = 0,

    /// <summary>
    /// 文件标准信息（大小/分配大小/链接计数/删除标记）。
    /// </summary>
    FileStandardInfo = 1,
    /// <summary>
    /// 文件名信息（长文件名/短文件名/父目录 ID）。
    /// </summary>
    FileNameInfo = 2,
    /// <summary>
    /// 文件重命名信息（新文件名/父目录 ID）。
    /// </summary>
    FileRenameInfo = 3,
    /// <summary>
    /// 文件处置信息（删除标记/重命名标记）。
    /// </summary>
    FileDispositionInfo = 4,
    /// <summary>
    /// 文件分配信息（分配大小/压缩大小/压缩标记）。
    /// </summary>
    FileAllocationInfo = 5,
    /// <summary>
    /// 文件末尾信息（文件末尾偏移量）。
    /// </summary>
    FileEndOfFileInfo = 6,
    /// <summary>
    /// 文件流信息（流名/流大小/流分配大小）。
    /// </summary>
    FileStreamInfo = 7,
    /// <summary>
    /// 文件压缩信息（压缩大小/压缩标记/压缩算法）。
    /// </summary>
    FileCompressionInfo = 8,
    /// <summary>
    /// 文件属性标记信息（文件属性/标记/重解析点标记）。
    /// </summary>
    FileAttributeTagInfo = 9,
    /// <summary>
    /// 文件 ID 目录信息（文件 ID/父目录 ID/文件名/文件大小/分配大小）。
    /// </summary>
    FileIdBothDirectoryInfo = 10,
    /// <summary>
    /// 文件 ID 目录重启信息（文件 ID/父目录 ID/文件名/文件大小/分配大小/重启标记）。
    /// </summary>
    FileIdBothDirectoryRestartInfo = 11,
    /// <summary>
    /// 文件 IO 优先级提示信息（IO 优先级提示/IO 优先级标记）。
    /// </summary>
    FileIoPriorityHintInfo = 12,
    /// <summary>
    /// 文件远程协议信息（远程协议/远程协议标记）。
    /// </summary>
    FileRemoteProtocolInfo = 13,
    /// <summary>
    /// 文件完整目录信息（文件 ID/父目录 ID/文件名/文件大小/分配大小/创建时间/修改时间/访问时间/属性/标记）。
    /// </summary>
    FileFullDirectoryInfo = 14,
    /// <summary>
    /// 文件完整目录重启信息（文件 ID/父目录 ID/文件名/文件大小/分配大小/创建时间/修改时间/访问时间/属性/标记/重启标记）。
    /// </summary>
    FileFullDirectoryRestartInfo = 15,
    /// <summary>
    /// 文件存储信息（逻辑扇区大小/物理扇区大小/性能扇区大小/文件系统有效物理扇区大小/标记/扇区对齐偏移量/分区对齐偏移量）。
    /// </summary>
    FileStorageInfo = 16,
    /// <summary>
    /// 文件对齐信息（文件对齐偏移量/分区对齐偏移量）。
    /// </summary>
    FileAlignmentInfo = 17,
    /// <summary>
    /// 文件 ID 信息（文件 ID/父目录 ID/文件名/文件大小/分配大小）。
    /// </summary>
    FileIdInfo = 18,
    /// <summary>
    /// 文件 ID 扩展目录信息（文件 ID/父目录 ID/文件名/文件大小/分配大小/创建时间/修改时间/访问时间/属性/标记）。
    /// </summary>
    FileIdExtdDirectoryInfo = 19,
    /// <summary>
    /// 文件 ID 扩展目录重启信息（文件 ID/父目录 ID/文件名/文件大小/分配大小/创建时间/修改时间/访问时间/属性/标记/重启标记）。
    /// </summary>
    FileIdExtdDirectoryRestartInfo = 20,
    /// <summary>
    /// 文件最大信息类（用于验证 infoClass 范围）。
    /// </summary>
    MaximumFileInfoByHandlesClass
}