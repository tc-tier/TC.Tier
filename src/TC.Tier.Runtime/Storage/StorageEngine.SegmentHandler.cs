namespace TC.Tier.Runtime.Storage;

internal sealed partial class StorageEngine
{
    /// <summary>
    /// 默认段处理器——<see cref="ISegmentHandler"/> 显式接口委托给引擎实例。
    /// </summary>
    /// <param name="owner">引擎实例</param>
    private sealed class DefaultSegmentHandler(StorageEngine owner) : ISegmentHandler
    {
        public void OnSegmentCreate(int segId, long growthLimit, bool isHighPriority)
        {
            // ★ 未注册的前置通知（ExtentLease 段满时预建下一段）= 纯预建提示——只喂预备池
            //   （lookahead 的本职），不入队正式 Create。正式任务只服务已注册段：未注册段的
            //   物理构建无处 callback 转正、又不在池里 → 注册时的正式 Create 必然重建
            //   （确定性双建：容量双计数/句柄覆盖/重复 meta 写，2026-08-16 建段日志实测 segId=1）。
            if (!owner._segmentTable.TryGetSegment(segId, out var view) || view is not { IsValid: true })
            {
                owner.ReplenishSegmentPool();
                return;
            }

            // ★ IO 层预备池命中（lookahead）——物理段现成（预建时 IO+meta+容量计数全部完成）：
            //   同步转正（Empty→Written 立即完成），写者零等待（WaitSegmentReady 窗口消失）。
            if (owner.TryConsumePooledSegment(segId))
            {
                owner._segmentTable.CreateSegmentCallback(segId, success: true);
                owner.ReplenishSegmentPool();   // 随取随补（用一个消一个）
                return;
            }

            // ★ 未命中（池空/首段/预建未完成）——走正式异步建段（worker 物理建段 + 回调段表解除 Empty（物理门开））。
            //   isHighPriority=true（Allocate 缺段强制建，lease 第一阶段等就绪）用 Critical；普通预建用 High。
            owner. _workerLoop.Enqueue(new WorkLoopItemTask
            {
                Event = SegmentWorkEvent.Create,
                SegId = segId,
                GrowthLimit = growthLimit,
            }, isHighPriority ? WorkerPriority.Critical : WorkerPriority.High);
            owner.ReplenishSegmentPool();       // 池空也补（下一次命中）
        }

        public void OnSegmentFull(int segId, long finalSize, long growthLimit)
        {
            // ★ 段满事件入队——worker 更新段 meta（maxOffset 定格、state=Full）。
            owner. _workerLoop.Enqueue(new WorkLoopItemTask
            {
                Event = SegmentWorkEvent.Full,
                SegId = segId,
                FinalSize = finalSize,
                GrowthLimit = growthLimit,
            }, WorkerPriority.Normal);
            // ★ 段满通知 → IO 层提前补池（尾段之后的现成段保持 N 个——写者到段边界时零等待）
            owner.ReplenishSegmentPool();
        }

        public void OnSegmentDelete(int segId)
        {
            // ★ 仅事件通知——段表自洽管理段状态，引擎侧物理段删除由 Compact/Reclaim 子系统直接处理，handler 无需动作。
        }

        public void OnSegmentReplace(int segId, long growthLimit, long maxOffset)
        {
            // ★ 仅事件通知——Compact 重建段由 Compact 子系统自管，handler 无需动作。
        }

        public void OnSegmentReclaim(int segId, long from, long to, long growthLimit)
        {
            // ★ 仅事件通知——Reclaim 回收由 Reclaim 子系统自管，handler 无需动作。
        }

        public void SubmitBackgroundWork(Action work)
        {
            // ★ 低频段表自洽任务入队（顺序执行，不需要优先级区分）。
            owner. _workerLoop.Enqueue(new WorkLoopItemTask
            {
                Event = SegmentWorkEvent.Background,
                BackgroundWork = work,
            }, WorkerPriority.Normal);
        }
    }
}