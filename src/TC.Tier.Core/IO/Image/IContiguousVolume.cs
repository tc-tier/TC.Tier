namespace TC.Tier.Core.IO.Image;

/// <summary>
/// 连续卷原始访问契约（dd 快道的介质侧入口——raw-medium-and-conversion-design §6.2）。
/// <para>★ 置位 <see cref="FileSystemCapabilities.ContiguousCapture"/> 的介质实现本接口，
///   暴露整卷原始字节视图（TierVolume 介质两形态：.tier 文件 / 块设备；多载体卷 = 逐载体段，§3.8）。</para>
/// <para>★ 契约铁律：实现必须保证调用方持有本实例的维护租约（<see cref="IFileSystem.EnterMaintenance"/>）——
///   载体访问不出实例、唯一性无例外（§2.4）。管线侧（<c>RootSpaceImage.Transfer</c>）负责进出租约。</para>
/// </summary>
internal interface IContiguousVolume
{
    /// <summary>
    /// 打开整卷原始字节视图（顺序拷贝用）。<paramref name="writable"/>=false 用于源端导出，
    /// true 用于目标端写入（写入前目标卷应为空白/将被整体覆盖的镜像目标）。
    /// </summary>
    /// <param name="writable">是否可写</param>
    /// <returns>整卷原始字节流</returns>
    Stream OpenVolumeBacking(bool writable);

    /// <summary>
    /// 镜像完成后重载（字节镜像覆盖了盘上状态——实例内存元数据须从盘重建。
    /// 管线在维护租约内于拷贝后调用；既有句柄此后引用过期条目——镜像目标不应有活跃句柄（文档化契约）。
    /// </summary>
    void OnMirrorCompleted();
}
