namespace TC.Tier.Runtime.Storage.Compact;

/// <summary>
/// Compact 整理子系统——把散落/有空洞的段搬迁到紧凑新段，回收旧段。
/// <para>★ 只依赖契约对象：<see cref="CompactLease"/>（含 Chunks——每段带旧段信息）+
///   <see cref="IFileHandle"/>（IO 子系统）。<b>不认识 <see cref="SegmentTable"/>、不认识
///   <see cref="StorageEngine"/></b>——搬迁所需信息全在 lease 中，无需穿透查段表。</para>
/// <para>★ 一律后台句柄形态：<see cref="Compact"/> / <see>
///         <cref>RangeCompact</cref>
///     </see>
///     返回
///   <see cref="IAsyncOperation{TResult}"/>——0 等待；等待/取消/超时由调用方控制
///   （<c>await op.WaitAsync(ct)</c> / <c>op.Cancel()</c> / 事件订阅）。</para>
/// <para>★ 失败现场保留 + marker 续传：Phase 2 失败保留（marker + 临时文件）；
///   <see cref="Recover"/>（启动恢复）/ <see cref="Retry"/>（运行时续传）从 marker 补执行
///   promote + 段表替换 + 删 marker——零重拷贝。</para>
/// </summary>
public interface ICompact : IDisposable, IAsyncDisposable
{
    /// <summary>当前子系统状态。</summary>
    CompactStatus Status { get; }

    /// <summary>当前是否有整理在跑。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 启动一次整理（批量 lease 原子完成——排他：未完成不允许重复 Compact）。
    /// </summary>
    /// <param name="leases">一个或多个 Compact lease（Chunks 携带旧段信息，无需查段表；
    ///   使用方填 <see cref="CompactChunk.SetReplacement"/> 或 <see cref="CompactChunk.MarkInvalid"/> 标记处置）。</param>
    /// <returns>操作句柄（Cancel / WaitAsync / 进度/完成/失败事件）。
    /// ★ 仅 Phase 1（拷贝）可取消；Phase 2（rename 后）已开始提交，不可取消——否则段表不一致。</returns>
    IAsyncOperation<CompactResult> Compact(params CompactLease[] leases);

    /// <summary>
    /// 启动区间 Compact（带地址翻译的磁盘碎片整理）——把 [from,to) 内的有效数据压实，
    /// 消除碎片间隙，压实区末尾到 to 之间 PunchHole 归还（连续空洞）。
    /// <para>★ <paramref name="addresses"/> 是上层需翻译的地址集合——每个不同的请求地址都进入
    ///   MigrationMap；allocated 地址映射到新地址，hole/不存在/区间外地址映射到 null。</para>
    /// <para>★ 区间外（from 前、to 后）数据原位不动；CommittedTail 不退。</para>
    /// </summary>
    /// <param name="lease">整理区间 lease（引擎造——排他占住 [from 段@0, 尾段 GrowthLimit]，数据窗 [from,to)）。</param>
    /// <param name="from">整理范围起始（含）。</param>
    /// <param name="to">整理范围结束（不含）。</param>
    /// <param name="addresses">需翻译的地址集合（段内偏移量，与长度无关）。</param>
    /// <returns>后台操作句柄——成功携带 <see cref="CompactResult"/>（NewLow/NewHigh WaterMark + MigrationMap）。</returns>
    IAsyncOperation<CompactResult> RangeCompact(
        CompactLease lease,
        LogicalAddress from,
        LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses);

    /// <summary>
    /// 申报活区间版 <see>
    ///     <cref>RangeCompact</cref>
    /// </see>
    /// ——上层使用方申报 [from,to) 内的活区间，搬迁规划按申报执行。
    /// <para>★ 未申报的已分配区间视为洞不搬迁（A8 信任模型：记录粒度真相只在使用方）。</para>
    /// </summary>
    /// <param name="lease">整理区间 lease（同无 livePlan 重载）。</param>
    /// <param name="from">整理范围起始（含）。</param>
    /// <param name="to">整理范围结束（不含）。</param>
    /// <param name="addresses">需翻译的地址集合（段内偏移量，与长度无关）。</param>
    /// <param name="livePlan">段 → 申报活区间（段内文件偏移），null/缺段 = 该段回退物理枚举。</param>
    /// <returns>后台操作句柄——成功携带 <see cref="CompactResult"/>（NewLow/NewHigh WaterMark + MigrationMap）。</returns>
    IAsyncOperation<CompactResult> RangeCompact(
        CompactLease lease,
        LogicalAddress from,
        LogicalAddress to,
        IReadOnlyList<LogicalAddress> addresses,
        IReadOnlyDictionary<int, List<(long Start, long End)>>? livePlan);

    /// <summary>
    /// 崩溃恢复——引擎重启后补执行未完成的 Compact（marker 记录的 Phase 2 崩溃）：
    /// 临时文件 → promote → 段表替换 → 删 marker。
    /// </summary>
    /// <param name="leaseFactory">lease 构造委托（基类按 marker 区间造新 lease）。</param>
    void Recover(CompactLeaseFactory leaseFactory);

    /// <summary>
    /// 运行时失败续传——从 marker 补执行上次失败的 Compact（与 <see cref="Recover"/> 同核心，
    /// 零重拷贝）。区别仅在触发时机：Recover = 启动恢复；Retry = 同进程内运行时失败
    /// （引擎 op.Failed 分流，句柄冲突关句柄后调用）。
    /// <para>★ marker 不存在（Phase 1 失败已清理现场）= no-op，调用方重新发起 Compact。</para>
    /// </summary>
    /// <param name="leaseFactory">lease 构造委托（同 <see cref="Recover"/>）。</param>
    void Retry(CompactLeaseFactory leaseFactory);

    /// <summary>
    /// 清除任务——放弃当前整理，回到空闲状态（失败后选择"不恢复、不重试，直接放弃"的场景）。
    /// </summary>
    void Clear();
}
