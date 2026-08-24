namespace TC.Tier.Core.IO;

/// <summary>
/// 打开共享轴——同文件并发打开约束（BCL <see cref="FileShare"/> 对应）。
/// <para>★ POSIX 无原生对应，映射为 advisory 锁语义（平台差异文档化）。</para>
/// <para>★ 保护边界（advisory 本质）：仅约束同进程内经同一 fs 实例打开的句柄——
///   外部进程 / 绕过本层的原生 IO 不受保护；跨进程互斥用卷锁（IFileSystem.AcquireExclusive）。</para>
/// </summary>
[Flags]
public enum FileSharing
{
    /// <summary>拒绝后续共享（独占）。</summary>
    None = 0,

    /// <summary>允许后续只读共享。</summary>
    Read = 1,

    /// <summary>允许后续只写共享。</summary>
    Write = 2,

    /// <summary>允许读写共享。</summary>
    ReadWrite = Read | Write,

    /// <summary>允许删除共享（POSIX/mem 上删除本就无条件成功，此位无观察效应）。</summary>
    Delete = 4,
}