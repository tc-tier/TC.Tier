using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Runtime.Structures.Log;

namespace TC.Tier.Products.Wal;

/// <summary>组提交追加结果——raft 消费面：分配的 [起始 index, 条数]。</summary>
/// <param name="StartIndex">本批分配的起始 index。</param>
/// <param name="Count">本批追加的条数。</param>
public readonly record struct WalAppendResult(long StartIndex, int Count);

/// <summary>重放产出的一条 entry——(index, 内容)。</summary>
/// <param name="Index">该条 entry 的日志 index。</param>
/// <param name="Data">entry 内容（一帧 payload）。</param>
public readonly record struct WalEntry(long Index, ReadOnlyMemory<byte> Data);

/// <summary>
/// ★ 批追加租约（ref struct——<see cref="ITierWal.BeginAppendBatch"/> 产出，using 包裹）：
/// 逐条 <see cref="WalAppendLease.Append"/>（返回该条 index，页满 = 底层让渡零阻塞）+ 地址→index
/// 簿记，Dispose 收尾释放。★ <b>绝不跨 await 持有</b>（持 WAL 写锁——ref struct 编译器强制不逃逸）。
/// <para>★ 分层：载荷帧格式知识归调用方（适配器）——lease 只收"一帧 payload"，内容零知识。</para>
/// <para>★ index 簿记宿主权威：index = AllocatedIndex + 1（单写者串行下每条 Append 后
/// AllocatedIndex = 该条 index）；租约零跨 await 可变状态——背压慢路的收尾在 static 助手
/// （只传值）内完成，续体线程安全。</para>
/// </summary>
public ref struct WalAppendLease
{
    private readonly TierWal _owner;
    private EntryLog.AppendBatch _batch;   // EntryLog 批（ref struct 字段——同栈存活）
    private readonly long _startIndex;
    private bool _open;

    /// <summary>内部装配构造（TierWal.BeginAppendBatch 产出）。</summary>
    /// <param name="owner">所属 TierWal 实例。</param>
    /// <param name="batch">底层 EntryLog 批追加句柄。</param>
    /// <param name="startIndex">本批起始 index（= 调用时 AllocatedIndex + 1）。</param>
    internal WalAppendLease(TierWal owner, EntryLog.AppendBatch batch, long startIndex)
    {
        _owner = owner;
        _batch = batch;
        _startIndex = startIndex;
        _open = true;
    }

    /// <summary>本批分配的起始 index（Begin 时刻 = AllocatedIndex + 1——写锁内取，无漂移）。</summary>
    public long StartIndex => _startIndex;

    /// <summary>本批已追加条数（index 簿记宿主权威——AllocatedIndex 停在 StartIndex-1 = 零条）。</summary>
    public int Count
    {
        get
        {
            var allocated = _owner.AllocatedIndex;
            return allocated < _startIndex ? 0 : (int)(allocated - _startIndex + 1);
        }
    }

    /// <summary>
    /// ★ 追加一帧 payload，返回分配的 index。页满 = 底层让渡（异步刷盘、零阻塞）；
    /// 双页都在途 = 背压同步等（诚实阻塞语义——单写者 lane 线程承担）。
    /// </summary>
    /// <param name="payload">一帧 payload（帧格式知识归调用方）。</param>
    /// <returns>该条分配的 index。</returns>
    public long Append(ReadOnlySpan<byte> payload)
    {
        if (!_open) throw new ObjectDisposedException(nameof(WalAppendLease));
        var index = _owner.AllocatedIndex + 1;   // 单写者：上一条已簿记（AllocatedIndex = 上一条 index）
        var addr = _batch.Append(payload);
        _owner.RecordAppended(index, addr);
        return index;
    }

    /// <summary>
    /// ★ 追加一帧 payload（异步轨，ValueTask）：底层页满让渡后同步完成（快路/让渡路 ValueTask
    /// 同步完成——趋零开销）；仅底层背压（双页都在途）真异步。
    /// <para>★ 透传 EntryLog 批 AppendAsync——底层没有的异步不加（假异步禁止）。租约本体不跨 await：
    /// 背压慢路的 await 发生在底层批内部（宿主侧 Exit→await→Re-Enter + 写锁交还孤儿认领协议），
    /// 本租约的下一个 Append/Dispose 在当前线程安全认领。</para>
    /// </summary>
    /// <param name="payload">一帧 payload（帧格式知识归调用方）。</param>
    /// <param name="ct">取消令牌（写入前检查）。</param>
    /// <returns>该条分配的 index。</returns>
    public ValueTask<long> AppendAsync(ReadOnlySpan<byte> payload, CancellationToken ct = default)
    {
        if (!_open) throw new ObjectDisposedException(nameof(WalAppendLease));
        var index = _owner.AllocatedIndex + 1;
        var vt = _batch.AppendAsync(payload, ct);
        if (vt.IsCompletedSuccessfully)
        {
            _owner.RecordAppended(index, vt.Result);
            return new ValueTask<long>(index);
        }
        return RecordAfterAppendAsync(_owner, index, vt);
    }

    /// <summary>Dispose：回写批尾状态 + 释放 EntryLog 写锁。出批零额外动作（对齐现出批行为——
    /// 组提交在底层页契约/EntryLog 提前提交链内生效）。</summary>
    public void Dispose()
    {
        if (!_open) return;
        _open = false;
        _batch.Dispose();
    }

    /// <summary>背压慢路收尾（static——租约 ref struct 状态零跨 await；只传值，续体线程安全）。</summary>
    private static async ValueTask<long> RecordAfterAppendAsync(TierWal owner, long index, ValueTask<LogicalAddress> vt)
    {
        var addr = await vt.ConfigureAwait(false);
        owner.RecordAppended(index, addr);
        return index;
    }
}

