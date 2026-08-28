namespace TC.Tier.Core.IO.Image;

/// <summary>采集/还原结果摘要（进度/审计）。</summary>
/// <param name="EntryCount">清单条目数</param>
/// <param name="FrameCount">数据帧数</param>
/// <param name="RawBytes">原始字节数</param>
public sealed record ImageSummary(long EntryCount, long FrameCount, long RawBytes);