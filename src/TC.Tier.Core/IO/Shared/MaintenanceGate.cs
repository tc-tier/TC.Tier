namespace TC.Tier.Core.IO.Shared;

/// <summary>
/// 根空间维护门闩核心（三介质共享内嵌件）——CAS 闭门 + 变异在途计数 + 双检防竞态。
/// <para>★ 状态机：<c>Open(0) → WritesRejected(1) / AllRejected(2) → Open(0)</c>——同一时刻至多一个租约（非重入，
///   二次 Enter 抛 <see cref="FileIOException"/>(<see cref="IOError.UnderMaintenance"/>)）。</para>
/// <para>★ 静默协议（设计 §8）：Enter = 闭门 → 轮询等待在途变异归零 → 返回 RAII 租约；Dispose = 开门。
///   变更计数只覆盖<b>变异操作</b>（fs 命名空间变更 + 句柄写族）——读不计数：读不影响采集一致性，
///   滑过一次无害（scope=All 时读被入口检查拒绝，但已开始的读不等待）。</para>
/// <para>★ 竞态封闭（双检）：<see cref="BeginMutation"/> 先查状态再计数、计数后<b>复查</b>状态——
///   Enter 的等待看到计数归零时，任何已过初检的变异者要么已计数（被复查拦截）、要么尚未计数（同样被拦截）。</para>
/// <para>★ 在途收敛是等待不是强制：消费者须先自行收敛业务在途（§8.2 消费者契约），门闩只兜数据面；
///   等待仅受 <c>ct</c> 取消（消费者违约挂死是其责任，非门闩责任）。</para>
/// </summary>
internal sealed class MaintenanceGate
{
    private const int OpenState = 0;
    private const int WritesRejected = 1;
    private const int AllRejected = 2;

    private int _state;              // Open → 维护态 → Open（CAS 单调）
    private int _inFlightMutations;  // 变异在途计数（Enter 等待归零的基准）
    private string? _reason;         // 诊断（当前租约理由）

    /// <summary>是否处于维护态（任一 scope）。</summary>
    public bool IsUnderMaintenance => Volatile.Read(ref _state) != OpenState;

    /// <summary>当前租约理由（非维护态为 null；诊断用）。</summary>
    public string? Reason => Volatile.Read(ref _state) == OpenState ? null : _reason;

    /// <summary>
    /// 进入维护态——闭门（CAS）→ 等待在途变异归零（1ms 轮询，ct 可取消）→ 返回租约（Dispose 开门）。
    /// 已在维护态 → 抛 <see cref="IOError.UnderMaintenance"/>（非重入）。
    /// </summary>
    public IDisposable Enter(string reason, MaintenanceScope scope, CancellationToken ct)
    {
        var target = scope == MaintenanceScope.AllOperations ? AllRejected : WritesRejected;
        var current = Interlocked.CompareExchange(ref _state, target, OpenState);
        if (current != OpenState)
            throw CreateMaintenanceException(nameof(Enter), null,
                $"根空间已在维护中（reason={_reason}）——门闩非重入，须先释放现有租约。");

        _reason = reason;
        try
        {
            // 等待在途变异归零：初检已过的变异者会被其计数后的复查拦截（双检协议），此处只需等已计数者退出。
            var spin = new SpinWait();
            while (Volatile.Read(ref _inFlightMutations) != 0)
            {
                ct.ThrowIfCancellationRequested();
                if (spin.NextSpinWillYield) Thread.Sleep(1);
                else spin.SpinOnce();
            }
        }
        catch
        {
            Volatile.Write(ref _state, OpenState);   // 等待被取消——回滚闭门，现场可恢复
            _reason = null;
            throw;
        }
        return new Lease(this);
    }

    /// <summary>
    /// 变异操作入口——通过则登记在途计数（Dispose 退出）；门已闭则抛 <see cref="IOError.UnderMaintenance"/>。
    /// 双检：登记后复查状态，封闭"初检通过 → Enter 闭门 → 迟到计数"竞态。
    /// </summary>
    public MutationScope BeginMutation(string operation, string? path)
    {
        if (Volatile.Read(ref _state) != OpenState)
            throw CreateMaintenanceException(operation, path, ScopeMessage());
        Interlocked.Increment(ref _inFlightMutations);
        if (Volatile.Read(ref _state) != OpenState)
        {
            Interlocked.Decrement(ref _inFlightMutations);   // 复查撞闭门——回滚计数并拒绝
            throw CreateMaintenanceException(operation, path, ScopeMessage());
        }
        return new MutationScope(this);
    }

    /// <summary>读操作入口（scope=All 时拒绝；WriteOperations 档放行）——不计数，不等待。</summary>
    public void ThrowIfReadsRejected(string operation, string? path)
    {
        if (Volatile.Read(ref _state) == AllRejected)
            throw CreateMaintenanceException(operation, path,
                "维护 scope=AllOperations——读写全部拒绝，须待租约释放。");
    }

    private string ScopeMessage() => Volatile.Read(ref _state) == AllRejected
        ? "维护 scope=AllOperations——读写全部拒绝。"
        : "维护 scope=WriteOperations——写操作拒绝（读放行）。";

    private static FileIOException CreateMaintenanceException(string operation, string? path, string detail)
        => new(IOError.UnderMaintenance, $"{operation} 被维护门闩拒绝：{detail}", path, operation);

    private void Release()
    {
        Interlocked.Exchange(ref _state, OpenState);
        _reason = null;
    }

    /// <summary>在途变异登记句柄（using 作用域 = 变异全程，含异步方法的 await 段）。</summary>
    public readonly struct MutationScope : IDisposable
    {
        private readonly MaintenanceGate _gate;

        internal MutationScope(MaintenanceGate gate) => _gate = gate;

        /// <summary>退出在途计数。</summary>
        public void Dispose() => Interlocked.Decrement(ref _gate._inFlightMutations);
    }

    /// <summary>维护租约——Dispose 开门（幂等：双重 Dispose 只开门一次）。</summary>
    private sealed class Lease : IDisposable
    {
        private MaintenanceGate? _gate;

        internal Lease(MaintenanceGate gate) => _gate = gate;

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            gate?.Release();
        }
    }
}
