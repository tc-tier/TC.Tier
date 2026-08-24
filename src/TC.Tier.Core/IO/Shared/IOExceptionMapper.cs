namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 原生 IO 异常 → <see cref="IOError"/> 分类器（Core/IO 内部共用）。
/// <para>★ 映射规则：优先看异常类型（OCE → Cancelled；权限类 → AccessDenied），
///   再看 HResult（Win32 码 / errno 码 → 语义分类），兜底 Unknown。</para>
/// <para>★ 语义与 Runtime.Storage 旧 IOErrorMapper.Classify 逐条一致（只读参照搬迁）。</para>
/// </summary>
// ReSharper disable once InconsistentNaming
internal static class IOExceptionMapper
{
    /// <summary>把原生异常映射为 <see cref="IOError"/>（不包装，仅分类）。</summary>
    public static IOError Classify(Exception ex) => ex switch
    {
        OperationCanceledException => IOError.Cancelled,
        ArgumentException => IOError.AlignmentError,
        UnauthorizedAccessException => IOError.AccessDenied,
        FileNotFoundException => IOError.NotFound,
        IOException io => ClassifyHResult(io.HResult),
        _ => IOError.Unknown,
    };

    /// <summary>HResult → IOError（Win32 码与 POSIX errno 的交集映射）。</summary>
    public static IOError ClassifyHResult(int hr)
    {
        // Win32 HResult 高 16 位 = facility，低 16 位 = code
        // 取低 16 位，与 POSIX errno 统一比较
        var code = hr & 0xFFFF;

        // Win32 ERROR_*（2 = FILE_NOT_FOUND, 5 = ACCESS_DENIED, 112 = DISK_FULL, 32 = SHARING_VIOLATION, 1117 = IO_DEVICE）
        // POSIX errno（2 = ENOENT, 13 = EACCES, 28 = ENOSPC, 16 = EBUSY, 5 = EIO）
        return code switch
        {
            2 => IOError.NotFound, // ERROR_FILE_NOT_FOUND / ENOENT
            5 => hr >> 16 == 0
                ? IOError.IOFailure // POSIX EIO（facility=0）
                : IOError.AccessDenied, // Win32 ERROR_ACCESS_DENIED（facility=7）
            11 => IOError.SharingViolation, // EAGAIN/EWOULDBLOCK（Unix：BCL 进程内 FileShare 冲突 / flock LOCK_NB 被占的统一形态）
            13 => IOError.AccessDenied, // EACCES
            16 => IOError.SharingViolation, // EBUSY
            17 => IOError.AlreadyExists, // EEXIST（CreateNew 已存在）
            28 => IOError.DiskFull, // ENOSPC
            32 => IOError.SharingViolation, // ERROR_SHARING_VIOLATION
            80 => IOError.AlreadyExists, // ERROR_FILE_EXISTS
            87 => IOError.AlignmentError, // ERROR_INVALID_PARAMETER（NO_BUFFERING 未对齐）
            112 => IOError.DiskFull, // ERROR_DISK_FULL
            183 => IOError.AlreadyExists, // ERROR_ALREADY_EXISTS
            1117 => IOError.IOFailure, // ERROR_IO_DEVICE
            _ => IOError.Unknown,
        };
    }

    /// <summary>
    /// 包装为 <see cref="FileIOException"/>（附错误分类、操作名与路径）——Core/IO 实现的统一 catch 出口。
    /// </summary>
    public static FileIOException Wrap(this Exception ex, string operation, string? path = null)
        => new(Classify(ex), $"{operation} failed: {ex.Message}", path, operation, ex);
}
