using TC.Tier.Contracts.Storage;
using TC.Tier.Contracts.Transactions;

namespace TC.Tier.Products.Kv;

/// <summary>
/// 完成语义三档（tierkv-design.md §2 完成语义轴——FASTER Completion 对齐）。
/// <para>★ 默认形态 = FASTER 模式：写路径内存档（零 IO），持久化 = checkpoint（周期泵/显式）；
///   强持久（写级不丢）是显式旋钮（逐写传 <see cref="Committed"/>），内部组提交摊薄。</para>
/// <para>复制组合下 Committed = 多数派提交（TierKvRaft 组装层语义，另立组装稿）。</para>
/// </summary>
public enum KvCommitPolicy
{
    /// <summary>落位即返回（缺省——纯内存写零 IO ≙ FASTER Upsert；崩溃丢 checkpoint 窗口内最近写，
    ///   Ring 驱逐链页复用前写穿提供容量下限）。持久化由 checkpoint 泵/显式 CheckpointAsync 承担。</summary>
    FireAndForget,

    /// <summary>强制数据不丢失（显式旋钮——逐写等待持久化提交，组提交摊薄 fsync；
    ///   需要写级耐久的调用方使用，代价=磁盘持久化写本质成本）。</summary>
    Committed,

    /// <summary>等待全部 pending 完成（会话语义——W2 会话全集启用）。</summary>
    WaitForPending,
}

/// <summary>
/// TierKv 恢复提示（重放窗口注入——同 ProbingIndexRecoveryHints 形态：Ring Ready 后索引重放窗口）。
/// </summary>
/// <param name="BeginAddress">重放窗口起始逻辑地址。</param>
/// <param name="EndAddress">重放窗口结束逻辑地址。</param>
public readonly record struct KvRecoveryHints(LogicalAddress BeginAddress, LogicalAddress EndAddress);

/// <summary>
/// CAS 回执（A.2b CompareAndSwap——乐观锁结果，TierKv/KvSession 双形态共用）。
/// </summary>
/// <param name="Swapped">是否换绑成功（false = 预期不匹配，零写入零事件）。</param>
/// <param name="CurrentAddress">Swapped=false 时的当前绑定（失败原因自明：≠ expected；
/// Invalid = 当前不存在——未绑定或已过期惰性读删）。Swapped=true 时为新绑定（= NewAddress）。</param>
/// <param name="NewAddress">Swapped=true 时的新绑定地址（写入记录）；false 时 Invalid。</param>
public readonly record struct KvCasResult(bool Swapped, LogicalAddress CurrentAddress,
    LogicalAddress NewAddress);

/// <summary>
/// TierKv 消费面契约（tierkv-design.md §0——单一泛型产品类，TierWal 同构）。
/// <para>★ 面分层：本接口 = 字节层（存储真面）；类型化值面（TValue 直存直取）由 [KvStore]
///   生成的封闭类经装配的 <see cref="IValueFormatter{TValue}"/> 承载（D7：生成器自动 + builder 显式覆盖）。</para>
/// <para>★ 波次扩展：W2 会话全集 / W4 IKvFunctions 完整模型 / W5 检查点快速恢复 /
///   W7 范围扫描按波次扩展本接口。</para>
/// </summary>
/// <typeparam name="TKey">键类型（unmanaged + 判等闭环——builder 校验）。</typeparam>
public interface ITierKv<TKey> : ILifecycle<KvRecoveryHints>, IDisposable, IAsyncDisposable
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <summary>写入 key→值字节（同 key 覆写——append-only 换绑语义，旧值留 Ring 由回收治理）。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值字节（经 IValueFormatter 翻译后的 payload）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址（Ring 尾）。</returns>
    ValueTask<LogicalAddress> PutAsync(TKey key, ReadOnlyMemory<byte> value, CancellationToken ct = default);

    /// <summary>点查 key → 值字节（未命中返回 false；命中时 destination 须 ≥ 值长度）。</summary>
    /// <param name="key">键。</param>
    /// <param name="destination">读出目标缓冲（命中时须不小于值长度）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中且成功写入 destination 为 true；未命中或缓冲不足为 false。</returns>
    ValueTask<bool> TryGetAsync(TKey key, Memory<byte> destination, CancellationToken ct = default);

    /// <summary>点查 key → 值字节副本（未命中返回 null）。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中的值字节副本；未命中返回 null。</returns>
    ValueTask<byte[]?> TryGetBytesAsync(TKey key, CancellationToken ct = default);

    /// <summary>删除 key（墓碑语义——恢复重放跳过已删 key）。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>删除成功为 true；key 不存在为 false。</returns>
    ValueTask<bool> DeleteAsync(TKey key, CancellationToken ct = default);

    /// <summary>存活条目数（索引 O(1) 计数）。</summary>
    long Count { get; }

    /// <summary>Ring 尾地址（数据写入末尾）。</summary>
    LogicalAddress TailAddress { get; }

    /// <summary>Ring 头地址（截断下界）。</summary>
    LogicalAddress BeginAddress { get; }

    /// <summary>Ring 参与者（2PC——Session 域注册用，W2）。</summary>
    ITransactionParticipant RingParticipant { get; }

    /// <summary>索引参与者（2PC——Session 域注册用，W2）。</summary>
    ITransactionParticipant IndexParticipant { get; }
}
