namespace TC.Tier.Core.IO;

/// <summary>
/// 统一跨平台文件 IO 错误码——Core/IO 的底层统一错误分类（替代原生 Win32 HResult / errno 的碎片化处理）。
/// <para>★ 值兼容 Runtime.Storage 旧 <c>IOError</c>（前 9 个成员序号一致），仅追加新成员；
///   适配迁移期两套并存（本枚举属家族 A，旧枚举随旧目录消亡）。</para>
/// </summary>
// ReSharper disable once InconsistentNaming
public enum IOError
{
    /// <summary>无错误。</summary>
    None,

    /// <summary>文件/路径不存在（Win32 ERROR_FILE_NOT_FOUND / ENOENT）。</summary>
    NotFound,

    /// <summary>权限拒绝（ERROR_ACCESS_DENIED / EACCES）。</summary>
    AccessDenied,

    /// <summary>卷/配额已满（ERROR_DISK_FULL / ENOSPC）。</summary>
    DiskFull,

    /// <summary>DIO 三重对齐未满足（ERROR_INVALID_PARAMETER / EINVAL）。</summary>
    AlignmentError,

    /// <summary>底层 IO 故障（ERROR_IO_DEVICE / EIO）。</summary>
    // ReSharper disable once InconsistentNaming
    IOFailure,

    /// <summary>共享冲突（ERROR_SHARING_VIOLATION / EBUSY）。</summary>
    SharingViolation,

    /// <summary>操作被取消（OperationCanceledException）。</summary>
    Cancelled,

    /// <summary>未分类的未知错误。</summary>
    Unknown,

    /// <summary>能力位未置位的实现上调用了该操作（如 macOS 上 CollapseRange）——不支持且无回退。</summary>
    Unsupported,

    /// <summary>CreateNew 模式撞已存在文件（ERROR_ALREADY_EXISTS / EEXIST）；Move(overwrite=false) 目标已存在同此码。</summary>
    AlreadyExists,

    /// <summary>
    /// 条件前置失败（对象层 412 PreconditionFailed 语义）：If-Match 失配（对象已被并发替换）/
    /// If-NoneMatch="*" 撞已存在（抢占失败）。fencing 锁与条件写的精确判别码。
    /// </summary>
    PreconditionFailed,

    /// <summary>删除非空目录（Win32 ERROR_DIR_NOT_EMPTY=145 / ENOTEMPTY=39）——根空间 DeleteDirectory 专用判别码。</summary>
    DirectoryNotEmpty,

    /// <summary>
    /// 根空间处于维护态（EnterMaintenance 租约生效中）——被 scope 拒绝的操作专用判别码。
    /// <para>★ 与 <see cref="Unsupported"/>（能力位语义）分离：调用方可将其映射为"维护中"提示而非平台不支持
    /// （raw-medium-and-conversion-design §8.1）。</para>
    /// </summary>
    UnderMaintenance,

    /// <summary>
    /// 卷只读（raw-medium-and-conversion-design §4.1）：显式只读打开 / dirty 降级形态 / 多载体成员缺失
    /// 降级卷上的写意图操作专用判别码。
    /// <para>★ 与 <see cref="AccessDenied"/>（权限问题）分离：调用方可精确映射"只读卷"提示并给出
    /// 修复指引（全量成员重开等），不与宿主权限失败混淆。</para>
    /// </summary>
    ReadOnlyVolume,
}
