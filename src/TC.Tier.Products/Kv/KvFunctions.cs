namespace TC.Tier.Products.Kv;

/// <summary>
/// KV 操作状态（FASTER Status 档位对齐：NotFound/Ok/Error）。
/// </summary>
public enum KvStatus : byte
{
    /// <summary>未命中（Read 无值；RMW 走 Initial 流前的空态）。</summary>
    NotFound = 0,

    /// <summary>命中/操作成功。</summary>
    Ok = 1,

    /// <summary>操作失败（RMW 折叠流返回 false 等）。</summary>
    Error = 2,
}

/// <summary>
/// KV Functions 完整模型（tierkv-design.md §1——FASTER IFunctions 五参形态完整落地：用户裁定）。
/// <para>★ RMW 三流（§3.3 统一为「读折叠 → Format → 追加 → 索引 CAS 换绑」）：
/// Initial（key 无值造值）/ Copy（不可变记录折叠——旧记录保留，版本历史是我们的多出能力）/
/// InPlace（语义位——append-only 无可变区，实现为「本记录追加前最后折叠」：引擎先征询本钩子，
/// false 回落 Copy 流——对齐 FASTER mutable→immutable 派生序）。</para>
/// <para>★ 返回 false 契约：Initial/Copy 返回 false = 折叠失败 → RMW 以 <see cref="KvStatus.Error"/>
/// 收口（零写入）；InPlaceUpdater 返回 false = 本槽位放弃 → 回落 Copy 流（FASTER 同款派生）。</para>
/// <para>★ 写侧钩子为语义位校验面（返回 false = 拒绝该写入——FASTER 原型为 void/bool 混合，
/// 本契约统一 bool 值语义）；读侧钩子负责 value→output 翻译。</para>
/// </summary>
/// <typeparam name="TKey">键类型（unmanaged + IEquatable）。</typeparam>
/// <typeparam name="TInput">操作输入（RMW 增量/读参数——用户定义）。</typeparam>
/// <typeparam name="TValue">值类型（经 <see cref="IValueFormatter{TValue}"/> 纯转 byte 存储）。</typeparam>
/// <typeparam name="TOutput">操作输出（读/RMW 的翻译产物——用户定义）。</typeparam>
/// <typeparam name="TContext">用户上下文（完成回调回传）。</typeparam>
public interface IKvFunctions<TKey, TInput, TValue, TOutput, TContext>
    where TKey : unmanaged, IEquatable<TKey>
{
    // ── RMW 三流（读折叠核）──

    /// <summary>Initial 流：key 无值——由 input 造值（value 入参为 default，输出折叠结果）。
    /// 返回 false = 折叠失败（RMW 以 Error 收口，零写入）。</summary>
    /// <param name="key">键（ref——引擎复用槽位，禁改写）。</param>
    /// <param name="input">RMW 操作输入（增量/参数）。</param>
    /// <param name="value">入参为 default（key 无值）；输出折叠出的新值。</param>
    /// <param name="output">折叠翻译产物（回传给完成回调）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <returns>折叠成功为 true；失败为 false（RMW 以 Error 收口，零写入）。</returns>
    bool InitialUpdater(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context);

    /// <summary>Copy 流：命中记录不可变折叠——oldValue+input 折叠出 newValue（旧记录保留——版本历史）。
    /// 返回 false = 折叠失败（RMW 以 Error 收口，零写入）。</summary>
    /// <param name="key">键（ref——引擎复用槽位，禁改写）。</param>
    /// <param name="input">RMW 操作输入（增量/参数）。</param>
    /// <param name="oldValue">命中记录的旧值。</param>
    /// <param name="newValue">折叠出的新值。</param>
    /// <param name="output">折叠翻译产物（回传给完成回调）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <returns>折叠成功为 true；失败为 false（RMW 以 Error 收口，零写入）。</returns>
    bool CopyUpdater(ref TKey key, ref TInput input, ref TValue oldValue, ref TValue newValue, ref TOutput output, ref TContext context);

    /// <summary>InPlace 等价流（语义位）：FASTER 原地更新槽位——append-only 恒追加，实现为
    /// 「本记录追加前最后折叠」（引擎对命中记录先征询本钩子，折叠语义=原地改写 value）。
    /// 返回 false = 回落 Copy 流（FASTER mutable→immutable 派生序）。</summary>
    /// <param name="key">键（ref——引擎复用槽位，禁改写）。</param>
    /// <param name="input">RMW 操作输入（增量/参数）。</param>
    /// <param name="value">命中记录的当前值；输出折叠后的新值。</param>
    /// <param name="output">折叠翻译产物（回传给完成回调）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <returns>折叠成功为 true；失败为 false（回落 Copy 流）。</returns>
    bool InPlaceUpdater(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context);

    // ── 读侧钩子（value→output 翻译）──

    /// <summary>单读者（排他读上下文——Serializable 会话临界区）。</summary>
    /// <param name="key">键（ref——引擎复用槽位）。</param>
    /// <param name="input">读操作输入（参数）。</param>
    /// <param name="value">命中的值（由引擎从记录读出）。</param>
    /// <param name="output">读出翻译产物。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    void SingleReader(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context);

    /// <summary>并发读者（共享记录读——缺省 kv 读路径）。</summary>
    /// <param name="key">键（ref——引擎复用槽位）。</param>
    /// <param name="input">读操作输入（参数）。</param>
    /// <param name="value">命中的值（由引擎从记录读出）。</param>
    /// <param name="output">读出翻译产物。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    void ConcurrentReader(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context);

    // ── 写侧钩子（语义位校验面——append-only：写恒为新记录追加）──

    /// <summary>单写者（kv 直连 Upsert 上下文）。返回 false = 拒绝该写入（Upsert 以 NotFound 收口）。</summary>
    /// <param name="key">键（ref——引擎复用槽位）。</param>
    /// <param name="value">待写入的值。</param>
    /// <returns>接受写入为 true；拒绝为 false。</returns>
    bool SingleWriter(ref TKey key, ref TValue value);

    /// <summary>并发写者（会话 Upsert 上下文——语义位与 Single 同形）。返回 false = 拒绝该写入。</summary>
    /// <param name="key">键（ref——引擎复用槽位）。</param>
    /// <param name="value">待写入的值。</param>
    /// <returns>接受写入为 true；拒绝为 false。</returns>
    bool ConcurrentWriter(ref TKey key, ref TValue value);

    // ── 完成回调 ──

    /// <summary>Read 完成回调。</summary>
    /// <param name="key">键。</param>
    /// <param name="input">读操作输入。</param>
    /// <param name="output">读出翻译产物。</param>
    /// <param name="status">操作状态（NotFound/Ok/Error）。</param>
    /// <param name="context">用户上下文。</param>
    void ReadCompletionCallback(ref TKey key, ref TInput input, ref TOutput output, KvStatus status, TContext context);

    /// <summary>Upsert 完成回调。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">写入的值。</param>
    /// <param name="status">操作状态（NotFound/Ok/Error）。</param>
    /// <param name="context">用户上下文。</param>
    void UpsertCompletionCallback(ref TKey key, ref TValue value, KvStatus status, TContext context);

    /// <summary>Delete 完成回调。</summary>
    /// <param name="key">键。</param>
    /// <param name="status">操作状态（NotFound/Ok/Error）。</param>
    /// <param name="context">用户上下文。</param>
    void DeleteCompletionCallback(ref TKey key, KvStatus status, TContext context);

    /// <summary>RMW 完成回调（output 折叠产物回传）。</summary>
    /// <param name="output">折叠翻译产物。</param>
    /// <param name="status">操作状态（NotFound/Ok/Error）。</param>
    /// <param name="context">用户上下文。</param>
    void RmwCompletionCallback(ref TOutput output, KvStatus status, TContext context);
}

