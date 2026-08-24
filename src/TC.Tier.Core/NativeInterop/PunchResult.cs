namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 打洞结果。
/// </summary>
public enum PunchResult
{
    /// <summary>磁盘块已归还文件系统（真物理回收，文件变稀疏）。</summary>
    Punched,
    /// <summary>退化归零（tmpfs/不支持 PunchHole，memset 填零，语义正确但未归还块）。</summary>
    ZeroFilled,
    /// <summary>完全失败（归零也失败）。</summary>
    Failed,
}