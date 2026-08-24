namespace TC.Tier.Runtime.Storage;

internal sealed partial class StorageEngine
{
    /// <summary>
    /// 运行期后台 worker loop 任务类型——承载段生命周期事件（Create/Full）或低频后台任务（Background）。
    /// </summary>
    private enum SegmentWorkEvent : byte
    {
        /// <summary>建物理段（OnSegmentCreate 触发）。</summary>
        Create,

        /// <summary>段满通知（OnSegmentFull 触发）——更新段 meta。</summary>
        Full,

        /// <summary>低频后台任务（SubmitBackgroundWork 触发）——段表自洽的纯内存工作。</summary>
        Background,
    }

   /// <summary>
   /// 运行期后台 worker loop 任务项——承载段生命周期事件（Create/Full）或低频后台任务（Background）。
   /// </summary>
    private struct WorkLoopItemTask
    {
        /// <summary>事件类型（决定分发到哪个段操作）。</summary>
        public required SegmentWorkEvent Event { get; init; }

        /// <summary>段号。</summary>
        public int SegId { get; init; }

        /// <summary>段生长上限（Create/Full 用）。</summary>
        public long GrowthLimit { get; init; }

        /// <summary>段满时的最终大小（Full 用）。</summary>
        public long FinalSize { get; init; }

        /// <summary>低频后台任务回调（Background 用）。</summary>
        public Action? BackgroundWork { get; init; }
    }

    /// <summary>
    /// 运行期后台 worker loop——默认实现空循环（内存/null 引擎用）。
    /// </summary>
    /// <param name="owner">存储引擎实例</param>
    /// <param name="logger">日志记录器</param>
    private sealed class DefaultEngineWorkerLoop(
        StorageEngine owner,
        ILogger? logger,
        int consumerCount)
        : BackgroundWorkerLoop<WorkLoopItemTask>(
            owner._workerScheduler,
            consumerCount, name: nameof(StorageEngine), logger: logger)
    {
        private readonly ILogger? _logger = logger;

        protected override ValueTask ProcessItemAsync(WorkLoopItemTask item, CancellationToken ct)
        {
            switch (item.Event)
            {
                case SegmentWorkEvent.Create:
                    // ★ 池取用/声明 single-flight（与池补建互斥防双建——审计）
                    //   → 建物理段 → 成败都回调段表（CreateSegmentCallback 解除 Empty（物理门开），唤醒等段的写路径）。
                    owner.EnsureSegmentPhysical(item.SegId, item.GrowthLimit, ct);
                    break;
                case SegmentWorkEvent.Full:
                    // ★ 段满：更新段 meta（Compact 范围内的段 OnSegmentFullCoreAsync 内部自跳过）。
                    try
                    {
                        // ★ Compact 范围内的段跳过——避免遗留 Full 任务占用 Compact 正在 rename 的文件。
                        //   底层地址分配器/worker 完全正常，这里只是 ISegmentLifecycle 实现做业务上下文区分。
                        if (owner.IsSegmentUnderCompact(item.SegId))
                        {
                            _logger?.LogDebug("OnSegmentFullAsync seg#{SegId} 跳过：段在 Compact 范围内（遗留 Full 任务，Compact 负责新段 meta）", item.SegId);
                            return ValueTask.CompletedTask;
                        }
                        // ★ 段元组内联直写（FileExtra 同步强一致）——extension 携带段区间摘要（VII-3 保真，预算化编码）。
                        owner.WriteSegmentTuple(item.SegId, StableState.Full, maxOffset: item.FinalSize, growthLimit: item.GrowthLimit,
                            realSize: item.FinalSize, owner.EncodeExtentSummary(item.SegId));
                    }
                    catch (Exception ex)
                    {
                        owner.Logger?.LogError(ex, "OnSegmentFull 处理失败 segId={SegId}", item.SegId);
                    }

                    break;
                case SegmentWorkEvent.Background:
                    // ★ 低频段表自洽任务（如区间表压缩）——直接执行（不依赖引擎段操作）。
                    try
                    {
                        item.BackgroundWork?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "SubmitBackgroundWork 执行异常");
                    }

                    break;
            }
            return ValueTask.CompletedTask;
        }
    }
}