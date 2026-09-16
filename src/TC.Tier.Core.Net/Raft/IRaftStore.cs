using System.Buffers;

namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// raft 引擎对持久化的全部需求面（spec-12 §8.1——从引擎消费面提取的端口；实现 =
/// 适配器（组装层 TierWAL）/ 夹具（测试项目 InMemoryRaftStore·FsRaftStore）。
/// Core.Net 零存储自有——数据持久化端口唯一。
/// <para>★ 条目 = (Term, Kind, Content) 三元组（wire v3——kind 升格结构字段，信封格式消灭；
/// Kind 取值 <see cref="RaftEntryKind"/>）——日志帧格式知识归适配器（引擎与存储互不泄露布局）。</para>
/// <para>★ 双水位模型：AllocatedIndex = 追加即分配（含未持久化窗口）；
///   PersistedIndex = 已持久化尾（<see cref="WaitForPersistedAsync"/> 推进——fsync 时机由协议层控制，
///   应答前同步点）。</para>
/// <para>★ 不变量：term 单调不降；<see cref="AppendAsync"/> 的 prevIndex 为前置断言
///   （冲突回退一体——prevIndex &lt; 当前尾 = 截断后重写）；快照区（≤ SnapshotIndex）不可回写。</para>
/// </summary>
public interface IRaftStore
{
    /// <summary>当前任期（启动恢复）。</summary>
    long Term { get; }

    /// <summary>本任期投票对象（Empty = 未投；启动恢复）。</summary>
    NodeId VotedFor { get; }

    /// <summary>已应用 index（apply 管道推进——重启从 applied+1 重放）。</summary>
    long AppliedIndex { get; }

    /// <summary>日志尾 index（含未持久化——选举新鲜度裁决输入）。</summary>
    long LastLogIndex { get; }

    /// <summary>日志尾 term（空日志 = 0）。</summary>
    long LastLogTerm { get; }

    /// <summary>已分配尾（含未持久化窗口）。</summary>
    long AllocatedIndex { get; }

    /// <summary>已持久化尾（本地 fsync 水位——协议层据此应答/推进 matchIndex）。</summary>
    long PersistedIndex { get; }

    /// <summary>快照覆盖点 N₀（0 = 无快照；index ≤ 此值信任快照）。</summary>
    long SnapshotIndex { get; }

    /// <summary>启动恢复（幂等——读 meta + 重建日志索引/尾缓存）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask InitializeAsync(CancellationToken ct = default);

    /// <summary>原子更新 (term, votedFor) 并持久化（抬任期/授权投票/降级清 votedFor 共用——应答前落盘）。</summary>
    /// <param name="term">任期（单调不降——回退抛）。</param>
    /// <param name="votedFor">投票对象（Empty = 清除）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask WriteTermAndVoteAsync(long term, NodeId votedFor, CancellationToken ct = default);

    /// <summary>原子更新 appliedIndex 并持久化（apply 管道节流调用）。</summary>
    /// <param name="index">已应用 index。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask UpdateAppliedIndexAsync(long index, CancellationToken ct = default);

