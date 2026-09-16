namespace TC.Tier.Runtime.Storage;

internal sealed partial class StorageEngine
{
    // ═══════════════════════════════════════════════════════════════
    //  截断/回收——基类统一实现（走 IFileHandle.PunchHole + 释放句柄）
    //  ★ AddressSpace→SegmentTable 迁移：Registry.* → SegmentTable.*（ReclaimHead→ReclaimHeadLease 等）
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public void ReclaimHead(LogicalAddress address)
    {
        ThrowIfDisposed();
        _faults?.OnOpEnter("ReclaimHead");
        EnsureReady();
        int newMinSegId = address.SegId;
        // ★ 目标 ≤ 当前 MinAddress 是 no-op（段号更小，或同段 Offset 更小或相等）。
        //   不能 `if (newMinSegId <= MinSegId) return`——漏掉同段内头截断（retention 常见场景）。
        if (address <= _segmentTable.MinAddress) return;

        // 三阶段（#254 写前日志化重排）：lease 占区间 + 表提交收缩并持久化 + 物理删段
        //   （原序：物理删段先行、lease.Commit/表持久化殿后——崩溃窗口内持久化表仍含活性段
        //   条目而文件已删 → 恢复加载即指向已删段。重排后三窗口全收敛：
        //   Commit 前 → 表/文件双完整；Commit 后 Save 前 → 文件完整（表可恢复）；
        //   Save 后删段前/中 → 持久化表已墓碑（残留文件无害，读侧 Hollow 语义））
        using var lease = _segmentTable.ReclaimHeadLease(address);
        var minSegIdBefore = _segmentTable.MinSegId;   // ★ Commit 会推 MinSegId——物理删段循环用前置界
        // ★ L21 修复（）：物理删段/段内打洞前对 [MinSegId, newMinSegId] 全量升序
        //   段排他锁清场——一致读者（构造期持共享锁）与异步 DirtyRead（跨 await 持共享锁）
        //   被等待完成后才删文件，新读者被挡在锁外：删段与读互斥闭环（旧实现 ShrinkHead 不取
        //   排他 → 读者持共享照删 → 读 ODE/FileNotFound）。升序获取与 LockRange 构造序一致，
        //   无死锁环。Commit（ShrinkHead 标 Invalid + 事件）完成后统一释放。
        var heldLocks = new List<SpinRWLock>(newMinSegId - minSegIdBefore + 1);
        try
        {
            // ★ 段内打洞：lease 占 [MinAddress, address) 全区间，含 newMinSegId 段的 [0, address.Offset)。
            //   循环只删整段（segId < newMinSegId），当前段 newMinSegId 的段内子区间需单独 PunchHole，
            //   否则物理数据残留（重启/再读读到旧数据）。
            //   ★ 打洞互斥走段排他锁（L18 修复——覆盖同步/异步全读路径）。
            if (address.Offset > 0)
            {
                PunchHoleViaDrain(newMinSegId, 0, address.Offset, CancellationToken.None);
            }

            // ★ #254：表先提交收缩并持久化（checkpoint 切面；扫盘切面 no-op——其恢复本就按物理
            //   现状扫盘重建，天然自洽），物理删段殿后
            lease.Commit();
            RefreshSegmentMetaExtents(newMinSegId);
            SaveAddressTable();

            for (var segId = minSegIdBefore; segId < newMinSegId; segId++)
            {
                if (_segmentTable.TryGetLock(segId, out var segLock) && segLock is not null)
                {
                    segLock.AcquireExclusive();
                    heldLocks.Add(segLock);
                }
                DropPendingSegmentTuple(segId);   // ★ 泵脏集同步丢弃——遗留元组不复活已删段
                ReleaseSegmentHandles(segId);
                DeleteSegment(segId, _segmentTable.TryGetSegment(segId, out var seg) ? (seg?.RealSize ?? 0) : 0);
            }
        }
        finally
        {
            foreach (var segLock in heldLocks)
                segLock.ReleaseExclusive();
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 跨段完备形态（对齐 <see cref="Reclaim"/>/<see cref="ReclaimHead"/>——家族一致性修复）：
    ///   lease 逐 chunk 枚举（覆盖 newTail 所在段 + 其后的整段们）+ 逐 chunk
    ///   <see cref="PunchHoleViaDrain"/>（持段排他锁——L18 读互斥；Punch 替代旧 SetLength——
    ///   非稀疏预分配段不再被削文件（挂载级 PreallocationMode 保持），stale 数据物理清零）
    ///   + 逐段 meta 摘要刷新；双尾水位退化由 lease.Commit → ShrinkTail 收口（不变）。
    ///   旧实现只对 newTail.SegId 单段 SetLength：跨段 stale 驻留 + 无锁并发 + 预分配破坏
    ///   （ReclaimTailCrossSegmentTests 定针）。
    /// </remarks>
    public void ReclaimTail(LogicalAddress newTail)
    {
        ThrowIfDisposed();
        _faults?.OnOpEnter("ReclaimTail");
        EnsureReady();
        using var lease = _segmentTable.ReclaimTailLease(newTail);
        var touched = new HashSet<int>();
        var iter = lease.GetEnumerator();
        while (iter.MoveNext())
        {
            var chunk = iter.Current;
            touched.Add(chunk.SegId);
            if (chunk.Length > 0)
            {
                PunchHoleViaDrain(chunk.SegId, chunk.SegOff, chunk.Length, CancellationToken.None);
                InvalidateHoleRatioCache(chunk.SegId);
            }
            iter.CommitCurrent();
        }
        lease.Commit();
        foreach (var segId in touched)
            RefreshSegmentMetaExtents(segId);
    }

    /// <inheritdoc/>
    public void Reclaim(LogicalAddress? from, LogicalAddress? to)
    {
        ThrowIfDisposed();
        _faults?.OnOpEnter("Reclaim");
        EnsureReady();
        if (from is null || to is null) return;
        if (from.Value >= to.Value) return;
        using var lease = _segmentTable.ReclaimLease(from.Value, to.Value);
        var touched = new HashSet<int>();
        var iter = lease.GetEnumerator();
        while (iter.MoveNext())
        {
            var chunk = iter.Current;
            touched.Add(chunk.SegId);
            if (chunk.Length > 0)
            {
                PunchHoleViaDrain(chunk.SegId, chunk.SegOff, chunk.Length, CancellationToken.None);
                InvalidateHoleRatioCache(chunk.SegId);
            }
            iter.CommitCurrent();
        }
        lease.Commit();

        // ★ VII-3 保真：打洞改了区间布局（sparse 位）——刷新涉及段的 meta 摘要
        //   （段满时写的摘要在打洞前，stale；Dispose 补写只覆盖 Ready 态段）。
        foreach (var segId in touched)
            RefreshSegmentMetaExtents(segId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// ★ 真后台：物理 PunchHole 投递到线程池（经 <see cref="RunBackgroundTask"/> 注册到统一跟踪表），
    ///   调用方立即返回 <see cref="IAsyncOperation"/> 句柄（0 等待——命名 决策：Start 动词
    ///   表达"启动后台操作"，Async 后缀留给可 await 的方法）。
    /// <para>★ 逐段 PunchHole 完成触发 <see cref="IAsyncOperation.Progress"/>；全部完成触发
    ///   <see cref="IAsyncOperation"/> 成功终态（WaitAsync 返回）；异常触发 <see cref="IAsyncOperation.Failed"/>
    ///   （携带 <c>lastPunchedOffset</c> 供调用方决定重试剩余区间）。</para>
    /// <para>★ PunchHole 不可回退——已打洞的块物理销毁，失败只能从断点继续。</para>
    /// <para>★ Dispose 时 <see cref="_backgroundCts"/> cancel，后台任务在下一个取消检查点退出。</para>
    /// </remarks>
    public IAsyncOperation StartReclaim(LogicalAddress? from, LogicalAddress? to, CancellationToken ct)
    {
        ThrowIfDisposed();
        _faults?.OnOpEnter("StartReclaim");
        EnsureReady();

        var op = new AsyncOperation("reclaim", Logger, _backgroundCts.Token);

        if (from is null || to is null || from.Value >= to.Value)
        {
            // 空区间——立即完成
            op.ReportSucceeded();
            return op;
        }

        var fromV = from.Value;
        var toV = to.Value;
        // 经基类统一跟踪表启动后台任务：占 lease → 逐段 PunchHole → 触发 Progress/完成/Failed
        // ★ 不带排他（Reclaim 可与读/写并发）；取消令牌绑定 BackgroundCts（Dispose 时 cancel）
        RunBackgroundTask(bgCt =>
        {
            LogicalAddress lastPunched = default;
            try
            {
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(bgCt, ct);
                try
                {
                    using var lease = _segmentTable.ReclaimLease(fromV, toV, bgCt);
                    var totalLen = lease.Length;
                    long punchedLen = 0;
                    var iter = lease.GetEnumerator();
                    while (iter.MoveNext())
                    {
                        linkedCts.Token.ThrowIfCancellationRequested();
                        var chunk = iter.Current;
                        if (chunk.Length > 0)
                        {
                            // ★ PunchHole 走 epoch drain——延迟到所有 reader 退出 epoch 后执行
                            //   DirtyRead 持 epoch 期间，drain 被阻塞（防撕裂）
                            PunchHoleViaDrain(chunk.SegId, chunk.SegOff, chunk.Length, linkedCts.Token);
                            punchedLen += chunk.Length;
                            lastPunched = new LogicalAddress(chunk.SegId, chunk.SegOff + chunk.Length);
                            op.ReportProgress(totalLen > 0 ? (double)punchedLen / totalLen : 1.0);
                        }

                        iter.CommitCurrent();
                    }

                    lease.Commit();
                    op.ReportSucceeded();
                }
                catch (Exception ex)
                {
                    // lastPunchedOffset 携带在 ex.Data，供调用方决定重试剩余区间
                    if (!ex.Data.Contains("lastPunchedOffset"))
                        ex.Data["lastPunchedOffset"] = lastPunched;
                    op.ReportFailed(ex);
                }

                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        });

        return op;
    }

    /// <summary>
    /// 物理 PunchHole 与并发读互斥——持<b>段排他锁</b>执行（L18 修复，）。
    /// <para>★ 同步语义：无并发 reader 持段共享锁时（常见 + 单线程测试），排他锁立即取得，
    ///   打洞当场完成——<see cref="Reclaim"/> 返回时紧随的同步 <see cref="Read"/> 即读到归零数据。</para>
    /// <para>★ 互斥语义：DirtyRead（同步/异步）与 Consistent 读者都持段共享锁跨 IO——
    ///   排他锁等待其完成后才打洞，全读路径无撕裂。旧 epoch drain 只覆盖同步读
    ///   （thread-static 禁跨 await），异步 DirtyRead 是裸露面。</para>
    /// <para>★ 写路径不持段锁——打洞不挡写者；锁序无环（打洞点不持 extent 锁）。</para>
    /// </summary>
    private void PunchHoleViaDrain(int segId, long segOff, long length, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // ★ L18 修复（）：打洞与读的互斥从 epoch drain 改<b>段排他锁</b>——
        //   旧 epoch 只覆盖同步 DirtyRead（thread-static 禁跨 await）；异步 DirtyRead
        //   跨 await 持段共享锁却不持 epoch → punch 撕裂读（P0，代码级实锤）。段排他锁
        //   统一覆盖全部读路径：同步/异步 DirtyRead 的段共享锁 + Consistent 读者的构造期
        //   共享锁都被等待。写路径不持段锁（不受影响）；锁序无环（打洞点不持 extent 锁，
        //   ShrinkTail 的 segment→extent 序无对向路径）。
        //   段不在表（建段/删段窗口）时无读者可护——直接打洞（best-effort 物理归零）。
        Exception? punchError = null;
        var segLock = TryGetPunchLock(segId);
        if (segLock is not null)
        {
            segLock.AcquireExclusive();
            try
            {
                PunchClamped(segId, segOff, length);
            }
            catch (Exception ex)
            {
                punchError = ex;
            }
            finally
            {
                segLock.ReleaseExclusive();
            }
        }
        else
        {
            try
            {
                PunchClamped(segId, segOff, length);
            }
            catch (Exception ex)
            {
                punchError = ex;
            }
        }
        // ★ 异常语义直达调用方（ReclaimAsync → NotifyFailed / 同步 Reclaim 直接抛）——
        //   与旧 drain 版捕获重抛契约一致（故障注入实锤 0% 覆盖期缺陷的保真修复）。
        if (punchError is not null) throw punchError;
    }

    /// <summary>物理打洞——区间钳位到段文件实际长度。
    /// <para>★ 稀疏介质（preallocateFile=false）物理文件按需物化，短于段地址空间预留
    ///   （ReclaimTail/Reclaim lease 端点可达 GrowthLimit 整段）——越物理尾的区间无字节可回收，
    ///   钳位后打洞（钳剩 ≤ 0 整段跳过）；<see cref="IFileHandle.PunchHole"/>"越文件长度抛
    ///   IOFailure"契约原样保留，作为真实越界的绊线。句柄形态由能力探测一次决定
    ///   （DIO 句柄上边缘零写 = 扇区 RMW，见 DiskFileHandle.WriteZeroes）。</para></summary>
    private void PunchClamped(int segId, long segOff, long length)
    {
        using var handle = GetWriteHandle(segId);
        var punchLen = Math.Min(length, handle.Length - segOff);
        if (punchLen > 0) handle.PunchHole(segOff, punchLen);
    }

    /// <summary>L18：取段锁用于打洞互斥——段在表返回锁，不在表（Hollow）返回 null。</summary>
    private SpinRWLock? TryGetPunchLock(int segId)
        => _segmentTable.TryGetLock(segId, out var segLock) && segLock is not null ? segLock : null;

    /// <summary>
    /// ★ 统一删段入口——物理删除（模板方法）。
    /// <para>调用方只调本方法，不再分别调 <see cref="DeleteSegmentPhysical"/>。</para>
    /// <para>★ 不含句柄释放——调用方须先 <see cref="ReleaseSegmentHandles"/>（物理删前释放文件占用）；
    ///   FileExtra 随宿主文件消亡（§3.6 删除契约）——无墓碑写。</para>
    /// </summary>
    /// <param name="segId">待删段号。</param>
    /// <param name="realSize">段真实大小（保留参数形状——容量记账已归 fs，忽略）。</param>
    private void DeleteSegment(int segId, long realSize)
    {
        _ = realSize;
        DeleteSegmentPhysical(segId);
    }

    /// <summary>
    /// 物理删除段——fs.Delete（池句柄已由调用方 RemoveAll 收口；best-effort：不存在视为成功）。
    /// <para>★ SharingViolation 有界重试：删除撞"本段 meta ADS 写在途"（WriteFileMeta 持主文件
    ///   ADS 句柄，其 flush 在饱和卷上可达秒级——AdsMetaGate 串行门放大排队）+ 句柄关闭延迟窗口。
    ///   递增退避上限 ~3s；耗尽降级告警（段表已逻辑死亡、物理残留=可观测空间泄漏，不阻断
    ///   Reclaim 语义）。冷路径（删段）。</para>
    /// </summary>
    private void DeleteSegmentPhysical(int segId)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                _fs.Delete(SegmentFileName(segId));
                return;
            }
            catch (FileIOException ex) when (ex.Error == IOError.NotFound)
            {
                return;   // 已删/从未建，正常
            }
            catch (FileIOException ex) when (ex.Error == IOError.SharingViolation && attempt < 5)
            {
                var backoff = 100 << Math.Min(attempt, 4);   // 100/200/400/800/1600ms，累计 ~3.1s
                Logger?.LogDebug($"DeleteSegmentPhysical: delete raced with in-flight meta write/handle close (attempt {attempt + 1}), retrying {SegmentFileName(segId)} in {backoff}ms: {ex.Message}");
                Thread.Sleep(backoff);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "DeleteSegmentPhysical: failed to delete {Path}", SegmentFileName(segId));
                return;
            }
        }
    }

    /// <summary>★ 测试访问器：宿主文件系统（Compact marker 多文件测试用——同介质写 marker 残留）。</summary>
    internal IFileSystem MarkerFileSystemForTest => _fs;

}
