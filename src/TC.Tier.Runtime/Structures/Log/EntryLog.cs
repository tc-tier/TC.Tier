
namespace TC.Tier.Runtime.Structures.Log;

/// <summary>
/// EntryLog — 通用 entry 顺序日志实现类（WAL 是其典型用途，非唯一用途）。
/// <para>★ EntryLog.md：通用 per-entry [16B EntryCodec header][payload+4B对齐] 纯追加顺序日志。</para>
/// <para>★ 提交模型（两层，职责分离）：</para>
/// <para>  1. <b>底层页提交契约（恒成立，不可配置）</b>：Append 跨页 → FlushPage 数据落盘 →
///         <see cref="OnPageFlushed"/> 同步执行 commit 链（meta.Commit + 推进 CommittedOffset）。
///         这是页模式的本质保证——前一页换出时即持久化，无需任何策略注入。最坏情况：写满一页才提交。</para>
/// <para>  2. <b>可选提前提交（注入式优化，降延迟）</b>：<see cref="ICommitPolicy"/> 在页未满时提前触发提交。
///         注入 null（默认）= 仅依赖底层页契约；注入 <see cref="GroupCommitPolicy"/> = 三维度提前提交。</para>
/// <para>★ 上述两层取代旧的"fire-and-forget 后台循环 + Append 内 spawn 提交"模型——后者线程池饥饿即系统挂，
///         且错误被静默吞。新模型提交都在写线程同步完成，无并发、错误冒泡。</para>
/// </summary>
public sealed partial class EntryLog : LogBase
{
    private readonly ICommitPolicy _earlyCommitPolicy; // ★ 永不为 null：构造时从 settings 自动创建默认 GroupCommitPolicy

    private readonly TimeSpan _commitInterval;

    // commit 状态（CommittedOffset 单调推进）
    private LogicalAddress _committedOffset;
    private int _earlyUnflushedCount; // 自上次"提前提交"以来累计条数（UnflushedBytes 改地址差实时算，见 BuildSnapshot）
    private long _lastCommitTicks;
    private readonly object _commitLock = new();
    // ★ 提交重入守卫：CommitCore→AppendMeta 写 meta entry 时若触发页满 FlushWindow→OnPageFlushed→CommitCore
    //   会无限递归（栈溢出）。meta entry 是提交记录本身，不需要再触发提交——重入时跳过 CommitCore。
    private volatile bool _inCommit;
    private CancellationTokenSource? _loopCts;
    private Task? _earlyCommitLoopTask;
    private Exception? _lastCommitError; // 提交错误冒泡（上层查询）
    private readonly EntryLogSettings _settings;

    /// <summary>已 commit 的最高地址（commit 边界水位）。</summary>
    public LogicalAddress CommittedOffset
    {
        get { lock (_commitLock) return _committedOffset; }
    }

    /// <summary>最近一次提交错误（若有）。上层应周期查询以发现持久化失败。</summary>
    public Exception? LastCommitError => Volatile.Read(ref _lastCommitError);

