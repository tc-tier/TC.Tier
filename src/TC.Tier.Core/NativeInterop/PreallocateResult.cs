namespace TC.Tier.Core.NativeInterop;

/// <summary>
/// 预分配结果。
/// </summary>
public enum PreallocateResult
{
    /// <summary>真实物理分配成功（磁盘块已预留，写性能最优）。</summary>
    RealAlloc,
    /// <summary>降级为稀疏文件（逻辑大小已设但未真实分配，写时按需分配）。</summary>
    SparseFallback,
    /// <summary>完全失败（预分配 best-effort，调用方应继续运行）。</summary>
    Failed,
}