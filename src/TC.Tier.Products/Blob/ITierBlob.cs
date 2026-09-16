using TC.Tier.Contracts.Lifecycle;
using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Transactions;

namespace TC.Tier.Products.Blob;

/// <summary>对象状态（tierblob-spec §2 对象表 payload State 字段）。</summary>
public enum BlobState : byte
{
    /// <summary>活跃——可读可枚举。</summary>
    Active = 0,

    /// <summary>墓碑——已删除或恢复对账判损坏；List/Get 不可见（IncludeTombstones 枚举可见），空间保留。</summary>
    Tombstone = 1,
}

/// <summary>写入结果——对象句柄（= 帧流起始 LogicalAddress，地址一等公民：可持久化/传输/凭地址直达）。</summary>
/// <param name="ObjectId">对象句柄（帧流起始 LogicalAddress）。</param>
/// <param name="Length">对象用户数据长度（字节；不含帧头/对齐补零/帧尾）。</param>
public readonly record struct BlobPutResult(LogicalAddress ObjectId, long Length);

/// <summary>对象元信息（GetInfoAsync 产物——零数据 IO，对象表点查）。</summary>
/// <param name="ObjectId">对象句柄。</param>
/// <param name="Length">用户数据长度（字节）。</param>
/// <param name="CreatedTicks">创建时刻（UTC Ticks）。</param>
/// <param name="State">对象状态。</param>
public readonly record struct BlobInfo(LogicalAddress ObjectId, long Length, long CreatedTicks, BlobState State);

/// <summary>List 过滤器（表序 = 地址序交付）。</summary>
/// <param name="FromAddress">句柄下界（含）。default = 负无穷。</param>
/// <param name="ToAddress">句柄上界（不含）。default（Offset ≤ 0）= 无上界（Empty 是合法地址故以 Offset 判界）。</param>
/// <param name="IncludeTombstones">是否包含墓碑（运维面；默认 false = 仅 Active）。</param>
public readonly record struct BlobListFilter(LogicalAddress FromAddress, LogicalAddress ToAddress, bool IncludeTombstones);

/// <summary>存储观测快照（GetStatsAsync 产物——tierblob-spec §6 统计面）。</summary>
/// <param name="ObjectCount">Active 对象数。</param>
/// <param name="TombstoneCount">墓碑数（含删除与恢复对账标损）。</param>
/// <param name="Bytes">引擎已用区间（TruncatedAddress → PhysicalWriteAddress 物理折算，含帧头/尾/对齐）。</param>
/// <param name="ReclaimableBytes">墓碑对象占用（物理折算——ReclaimDeleted 可回收的上界估计）。</param>
/// <param name="HeadAddress">截断头地址。</param>
/// <param name="TailAddress">逻辑写尾。</param>
public readonly record struct BlobStats(
    long ObjectCount,
    long TombstoneCount,
    long Bytes,
    long ReclaimableBytes,
    LogicalAddress HeadAddress,
    LogicalAddress TailAddress);

/// <summary>TierBlob 恢复 hints（spec §5——水位/对象表全自恢复，无注入项；预留对齐产品 hints 惯例）。</summary>
public readonly struct BlobRecoveryHints;

/// <summary>
/// TierBlob——SnapshotBase 的产品封面：无索引语义的 GB/TB 级大对象流式存取（tierblob-spec）。
/// <para>★ 对象句柄 = 起始 LogicalAddress（写完即得；同址即同对象——raft 形态下确定性 apply 全组同址，
///   泛化复制继承）。凭句柄直达数据，零元数据查询；GetInfoAsync 才查表。</para>
/// <para>★ 语义边界（spec 裁定②③）：顺序写整对象/顺序读——随机 seek 覆写不进产品语义（KV 的事）；
///   覆盖写 = 先 Delete 再 Put（新句柄）。</para>
/// <para>★ 持久化（分配-持久分离）：Complete 返回句柄 = 内存可见（含对象表登记已持久——表 Prepare/Confirm
///   收口）；数据面落盘显式 <see cref="FlushAsync"/>（引擎 fsync 屏障）。</para>
/// <para>★ 删除 = 墓碑 + 空间保留（裁定⑥）：物理回收走 <c>ReclaimDeletedAsync</c> 手动档（B1）。</para>
/// </summary>
public interface ITierBlob : ILifecycle<BlobRecoveryHints>, IDisposable, IAsyncDisposable
{
    // ══ 写入 ══