    /// <summary>
    /// 构造 EntryLog——通用 entry 顺序日志（WAL 是其典型用途）。
    /// <para>★ commitPolicy 为 null 时自动从 settings 构造默认 <see cref="GroupCommitPolicy"/>（三维度阈值全部生效）。</para>
    /// </summary>
    /// <param name="fileSystem">文件系统（主引擎与可选 Managed meta 引擎共用）。</param>
    /// <param name="settings">EntryLog 配置（页几何/提交阈值/MetaPolicyKind 等）。</param>
    /// <param name="commitPolicy">提交策略（null = 默认 GroupCommitPolicy 三维度提前提交）。</param>
    /// <param name="recovery">恢复策略（null = 默认 EntryLogRecovery——恢复后设 CommittedOffset + 启提前提交循环）。</param>
    /// <param name="cursorFactory">扫描游标工厂。</param>
    /// <param name="metaPolicyFactory">meta 策略工厂（null = 按 settings.MetaPolicyKind 默认装配）。</param>
    /// <param name="metaTransport">Transport 模式的外部 meta 传输（Managed/Disabled 不用）。</param>
    public EntryLog(IFileSystem fileSystem,EntryLogSettings settings,
        ICommitPolicy? commitPolicy = null,
        IRecovery<LogRecoveryHints>? recovery = null,
        LogCursorFactory<ILogCursor>? cursorFactory = null,
        MetaPolicyFactory<LogMetaHeader, LogMetaPayload>? metaPolicyFactory = null,
        IMetaTransport? metaTransport = null)
        : base(new Codec(),fileSystem, settings, recovery, cursorFactory, metaPolicyFactory, metaTransport)
    {
        _settings = settings;
        _commitInterval = settings.CommitInterval;
        // ★ commitPolicy 为 null 时，自动从 settings 构造默认 GroupCommitPolicy（三维度阈值全部生效）
        _earlyCommitPolicy = commitPolicy ?? new GroupCommitPolicy
        {
            MaxUnflushedBytes = settings.MaxUnflushedBytes,
            MaxUnflushedCount = settings.MaxUnflushedCount,
            Interval = settings.CommitInterval,
        };
        _committedOffset = LogicalAddress.Empty; // Initialize 后由 OnLogRecovered 更新（恢复后 _logicalTail 已正确）
        _lastCommitTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>★ EntryLog 专属 Recovery——继承 <see cref="LogBase.LogRecovery{TLogBase}"/>（复用四级回退），
    /// override <see cref="LogBase.LogRecovery{TLogBase}.OnLogRecovered"/> 在恢复后设 _committedOffset + 启 StartEarlyCommitLoop
    ///（修复原 OnInitialize 里 _committedOffset 取到 Empty 的 bug）。</summary>
    private sealed class EntryLogRecovery(EntryLog owner) : LogRecovery<EntryLog>(owner)
    {
        private readonly EntryLog _owner = owner;
        protected override void OnLogRecovered()
        {
            _owner._committedOffset = _owner.TailAddress; // ★ 恢复后 _logicalTail 已正确，_committedOffset 天然正确
            _owner.StartEarlyCommitLoop();
        }
    }

    /// <inheritdoc/>（默认 EntryLogRecovery——Initialize 的 CAS 闸门内创建一次；注入实例经构造函数直接赋 _recovery）
    protected override IRecovery<LogRecoveryHints> CreateRecovery()
        => new EntryLogRecovery(this);

    /// <summary>
    /// 每次 Append/AppendAsync/AppendBatch 后调用：累计未提交量，按策略判定是否提前提交。
    /// <para>★ 默认 GroupCommitPolicy（从 settings 三维度阈值构造），三维度任一满足即触发。</para>
    /// <para>★ P0 修复：提前提交必须先 flush（FlushUntil）把内存页数据落盘，再推进 CommittedOffset——</para>
    /// <para>  保证 §7 不变量 CommittedOffset ≤ FlushedUntil（已 commit 必已落盘）。</para>
    /// <para>  旧实现直接 CommitCore(TailAddress) 跳过 flush，TailAddress 可能指向仍在 _pageBuf 内存的 entry，</para>
    /// <para>  导致 _committedOffset 超前于真实落盘字节，崩溃丢失已 commit 数据。</para>
    /// </summary>
    protected override void OnAppended(LogicalAddress entryAddress, int payloadLength, bool isMeta)
    {
        // ★ meta entry 来自 AppendMeta 委托链（CommitCore→AppendMeta→策略→WriteMetaPayload→AppendCore isMeta=true），
        //   它本身就是提交动作的一部分，不能再触发提前提交——否则 CommitCore→写 meta→OnAppended→CommitCore 无限递归栈溢出。
        //   提前提交钩子只服务于业务 Append（isMeta=false）。
        if (isMeta) return;

        // ★ P0 修复：UnflushedBytes 改地址差实时算（BuildSnapshot 用 GetDistance），
        //   不再累计 _earlyUnflushedBytes（消除 AdvanceCommittedOffset 无条件重置的 TOCTOU）。
        Interlocked.Increment(ref _earlyUnflushedCount);

        var snap = BuildSnapshot();
        if (_earlyCommitPolicy.ShouldCommit(in snap))
        {
            // 同步执行提前提交（在写线程内，无 fire-and-forget，错误冒泡）
            CommitWithFlush(TailAddress);
        }
    }

    /// <summary>
    /// ★ 提前提交执行链（同步轨）：先 FlushUntil 落盘，再 CommitCore 推进水位。
    /// <para>区别于裸 <see cref="CommitCore"/>（仅供 OnPageFlushed 回调用——数据已落盘，不能再 flush）。</para>
    /// <para>提前提交触发源（OnAppended / 后台循环）的数据可能仍在内存页缓冲，必须先 flush 才能保证不变量。</para>
    /// <para>★ STORAGE-005 (#225) 崩溃一致性保证：顺序严格为 data fsync 先于 meta fsync——
    /// FlushUntil(Step1) 经 engine.Write + engine.Flush 把 commitTarget 及之前的页落盘（含 fsync），
    /// 之后 CommitCore(Step2) 才 AppendMeta + meta.Commit（meta fsync）。断电时 meta 绝不会标记
    /// 一个 data 尚未落盘的 commit 点。</para>
    /// <para>★ WriteThrough 降级：当 EntryLog 底层引擎 PersistenceMode == WriteThrough 时，engine.Flush
    /// 是 no-op（每次 engine.Write 已同步落盘），data 持久性由 WriteThrough 保证而非显式 fsync。
    /// 此路径的 EntryLogSettings 默认走 WriteThrough（见 §0.6 Mode C），故 FlushUntil 的显式 fsync
    /// 在该模式下被吸收——不变量仍成立。</para>
    /// </summary>
    private void CommitWithFlush(LogicalAddress commitTarget)
    {
        // 1. FlushUntil：把内存页（含 commitTarget 及之前）落盘。FlushWindow 会回调 OnPageFlushed(flushedUntil)
        //    同步推进 CommittedOffset 到 flushedUntil（页契约）。
        //    ★ STORAGE-005：data 先于 meta 落盘——此步的 engine.Write + Flush 在 Step2 写 meta 之前完成。
        FlushUntil(commitTarget);
        // 2. commitTarget 可能 > flushedUntil（末页未满时 FlushUntil 只 flush 到页边界）——显式推进到 commitTarget。
        //    ★ CommitCore 内 AppendMeta + meta.Commit（meta fsync）在此步——data 已在 Step1 落盘。
        CommitCore(commitTarget);
    }

    /// <summary>★ 提前提交执行链（异步轨）：先 FlushUntilAsync 落盘，再 CommitCoreAsync 推进水位。</summary>
    private async ValueTask CommitWithFlushAsync(LogicalAddress commitTarget, CancellationToken ct = default)
    {
        await FlushUntilAsync(commitTarget, ct).ConfigureAwait(false);
        await CommitCoreAsync(commitTarget).ConfigureAwait(false);
    }

    /// <summary>
    /// ★ 底层页提交契约（恒成立）：每次页 flush 落盘后同步 commit 到 flushedUntil。
    /// <para>由基类 FlushPage（跨页 Append / Flush / Dispose）在 IO 完成后调用。此方法在写线程同步执行，
    /// 无并发、无 fire-and-forget、错误冒泡到 <see cref="LastCommitError"/>。</para>
    /// <para>这是页模式的本质保证——前一页换出即持久化（数据 + meta + CommittedOffset 推进）。
    /// 不依赖任何注入策略，删除 <see cref="ICommitPolicy"/> 此保证依然成立。</para>
    /// </summary>
    /// <param name="committedTail">已 flush 落盘的页尾地址（含）</param>
    protected override void OnPageFlushed(LogicalAddress committedTail)
    {
        // ★ 重入守卫：CommitCore→AppendMeta 写 meta entry 触发的页 flush 不再递归提交（防栈溢出）
        if (_inCommit) return;
        // ★ Dispose 守卫：FlushOnDispose 期间的页 flush 不再触发 commit——Dispose 的职责是把
        //   脏页冲到盘上（避免数据丢失），但不应隐式篡改 commit 边界（CommittedOffset 的推进
        //   应由显式 CommitAsync/group commit 驱动；未 commit 的末页重启后按未 commit 处理，符合 WAL 语义）。
        if (IsDisposed) return;
        // 仅当 flushedUntil 超过当前 commit 边界才提交（避免末页重复 flush 时无谓 commit）
        lock (_commitLock) { if (committedTail <= _committedOffset) return; }
        CommitCore(committedTail);
    }

    /// <summary>
    /// ★ 底层页提交契约（异步轨）：异步入口（AppendAsync/FlushAsync/PrepareAsync）的页提交挂载点。
    /// <para>走纯异步 commit 链（<see cref="CommitCoreAsync"/> → meta.CommitAsync），无 sync-over-async。</para>
    /// </summary>
    /// <param name="committedTail">已 flush 落盘的页尾地址（含）</param>
    protected override async ValueTask OnPageFlushedAsync(LogicalAddress committedTail)
    {
        // ★ 重入守卫（同 OnPageFlushed）
        if (_inCommit) return;
        // ★ Dispose 守卫（同 OnPageFlushed 同步版）
        if (IsDisposed) return;
        lock (_commitLock) { if (committedTail <= _committedOffset) return; }
        await CommitCoreAsync(committedTail).ConfigureAwait(false);
    }

    /// <summary>
    /// ★ Abort 尾截断后夹 CommittedOffset 到回退点——group commit 可能把悬干数据标记为已提交
    /// （Prepare 的 AppendMeta(TailAddress) 同样推进 meta CommittedOffset），回滚必须一并夹回，
    /// 否则 CommittedOffset &gt; TailAddress 违反 §7 不变量、Replay 读到已物理销毁的区域。
    /// </summary>
    /// <param name="rollbackTail">回退的尾地址（含）</param>
    protected override void OnAborted(LogicalAddress rollbackTail)
    {
        lock (_commitLock)
        {
            if (_committedOffset > rollbackTail) _committedOffset = rollbackTail;
        }
    }

    /// <summary>
    /// ★ 普通尾截断（raft 冲突修正——TruncateSuffix）后夹 CommittedOffset 到截断边界：
    /// 同 OnAborted 语义（截断后 commit 边界不得越过物理尾——否则 Replay 越界跳段报
    /// Segment not found）。OnAborted 只覆盖 2PC Abort 路径，此处补全普通截断。
    /// </summary>
    /// <param name="rollbackTail">回退的尾地址（含）</param>
    protected override void OnTailTruncated(LogicalAddress rollbackTail)
    {
        lock (_commitLock)
        {
            if (_committedOffset > rollbackTail) _committedOffset = rollbackTail;
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 提交执行链（同步内联，临界区内捕获+推进合并）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 提交核心（同步轨）：数据已落盘到 commitTarget，本方法把 meta + CommittedOffset 推进到 commitTarget。
    /// <para>★ commit 覆盖全部待提交数据（≤ commitTarget），提交后直接重置计数器——不再用 Offset 差值做地址算术
    /// （LogicalAddress.Offset 是段内偏移，跨段减法无意义）。</para>
    /// <para>★ 错误冒泡：异常写入 <see cref="_lastCommitError"/> 供上层查询，不静默吞。</para>
    /// </summary>
    /// <param name="commitTarget">提交目标地址（含）</param>
    private void CommitCore(LogicalAddress commitTarget)
    {
        // 临界区：单调推进 CommittedOffset 检查
        lock (_commitLock)
        {
            // ★ 零数据推进 + 无 opaque 脏 → 无可提交；纯 opaque 提交（数据为空但 meta 完整）凭脏标记放行
            if (commitTarget <= _committedOffset && !_opaqueDirty) return;
        }

        // meta 持久化（同步轨：经 AppendMeta 委托策略，IO 锁外）
        // ★ _inCommit 守卫：AppendMeta 写 meta entry 时若触发页满 FlushWindow→OnPageFlushed，
        //   OnPageFlushed 见 _inCommit=true 跳过 CommitCore，防无限递归栈溢出。
        _inCommit = true;
        try
        {
            AppendMeta(commitTarget);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastCommitError, ex);
            throw;
        }
        finally
        {
            _inCommit = false;
        }

        AdvanceCommittedOffset(commitTarget);
    }

    /// <summary>
    /// 提交核心（异步轨）：对等 <see cref="CommitCore"/>，走异步 meta.CommitAsync。
    /// </summary>
    /// <param name="commitTarget">提交目标地址（含）</param>
    private async ValueTask CommitCoreAsync(LogicalAddress commitTarget)
    {
        lock (_commitLock)
        {
            // ★ 同步版：零数据推进 + 无 opaque 脏 → 无可提交（纯 opaque 提交凭脏标记放行）
            if (commitTarget <= _committedOffset && !_opaqueDirty) return;
        }

        _inCommit = true;
        try
        {
            await AppendMetaAsync(commitTarget, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _lastCommitError, ex);
            throw;
        }
        finally
        {
            _inCommit = false;
        }

        AdvanceCommittedOffset(commitTarget);
    }

    /// <summary>推进 CommittedOffset + 重置计数器 + 唤醒等待者（CommitCore/CommitCoreAsync 共用尾部）。</summary>
    /// <param name="commitTarget">提交目标地址（含）</param>
    private void AdvanceCommittedOffset(LogicalAddress commitTarget)
    {
        lock (_commitLock)
        {
            if (commitTarget > _committedOffset)
            {
                _committedOffset = commitTarget;
                // ★ P0 修复：UnflushedBytes 改地址差实时算（BuildSnapshot），无需重置 _earlyUnflushedBytes。
                //   _earlyUnflushedCount 仍需重置（条数维度无地址等价物）；轻微偏差无害（只影响触发频率，不丢数据）。
                Interlocked.Exchange(ref _earlyUnflushedCount, 0);
            }

            _lastCommitTicks = DateTime.UtcNow.Ticks;
            Volatile.Write(ref _lastCommitError, null);
            Monitor.PulseAll(_commitLock);
        }
    }

    /// <summary>
    /// ★ 显式提交（异步轨）：用户显式调 CommitAsync，保证当前写游标 TailAddress 之前的所有 entry 已 commit。
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>表示异步提交操作的任务</returns>
    public async ValueTask CommitAsync(CancellationToken ct = default)
    {
        LogicalAddress commitTarget;
        lock (_commitLock)
        {
            commitTarget = TailAddress;
        } // 捕获当前写游标

        // ★ 复用 CommitWithFlushAsync：flush 落盘 + 推进水位（保证 §7 不变量）。
        //    用户显式调 CommitAsync 接受末页同步 flush（避免双页交替在 commit 路径的复杂性）。
        await CommitWithFlushAsync(commitTarget, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// ★ 等待 commit：阻塞等待直到 CommittedOffset ≥ <paramref name="untilAddress"/>。
    /// </summary>
    /// <param name="untilAddress">等待的目标地址</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>表示等待操作的任务</returns>
    public ValueTask WaitForCommitAsync(LogicalAddress untilAddress, CancellationToken ct = default)
    {
        while (CommittedOffset < untilAddress)
        {
            // 锁内等待 CommitCore 的 PulseAll 信号（取代旧的 Task.Delay(1) 轮询）
            lock (_commitLock)
            {
                if (_committedOffset >= untilAddress) return ValueTask.CompletedTask;
                if (_lastCommitError is { } err) return ValueTask.FromException(err); // 提交失败冒泡给等待者
                Monitor.Wait(_commitLock, 10); // 10ms 兜底超时（防信号丢失）
            }

            ct.ThrowIfCancellationRequested();
        }

        return ValueTask.CompletedTask;
    }

    // ═══════════════════════════════════════════════════════════════════
    // ★ 读 / 重放（WAL 核心能力）——只重放已 commit 的 entry（≤ CommittedOffset）
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// ★ 打开重放游标：从 <paramref name="fromAddress"/> 顺序扫描到当前 <see cref="CommittedOffset"/>
    /// （快照），只读已 commit 的 entry。未 commit 数据不会被重放（WAL 一致性语义）。
    /// <para>★ 返回的 cursor 的 <see cref="ILogCursor.CurrentPayload"/> 是零拷贝 Span（指向读帧内），
    /// 禁止跨 <c>MoveNext</c> 持有。回调式重放见 <see cref="Replay(EntryReplayHandler, Boolean)"/>。</para>
    /// <para>★ <paramref name="verifyCrc"/>=false（默认）：只验 Magic，跳 CRC（快速重放，约快 10-50×）。
    /// true = 每条全量验 CRC（数据完整性审计用）。</para>
    /// </summary>
    /// <param name="fromAddress">重放起始地址（默认 0 = 从头重放）。</param>
    /// <param name="verifyCrc">是否每条验 CRC（默认 false 快速扫描）。</param>
    private ILogCursor OpenReplayCursor(LogicalAddress fromAddress = default, bool verifyCrc = false)
    {
        EnsureNotDisposed();
        var committedSnapshot = CommittedOffset; // 快照：重放期间不随新 commit 扩展
        return OpenCursor(fromAddress, committedSnapshot, verifyCrc);
    }

    /// <summary>
    /// ★ 重放已 commit 的 entry（回调式，零装箱）：从 <paramref name="fromAddress"/> 扫到 <see cref="CommittedOffset"/>，
    /// 对每条 entry 调 <paramref name="handler"/>。
    /// </summary>
    /// <param name="fromAddress">重放起始地址（默认 0 = 从头重放）。</param>
    /// <param name="handler">处理每条 entry 的回调。</param>
    /// <param name="verifyCrc">是否每条验 CRC（默认 false 快速扫描）。</param>
    /// <returns>重放的 entry 条数。</returns>
    public long Replay(LogicalAddress fromAddress, EntryReplayHandler handler, bool verifyCrc = false)
    {
        ArgumentNullException.ThrowIfNull(handler);
        using var cursor = OpenReplayCursor(fromAddress, verifyCrc);
        long count = 0;
        while (cursor.MoveNext())
        {
            handler(cursor.CurrentPayload, cursor.CurrentIsMeta, cursor.CurrentAddress);
            count++;
        }

        return count;
    }

    /// <summary>
    /// ★ 从头重放（便捷重载，默认快速扫描）：对每条 entry 调 <paramref name="handler"/>。
    /// </summary>
    /// <param name="handler">处理每条 entry 的回调。</param>
    /// <param name="verifyCrc">是否每条验 CRC（默认 false 快速扫描）。</param>
    /// <returns>重放的 entry 条数。</returns>
    public long Replay(EntryReplayHandler handler, bool verifyCrc = false) => Replay(LogicalAddress.Empty, handler, verifyCrc);

    /// <summary>
    /// ★ 异步重放已 commit 的 entry（回调式，零装箱）：从 <paramref name="fromAddress"/> 扫到 <see cref="CommittedOffset"/>，
    /// 对每条 entry 调 <paramref name="handler"/>。
    /// </summary>
    /// <param name="fromAddress">重放起始地址（默认 0 = 从头重放）。</param>
    /// <param name="handler">处理每条 entry 的回调。</param>
    /// <param name="verifyCrc">是否每条验 CRC（默认 false 快速扫描）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重放的 entry 条数。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="handler"/> 为 null 时抛出。</exception>
    public async ValueTask<long> ReplayAsync(
        LogicalAddress fromAddress,
        AsyncEntryReplayHandler handler,
        bool verifyCrc = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        await using var cursor = OpenReplayCursor(fromAddress, verifyCrc);
        long count = 0;
        while (await cursor.MoveNextAsync(ct).ConfigureAwait(false))
        {
            await handler(cursor.CurrentPayload, cursor.CurrentIsMeta, cursor.CurrentAddress, ct).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    /// <summary>★ 从头异步重放（便捷重载，默认快速扫描）。</summary>
    /// <param name="handler">处理每条 entry 的回调。</param>
    /// <param name="verifyCrc">是否每条验 CRC（默认 false 快速扫描）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>重放的 entry 条数。</returns>
    /// <exception cref="ArgumentNullException">当 <paramref name="handler"/> 为 null 时抛出。</exception>
    public ValueTask<long> ReplayAsync(AsyncEntryReplayHandler handler, bool verifyCrc = false,
        CancellationToken ct = default)
        => ReplayAsync(LogicalAddress.Empty, handler, verifyCrc, ct);

    // ═══════════════════════════════════════════════════════════════════
    // 可选提前提交循环（仅时间维度提醒；字节/条数维度在 OnAppended 内联判定）
    // ═══════════════════════════════════════════════════════════════════

    private void StartEarlyCommitLoop()
    {
        // 显式禁用时间维度 → 不启动循环
        if (_commitInterval == TimeSpan.FromMilliseconds(-1)) return;

        _loopCts = new CancellationTokenSource();
        var ct = _loopCts.Token;
        _earlyCommitLoopTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_commitInterval, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                var snap = BuildSnapshot();
                if (!_earlyCommitPolicy.ShouldCommit(in snap)) continue;
                try
                {
                    // ★ 后台循环只推进 CommittedOffset 到 Log 自管真实水位 FlushedTail（已落盘 frame 尾），
                    //   不调 FlushPage——FlushPage 操作共享页缓冲 _pageBuf/_pageUsed，只能由写线程
                    //   (Append/Flush/CommitAsync) 串行调用，后台并发会损坏页缓冲状态。
                    //   末页仍在 _pageBuf 内存的数据不在 FlushedTail 内，由下次 Append 页满 FlushPage
                    //   或显式 CommitAsync 落盘。保证 §7 不变量 CommittedOffset ≤ FlushedTail（已 commit 必已落盘）。
                    //   ★ 不能用 engine.CommittedTail——它含 Allocate 预留空洞，会把 CommittedOffset 推到无数据区。
                    await CommitCoreAsync(FlushedTail).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _lastCommitError, ex);
                    // 循环内不重抛（保活）；错误由上层查询 LastCommitError 发现
                }
            }
        }, ct);
    }

    private CommitSnapshot BuildSnapshot()
    {
        // ★ P0 修复：UnflushedBytes 实时从地址差算（= GetDistance(CommittedOffset, TailAddress)），
        //   不再用累计计数器 _earlyUnflushedBytes——后者在 AdvanceCommittedOffset 无条件 Exchange(0) 重置时
        //   会清掉并发 OnAppended 刚 Add 的计数（TOCTOU），导致字节维度提前提交间歇失效。
        //   地址差实时反映"已 commit 之后又写了多少"，无并发维护问题。
        LogicalAddress committed = CommittedOffset;
        LogicalAddress tail = TailAddress;
        long unflushedBytes = committed >= tail
            ? 0
            : _engine.GetDistance(committed, tail);
        return new CommitSnapshot
        {
            UnflushedBytes = unflushedBytes,
            UnflushedCount = Volatile.Read(ref _earlyUnflushedCount),
            SinceLastCommit = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - Volatile.Read(ref _lastCommitTicks)),
        };
    }


    /// <summary>停止提前提交循环（cancel + 最多等 1s）并释放其 CTS，再走基类 FlushOnDispose 链（最后一次页提交）。</summary>
    /// <param name="disposing">true = 显式释放资源（调用方主动 Dispose）。</param>
    protected override void DisposeOverride(bool disposing)
    {
        _loopCts?.Cancel();
        try
        {
            _earlyCommitLoopTask?.Wait(1000);
        }
        catch
        {
            // ignored
        }

        _loopCts?.Dispose();
        base.DisposeOverride(disposing); // → LogBase.DisposeOverride → FlushOnDispose → OnPageFlushed → 最后一次页提交
    }
}