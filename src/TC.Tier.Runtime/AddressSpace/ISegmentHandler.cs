namespace TC.Tier.Runtime.AddressSpace;

/// <summary>
/// 段处理器——段的事件模型接口（AddressSpace 主类实现）。
/// <para>★ SegmentTable 经此接口触发段生命周期事件 + 提交低频后台任务，不直接依赖主类 /
///   LifecycleCoordinator / ISegmentLifecycle——依赖倒置消除反向依赖。</para>
/// <para>★ 主类实现此接口做两件事：① 段生命周期事件适配设备层 ISegmentLifecycle（带优先级队列入队）
///   ② 低频后台任务由 worker 顺序执行。</para>
/// <para>★ 两类回调的区分：</para>
/// <list type="bullet">
/// <item><description>段生命周期事件（OnXXX）：需要设备层（建/满/删/替换物理段），高频，需要优先级</description></item>
/// <item><description>后台任务（<see cref="SubmitBackgroundWork"/>）：段表自洽的纯内存工作（如区间表压缩），
///   低频，顺序执行不需要优先级</description></item>
/// </list>
/// <para>★ 单实现 + GDV（Guarded Devirtualization）→ JIT 内联为直接调用，零间接开销。</para>
/// </summary>
public interface ISegmentHandler
{
    /// <summary>段创建事件——物理段待建（主类适配 ISegmentLifecycle.CreateSegmentAsync）。</summary>
    /// <param name="segId">段号。</param>
    /// <param name="growthLimit">段生长上限（字节，建物理段必须知道多大）。</param>
    /// <param name="isHighPriority">true=高优先级（Allocate 缺段强制建，lease 第一阶段等就绪）；
    ///   false=普通优先级（段满时预建下一段、恢复时建段）。</param>
    void OnSegmentCreate(int segId, long growthLimit, bool isHighPriority);

    /// <summary>段满事件——通知设备层段已满（主类适配 ISegmentLifecycle.OnSegmentFullAsync）。</summary>
    /// <param name="segId">段号。</param>
    /// <param name="finalSize">段满时的最终大小（字节）。</param>
    /// <param name="growthLimit">段生长上限（字节）。</param>
    void OnSegmentFull(int segId, long finalSize, long growthLimit);

    /// <summary>段删除事件——整段物理删除（ReclaimHead 整段回收触发，主类适配 ISegmentLifecycle.DeleteSegmentAsync）。</summary>
    /// <param name="segId">段号。</param>
    void OnSegmentDelete(int segId);

    /// <summary>段替换事件——Compact 用新参数重建段（主类重建物理段 + 替换槽位）。</summary>
    /// <param name="segId">段号。</param>
    /// <param name="growthLimit">新段生长上限（字节）。</param>
    /// <param name="maxOffset">新段最大有效偏移（字节）。</param>
    void OnSegmentReplace(int segId, long growthLimit, long maxOffset);

    /// <summary>
    /// 段回收事件——ReclaimXXX 回收段时触发，通知设备层回收段的物理空间（主类适配 ISegmentLifecycle.ReclaimSegmentAsync）。
    /// </summary>
    /// <param name="segId">段号。</param>
    /// <param name="from">回收起始偏移（字节）。</param>
    /// <param name="to">回收结束偏移（字节）。</param>
    /// <param name="growthLimit">段生长上限（字节）。</param>
    void OnSegmentReclaim(int segId,long from,long to, long growthLimit);
    /// <summary>
    /// 提交低频后台任务——段表自洽的纯内存工作（如区间表压缩），主类 worker 异步顺序执行。
    /// <para>★ 低频任务不需要优先级区分——按提交顺序处理。</para>
    /// <para>★ 不阻塞热路径：段表构建完整工作单元后立即返回，worker 在后台执行。</para>
    /// </summary>
    /// <param name="work">完整的工作单元（段表闭包捕获所需状态，主类只负责执行）。</param>
    void SubmitBackgroundWork(Action work);
}