/// <summary>
/// ★ 批读租约（<see cref="ITierWal.ReadBatchLeaseSync"/> 产出——复制读热路径零分配原语）：
/// Entries = WAL 页缓冲切片的<b>惰性流式视图</b>（底层游标 MoveNext 驱动逐条产出——不预物化），
/// <b>仅租约存活期且单次枚举有效</b>（游标前进不可重放）；Dispose 归还页（视图失效）。
/// </summary>
public interface IWalBatchLease : IDisposable
{
    /// <summary>批条目流（页切片视图——异步游标驱动逐条产出，单次枚举，Dispose 后失效）。</summary>
    IAsyncEnumerable<WalEntry> Entries { get; }
}

/// <summary>
/// TierWAL——协议中立 WAL 产品（raft/p2p 的存储中线）。
/// <para>★ 定位：存储侧把 raft 协议要求的<b>全部持久化语义做齐但内容零知识</b>——entry 内容不解析、
///   元数据内容不解析（Opaque 槽）；raft/p2p（TC.Tier.Products.Net）是纯消费方，直接接线即可。</para>
/// <para>★ 能力映射（底层 EntryLog 已实现，TierWAL 只封装 + 自定 opaque 容器布局）：</para>
/// <para> - 追加：组提交 <see cref="AppendBatchAsync"/>（raft 攒批）/ 单条 <see cref="AppendSingleAsync"/></para>
/// <para> - 显式提交 <see cref="CommitAsync"/>（raft 同步点：一次 fsync = 一批持久化）</para>
/// <para> - 截断：<see cref="TruncatePrefixAsync"/>（头压缩，与写并行）/ <see cref="TruncateSuffixAsync"/>（冲突修正）</para>
/// <para> - 重放 <see cref="ReadFromAsync"/>（随机起点顺序读，index 定位 = 段表二分 + 段内扫帧计数）</para>
/// <para> - 元数据 Opaque 槽 <see cref="WriteMetaAsync"/>/<see cref="ReadMeta"/>（term/vote/config 原子替换）</para>
/// <para> - 快照导出/导入 <see cref="ExportSnapshotAsync"/>/<see cref="ImportSnapshotAsync"/>（一致性点 N₀ 进帧 Header）</para>
/// <para>★ index 承载（设计 §8.7）：raft 日志连续无空洞——index 由"起点 + 顺序计数"推导，
///   帧内零 index；段 anchor 表随 opaque 原子落盘（恢复 O(1) 读全表）。</para>
/// <para>★ 双水位：<see cref="AllocatedIndex"/>（内存计数，含攒批窗口）/ <see cref="PersistedIndex"/>
///   （最后显式提交水位——raft 语义的 last persisted index 可信下限，自动提交只提前落盘不推进）。</para>
/// </summary>
public interface ITierWal : ILifecycle<WalRecoveryHints>, IDisposable, IAsyncDisposable
{
    // === entryLog 追加（分配与持久化分离——raft 控制 fsync 时机）===

