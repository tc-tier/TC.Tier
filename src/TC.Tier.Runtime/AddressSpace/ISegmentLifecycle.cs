namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段生命周期接口：用于管理段的创建和满时的回调。
/// </summary>
public interface ISegmentLifecycle
{
    /// <summary>
    /// 创建段：创建一个新段。★异步。
    /// </summary>
    /// <param name="segId">段 ID</param>
    /// <param name="growthLimit">段增长限制</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>返回段是否创建成功</returns>
    ValueTask<bool> CreateSegmentAsync(int segId, long growthLimit, CancellationToken ct = default);

    /// <summary>
    /// 段满：段满时的回调。★异步。
    /// </summary>
    /// <param name="segId">段 ID</param>
    /// <param name="finalSize">段最终大小</param>
    /// <param name="growthLimit">段增长限制</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>返回一个表示操作完成的任务</returns>
    ValueTask OnSegmentFullAsync(int segId, long finalSize, long growthLimit, CancellationToken ct = default);
}