namespace TC.Tier.Core.IO;

/// <summary>
/// 内存映射视图——生命周期独立于父句柄（Map 私有化底层引用：mem=槽引用计数 / disk=OS 句柄复刻）。
/// <para>★ 铁律：必须 Dispose（其持有独立 OS 句柄/引用，不 Dispose = fd/引用泄漏）。</para>
/// <para>★ Dispose 后访问 <see cref="View"/> 抛 <see cref="ObjectDisposedException"/>（不返回悬垂 Memory）。</para>
/// <para>★ mem Sparse 模式写穿透契约：视图写在 Flush/Dispose 时写回文件（可见时点=Flush/Dispose，非实时）。</para>
/// </summary>
public interface IMappedSection : IDisposable
{
    /// <summary>映射视图内存（磁盘=MMF view；mem Reserved=槽直址零拷贝 / Sparse=物化副本）。</summary>
    Memory<byte> View { get; }

    /// <summary>映射级访问提示（madvise 族；不支持平台 no-op）。</summary>
    /// <param name="advise">访问提示</param>
    void Advise(FileAdvise advise);

    /// <summary>把脏视图写回文件（msync 语义；mem Sparse=脏区间物化写回）。</summary>
    void Flush();
}