    /// <summary>
    /// 批追加（prevIndex = 前置断言）：prevIndex == 当前尾 = 纯追加；
    /// prevIndex &lt; 当前尾 = 截断 (prevIndex, 尾] 后追加（冲突回退一体）；
    /// prevIndex &gt; 当前尾或 &lt; 快照点 = 违规抛。
    /// </summary>
    /// <param name="prevIndex">前置 index（断言匹配点）。</param>
    /// <param name="entries">条目（(Term, Kind, Content) 三元组）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>新日志尾 index。</returns>
    ValueTask<long> AppendAsync(long prevIndex, IReadOnlyList<(long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default);

    /// <summary>读条目（快照区外；越界/已截除 = false）。</summary>
    /// <param name="index">条目 index。</param>
    /// <param name="term">条目 term。</param>
    /// <param name="kind">条目种类（<see cref="RaftEntryKind"/>）。</param>
    /// <param name="content">条目内容。</param>
    /// <returns>false = 无条目。</returns>
    bool TryGetEntry(long index, out long term, out byte kind, out ReadOnlyMemory<byte> content);

    /// <summary>顺序扫描（快照区外；fromIndex 越界自动收敛到快照点后）。</summary>
    /// <param name="fromIndex">起始 index。</param>
    /// <param name="maxCount">至多产出条数。</param>
    /// <returns>条目序列。</returns>
    IEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> Scan(long fromIndex, int maxCount);

    /// <summary>顺序流读（apply/重放路径——从 fromIndex 起逐条产出至日志尾，快照区外）。
    /// ★ 复制热路径不使用本方法（<see cref="WriteEntriesToAsync"/> 直写帧缓冲）。</summary>
    /// <param name="fromIndex">起始 index。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>条目流。</returns>
    IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadEntriesAsync(long fromIndex, CancellationToken ct = default);

    /// <summary>
    /// 可复制条目计数（复制热路径前置——fromIndex 起连续已持久化条目数，至多 maxCount）。
    /// </summary>
    /// <param name="fromIndex">起始 index（须 &gt; SnapshotIndex 且 ≤ PersistedIndex；越界 = 0）。</param>
    /// <param name="maxCount">至多计数条数。</param>
    /// <returns>可读条数（0 = 无——滞后判定与空读护栏的判据）。</returns>
    int CountEntries(long fromIndex, int maxCount);

    /// <summary>
    /// 条目区直写（复制热路径专用——存储直写 RPC 帧缓冲，单拷贝存储→帧）：
    /// 把 [fromIndex, fromIndex+写入数) 的条目按 <see cref="RaftEntriesRegion"/> 布局
    /// （[Count 4B][×N: 固定头 13B + Content]，计数前缀由实现写入）写入 destination，
    /// term 逐条回填 termsOut。
    /// <para>★ 写入数 = min(count, PersistedIndex - fromIndex + 1)（只读已持久化区）；
    /// 返回实际写入条数。fromIndex 越界（≤ 快照点 / 超持久化尾）= 防御性返回 0
    /// （零写入——调用方空读护栏回挂自愈，不抛）。</para>
    /// </summary>
    /// <param name="fromIndex">起始 index。</param>
    /// <param name="count">至多写入条数。</param>
    /// <param name="destination">帧缓冲视图（AppendEntries 条目区）。</param>
    /// <param name="termsOut">term 回填数组（长度 ≥ 写入数——async 域用数组非 Span）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际写入条数（0 = 无可写）。</returns>
    ValueTask<int> WriteEntriesToAsync(long fromIndex, int count, IBufferWriter<byte> destination, long[] termsOut, CancellationToken ct = default);

    /// <summary>
    /// prevLog 匹配校验（复制应答裁决）：prevLogIndex == 0 → 匹配；
    /// ≤ SnapshotIndex → 信任快照；&gt; PersistedIndex → 不匹配（未持久化 = 重启即丢）；
    /// 其余读主数据该 index 处 term 比对。
    /// </summary>
    /// <param name="prevLogIndex">前置 index。</param>
    /// <param name="prevLogTerm">前置 term。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 匹配。</returns>
    ValueTask<bool> PrevLogMatchesAsync(long prevLogIndex, long prevLogTerm, CancellationToken ct = default);

    /// <summary>读指定 index 处条目 term（<see cref="SnapshotIndex"/> ≤ index ≤ 尾——越界抛）。
    /// ★ <see cref="SnapshotIndex"/> 边界（index == N₀）可读：返回快照边界条目 term——复制 lane 的
    /// 边界 prev（prevLogIndex == N₀）必须携带真 term，发 0 会被无快照 follower 的 term 比对拒绝
    /// （hint 退→安装→换届风暴）。</summary>
    /// <param name="index">条目 index（含快照边界 N₀）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>条目 term。</returns>
    ValueTask<long> ReadLogTermAsync(long index, CancellationToken ct = default);

    /// <summary>截尾（冲突修正）：删除 [indexInclusive, 尾]；不可删进快照区。</summary>
    /// <param name="indexInclusive">起始删除 index。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask TruncateSuffixFromAsync(long indexInclusive, CancellationToken ct = default);

    /// <summary>截头（快照/压缩）：删除 (SnapshotIndex, indexInclusive]；indexInclusive ≤ 快照点幂等。</summary>
    /// <param name="indexInclusive">删除至该 index（含）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask TruncatePrefixToAsync(long indexInclusive, CancellationToken ct = default);

    /// <summary>
    /// witness 高水位断言推进（二期-F2——DDR-F2 索引断言流）：Leader 声称其日志前沿为
    /// (index, term)——仅当 index 严格高于当前高水位且 term ≥ 记录任期时推进并持久化
    /// 高水位对，返回 true（不回退——单调性是投票安全性论证的组成）。
    /// </summary>
    /// <param name="index">Leader 日志前沿 index。</param>
    /// <param name="term">Leader 任期（高水位记录的任期口径）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>true = 已推进；false = 未推进（落后/同位/任期旧）。</returns>
    ValueTask<bool> AssertHighWatermarkAsync(long index, long term, CancellationToken ct = default);

    /// <summary>等待 index 已持久化（推进 fsync 水位——协议层应答前的同步点）。</summary>
    /// <param name="index">目标 index。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask WaitForPersistedAsync(long index, CancellationToken ct = default);

    /// <summary>
    /// 快照条目流读（spec-03 快照传输源——[1..snapshotIndex] 含快照区；snapshotIndex=0 = 空流）。
    /// 条目 = (Index, Term, Kind, Content) 四元组（快照传输帧由传输面编——存储零格式知识）。
    /// </summary>
    /// <param name="snapshotIndex">快照覆盖点（≤ 当前 <see cref="SnapshotIndex"/> 区间幂等读取；调用方以当前快照点调用）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>条目流（顺序产出）。</returns>
    IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> ReadSnapshotEntriesAsync(long snapshotIndex, CancellationToken ct = default);

    /// <summary>
    /// 快照导入（spec-03 follower 侧——重建 [1..snapshotIndex] 快照区 + 重锚）：
    /// 清当前全部日志 → 逐帧消费导入条目流（消费面 O(单帧) 驻留——传输面逐帧供给；
    /// 实现内部工作集形态自定）→ SnapshotIndex=snapshotIndex、Allocated/Persisted=snapshotIndex、
    /// LastLogTerm=尾条 term；term/votedFor/applied 不动（任期与投票是选举状态、
    /// applied 由业务重建路径推进）。
    /// </summary>
    /// <param name="snapshotIndex">快照覆盖点（导入后 SnapshotIndex = 此值）。</param>
    /// <param name="entries">快照条目流（[1..snapshotIndex]——index 连续性由调用方保证，实现按序落位）。</param>
    /// <param name="ct">取消令牌。</param>
    ValueTask ImportSnapshotAsync(long snapshotIndex, IAsyncEnumerable<(long Index, long Term, byte Kind, ReadOnlyMemory<byte> Content)> entries, CancellationToken ct = default);

    /// <summary>快照导入后刷新（日志已被重锚——尾缓存与 term 索引整体作废重建）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask RefreshTailAfterImportAsync(CancellationToken ct = default);
}
