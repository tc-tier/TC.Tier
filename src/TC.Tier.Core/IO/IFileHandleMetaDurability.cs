namespace TC.Tier.Core.IO;

/// <summary>
/// 元数据耐久化分级能力（可选实现）——FileExtra 平面的写入耐久性二档：
/// <para>★ <see cref="IFileHandle.SetFileExtra"/> = 持久写（写入即 fsync——读己写 + 掉电耐久；
///   churn 卷上 fsync 延迟无界，仅限低频锚点）。</para>
/// <para>★ <see cref="SetFileExtraCached"/> = 缓存写（写入即读——缓存管理器读己写恒成立；
///   掉电窗口内可能丢失，耐久化由调用方锚点/回扫泵显式刷盘——高频生命周期写专用）。</para>
/// <para>★ 未实现本接口的介质：SetFileExtra 即唯一形态（mem 无 fsync 成本、Remote 无本地刷盘概念）。</para>
/// </summary>
internal interface IFileHandleMetaDurability
{
    /// <summary>缓存写 FileExtra（写入即读；不 fsync——耐久化由锚点/回扫泵负责）。</summary>
    void SetFileExtraCached(ReadOnlyMemory<byte> extra);

    /// <summary>刷盘缓存中的 FileExtra 数据（不重写内容——把已缓存写落盘持久化）。</summary>
    void FlushFileExtra();
}
