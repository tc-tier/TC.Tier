namespace TC.Tier.Runtime.Storage.Compact;

internal sealed partial class DefaultCompactor(
    IStorageInfo deviceInfo,
    IFileSystem fileSystem,
    LightEpoch? epoch = null,
    ILogger? logger = null)
    : ICompact
{
    /// <summary>跟踪造的临时段 handle（PromoteTemp 后 Dispose——rename 后 handle 失效）。</summary>
    private readonly Dictionary<int, IFileHandle> _tempHandles = new();
    /// <summary>Compact 拷贝 chunk 大小（64KB，配合 AlignedMemoryManager 池化）。</summary>
    private const int CopyChunkSize = 64 * 1024;

    /// <summary>Compact commit marker 文件名后缀（不含目录）。</summary>
    private const string MarkerFileNameSuffix = ".compact.marker";

    // ═══════════════════════════════════════════════════════════════
    //  注入（共享）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>★ 存储信息视图（DeviceName/SegmentFileName/...）——经 IStorageInfo 注入，
    /// 子系统据此定位段文件、命名 marker。</summary>
    private readonly IStorageInfo _deviceInfo = deviceInfo;

    /// <summary>★ 根空间文件系统——Compact 全部 IO 经此（相对路径，引擎零介质分支同源）。</summary>
    private readonly IFileSystem _fileSystem = fileSystem;

    /// <summary>Device 名称（marker 文件命名用）——派生自 DeviceInfo。</summary>
    private string DeviceName => _deviceInfo.EngineName;

    /// <summary>段文件路径——派生自 DeviceInfo.SegmentFileName（自动跟随多段/单段模式）。</summary>
    private string GetSegmentPath(int segId) => _deviceInfo.SegmentFileName(segId);

    /// <summary>临时段路径——段路径 + ".compact" 后缀（Compact 专属概念，留在此层）。</summary>
    private string GetTempPath(int segId) => GetSegmentPath(segId) + MarkerTempSuffix;
    /// <summary>临时段文件名后缀。</summary>
    private const string MarkerTempSuffix = ".compact";

    /// <summary>日志通道（可选）。</summary>
    private readonly ILogger? _logger = logger;

    /// <summary>
    /// 可选注入的 LightEpoch（引擎的 epoch 保护实例）——RangeCompact 的 promote 原子协议用
    /// （drain 回调内 promote + lease.Commit：reader 全退出后才 rename）。
    /// <para>★ 2026-08-24：IEngineEpoch 消灭——drain 协议下沉 LightEpoch.DrainThen（Core 原语），
    ///   子系统不再经引擎包装接口（ReleaseSegmentHandles 越权面归零，失败决策权归使用方）。</para>
    /// </summary>
    private readonly LightEpoch? _epoch = epoch;

    // ═══════════════════════════════════════════════════════════════
    //  内部状态（标准化后台线程模板）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>当前状态（CompactStatus）。</summary>
    private volatile int _status = (int)CompactStatus.Completed;

    /// <summary>排他标志——CAS 0→1，保证同一时刻至多 1 个 Compact。</summary>
    private int _compacting;

    /// <summary>取消令牌——Dispose/Cancel 时 cancel（AsyncOperation 链接到它，取消在途整理）。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 在途整理任务（轻量单例后台模式，2026-08-24：替代 BackgroundWorkerLoop + 专用调度器）。
    /// <para>★ Compact 排他（<see cref="_compacting"/> CAS）保证至多 1 个在途——队列/多消费者/优先级
    ///   全无用，与 StartReclaim 同构：<see>
    ///       <cref>Task.Run</cref>
    ///   </see>
    ///   立即执行 + op 句柄驱动完成。</para>
    /// <para>★ 同步长 IO（拷贝/rename/GC）跑公共线程池：Compact 是低频冷路径，偶发占一个池线程
    ///   可接受（线程池饥饿增长兜底）；原"own 单线程调度器"防的是共享调度器饿死异步 worker，
    ///   公共池无此问题。</para>
    /// </summary>
    private Task? _inFlight;


    /// <summary> Dispose 标志。</summary>
    private int _disposed;

    // ═══════════════════════════════════════════════════════════════
    //  后端特定——子类 override
    // ═══════════════════════════════════════════════════════════════

    // ★ GetSegmentPath / GetTempPath 基于 IStorageInfo（见上方注入区）。

    /// <summary>是否支持 commit marker（磁盘=true, 内存=false）。</summary>
    private bool SupportsMarker => true;

    /// <summary>后端是否能可靠区分 allocated range 与 hole。</summary>
    private bool SupportsRangeCompact => !OperatingSystem.IsMacOS();

    /// <summary>晋升临时段为正式段（fs.Move 换名：磁盘=原子 rename，内存=命名空间槽位转移）。失败抛类型化异常由使用方决策。</summary>
    private  void PromoteTemp(int segId)
    {
        // 先关闭临时段 handle（rename 前释放对 .compact 文件的占用）
        IFileHandle? tempHandle;
        lock (_tempHandles)
        {
            _tempHandles.Remove(segId, out tempHandle);
        }
        tempHandle?.Dispose();

        string compactPath = GetTempPath(segId);
        string finalPath = GetSegmentPath(segId);

        // ★ Compact 契约：调用方（引擎）负责在 Compact 前关闭全部缓存句柄。
        //   rename 失败允许——失败决策权归使用方（2026-08-24 用户裁定）：抛类型化异常
        //   （FileIOException.SharingViolation），引擎捕获后关自己句柄 + 续传（不重拷贝）。
        //   ★ GC 时机：目标文件被 OpenSourceHandle 只读打开过（using Dispose 后异步句柄的内核释放
        //   可能尚未完成）——首次失败强制 GC 跑 finalizer 让内核放手，再重试。
        //   ★ 预算 5×100ms=500ms：只兜"短命句柄内核释放"窗口（GC 后即成功）；引擎缓存句柄
        //   （持久持有）等多久都白等——快速失败交引擎决策（旧 60 次×100ms=6s 为自消化而设，已废）。
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_fileSystem.Exists(compactPath))
                    _fileSystem.Move(compactPath, finalPath, overwrite: true);   // 原子换名 + 父目录 fsync 内建
                else
                    _fileSystem.FlushRoot();
                return;
            }
            catch (Exception ex) when (ex is IOException && attempt < 5)   // FileIOException : IOException——Core catch-all Wrap 后原生异常不逃逸
            {
                // 首次失败：强制 GC 跑 finalizer，释放 Dispose 了但内核尚未放手的 IOCP 只读句柄
                if (attempt == 0)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                if (attempt % 5 == 0)
                    _logger?.LogWarning(ex, "PromoteTemp rename attempt {Attempt} 失败 segId={segId}，退避重试", attempt, segId);
                Thread.Sleep(100);
            }
        }
    }


    private void DeleteSegment(int segId)
    {
        string path = GetSegmentPath(segId);
        try { if (_fileSystem.Exists(path)) _fileSystem.Delete(path); }
        catch (Exception ex) { _logger?.LogWarning(ex, "DeleteSegment 失败 segId={segId}", segId); }
    }



    private void DeleteAllTemps()
    {
        IFileHandle[] handles;
        lock (_tempHandles)
        {
            handles = [.. _tempHandles.Values];
            _tempHandles.Clear();
        }
        foreach (var handle in handles)
        {
            try { handle.Dispose(); }
            catch (Exception ex) { _logger?.LogWarning(ex, "DeleteAllTemps: Dispose temp handle 失败"); }
        }

        if (!_fileSystem.DirectoryExists(DeviceName)) return;
        foreach (var file in _fileSystem.EnumerateFiles(DeviceName, "*.compact"))
        {
            try { _fileSystem.Delete($"{DeviceName}/{file.Name}"); }
            catch (Exception ex) { _logger?.LogWarning(ex, "DeleteAllTemps: 删 {path} 失败", file.Name); }
        }

        // ★ marker tmp 残留清理（L4 取证，2026-08-21）：marker 写失败路径遗留空 .marker.tmp——
        //   虽不再砖死后续 Compact（WriteCommitMarker 改 Truncate 覆写），失败路径仍应清理干净。
        if (SupportsMarker)
        {
            var markerTmp = MarkerPath + ".tmp";
            try { if (_fileSystem.Exists(markerTmp)) _fileSystem.Delete(markerTmp); }
            catch (Exception ex) { _logger?.LogWarning(ex, "DeleteAllTemps: 删 marker tmp 失败"); }
        }
    }

    /// <summary>指定段的临时镜像是否存在。</summary>
    private  bool TempExists(int segId) => _fileSystem.Exists(GetTempPath(segId));

    /// <summary>获取段当前文件大小（用于 SetReplacement/TrackSegmentCreated）。</summary>
    private long GetSegmentLength(int segId)
    {
        try { return _fileSystem.Stat(GetSegmentPath(segId)).Length; }
        catch { return 0; }
    }

    /// <summary>段是否存在。</summary>
    private bool SegmentExists(int segId)
    {
        return _fileSystem.Exists(GetSegmentPath(segId));
    }

    // ═══════════════════════════════════════════════════════════════
    //  ICompact 实现
    // ═══════════════════════════════════════════════════════════════

    /// <inheritdoc/>
    public CompactStatus Status => (CompactStatus)Volatile.Read(ref _status);

    /// <inheritdoc/>
    public bool IsRunning => Volatile.Read(ref _compacting) != 0;

    /// <inheritdoc/>
    public IAsyncOperation<CompactResult> Compact(params CompactLease[] leases)
    {
        ThrowIfDisposed();
        if (leases is null || leases.Length == 0)
            throw new ArgumentException("leases 不能为空", nameof(leases));
        EnsureNoPendingCommitMarker();
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
            throw new InvalidOperationException("另一个 Compact 操作正在进行中");

        // ★ 统一后台操作句柄（2026-08-24）：AsyncOperation{TResult}——状态机/事件时序/取消/进度
        //   全收口在 Core 原语（旧 CompactOp 手写 TCS+事件+时序包装消亡）；取消链接 _cts
        //   （DefaultCompactor Dispose 时 cancel 在途整理）
        var op = new AsyncOperation<CompactResult>("compact", _logger, _cts.Token);
        RunCompactInBackground(new CompactTask(leases, op));   // ★ Task.Run 立即执行（排他保证单在途）
        return op;
    }

    /// <inheritdoc/>
    public void Recover(CompactLeaseFactory leaseFactory)
    {
        ThrowIfDisposed();
        // 读 marker → 补执行未完成的 Compact（Phase 2 崩溃恢复）
        RecoverCompactMarker(leaseFactory);
    }

    /// <inheritdoc/>
    public void Retry(CompactLeaseFactory leaseFactory)
    {
        ThrowIfDisposed();
        // ★ 运行时失败续传（2026-08-24 语义修正——用户裁定"失败重试调 Retry"）：
        //   现场保留契约下失败必有 marker——补执行（RecoverCompactMarker 同核心：临时文件 →
        //   promote → 段表替换 → 删 marker），零重拷贝、零强制等待。
        //   旧实现（_cachedRanges 重造 lease 重拷贝 + GetAwaiter().GetResult()）废除——
        //   与"一律后台句柄 / 失败不重拷贝"裁定一致；marker 不存在（Phase 1 失败已清理现场）
        //   = no-op（无续传目标，调用方重新发起 Compact）。
        RecoverCompactMarker(leaseFactory);
    }

    /// <inheritdoc/>
    public void Clear()
    {
        ThrowIfDisposed();
        // 删临时段 → 回空闲（无队列可清——单在途模型；在途任务继续跑完，
        // 其 wrapper 完成时幂等复位 _compacting，不干扰新 Compact）
        Volatile.Write(ref _compacting, 0);
        Volatile.Write(ref _status, (int)CompactStatus.Completed);
        try
        {
            DeleteAllTemps();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Compact.Clear: DeleteAllTemps 失败");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  后台执行（轻量单例——与 StartReclaim 同构；替代 BackgroundWorkerLoop）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 启动整理任务（Task.Run 公共池）——排他保证至多 1 个在途，无队列。
    /// <para>★ <see cref="RunCompactLifecycleSafe"/> 全 catch（异常只走 op 通知）——返回的 Task 永不 fault，
    ///   Dispose 等待只需观察完成。</para>
    /// </summary>
    private void RunCompactInBackground(CompactTask task)
    {
        Volatile.Write(ref _inFlight, Task.Run(() => RunCompactLifecycleSafe(task)));
    }

    /// <summary>启动任意后台整理执行体（RangeCompact 用——执行体自持异常隔离 + op 通知）。</summary>
    private void RunCompactInBackground(Action body)
    {
        Volatile.Write(ref _inFlight, Task.Run(body));
    }

    /// <summary>单个 Compact 生命周期（异常隔离 + 收尾 _compacting）——由后台任务（Task.Run）调。
    /// <para>★ 排他释放 happens-before 完成通知（L2 销案，2026-08-21）：op 完成通知（TrySetResult
    ///   唤醒等待者）原在 lifecycle 尾部、复位 <c>_compacting</c> 在本 wrapper finally——两者之间的
    ///   抢占窗口下，等待者苏醒后直奔下一次 Compact 撞 CAS 排他（"另一个 Compact 操作正在进行中"，
    ///   满负载偶发 flaky 根因）。故 lifecycle 只返回结果/抛异常，复位先于通知由本 wrapper 统一收口。</para></summary>
    private void RunCompactLifecycleSafe(CompactTask task)
    {
        CompactResult? result = null;
        Exception? failure = null;
        try
        {
            result = RunCompactLifecycle(task);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            Volatile.Write(ref _compacting, 0);   // ★ 先放排他——完成通知前（等待者苏醒即可重新入闸）
        }

        if (failure is not null)
        {
            // 单任务异常不杀 worker——通知 op 失败，继续处理下一个
            if (!task.Op.IsCompleted)
                task.Op.ReportFailed(failure);
        }
        else if (result is { } ok)
        {
            task.Op.ReportSucceeded(ok);
        }
    }



    // ═══════════════════════════════════════════════════════════════
    //  生命周期
    // ═══════════════════════════════════════════════════════════════

    private void ThrowIfDisposed()
    {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, GetType().Name);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        Dispose(true);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        await DisposeAsyncCore().ConfigureAwait(false);
    }

    /// <summary>
    /// 释放（2026-08-24 审查修复——Dispose 与在途任务的完整处理协议）。
    /// <para>★ 先抢 <see cref="_compacting"/> 排他（与 <see cref="Compact"/> 入闸同一把锁）：
    ///   抢到 = 无在途任务（此后新 Compact 入闸被 CAS 拒绝——不会启动新任务）；
    ///   没抢到 = 有在途——Cancel + 有界等待退出。</para>
    /// <para>★ 等 <c>_inFlight</c> 出现（任务启动与字段赋值的窗口——Volatile 读 + 有界自旋）。
    ///   5s 超时后任务仍可能在跑（长拷贝无法强杀）——<b>不 Dispose _cts</b>（任务还需取消信号
    ///   自行退出回滚），仅告警泄漏；引擎侧编排（门闩时序）保证正常路径 Dispose 前在途已完成。</para>
    /// <para>★ 兜底 <see cref="DeleteAllTemps"/>：任务正常退出已清理；超时/异常路径残留在此收口。</para>
    /// </summary>
    private void Dispose(bool disposing)
    {
        if (!disposing) return;

        // ★ 抢排他——抢到 = 无在途；没抢到 = 有在途需等待
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
        {
            // 有在途任务：取消 + 等退出
            try
            {
                _cts.Cancel();
            }
            catch
            {
                /* ignored */
            }

            // ★ 等 _inFlight 出现（Compact 已入闸但任务未赋值窗口）——有界自旋，任务可能因取消
            //   立即退出（未赋值即终）——自旋上限后放弃（取消已生效，任务自行退出回滚）。
            var spinner = new SpinWait();
            while (Volatile.Read(ref _inFlight) is null)
            {
                if (spinner.Count > 200) break;
                spinner.SpinOnce();
            }

            // ★ 有界等待退出（5s——对齐原 BackgroundWorkerLoop exitTimeout 语义）
            try
            {
                _inFlight?.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DefaultCompactor.Dispose: 等待在途整理退出超时/异常");
            }

            // ★ 超时后任务仍可能运行——不 Dispose _cts（任务还需取消信号），_cts 随对象 GC 回收
        }

        // ★ 兜底清理临时资源（正常路径任务已清；残留在此收口——句柄 + 临时文件）
        try
        {
            DeleteAllTemps();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DefaultCompactor.Dispose: DeleteAllTemps 兜底清理失败");
        }

        Volatile.Write(ref _compacting, 0);   // 复位（对象已死，状态一致）
        Volatile.Write(ref _inFlight, null);

        try
        {
            _cts.Dispose();
        }
        catch
        {
            /* ignored */
        }
    }

    /// <summary>异步释放——同 <see cref="Dispose(bool)"/> 语义（抢排他/等退出/兜底清理），
    /// 等待用异步 await（不阻塞调用线程）。</summary>
    private async ValueTask DisposeAsyncCore()
    {
        if (Interlocked.CompareExchange(ref _compacting, 1, 0) != 0)
        {
            try
            {
                await _cts.CancelAsync();
            }
            catch
            {
                /* ignored */
            }

            var spinner = new SpinWait();
            while (Volatile.Read(ref _inFlight) is null)
            {
                if (spinner.Count > 200) break;
                spinner.SpinOnce();
            }

            try
            {
                if (_inFlight is not null)
                    await _inFlight.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DefaultCompactor.DisposeAsync: 等待在途整理退出超时/异常");
            }
        }

        try
        {
            DeleteAllTemps();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DefaultCompactor.DisposeAsync: DeleteAllTemps 兜底清理失败");
        }

        Volatile.Write(ref _compacting, 0);
        Volatile.Write(ref _inFlight, null);
        try
        {
            _cts.Dispose();
        }
        catch
        {
            /* ignored */
        }
    }



    // ═══════════════════════════════════════════════════════════════
    //  CompactTask——队列任务（租约 + 统一后台操作句柄 AsyncOperation{TResult}）
    // ═══════════════════════════════════════════════════════════════

    /// <summary>CompactTask——队列任务（租约 + 统一后台操作句柄）。</summary>
    private readonly struct CompactTask
    {
        internal readonly CompactLease[] Leases;
        internal readonly AsyncOperation<CompactResult> Op;

        internal CompactTask(CompactLease[] leases, AsyncOperation<CompactResult> op)
        {
            Leases = leases;
            Op = op;
        }
    }
}
