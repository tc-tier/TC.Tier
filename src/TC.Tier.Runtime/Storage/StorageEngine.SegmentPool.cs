namespace TC.Tier.Runtime.Storage;

/// <summary>
/// IO 层段预备池 partial（lookahead）——<b>物理建段是 IO 层的事</b>（架构约定 ）：
/// <list type="bullet">
/// <item>段表 = 逻辑层：只发通知（<c>OnSegmentCreate/OnSegmentFull</c>）+ 提供剩余容量；不关心物理预建。</item>
/// <item>IO 层（本 partial）= 物理资产：收到通知后<b>攒 N 个现成物理段</b>（文件+meta+容量计数全部就绪），
///   段表要段时<b>秒取</b>（用一个消一个），随取随补，Dispose 直接毁余量。</item>
/// </list>
/// <para>★ 效果：写者<b>永不等待建段</b>——段表 EnsureSegmentsForLength 触发 OnSegmentCreate 时，
///   池命中 → CreateSegmentCallback 同步转正（Empty→Ready 立即完成），WaitSegmentReady 窗口消失
///   （历史高 N flaky 的根因「写者同步等异步建段」由此根治，EngineWorkerConsumerCount 可安全调高）。</para>
/// <para>★ 池内段号 = 段表尾段之后的连续 N 个（tail+1 .. tail+N）；单段模式禁用（只有 seg0）。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    // === 池状态（全部 _poolLock 下访问）===
    private readonly object _poolLock = new();
    private readonly HashSet<int> _pooledSegIds = new();   // 物理就绪（IO+meta+容量计数已完成）
    private readonly HashSet<int> _poolPending = new();   // 补建调度中（防重复入队）
    /// <summary>
    /// 构建声明（single-flight）：segId → 完成信号（true=已入池可取用 / false=弃建或失败）。
    /// <para>★ 池补建（<see cref="PreCreateSegmentPhysical"/>）与正式建段
    ///   （<see cref="EnsureSegmentPhysicalAsync"/>）经此互斥——N≥2 双消费者并发建同一物理段
    ///   的窗口（容量双重计数 / 句柄缓存覆盖泄漏 / 重复等值 meta 写）由此关死（引擎 N≥2 审计）。</para>
    /// </summary>
    private readonly Dictionary<int, TaskCompletionSource<bool>> _poolBuildGates = new();
    private bool _poolEnabled;
    /// <summary>本生命周期发生过 hint 截断（ApplyHints 小值修正删过段）——抑制池：
    /// 截断后预建会把刚删的段以空文件"复活"，违反"hint 之后整段删除"的可观测语义（）。</summary>
    private bool _poolSuppressedByTruncation;

    /// <summary>
    /// lookahead 深度——池保持的现成段数（尾段之后 N 个）。默认 2（开——写者零等待）。
    /// <para>★ 默认开的依据（）：WaitSegmentReady 无超时 park 在并行负载下实测饿死写者
    ///   （dotnet-stack 取证：写者物理门等待 park 120s+，count 771/800 零失败——等待者永不醒；§6.1 零锁化后该家族结构性消灭）。
    ///   池命中 → CreateSegmentCallback 同步转正（Empty→Ready 立即完成）→ 写者零等待，该路径根治。
    ///   hint 截断的生命周期自动抑制（ApplyHintTruncationUpfront → SuppressSegmentPoolForLifecycle）。</para>
    /// <para>★ 单段模式（EnableSegmentation=false）自动禁用（InitializeSegmentPool 判定）。</para>
    /// </summary>
    private static int SegmentLookaheadCount => 2;

    /// <summary>恢复期 hint 截断发生——抑制本生命周期的池（见 _poolSuppressedByTruncation）。</summary>
    internal void SuppressSegmentPoolForLifecycle() => _poolSuppressedByTruncation = true;

    /// <summary>初始化池（OnInitializeComplete 调——此时 EnableSegmentation 已由恢复流程定）。</summary>
    private void InitializeSegmentPool()
    {
        _poolEnabled = EnableSegmentation && SegmentLookaheadCount > 0 && !_poolSuppressedByTruncation;
        if (_poolEnabled) ReplenishSegmentPool();
    }

    /// <summary>
    /// 补池——为尾段之后 N 个 segId 中「段表没有 + 池没有 + 不在补建中」的段调度物理预建（worker 执行）。
    /// <para>★ 幂等；触发点：初始化 / OnSegmentCreate（取走一个后）/ OnSegmentFull（段满提前建）。</para>
    /// </summary>
    private void ReplenishSegmentPool()
    {
        if (!_poolEnabled) return;
        List<int>? schedule = null;
        lock (_poolLock)
        {
            var baseSegId = AllocatedTail.SegId;   // 尾段（池为其之后 N 个做准备）
            var need = SegmentLookaheadCount - _pooledSegIds.Count;
            for (var sid = baseSegId + 1; need > 0; sid++)
            {
                if (_pooledSegIds.Contains(sid) || _poolPending.Contains(sid)) { need--; continue; }
                // 段表已有该段（逻辑存在——正式建段路径 owns it）→ 跳过不占池额度
                if (_segmentTable.TryGetSegment(sid, out var view) && view is not null) continue;
                _poolPending.Add(sid);
                (schedule ??= new List<int>()).Add(sid);
                need--;
            }
        }
        if (schedule is null) return;
        foreach (var sid in schedule)
        {
            var captured = sid;
            // 池补建走 worker 的 Background 通道（与 handler.SubmitBackgroundWork 同款——低频自洽任务）
            _workerLoop.Enqueue(new WorkLoopItemTask
            {
                Event = SegmentWorkEvent.Background,
                BackgroundWork = () => PreCreateSegmentPhysical(captured),
            }, WorkerPriority.Normal);
        }
    }

    /// <summary>
    /// 池预建执行（worker 线程）——物理建段 + 入池。
    /// <para>★ 全程守卫：Dispose 关池后立即退出（防与 Resources.Dispose 竞态——worker 线程在引擎
    ///   释放后触碰 IO 组件 = 未处理异常崩宿主，实测）；任何异常吞掉（补建是优化，绝不致命）。</para>
    /// <para>★ single-flight：经 <see cref="TryAcquirePoolBuildGate"/> 声明构建权——正式 Create 任务
    ///   正在建该段时弃建；建成后<b>无条件入池</b>（段表已注册亦入：等 gate 的正式任务正好取用转正，
    ///   双建已被 gate 互斥排除——旧"复查弃建"分支反而留下双计数/句柄覆盖窗口）。</para>
    /// </summary>
    private void PreCreateSegmentPhysical(int segId)
    {
        TaskCompletionSource<bool>? gate = null;
        try
        {
            if (!_poolEnabled) return;   // Dispose 关池——余量清理由 Sweep 负责

            // ★ 过期任务守卫（VII-9）：池只建「尾前哨窗口」（tail+1..tail+N）内的段——
            //   任务滞留期间尾已推进到/越过 segId（该段或已转正或已被 ReclaimHead 回收摘索引），
            //   此时构建 = 复活已删段文件 + 容量重计。
            if (segId <= AllocatedTail.SegId) return;

            // ★ 声明构建权（段表复查与声明同临界区，见 TryAcquirePoolBuildGate）：
            //   null = 段表已正式接管（正式路径 owns）或他方构建中 → 弃建
            gate = TryAcquirePoolBuildGate(segId);
            if (gate is null) return;

            CreateSegmentPhysical(segId, SegmentGrowthLimit);

            if (!_poolEnabled) return;   // 建 IO 中引擎开始 Dispose——物理已建，余量清理由 Sweep 负责

            // ★ 完成者负责回调（lease-protocol-typed §1）："池命中也只是同步执行同一回调——压缩时间
            //   不压缩状态路径"。构建期间段表可能已注册该段（正式 Create 任务按 InFlight 作废等着）——
            //   此时由本完成者代执行幂等转正回调；未注册 → 正常入池（lookahead 余量）。
            //   ★ 终局原子化（§XI 同族第四次收口）：registered 复查、gate 移除、入池/回调判定同临界区——
            //     复查在临界区外时，正式任务可在「复查 false 之后、gate 移除之前」窗口内按 InFlight 作废，
            //     而本完成者按 stale 复查入池不发回调 → 已注册段永卡 Empty（写者 60s 有界等待后抛错）。
            FinalizePoolBuildAsCompleter(segId, gate);
            gate = null;
        }
        catch
        {
            // 补建失败/竞态——弃该 id（下次补池重试）；池是优化路径，绝不向 worker 抛。
            // 若段表已注册且仍在建（Empty，有人等转正）→ 失败回调（Broken，物理门永关）
            lock (_poolLock) _poolPending.Remove(segId);
            if (_segmentTable.TryGetSegment(segId, out var errView)
                && errView is { IsValid: true } v
                && v.StableState == StableState.Empty)
                _segmentTable.CreateSegmentCallback(segId, success: false);
            if (gate is not null)
                CleanupFailedSegmentBuild(segId);   // ★ A7：曾实际建段（gate 在手）→ IO 层清尸；弃建早退（gate=null）无残可清
        }
        finally
        {
            // 未完成出口（弃建/失败/Dispose 竞态）——释放 gate 让等待的正式任务转自建；清 pending
            if (gate is not null) CompletePhysicalBuild(segId, gate, pooled: false);
            lock (_poolLock) _poolPending.Remove(segId);
        }
    }


    /// <summary>
    /// 取走一个现成段（OnSegmentCreate 快路径调）——命中返回 true：物理段已就绪，
    /// 调用方直接 <c>CreateSegmentCallback(segId, true)</c> 同步转正，写者零等待。
    /// </summary>
    private bool TryConsumePooledSegment(int segId)
    {
        if (!_poolEnabled) return false;
        lock (_poolLock)
        {
            if (!_pooledSegIds.Remove(segId)) return false;
            // ★ L17 收口（）：转正回调与取用同临界区——旧实现"取用后、回调前"的
            //   抢占窗口：段已出池、尚未 Ready、无 gate → 并发正式 Create 见「Empty+未池化+
            //   无在途」→ 第二次物理构建（全量并发时序 ~1/6 轮实锤，seg 双建诊断：pooled 已
            //   无该段、claim 判 Empty、create 时 st=Ready——回调恰在 claim 与 build 之间落地）。
            _segmentTable.CreateSegmentCallback(segId, success: true);
            return true;
        }
    }

    /// <summary>
    /// 声明池补建构建权（_poolLock 下）——段表已正式接管或他方构建在途时返回 null（弃建）。
    /// <para>★ 段表复查必须与声明<b>同临界区</b>：「复查通过 → 声明」之间段表可能被正式建段注册
    ///   并完成整个构建——过期的复查会让池任务在正式构建完成后再次声明重建（顺序双建，
    ///   N=2 实测 segId=1；gate 只互斥并发构建，防不了拿过期复查的顺序重建）。</para>
    /// </summary>
    private TaskCompletionSource<bool>? TryAcquirePoolBuildGate(int segId)
    {
        lock (_poolLock)
        {
            if (_segmentTable.TryGetSegment(segId, out var view) && view is not null) return null;
            if (_poolBuildGates.ContainsKey(segId)) return null;
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _poolBuildGates[segId] = gate;
            return gate;
        }
    }

    /// <summary>正式建段任务的原子「取用或声明」四态结果。</summary>
    private enum PhysicalBuildClaim
    {
        /// <summary>池命中取用——物理现成，调用方执行转正回调。</summary>
        Consumed,
        /// <summary>声明成功——调用方独占构建权，完成后必须 CompletePhysicalBuild。</summary>
        Claimed,
        /// <summary>他方（池补建）构建在途——其完成者会代执行幂等回调，本任务直接作废（worker 零等待）。</summary>
        InFlight,
        /// <summary>过期/已毕——段已摘索引（回收/截断）或已非 Empty（Ready/Full/Compacting/Broken/Invalid），绝不重建。</summary>
        Abandoned,
    }

    /// <summary>
    /// 正式建段任务的原子「取用/守卫/声明」——四态一锤定音（同一临界区，VII-9 残口收死）。
    /// <para>★ 过期/已毕守卫必须与声明<b>同临界区</b>：守卫（段表复查）与声明之间段可能被
    ///   ReclaimHead 回收（Invalid/摘索引）——拿过期守卫去 claim = 复活已删段重建。</para>
    /// <para>★ 取用、守卫、声明三者原子：守卫间隙里池任务完成整个构建的双建窗口也一并关死。</para>
    /// </summary>
    private PhysicalBuildClaim TryConsumeOrClaimPhysicalBuild(int segId,
        out TaskCompletionSource<bool>? ownGate)
    {
        ownGate = null;
        lock (_poolLock)
        {
            if (_poolEnabled && _pooledSegIds.Remove(segId))
            {
                // ★ L17 收口（）：同 TryConsumePooledSegment——回调与取用同临界区，
                //   消「已出池、未 Ready、无 gate」的二次构建窗口。
                _segmentTable.CreateSegmentCallback(segId, success: true);
                return PhysicalBuildClaim.Consumed;
            }
            // ★ 守卫（与声明同临界区）：仅「已注册且 Empty」才可建——唯一待建态（状态机源态判定）；
            //   Ready/Full/Compacting/Broken/Invalid 一律作废，绝不重建
            if (!_segmentTable.TryGetSegment(segId, out var view) || view is null) return PhysicalBuildClaim.Abandoned;
            if (view.Value.StableState != StableState.Empty) return PhysicalBuildClaim.Abandoned;
            if (_poolBuildGates.TryGetValue(segId, out var inFlight))
                return PhysicalBuildClaim.InFlight;   // 完成者（池任务）代执行回调
            ownGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _poolBuildGates[segId] = ownGate;
            return PhysicalBuildClaim.Claimed;
        }
    }

    /// <summary>
    /// 完成构建声明——释放 gate（等待方继续取用/自建）+ pooled 时入池。
    /// 幂等防御：gate 已被替换则只发信号不动新 gate。
    /// </summary>
    private void CompletePhysicalBuild(int segId, TaskCompletionSource<bool> gate, bool pooled)
    {
        lock (_poolLock)
        {
            if (_poolBuildGates.TryGetValue(segId, out var cur) && cur == gate)
                _poolBuildGates.Remove(segId);
            if (pooled && _poolEnabled) _pooledSegIds.Add(segId);
        }
        // ★ 锁外发信号——RunContinuationsAsynchronously 使等待方续体回流调度器线程，不在本线程内联
        gate.TrySetResult(pooled);
    }

    /// <summary>
    /// 池完成者的成功终局——registered 复查、gate 移除、入池/转正回调<b>同临界区</b>（与
    /// <see cref="TryConsumeOrClaimPhysicalBuild"/> 同锁原子可见，§XI 同族第四次收口：
    /// 取用-声明、复查-声明、守卫-声明之后的「终局-复查」）。
    /// <para>★ 兑现 InFlight 握手契约：正式 Create 任务见在途 gate 作废（InFlight）时，依赖本完成者
    ///   对已注册段执行幂等转正回调。复查若在临界区外（历史缺陷）：正式任务在「复查 false 之后、
    ///   gate 移除之前」窗口内作废 → 完成者按 stale 复查入池不发回调 → 已注册段永卡 Empty，
    ///   且注册仅通知一次、无后续取用机会——写者 60s 有界等待后抛错。</para>
    /// <para>★ 回调（幂等 CAS + 单向闩 Set，零锁等待）入临界区执行：后续正式任务只见「gate 在 →
    ///   InFlight（本完成者回调）」或「非 Empty → Abandoned」，两态之间无窗口。锁序安全：
    ///   <see cref="SegmentTable.CreateSegmentCallback"/> 不取段表三级锁（volatile 读 + CAS + 闩内锁），
    ///   且 <see cref="TryAcquirePoolBuildGate"/> 已有 _poolLock 下查段表先例。</para>
    /// </summary>
    private void FinalizePoolBuildAsCompleter(int segId, TaskCompletionSource<bool> gate)
    {
        bool registered;
        lock (_poolLock)
        {
            _poolPending.Remove(segId);
            registered = _segmentTable.TryGetSegment(segId, out _);
            if (_poolBuildGates.TryGetValue(segId, out var cur) && cur == gate)
                _poolBuildGates.Remove(segId);
            if (registered)
                _segmentTable.CreateSegmentCallback(segId, success: true);   // 幂等（Empty 才 MarkReady）
            else if (_poolEnabled)
                _pooledSegIds.Add(segId);
        }
        // ★ 信号语义与 CompletePhysicalBuild 对齐（true=已入池可取用；无等待方，诊断语义）
        gate.TrySetResult(!registered);
    }

    /// <summary>
    /// 正式建段任务体（worker Create 事件）——**worker 零等待**：原子四态一锤定音 + 建段 + 段表回调。
    /// <para>★ 四态（<see cref="TryConsumeOrClaimPhysicalBuild"/>，取用/守卫/声明同临界区）：
    ///   Consumed=池命中 → 幂等转正回调 + 随取随补；Claimed=独占构建 → 成败都回调（gate 在 finally 释放）；
    ///   InFlight=池补建在途 → **直接作废**（其完成者代执行同一回调——"池命中也只是同步执行同一回调，
    ///   压缩时间不压缩状态路径"）；Abandoned=过期/已毕 → 作废，绝不重建（VII-9）。</para>
    /// <para>★ 建段任务的唯一职责 = 为「已注册且 Empty」的段完成物理构建并回调（lease-protocol-typed
    ///   §1）；等待 Ready 是 lease 协议的事（chunk 第一拍/扫尾），worker 不等待、不重试。</para>
    /// </summary>
    private void EnsureSegmentPhysical(int segId, long growthLimit, CancellationToken ct)
    {
        switch (TryConsumeOrClaimPhysicalBuild(segId, out var ownGate))
        {
            case PhysicalBuildClaim.Consumed:
                // ① 池命中——物理现成（转正回调已在 claim 临界区内执行——L17 收口），随取随补
                ReplenishSegmentPool();
                return;

            case PhysicalBuildClaim.Claimed:
                // ② 独占构建 → 成败都回调段表（解除 Empty（物理门开）；gate 在 finally 释放）
                try
                {
                    CreateSegmentPhysical(segId, growthLimit);
                    _segmentTable.CreateSegmentCallback(segId, success: true);
                }
                catch (Exception ex)
                {
                    _segmentTable.CreateSegmentCallback(segId, success: false);
                    CleanupFailedSegmentBuild(segId);   // ★ A7：IO 层清尸（句柄+meta+半建文件）——Broken 终态不残留物理资产
                    Logger?.LogError(ex, "建段失败 segId={SegId} growthLimit={GrowthLimit}", segId, growthLimit);
                }
                finally { CompletePhysicalBuild(segId, ownGate!, pooled: false); }
                return;

            case PhysicalBuildClaim.InFlight:
                // ③ 池补建在途——完成者（PreCreateSegmentPhysical）对已注册段代执行幂等回调，本任务作废
                return;

            case PhysicalBuildClaim.Abandoned:
                // ④ 过期/已毕——段已摘索引或已非 Empty，绝不重建（等待者由稳态迁移/原完成者唤醒）
                return;
        }
    }

    /// <summary>
    /// Dispose 清池——毁掉未消费的余量物理段（已消费转正的段归段表管，不在此删）。
    /// <para>★ 调用时机：DisposeOverride 中 worker 已停之后（无补建竞态）。</para>
    /// </summary>
    private void SweepSegmentPoolOnDispose()
    {
        int[] leftover;
        lock (_poolLock)
        {
            leftover = new int[_pooledSegIds.Count + _poolPending.Count];
            _pooledSegIds.CopyTo(leftover);
            _poolPending.CopyTo(leftover, _pooledSegIds.Count);
            _pooledSegIds.Clear();
            _poolPending.Clear();
            _poolBuildGates.Clear();   // worker 已停——在途 gate 的等待方由 ct 取消兜底
            _poolEnabled = false;
        }
        foreach (var sid in leftover)
        {
            // 已被段表正式接管的（消费转正）不删；pending 未完成的物理可能半建——best-effort 删
            if (_segmentTable.TryGetSegment(sid, out var view) && view is not null) continue;
            ReleaseSegmentHandles(sid);
            DeleteSegment(sid, 0);
        }
    }
}
