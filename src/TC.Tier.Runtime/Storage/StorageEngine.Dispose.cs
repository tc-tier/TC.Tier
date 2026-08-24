namespace TC.Tier.Runtime.Storage;
/// <summary>
/// 存储 Dispose partial——引擎特有的 task 协调（cancel+wait）由 <see cref="DisposeOverride"/> 承担；
/// 组件释放（Registry/Epoch）已进 <see cref="LifecycleBase{THints}.Resources"/>，由基类 non-virtual Dispose 模板统一转发。
/// <para>★ Dispose 编排（由 <see cref="LifecycleBase{THints}"/>.Dispose 驱动）：
///   CAS 防双释放 → WarnIfNotInitialized → CancelRecoveryAndCleanup（等后台恢复 task）→
///   <see cref="DisposeOverride"/>（本文件：cancel+wait 后台 task + stop epoch worker + 段预备池清扫 +
///   未满段元组补写 + 唤醒等待者 + 池收口 + DeleteOnClose 清树）→
///   Resources.Dispose（释放 Registry/Epoch/Compact/Checkpoint/SegmentTable，逆序聚合异常）→ Unregister → SuppressFinalize。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    /// <summary>★ 子类同步额外清理钩子——cancel+wait 后台 task + stop epoch worker + 物理资产收口。</summary>
    protected override void DisposeOverride(bool disposing)
    {
        if (!disposing) return;
        // ★ 池先关（第一拍）——防 worker 补建任务与 Resources.Dispose 竞态（释放后触碰 IO = 崩宿主）
        lock (_poolLock) _poolEnabled = false;
        // ★ 后台任务协同：触发所有 in-flight 后台任务取消 + 精确等全部退出（5s 超时兜底）
        CancelAndWaitBackgroundTasks();
        // ★ epoch drain worker 先停（C1：reader 已无，drain 安全退出）
        StopEpochProtection();
        // ★ IO 层段预备池清池（worker 已停，无补建竞态）——毁未消费余量物理段（架构约定：余量直接毁掉）
        SweepSegmentPoolOnDispose();
        // ★ 未满段元组补写（Ready 态且 maxOffset>0 的尾段——否则元组停在 0，预分配模式扫盘
        //   maxOffset=0 → reopen 双尾归零、数据全"丢"，2026-08-14 真实事故）。
        WriteUnfinishedSegmentTuples();
        // ★ 唤醒所有段上 ReadyLock 等待者（抛 ObjectDisposedException），防 Dispose 后死等。
        try
        {
            _segmentTable.PulseAllSegmentsReady();
        }
        catch (IndexOutOfRangeException)
        {
            // 并发 ReclaimHead 收缩了段表——剩余等待者由 Dispose 超时兜底
        }
        // ★ 句柄池全量收口（先于删树——Windows 占用致删失败）。
        ReleaseAllHandles();
        if (_options.DeleteOnClose)
            DeleteEngineSubtree();
    }

    /// <summary>★ 异步额外清理——同同步版。</summary>
    protected override async ValueTask DisposeOverrideAsync(bool disposing)
    {
        if (!disposing) return;
        lock (_poolLock) _poolEnabled = false;   // ★ 池先关（防补建与释放竞态）
        await CancelAndWaitBackgroundTasksAsync().ConfigureAwait(false);
        StopEpochProtection(); // ★ epoch drain worker 先停
        SweepSegmentPoolOnDispose();
        WriteUnfinishedSegmentTuples();
        try
        {
            _segmentTable.PulseAllSegmentsReady();
        }
        catch (IndexOutOfRangeException)
        {
        }
        ReleaseAllHandles();
        if (_options.DeleteOnClose)
            DeleteEngineSubtree();
    }

    /// <summary>
    /// 未满 Written 段的元组补写（Dispose 联动，FileExtra 同步强一致）。
    /// <para>★ Full 段已在 OnSegmentFull 落盘；Invalid/Empty 段无意义跳过。</para>
    /// </summary>
    private void WriteUnfinishedSegmentTuples()
    {
        var minSeg = _segmentTable.MinSegId;
        var count = _segmentTable.SegCount;
        for (var i = 0; i < count; i++)
        {
            var segId = minSeg + i;
            if (!_segmentTable.TryGetSegment(segId, out var view) || view is not { IsValid: true } v) continue;
            if (v.StableState != StableState.Ready) continue;
            if (v.MaxOffset <= 0) continue;
            try
            {
                WriteSegmentTuple(segId, v.StableState, maxOffset: v.MaxOffset, growthLimit: v.GrowthLimit,
                    realSize: v.RealSize, EncodeExtentSummary(segId));   // ★ 携带区间摘要（VII-3 保真）
            }
            catch (Exception ex)
            {
                Logger?.LogWarning(ex, "Dispose 元组补写 seg#{SegId} 失败（非致命）", segId);
            }
        }
    }

    /// <summary>取消并等待所有 in-flight 后台 Task（同步版）。</summary>
    private void CancelAndWaitBackgroundTasks()
    {
        _backgroundCts.Cancel();
        foreach (var t in _backgroundTasks.Values.ToArray())
        {
            try
            {
#pragma warning disable TCSG031 // 设计必需：Dispose 等后台任务（有界超时）
                t.Wait(TimeSpan.FromSeconds(5));
#pragma warning restore TCSG031
            }
            catch
            {
                /* Task 内异常已通过 op.Notify* 上报 */
            }
        }

        _backgroundTasks.Clear();
        _backgroundCts.Dispose();
    }

    /// <summary>取消并等待所有 in-flight 后台 Task（异步版)。</summary>
    private async ValueTask CancelAndWaitBackgroundTasksAsync()
    {
        try
        {
            await _backgroundCts.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
            /* ignored */
            Logger?.LogWarning("CancelAndWaitBackgroundTasksAsync: BackgroundCts.CancelAsync() failed.");
        }

        foreach (var t in _backgroundTasks.Values.ToArray())
        {
            try
            {
                await Task.WhenAny(t, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);
            }
            catch
            {
                Logger?.LogWarning("CancelAndWaitBackgroundTasksAsync: background task wait failed.");
            }
        }

        _backgroundTasks.Clear();
        _backgroundCts.Dispose();
    }

    /// <summary>
    /// 删除引擎子树全部产物（仅 DeleteOnClose）——best-effort：单项失败仅记 warning 不抛（Dispose 不应抛）。
    /// <para>★ Core 无递归删除（危险操作不藏糖）——文件全删 + 子目录按深度逆序 + 引擎目录，显式组合。</para>
    /// </summary>
    private void DeleteEngineSubtree()
    {
        try
        {
            foreach (var file in _fs.EnumerateFiles(EngineName, "*", recursive: true))
            {
                try { _fs.Delete($"{EngineName}/{file.Name}"); }
                catch (Exception ex) { Logger?.LogWarning(ex, "DeleteOnClose: failed to delete {Path}", file.Name); }
            }

            var subDirs = _fs.EnumerateDirectories(EngineName, "*", recursive: true)
                .Select(d => d.Name)
                .OrderByDescending(n => n.Count(c => c == '/'))
                .ToList();
            foreach (var dir in subDirs)
            {
                try { _fs.DeleteDirectory($"{EngineName}/{dir}"); }
                catch (Exception ex) { Logger?.LogWarning(ex, "DeleteOnClose: failed to remove directory {Path}", dir); }
            }

            _fs.DeleteDirectory(EngineName);
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "DeleteOnClose: failed to clean engine subtree {Engine}", EngineName);
        }
    }
}