    /// <summary>
    /// ★ 开启批追加租约（ref struct，using 包裹——消费方控制形态）：逐条 <see cref="WalAppendLease.Append"/>
    /// 写入（返回该条 index）。适配器甜区：复用 scratch 逐条帧化直写（集合形态的 N+1 次分配/批 →
    /// ~0）。★ 绝不跨 await 持有（持 WAL 写锁——ref struct 编译器强制）；单写者纪律：租约存活期
    /// 禁止其他写者 Begin。
    /// </summary>
    /// <returns>批追加租约（StartIndex = AllocatedIndex + 1——写锁内取，无漂移）。</returns>
    WalAppendLease BeginAppendBatch();

    /// <summary>
    /// 组提交追加：一批 entry 写入 WAL，返回分配的 [起始 index, 条数]。
    /// <para>★ 持久化时机 = 组提交策略（Options 三维度）自动触发，或调用方显式 <see cref="CommitAsync"/>——
    ///   raft 语义：AppendEntries 攒批 → CommitAsync → 应答（一次 fsync = 一批持久化）。</para>
    /// <para>★ 内部 = <see cref="BeginAppendBatch"/> 租约包装（单一真源）。</para>
    /// </summary>
    /// <param name="entries">要追加的 entry 列表（每条 = 一帧 [len 4B][payload]）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分配的 [起始 index, 条数]。</returns>
    ValueTask<WalAppendResult> AppendBatchAsync(IReadOnlyList<ReadOnlyMemory<byte>> entries, CancellationToken ct);

    /// <summary>
    /// 单条追加：一条 entry 写入 WAL，返回 [index, 1]（持久化时机同上；三维度全 0 配置 = 每写即提交的单条提交形态）。
    /// </summary>
    /// <param name="entry">要追加的 entry（一帧 [len 4B][payload]）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>分配的 [index, 1]。</returns>
    ValueTask<WalAppendResult> AppendSingleAsync(ReadOnlyMemory<byte> entry, CancellationToken ct);

    // === 显式提交（开放提交方法——raft 同步点）===

    /// <summary>
    /// 显式持久化：当前写游标前的全部 entry + opaque 容器原子落盘（映射 EntryLog.CommitAsync——
    /// flush 落盘 + 推进 PersistedIndex；调用方在需要同步点（应答客户端/推进 matchIndex）前调用）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>提交完成的 <see cref="ValueTask"/>。</returns>
    ValueTask CommitAsync(CancellationToken ct);

    /// <summary>
    /// 阻塞等到 index 已持久化（映射 EntryLog.WaitForCommitAsync——raft 推进 commitIndex 的依据）。
    /// </summary>
    /// <param name="index">要等待的持久化索引。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步完成的 <see cref="ValueTask"/>。</returns>
    ValueTask WaitForPersistedAsync(long index, CancellationToken ct);

    // === 重放（随机起点顺序读）===

    /// <summary>
    /// 从 index 顺序流式读 entry（定位 = 段表二分 + 段内扫帧计数，见设计 §8.7；冷节点快照后从 SnapshotIndex+1 调用）。
    /// </summary>
    /// <param name="startIndex">起始索引。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步流式返回的 <see cref="WalEntry"/>。</returns>
    IAsyncEnumerable<WalEntry> ReadFromAsync(long startIndex, CancellationToken ct);

