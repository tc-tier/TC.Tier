using TC.Tier.Core.Epochs;

namespace TC.Tier.Products.Kv;

/// <summary>
/// KvSession——TierKv 会话（tierkv-design.md §3.1/§3.2，W2 会话全集）：写集可见性 + 原子批（F2
/// 多 key 全或无）+ pending 簿记（CompletePendingAsync 刷盘收口）+ EPVS 版本保护（Serializable）。
/// <para>★ 单线程对象（FASTER 同契约）：一个会话实例只归一个逻辑线程使用，内部零锁；
/// 并发正确性由「每会话一实例」表达。</para>
/// <para>★ 档位语义（<see cref="KvSessionConditions"/>）：None=读恒走索引；ReadMyWrites（缺省 D3）=
/// 写立即应用+写集读己之写；Serializable=RMW 全集+同步临界区 EPVS pin。</para>
/// <para>★ EPVS pin 粒度 = 同步临界区（async-first 判例）：Serializable 的同步写在版本保护下执行——
/// 版本过渡不可能跨越进行中的临界区；操作之间过渡可完成。设计稿 §3.1「ctor Enter/Dispose Leave」的
/// 生命周期 pin 与 LightEpoch 线程亲和（每线程 Resume/Suspend 必须同线程配对）+ 异步续体线程跳转
/// 不可兼得——落定 per-op 保护形态（FASTER ClientSession 同为 per-op 保护）；版本化快照读 W5 深化。</para>
/// <para>★ pending 模型：写恒登记高水位地址（FireAndForget/WaitForPending 不即时刷）；
/// <see cref="CompletePendingAsync"/> 把 [FlushedUntil, 高水位] 一次刷盘收口（组提交形态——
/// Committed 档即时刷为逐写 FlushUntil，组提交策略 W6 接管）。</para>
/// <para>★ 原子批（F2）：Begin 后写走暂存（stage 可见性隔离——本会话读可见、他人与索引不可见）；
/// CommitAsync = Ring 追加 → 2PC Prepare（落盘悬空）→ ConfirmCommitted（提交点）→ 索引换绑
/// （可见性）——崩溃任一窗口全或无（悬空 Prepare 恢复即弃 + 派生重放教义）。</para>
/// </summary>
public sealed class KvSession<TKey, TValue> : IDisposable
    where TKey : unmanaged, IEquatable<TKey>
{
    private readonly TierKv<TKey, TValue> _kv;
    private readonly KvSessionConditions _conditions;
    private readonly EpochProtectedVersionScheme _epvs;

    /// <summary>写集（ReadMyWrites/Serializable——key 最近一次写的 payload；null 值=墓碑；None 恒 null）。
    /// <para>★ payload 为用户视图字节（非存储帧）；expiry = 0 无过期（W8 TTL 惰性判定）。</para></summary>
    private Dictionary<TKey, (byte[]? Payload, long Expiry)>? _writeSet;

    /// <summary>本会话已发未刷写的最高地址（pending 高水位——CompletePendingAsync 收口）。</summary>
    private LogicalAddress _pendingWatermark;

    /// <summary>原子批暂存（BeginAtomicBatch 非 null 期间写走暂存不应用；值 null=墓碑）。</summary>
    private List<KeyValuePair<TKey, (byte[]? Payload, long Expiry)>>? _batch;

    private bool _disposed;

    /// <summary>内部装配构造（TierKv 创建会话时调用）。</summary>
    /// <param name="kv">所属 TierKv 实例。</param>
    /// <param name="conditions">会话一致性档位。</param>
    internal KvSession(TierKv<TKey, TValue> kv, KvSessionConditions conditions)
    {
        _kv = kv;
        _conditions = conditions;
        _epvs = kv.SessionEpvs;
        SessionVersion = kv.Versions.NextVersion();   // D1 session-version——创建即取单调版本
        if (conditions != KvSessionConditions.None)
            _writeSet = new Dictionary<TKey, (byte[]? Payload, long Expiry)>();
    }

    /// <summary>会话档位。</summary>
    public KvSessionConditions Conditions => _conditions;

    /// <summary>会话版本（D1 session-version——创建时自版本分配器取单调值；会话写经分配器全局推进，
    /// 本会话消费版本为其单调子序列；先创建的会话版本恒小）。</summary>
    public long SessionVersion { get; }

    // ═══════════════════════════════════════════════════════════════════
    // 读（写集优先 → 索引；批内暂存优先 → 写集 → 索引）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>点查 key → 值字节副本（未命中 false；命中时 value 为独立副本）。档位语义见类型注释。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中时 Found=true 且 Value 为独立字节副本；未命中 Found=false、Value=null。</returns>
    public async ValueTask<(bool Found, byte[]? Value)> TryGetBytesAsync(TKey key,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();

        // 原子批暂存优先（stage 可见性隔离——本会话批内读可见自己的批写）
        if (_batch is not null)
        {
            for (var i = _batch.Count - 1; i >= 0; i--)
            {
                if (!EqualityComparer<TKey>.Default.Equals(_batch[i].Key, key)) continue;
                var (staged, exp) = _batch[i].Value;
                return staged is { } sv && !KvValueFraming.IsExpired(exp, DateTime.UtcNow.Ticks)
                    ? (true, sv)
                    : (false, null);   // 批内墓碑/过期——未命中
            }
        }

        // 写集优先（ReadMyWrites/Serializable——本会话最后一次写胜出他人并发覆盖）
        if (_writeSet is not null && _writeSet.TryGetValue(key, out var entry))
        {
            var (payload, exp) = entry;
            return payload is { } v && !KvValueFraming.IsExpired(exp, DateTime.UtcNow.Ticks)
                ? (true, v)
                : (false, null);   // 写集墓碑/过期——未命中
        }

        var bytes = await _kv.TryGetBytesAsync(key, ct).ConfigureAwait(false);
        return bytes is { } value ? (true, value) : (false, null);
    }

    /// <summary>点查 key → 格式化值（经 formatter 逆翻译；未命中 false）。</summary>
    /// <param name="key">键。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>命中时 Found=true 且 Value 为经 formatter 逆翻译的值；未命中 Found=false。</returns>
    public async ValueTask<(bool Found, TValue Value)> TryGetFormattedAsync(TKey key,
        CancellationToken ct = default)
    {
        var (found, bytes) = await TryGetBytesAsync(key, ct).ConfigureAwait(false);
        if (!found)
            return (false, default!);
        return (true, _kv.Formatter.Parse(bytes!));
    }

    // ═══════════════════════════════════════════════════════════════════
    // 写（立即应用路径；批内转暂存）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>写入 key→值字节。批内 = 暂存（CommitBatchAsync 才应用）；批外 = 立即应用 + 按
    /// <paramref name="policy"/> 决定持久化时机（Committed 即时刷；FireAndForget/WaitForPending
    /// 登记高水位由 <see cref="CompletePendingAsync"/> 收口）。<paramref name="timeToLive"/> =
    /// TTL（过期惰性读删，W8）。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值字节。</param>
    /// <param name="policy">完成语义（Committed 即时刷；FireAndForget/WaitForPending 登记高水位）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址（批内暂存返回 Invalid）。</returns>
    public async ValueTask<LogicalAddress> PutAsync(TKey key, ReadOnlyMemory<byte> value,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget, TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var expiry = timeToLive is { } ttl ? DateTime.UtcNow.Ticks + ttl.Ticks : 0;
        if (_batch is not null)
        {
            _batch.Add(new(key, (value.ToArray(), expiry)));
            return LogicalAddress.Invalid;   // 暂存未落地址（Invalid="没有值"哨兵）——提交后才有
        }

        var copy = value.ToArray();
        var addr = await _kv.PutAsync(key, value, policy, timeToLive, ct).ConfigureAwait(false);
        if (_writeSet is not null)
            _writeSet[key] = (copy, expiry);   // 写集登记独立副本——防调用方复用缓冲污染
        await ApplyPolicyAsync(addr, policy, ct).ConfigureAwait(false);
        return addr;
    }

    /// <summary>写入 key→格式化值（formatter 翻译；批内暂存语义同字节面）。
    /// ★ 批外单分配：formatter 直填帧缓冲后落环——原链路 formatter 缓冲 → PutAsync 内 Frame
    /// 再拷 → 写集 ToArray 三次分配收敛为「framed + 写集 payload 副本」两次（写集独立副本
    /// 是读己之写防缓冲复用的必要语义，保留）。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">待格式化写入的值。</param>
    /// <param name="policy">完成语义（Committed 即时刷；FireAndForget/WaitForPending 登记高水位）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>写入记录的逻辑地址（批内暂存返回 Invalid）。</returns>
    public async ValueTask<LogicalAddress> PutFormattedAsync(TKey key, TValue value,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget, TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var expiry = timeToLive is { } ttl ? DateTime.UtcNow.Ticks + ttl.Ticks : 0;
        if (_batch is not null)
        {
            var staged = new byte[_kv.Formatter.GetSize(value)];
            _kv.Formatter.Format(value, staged);
            _batch.Add(new(key, (staged, expiry)));
            return LogicalAddress.Invalid;
        }

        var framed = KvValueFraming.AllocateFrame(_kv.Formatter.GetSize(value), expiry);
        _kv.Formatter.Format(value, framed.AsSpan(KvValueFraming.HeaderSize));
        var addr = await _kv.PutFramedAsync(key, framed, ct).ConfigureAwait(false);
        if (_writeSet is not null)
            _writeSet[key] = (framed.AsSpan(KvValueFraming.HeaderSize).ToArray(), expiry);
        await ApplyPolicyAsync(addr, policy, ct).ConfigureAwait(false);
        return addr;
    }

    /// <summary>同步写入（快路径；批内禁用——批内写经 PutAsync 暂存）。Serializable：同步临界区在
    /// EPVS 版本保护下执行（Enter/Leave 同线程同步段——async-first 兼容）。</summary>
    /// <param name="key">键。</param>
    /// <param name="value">值字节。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <returns>写入记录的逻辑地址。</returns>
    /// <exception cref="InvalidOperationException">原子批进行中（批内写经 PutAsync 暂存）。</exception>
    public LogicalAddress Put(TKey key, ReadOnlySpan<byte> value,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget)
    {
        ThrowIfDisposed();
        if (_batch is not null)
            throw new InvalidOperationException("原子批进行中——批内写经 PutAsync 暂存，同步 Put 仅批外使用");

        var copy = value.ToArray();
        LogicalAddress addr;
        if (_conditions == KvSessionConditions.Serializable)
        {
            _epvs.Enter();
            try
            {
                addr = _kv.Put(key, value);
            }
            finally
            {
                _epvs.Leave();
            }
        }
        else
        {
            addr = _kv.Put(key, value);
        }

        if (_writeSet is not null)
            _writeSet[key] = (copy, 0);   // 写集登记独立副本（同步 Put 无 TTL）
        TrackPending(addr);
        if (policy == KvCommitPolicy.Committed)
            _kv.Flush(addr);
        return addr;
    }

    /// <summary>删除 key（墓碑）。批内 = 暂存墓碑；批外 = 立即应用。</summary>
    /// <param name="key">键。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>key 存在并删除为 true；key 不存在为 false。</returns>
    public async ValueTask<bool> DeleteAsync(TKey key,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_batch is not null)
        {
            _batch.Add(new(key, ((byte[]?)null, 0)));
            return true;
        }

        var existed = await _kv.DeleteAsync(key, ct).ConfigureAwait(false);
        if (existed)
        {
            if (_writeSet is not null)
                _writeSet[key] = (null, 0);   // 墓碑进写集——本会话后续读未命中
            await ApplyPolicyAsync(_kv.TailAddress, policy, ct).ConfigureAwait(false);
        }
        return existed;
    }

    // ═══════════════════════════════════════════════════════════════════
    // 原子批（F2 多 key 全或无）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 开启原子批：此后写走暂存（本会话读可见——stage 隔离；他人与索引不可见），直到
    /// <see cref="CommitBatchAsync"/>（全应用）或 <see cref="AbortBatch"/>（全丢弃）。
    /// </summary>
    /// <exception cref="InvalidOperationException">原子批已在进行中（禁止嵌套）。</exception>
    public void BeginAtomicBatch()
    {
        ThrowIfDisposed();
        if (_batch is not null)
            throw new InvalidOperationException("原子批已在进行中——禁止嵌套");
        _batch = new List<KeyValuePair<TKey, (byte[]? Payload, long Expiry)>>();
    }

    /// <summary>
    /// 提交原子批（全应用——提交点语义）：Ring 追加全部暂存 → 2PC Prepare（整体落盘悬空）→
    /// ConfirmCommitted（提交点）→ 索引换绑（可见性）。崩溃任一窗口全或无（悬空 Prepare 恢复即弃）。
    /// 批内写视为已持久化（Prepare 落盘）——不经 pending。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <exception cref="InvalidOperationException">无进行中的原子批。</exception>
    /// <returns>任务在批内全部写整体提交（Prepare 落盘 + Confirm + 索引换绑）完成后完成。</returns>
    public async ValueTask CommitBatchAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var batch = _batch ?? throw new InvalidOperationException("无进行中的原子批——先 BeginAtomicBatch");

        await _kv.CommitBatchAsync(batch, ct).ConfigureAwait(false);

        // 提交后批写并入写集（ReadMyWrites/Serializable——本会话后续读见批值）
        if (_writeSet is not null)
            foreach (var (key, entry) in batch)
                _writeSet[key] = (entry.Payload, entry.Expiry);
        _batch = null;
    }

    /// <summary>回滚原子批（全丢弃——暂存零应用，写集零污染）。</summary>
    /// <exception cref="InvalidOperationException">无进行中的原子批。</exception>
    public void AbortBatch()
    {
        ThrowIfDisposed();
        _ = _batch ?? throw new InvalidOperationException("无进行中的原子批——先 BeginAtomicBatch");
        _batch = null;
    }

    /// <summary>原子批是否进行中。</summary>
    public bool IsBatchActive => _batch is not null;

    // ═══════════════════════════════════════════════════════════════════
    // pending 收口（CompletePendingAsync——会话簿记 + 背压点）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 收口本会话全部 pending 写（FireAndForget/WaitForPending 登记的高水位）——
    /// Ring 刷盘至高水位，其后本会话写均持久化。批提交写不在此列（Prepare 已落盘）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>刷盘任务（无 pending 时为已完成）。</returns>
    public ValueTask CompletePendingAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var watermark = _pendingWatermark;
        if (watermark == LogicalAddress.Empty)
            return ValueTask.CompletedTask;
        return _kv.FlushAsync(watermark, ct);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Functions 完整模型面（W4——读钩子按档位派发 + RMW 写集集成）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Functions 驱动读：写集/批暂存命中（RMW/Serializable 档）→ 本会话值直接翻译（墓碑=NotFound）；
    /// 未命中 → 索引读（kv 引擎 ConcurrentReader 路径）。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="input">读操作输入。</param>
    /// <param name="functions">KV Functions 完整模型（读侧钩子翻译 value→output）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态与翻译产物（NotFound/Ok）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    public async ValueTask<(KvStatus Status, TOutput Output)> ReadAsync<TInput, TOutput, TContext>(
        TKey key, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(functions);

        var (tracked, local, localExpiry) = PeekLocalValue(key);
        if (!tracked && _conditions != KvSessionConditions.Serializable)
            return await _kv.ReadAsync(key, input, functions, context, ct).ConfigureAwait(false);

        var output = default(TOutput)!;   // functions 契约：output ref 非空；NotFound 语义由 status 承载
        if (tracked && KvValueFraming.IsExpired(localExpiry, DateTime.UtcNow.Ticks))
        {
            functions.ReadCompletionCallback(ref key, ref input, ref output, KvStatus.NotFound, context);
            return (KvStatus.NotFound, output);   // 写集/批条目已过期——惰性读删
        }
        KvStatus status;
        if (tracked && local is { } bytes)
        {
            var value = _kv.Formatter.Parse(bytes);
            if (_conditions == KvSessionConditions.Serializable)
                functions.SingleReader(ref key, ref input, ref value, ref output, ref context);
            else
                functions.ConcurrentReader(ref key, ref input, ref value, ref output, ref context);
            status = KvStatus.Ok;
        }
        else if (_conditions == KvSessionConditions.Serializable)
        {
            // Serializable 索引读——排他读上下文派发单读者钩子
            var indexBytes = await _kv.TryGetBytesAsync(key, ct).ConfigureAwait(false);
            if (indexBytes is { } idxBytes)
            {
                var value = _kv.Formatter.Parse(idxBytes);
                functions.SingleReader(ref key, ref input, ref value, ref output, ref context);
                status = KvStatus.Ok;
            }
            else
            {
                status = KvStatus.NotFound;
            }
        }
        else
        {
            status = KvStatus.NotFound;   // 写集/批墓碑
        }
        functions.ReadCompletionCallback(ref key, ref input, ref output, status, context);
        return (status, output);
    }

    /// <summary>
    /// Functions 驱动 RMW（三流折叠经 kv 引擎；Ok 后写集刷新——本会话自见折叠结果；
    /// policy 决定持久化时机）。批内不支持（批+RMW 组合语义 W6 收口）。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="input">RMW 操作输入（增量/参数）。</param>
    /// <param name="functions">KV Functions 完整模型（三流折叠）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态（Ok/Error）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    public async ValueTask<KvStatus> RmwAsync<TInput, TOutput, TContext>(
        TKey key, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(functions);

        // ═══ 批内 RMW（W6 尾巴——组合语义）：读解析（批→写集→存储）→ 三流折叠 → 暂存折叠结果；
        // 折叠产物随批提交帧化落环（批内零 IO）。RMW 失败 = Error 不暂存（批其余操作照常）。
        if (_batch is not null)
        {
            var (batchTracked, batchLocal, _) = PeekLocalValue(key);
            byte[]? current = batchTracked ? batchLocal : await _kv.TryGetBytesAsync(key, ct).ConfigureAwait(false);

            var output = default(TOutput)!;   // functions 契约：output ref 非空；NotFound 语义由 status 承载
            byte[] staged;
            if (current is null)
            {
                var newValue = default(TValue)!;
                if (!functions.InitialUpdater(ref key, ref input, ref newValue, ref output, ref context))
                {
                    functions.RmwCompletionCallback(ref output, KvStatus.Error, context);
                    return KvStatus.Error;   // 折叠失败——零暂存
                }
                staged = newValue is null ? [] : ToBytes(newValue);
            }
            else
            {
                var oldValue = _kv.Formatter.Parse(current)!;   // 值帧契约：current 为存在帧——Parse 非空
                var newValue = oldValue;
                if (functions.InPlaceUpdater(ref key, ref input, ref newValue, ref output, ref context))
                {
                    staged = ToBytes(newValue);
                }
                else
                {
                    var folded = default(TValue)!;
                    if (!functions.CopyUpdater(ref key, ref input, ref oldValue, ref folded, ref output, ref context))
                    {
                        functions.RmwCompletionCallback(ref output, KvStatus.Error, context);
                        return KvStatus.Error;
                    }
                    staged = ToBytes(folded);
                }
            }

            _batch.Add(new(key, (staged, 0)));   // 批内 RMW 无 TTL（随批提交落环）
            if (_writeSet is not null)
                _writeSet[key] = (staged, 0);
            functions.RmwCompletionCallback(ref output, KvStatus.Ok, context);
            return KvStatus.Ok;
        }

        var status = await _kv.RmwAsync(key, input, functions, context, KvCommitPolicy.FireAndForget,
            timeToLive, ct).ConfigureAwait(false);
        if (status == KvStatus.Ok)
        {
            if (_writeSet is not null)
                _writeSet[key] = (await _kv.TryGetBytesAsync(key, ct).ConfigureAwait(false),
                    timeToLive is { } ttl ? DateTime.UtcNow.Ticks + ttl.Ticks : 0);   // 自见折叠结果（RMW 重置 TTL）
            await ApplyPolicyAsync(_kv.TailAddress, policy, ct).ConfigureAwait(false);
        }
        return status;

        byte[] ToBytes(TValue v)
        {
            var size = _kv.Formatter.GetSize(v);
            var buf = new byte[size];
            _kv.Formatter.Format(v, buf);
            return buf;
        }
    }

    /// <summary>
    /// Functions 驱动 Upsert（会话上下文走
    /// <see cref="IKvFunctions{TKey, TInput, TValue, TOutput, TContext}.ConcurrentWriter"/> 校验钩子——
    /// false 拒绝以 Error 收口零写入）。批内不支持。
    /// </summary>
    /// <param name="key">键。</param>
    /// <param name="value">待写入的值。</param>
    /// <param name="input">操作输入。</param>
    /// <param name="functions">KV Functions 完整模型（ConcurrentWriter 校验钩子）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <param name="timeToLive">TTL（过期惰性读删；null=无过期）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态（Ok=接受；Error=ConcurrentWriter 拒绝）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    /// <exception cref="InvalidOperationException">原子批进行中（批内 Upsert 暂不支持）。</exception>
    public async ValueTask<KvStatus> UpsertAsync<TInput, TOutput, TContext>(
        TKey key, TValue value, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(functions);
        if (_batch is not null)
            throw new InvalidOperationException("原子批进行中——批内 Upsert 暂不支持");

        if (!functions.ConcurrentWriter(ref key, ref value))
        {
            functions.UpsertCompletionCallback(ref key, ref value, KvStatus.Error, context);
            return KvStatus.Error;
        }

        var size = _kv.Formatter.GetSize(value);
        var buffer = new byte[size];
        _kv.Formatter.Format(value, buffer);
        await PutAsync(key, buffer, policy, timeToLive, ct).ConfigureAwait(false);   // 写集登记随 PutAsync

        functions.UpsertCompletionCallback(ref key, ref value, KvStatus.Ok, context);
        return KvStatus.Ok;
    }

    /// <summary>Functions 驱动删除（写集墓碑 + DeleteCompletionCallback NotFound/Ok）。批内不支持。</summary>
    /// <param name="key">键。</param>
    /// <param name="input">操作输入。</param>
    /// <param name="functions">KV Functions 完整模型（DeleteCompletionCallback 回调）。</param>
    /// <param name="context">用户上下文（完成回调回传）。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>操作状态（Ok=删除；NotFound=key 不存在）。</returns>
    /// <exception cref="ArgumentNullException">functions 为 null。</exception>
    /// <exception cref="InvalidOperationException">原子批进行中（批内 Delete 暂不支持）。</exception>
    public async ValueTask<KvStatus> DeleteAsync<TInput, TOutput, TContext>(
        TKey key, TInput input,
        IKvFunctions<TKey, TInput, TValue, TOutput, TContext> functions,
        TContext context,
        KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(functions);
        if (_batch is not null)
            throw new InvalidOperationException("原子批进行中——批内 Delete 暂不支持");

        var existed = await _kv.DeleteAsync(key, ct).ConfigureAwait(false);
        var status = existed ? KvStatus.Ok : KvStatus.NotFound;
        if (existed)
        {
            if (_writeSet is not null)
                _writeSet[key] = (null, 0);
            await ApplyPolicyAsync(_kv.TailAddress, policy, ct).ConfigureAwait(false);
        }
        functions.DeleteCompletionCallback(ref key, status, context);
        return status;
    }

    /// <summary>地址版 CAS（乐观锁——kv 引擎地址版 CompareAndSwapAsync）。
    /// 成功后写集登记（ReadMyWrites/Serializable 自见新值，过期语义与盘上帧一致）。</summary>
    /// <param name="key">键。</param>
    /// <param name="expectedAddress">预期的当前绑定地址（Invalid = 预期不存在）。</param>
    /// <param name="value">新值字节。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <param name="timeToLive">TTL（获取即租约——null = 无过期；引擎临界区内比较通过后起算）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CAS 回执（换绑结果 + 当前/新绑定地址——fencing token = NewAddress）。</returns>
    /// <exception cref="InvalidOperationException">原子批进行中（CAS 即时语义与批暂存冲突——批内不支持）。</exception>
    public async ValueTask<KvCasResult> CompareAndSwapAsync(TKey key, LogicalAddress expectedAddress,
        ReadOnlyMemory<byte> value, KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_batch is not null)
            throw new InvalidOperationException("原子批进行中——CAS 即时语义与批暂存冲突，批内不支持");

        var result = await _kv.CompareAndSwapAsync(key, expectedAddress, value, policy, timeToLive, ct)
            .ConfigureAwait(false);
        if (result.Swapped)
        {
            if (_writeSet is not null)
                _writeSet[key] = (value.ToArray(), KvValueFraming.ExpiryTicks(timeToLive));
            await ApplyPolicyAsync(result.NewAddress, policy, ct).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>值版便捷 CAS（kv 引擎值版 CompareAndSwapAsync；expectedValue=null = 预期不存在）。
    /// 成功后写集登记（ReadMyWrites/Serializable 自见新值，过期语义与盘上帧一致）。</summary>
    /// <param name="key">键。</param>
    /// <param name="expectedValue">预期的当前值字节（null = 预期不存在）。</param>
    /// <param name="value">新值字节。</param>
    /// <param name="policy">完成语义（Committed 即时刷；其余登记高水位）。</param>
    /// <param name="timeToLive">TTL（获取即租约——null = 无过期；引擎临界区内比较通过后起算）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>CAS 回执（换绑结果 + 当前/新绑定地址——fencing token = NewAddress）。</returns>
    /// <exception cref="InvalidOperationException">原子批进行中（CAS 即时语义与批暂存冲突——批内不支持）。</exception>
    public async ValueTask<KvCasResult> CompareAndSwapAsync(TKey key, byte[]? expectedValue,
        ReadOnlyMemory<byte> value, KvCommitPolicy policy = KvCommitPolicy.FireAndForget,
        TimeSpan? timeToLive = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (_batch is not null)
            throw new InvalidOperationException("原子批进行中——CAS 即时语义与批暂存冲突，批内不支持");

        var result = await _kv.CompareAndSwapAsync(key, expectedValue, value, policy, timeToLive, ct)
            .ConfigureAwait(false);
        if (result.Swapped)
        {
            if (_writeSet is not null)
                _writeSet[key] = (value.ToArray(), KvValueFraming.ExpiryTicks(timeToLive));
            await ApplyPolicyAsync(result.NewAddress, policy, ct).ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>会话本地值探查（批暂存后进先出 → 写集；Tracked=false=无本地记录——索引回落；
    /// Payload null=本地墓碑）。</summary>
    private (bool Tracked, byte[]? Payload, long Expiry) PeekLocalValue(TKey key)
    {
        if (_batch is not null)
            for (var i = _batch.Count - 1; i >= 0; i--)
                if (EqualityComparer<TKey>.Default.Equals(_batch[i].Key, key))
                    return (true, _batch[i].Value.Payload, _batch[i].Value.Expiry);
        if (_writeSet is not null && _writeSet.TryGetValue(key, out var staged))
            return (true, staged.Payload, staged.Expiry);
        return (false, null, 0);
    }

    // ═══════════════════════════════════════════════════════════════════
    // 释放
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>结束会话：丢弃未提交原子批（安全——零应用）+ 回收写集。EPVS 保护为 per-op
    /// 同步临界区形态——释放无 epoch 义务（无线程亲和契约）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _batch = null;   // 未提交批 = 全丢弃（暂存零应用）
        _writeSet = null;
        _kv.OnSessionDisposed();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 内部装配（写集登记在应用点同步完成；此处只管持久化策略与水位）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>持久化策略（Committed 即时刷；FireAndForget/WaitForPending 登记高水位）。</summary>
    private async ValueTask ApplyPolicyAsync(LogicalAddress addr, KvCommitPolicy policy,
        CancellationToken ct)
    {
        if (!addr.IsValid)
            return;
        if (policy == KvCommitPolicy.Committed)
            await _kv.FlushAsync(addr, ct).ConfigureAwait(false);
        else
            TrackPending(addr);
    }

    private void TrackPending(LogicalAddress addr)
    {
        if (addr.IsValid && addr != LogicalAddress.Empty && addr > _pendingWatermark)
            _pendingWatermark = addr;
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);
}