/// <summary>
/// IKvFunctions 默认基座（虚拟 no-op 缺省——写钩子接受/读钩子零翻译/完成回调零操作；
/// RMW 三流保持抽象——折叠语义是 Functions 的本质，无安全缺省）。
/// </summary>
public abstract class KvFunctionsBase<TKey, TInput, TValue, TOutput, TContext>
    : IKvFunctions<TKey, TInput, TValue, TOutput, TContext>
    where TKey : unmanaged, IEquatable<TKey>
{
    /// <inheritdoc />
    public virtual bool SingleWriter(ref TKey key, ref TValue value) => true;

    /// <inheritdoc />
    public virtual bool ConcurrentWriter(ref TKey key, ref TValue value) => true;

    /// <inheritdoc />
    public virtual void SingleReader(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context)
    {
    }

    /// <inheritdoc />
    public virtual void ConcurrentReader(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context)
    {
    }

    /// <inheritdoc />
    public virtual void ReadCompletionCallback(ref TKey key, ref TInput input, ref TOutput output, KvStatus status, TContext context)
    {
    }

    /// <inheritdoc />
    public virtual void UpsertCompletionCallback(ref TKey key, ref TValue value, KvStatus status, TContext context)
    {
    }

    /// <inheritdoc />
    public virtual void DeleteCompletionCallback(ref TKey key, KvStatus status, TContext context)
    {
    }

    /// <inheritdoc />
    public virtual void RmwCompletionCallback(ref TOutput output, KvStatus status, TContext context)
    {
    }

    /// <inheritdoc />
    public abstract bool InitialUpdater(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context);

    /// <inheritdoc />
    public abstract bool CopyUpdater(ref TKey key, ref TInput input, ref TValue oldValue, ref TValue newValue, ref TOutput output, ref TContext context);

    /// <inheritdoc />
    public abstract bool InPlaceUpdater(ref TKey key, ref TInput input, ref TValue value, ref TOutput output, ref TContext context);
}