    /// <summary>整对象单发写（内部走流式会话——CRC64 增量零驻留）。</summary>
    /// <param name="data">对象数据字节。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象句柄 + 长度。</returns>
    /// <exception cref="InvalidOperationException">超出 MaxBytes 容量上限（fail-fast）。</exception>
    ValueTask<BlobPutResult> PutAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default);

    /// <summary>打开流式写会话（边写边算 CRC64，不驻内存整块）。
    /// ★ 会话独占写通道（产品级单写者——结构层写尾单会话契约）；会话存活期间其它写操作排队。
    /// Dispose 未 Complete = Abort（尾截断回滚）。</summary>
    /// <param name="expectedLength">≥0 = 定长契约（须恰好写满 N 字节才可 Complete；容量守卫先行）；
    /// -1 = 不定长动态流（导出/管道常态，帧尾 TotalLength 收口）。</param>
    /// <returns>写会话。</returns>
    /// <exception cref="InvalidOperationException">定长预取判超 MaxBytes。</exception>
    BlobWriteSession OpenWrite(long expectedLength = -1);

    // ══ 读取（地址直达）══

    /// <summary>定长读（免帧解析快速路径——数据区物理连续，直接段感知读）。</summary>
    /// <param name="objectId">对象句柄。</param>
    /// <param name="dst">目标缓冲区（须 ≥ 对象长度）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>实际读取的字节数（= 对象长度）。</returns>
    /// <exception cref="KeyNotFoundException">句柄不存在或已墓碑。</exception>
    /// <exception cref="ArgumentException">dst 小于对象长度。</exception>
    ValueTask<int> GetAsync(LogicalAddress objectId, Memory<byte> dst, CancellationToken ct = default);

    /// <summary>打开流式读会话（CRC64 逐帧校验——读至对象末尾自动验帧，校验失败抛 IOException）。</summary>
    /// <param name="objectId">对象句柄。</param>
    /// <returns>读会话。</returns>
    /// <exception cref="KeyNotFoundException">句柄不存在或已墓碑。</exception>
    BlobReadSession OpenRead(LogicalAddress objectId);

    /// <summary>对象元信息（对象表点查，零数据 IO）。</summary>
    /// <param name="objectId">对象句柄。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象元信息（含墓碑态——运维面可见）。</returns>
    /// <exception cref="KeyNotFoundException">句柄不存在。</exception>
    ValueTask<BlobInfo> GetInfoAsync(LogicalAddress objectId, CancellationToken ct = default);

    // ══ 枚举 / 治理 ══

    /// <summary>对象枚举（对象表序 = 地址序）。</summary>
    /// <param name="filter">过滤器（default = 全部 Active，全地址域）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>对象元信息流。</returns>
    IAsyncEnumerable<BlobInfo> ListAsync(BlobListFilter filter = default, CancellationToken ct = default);

    /// <summary>删除 = 表墓碑（裁定⑥——空间保留，List/Get 即刻不可见）。幂等：已墓碑 no-op。</summary>
    /// <param name="objectId">对象句柄。</param>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="KeyNotFoundException">句柄不存在。</exception>
    ValueTask DeleteAsync(LogicalAddress objectId, CancellationToken ct = default);

    /// <summary>引擎已用区间（物理折算，含帧开销与对齐补零）。</summary>
    long Bytes { get; }

    /// <summary>落盘屏障：数据引擎 fsync（Complete 已保证对象表登记持久）。</summary>
    /// <param name="ct">取消令牌。</param>
    ValueTask FlushAsync(CancellationToken ct);

    /// <summary>★ 对象表参与者（spec 裁定⑤）：注册即得"业务写 + 对象登记"2PC 原子——
    ///   会话域内（Prepare → Confirm/Abort）的对象登记随事务整体提交/回滚（登记帧尾截断 + 内存表回退）。
    ///   会话期间的对象登记自动挂入事务（deferred commit）。</summary>
    /// <returns>对象表事务参与者。</returns>
    ITransactionParticipant GetParticipant();
}
