namespace TC.Tier.Core.IO;

/// <summary>
/// 日志化卷能力面（V2 §1.2 增量导出管线对接——TCA1 新帧族「delta 帧」的源/目标判据）。
/// <para>由 TierVolumeFs 实现；管线（<see cref="Image.RootSpaceImage"/>）经此面完成增量导出/还原——
/// 与 <see cref="Image.IContiguousVolume"/>（dd 快道判据）同族：能力位诚实、无能力即诚实拒绝。</para>
/// </summary>
internal interface IJournaledVolume
{
    /// <summary>增量导出（baseLsn 起至已提交头的记录流——须日志卷；基点过旧/超前即拒）。</summary>
    /// <param name="output">输出流（管线提供的 IAsyncTransferWriter）</param>
    /// <param name="baseLsn">增量基点 LSN（导出起点）。</param>
    /// <returns>增量导出摘要（清单条目数 + 数据帧数 + 原始字节数）。</returns>
    TierVolume.SnapshotDeltaSummary ExportDeltaTo(Stream output, ulong baseLsn);

    /// <summary>增量还原（同基线卷重放——基线校验 + 逐记录重放 + 检查点收口）。</summary>
    /// <param name="input">输入流（管线提供的 IAsyncTransferReader）</param>
    /// <returns>增量还原摘要（清单条目数 + 数据帧数 + 原始字节数）。</returns>
    TierVolume.SnapshotDeltaSummary ApplyDeltaFrom(Stream input);
}