    /// <summary>
    /// ★ 同步重放（同 <see cref="ReadFromAsync"/> 语义——随机起点顺序读；同步轨：
    /// 页帧同步加载 + MoveNext 同步，零异步排队）。适用方 = 专用 worker 线程
    /// （apply 管道/状态机循环——热数据直读零 IO 等待；冷区同步回源阻塞由专用线程承担，
    /// 换取消异步读链的偶发排队延迟——2026-08-27 性能诊断：apply 链异步读 2-6ms 偶发等待
    /// 是串行复制的固定成本主体）。
    /// <para>★ 零拷贝（2026-08-29 性能）：产出 <see cref="WalEntry.Data"/> = WAL 页缓冲切片
    /// （页租借模式——换页不还池，游标持有至枚举结束）——<b>仅枚举存活期有效</b>
    /// （含循环体内 await）；跨枚举持有（如复制读收集后发送）须自行拷贝。</para></summary>
    /// <param name="startIndex">起始索引。</param>
    /// <returns>顺序枚举的 <see cref="WalEntry"/>。</returns>
    IEnumerable<WalEntry> ReadFromSync(long startIndex);

    /// <summary>
    /// ★ 批读租借（复制读热路径零分配原语——2026-08-29 性能）：一次游标 lease 收集
    /// [startIndex, startIndex+maxCount) 的页切片列表——<b>零拷贝零分配</b>（页切片视图，
    /// 无逐条 ToArray）。调用方在租约存活期内完成拷贝/消费，<see cref="System.IDisposable.Dispose"/>
    /// 后视图失效（页归还池）。</summary>
    /// <param name="startIndex">起始索引。</param>
    /// <param name="maxCount">最多收集条数。</param>
    /// <param name="ct">取消令牌（页 IO 取消——贯通租约枚举）。</param>
    /// <returns>租约（Entries = 页切片视图流）。</returns>
    IWalBatchLease ReadBatchLeaseSync(long startIndex, int maxCount, CancellationToken ct = default);

    // === 水位（双水位：分配 vs 持久化——对应引擎 AllocatedTail/CommittedTail）===

    /// <summary>已分配尾 index（含未持久化缓冲——raft 攒批窗口）。</summary>
    long AllocatedIndex { get; }

    /// <summary>
    /// 本地已持久化尾 index（Append + CommitAsync 后推进）。
    /// <para>★ 命名澄清（raft 语义）：这是<b>本地 fsync 水位（last persisted index）</b>，不是集群 commitIndex
    ///   （多数派确认——单机给不了，由协议层根据复制进度计算）；协议层据此应答/推进 matchIndex。</para>
    /// </summary>
    long PersistedIndex { get; }

    /// <summary>本地持久化水位查询（index ≤ PersistedIndex 即已持久化）。</summary>
    /// <param name="index">要查询的索引。</param>
    /// <returns>如果索引已持久化则返回 true，否则返回 false。</returns>
    bool IsPersisted(long index);

    // === 截断（raft 冲突修正 / 日志压缩）===

    /// <summary>截尾部 [newTailIndex+1, ∞)：raft 冲突日志修正（TruncateSuffix→ReclaimTail 映射）。</summary>
    /// <param name="newTailIndex">新的尾索引。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步完成的 <see cref="ValueTask"/>。</returns>
    ValueTask TruncateSuffixAsync(long newTailIndex, CancellationToken ct);

    /// <summary>截头部 [0, newHeadIndex)：快照压缩后回收（TruncatePrefix→ReclaimHead 映射；与写完全并行）。</summary>
    /// <param name="newHeadIndex">新的头索引。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步完成的 <see cref="ValueTask"/>。</returns>
    ValueTask TruncatePrefixAsync(long newHeadIndex, CancellationToken ct);

    // === 元数据 Opaque 槽（原子替换，内容零知识）===

