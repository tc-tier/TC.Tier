namespace TC.Tier.Core.IO.Mem;

/// <summary>
/// 内存分配模式。
/// <para><see cref="Sparse"/>：真稀疏——按页写时分配，用多少占多少（PunchHole 真释放页归还池）。</para>
/// <para><see cref="Reserved"/>：创建即占——文件单块连续直址（保留现状槽直址语义），Map 零拷贝；
///   建议配 <see cref="MemoryFileSystemOptions.QuotaBytes"/> 形成硬配额。</para>
/// </summary>
public enum MemoryAllocationMode
{
    /// <summary>真稀疏（默认）：页表布局，洞=未分配页读零，物理占用=已写页。</summary>
    Sparse,

    /// <summary>预留：文件创建即租单块连续 buffer——零运行时分配 + 直址/Map 零拷贝；PunchHole 逻辑记账不还物理。</summary>
    Reserved,
}