    /// <summary>
    /// 原子写元数据 blob（term/vote/config——内容由协议层定义；经 opaque 容器搭 EntryLog 水位线落盘，CRC 校验兜底）。
    /// </summary>
    /// <param name="opaque">要写入的元数据 blob。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步完成的 <see cref="ValueTask"/>。</returns>
    ValueTask WriteMetaAsync(ReadOnlyMemory<byte> opaque, CancellationToken ct);

    /// <summary>读当前元数据 blob；未写 = Empty。</summary>
    /// <returns>当前元数据 blob。</returns>
    ReadOnlyMemory<byte> ReadMeta();

    // === 镜像快照（第三部件——主数据镜像 + 增量压缩 + 注入传输面）===

    /// <summary>当前快照覆盖到 index N₀（快照后/导入后；无快照 = 0）。</summary>
    long SnapshotIndex { get; }

    /// <summary>
    /// 当前快照覆盖起点 index（镜像帧流第一条的 index；无快照 = 0）。
    /// <para>★ 快照区 index 对齐的前提：快照帧流 = [SnapshotHeadIndex .. SnapshotIndex] 的原始条目帧
    ///   （帧内零 index——raft 日志 index 由起点+顺序计数推导，见设计 §8.7）；冷启动重建状态机
    ///   时从帧流第一条起按该起点计数，业务 apply 的 index 与日志序一致。</para>
    /// </summary>
    long SnapshotHeadIndex { get; }

    /// <summary>
    /// raft 日志压缩（一体：主数据镜像生成 → 本地快照存储 → 增量压缩截断）。
    /// <para>★ 快照内容 = 主数据 [Head..N₀] 的<b>原始条目帧流</b>（WAL 自己从主数据生成——非 raft 状态机镜像；
    ///   N₀ = 调用时 PersistedIndex——只含已持久化）；随后 <see cref="TruncatePrefixAsync"/>(N₀+1) 压缩已镜像段
    ///   （先快照后截断——被截区有快照覆盖）。</para>
    /// <para>★ 本地存储恒为内部快照结构（IncrementalSnapshot——raft 冷启动必须本地载入）；
    ///   传输（Export/Import）经注入的 <see cref="IAsyncTransferPersistence"/>（默认回落 = 内部快照结构）。
    ///   调用时机 = leader 定期压缩（raft 惯例——低频）。</para>
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>快照覆盖点 N₀（= 调用时 PersistedIndex）。</returns>
    ValueTask<long> SnapshotAsync(CancellationToken ct);

    /// <summary>
    /// 流式读回最近一次镜像快照的条目帧流（raft 冷启动重建状态机分块消费——每条 = 一帧
    /// [len 4B][payload]，与 <see cref="ExportSnapshotAsync"/> 导出格式同构）。
    /// 无快照 = 空流。与 <see cref="SnapshotIndex"/> 配套：载入 N₀ 后重建镜像、再向 leader 汇报 N₀ 等增量。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步流式返回的条目帧流。</returns>
    IAsyncEnumerable<ReadOnlyMemory<byte>> ReadSnapshotEntriesAsync(CancellationToken ct);

    /// <summary>
    /// 导出快照经注入传输面（WriteHeader(N₀) + 条目帧流 + Footer CRC 校验——WalSnapshotFormat）。
    /// ★ 会话经注入 <see cref="IAsyncTransferPersistence"/> 打开（Builder.WithSnapshotPersistence）；
    ///   未注入（无传输面）抛 InvalidOperationException（单机场景不需要导出——快照已本地化）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步完成的 <see cref="ValueTask"/>。</returns>
    ValueTask ExportSnapshotAsync(CancellationToken ct);

    /// <summary>
    /// 经注入传输面导入快照（读 Header(N₀) + 帧流 + Footer 校验 → <b>替换</b>本地快照（清旧段 + 安装新段））。
    /// ★ 导入后 <see cref="SnapshotIndex"/> = N₀——raft 应用快照内容后向 leader 汇报 N₀，leader 从 (N₀, 尾] 推增量。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>异步完成的 <see cref="ValueTask"/>。</returns>
    ValueTask ImportSnapshotAsync(CancellationToken ct);
}
