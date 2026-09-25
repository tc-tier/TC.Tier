using System.Collections.Concurrent;

namespace TC.Tier.Runtime.Storage;

/// <summary>
/// 引擎故障注入 partial——件二引擎缝的挂载与实现（接口契约见 <see cref="IStorageEngineFaultInjector"/>）。
/// <para>★ 挂载面：各公开 op 入口一行哨兵分发（<c>_faults?.OnOpEnter(op)</c> / 异步 await 版）——
///   面关闭（缺省）一次空引用短路，面开启规则表为空时一次队列空短路（零开销缺省）。</para>
/// <para>★ 状态窗实现（借引擎内部真实机制，不虚构故障模型）：</para>
/// <list type="bullet">
/// <item>RecoveringWindow——恢复核心入口等待窗清（<see cref="WaitRecoveryWindowAsync"/>，Recovery partial 调）；
///   存续期间 op 入口按"尚未完成恢复"拒绝（对齐 EnsureReady 异常语义）。</item>
/// <item>CompactActive——<see cref="CompactLease"/> 排他占住 [MinAddress, 数据尾段段末)（占区间协议同真实
///   Compact，L19 扩段末语义；冲突写者经 AcquireExtent 自旋让位）。</item>
/// <item>ThrottleSaturated——<see cref="EffectiveThrottleFactor"/> 强制饱和（EnsureCpuCapacity 拒绝/自旋路径）。</item>
/// </list>
/// </summary>
internal sealed partial class StorageEngine
{
    /// <summary>故障注入器实例（<see cref="StorageEngineOptions.EnableFaultInjection"/> 开启时创建；null = 面关闭）。</summary>
    private FaultInjector? _faults;

    /// <summary>故障注入面（显式开启面——缺省 null；常设对抗面，非测试代码泄漏）。</summary>
    public IStorageEngineFaultInjector? Faults => _faults;

    /// <summary>节流有效系数——节流饱和窗强制饱和（EnsureCpuCapacity 拒绝/自旋路径的确定性触发），
    /// 否则取真实采样；限流未武装（SampleInterval=null，采样器不存在）时恒 0——故障窗不受影响。</summary>
    private double EffectiveThrottleFactor
        => _faults is { } f && f.IsState(EngineFaultState.ThrottleSaturated) ? 1.0 : _cpuSampler?.ThrottleFactor ?? 0.0;

    /// <summary>恢复窗等待——恢复核心入口调（RecoveringWindow 存续期间挂起恢复启动，Reset 放行）。</summary>
    private Task WaitRecoveryWindowAsync(CancellationToken ct)
        => _faults?.WaitRecoveryWindowAsync(ct) ?? Task.CompletedTask;

    /// <summary>Dispose 拆除——状态窗全退（排他占住先于段表释放）+ 挂起点放行（被挂线程不跨 Dispose 阻塞）。</summary>
    private void TeardownFaults()
    {
        try
        {
            _faults?.Reset();
        }
        catch (Exception ex)
        {
            Logger?.LogWarning(ex, "故障注入面拆除异常（engine={Engine}）", EngineName);
        }
    }

    /// <summary>引擎缝注入器实现（嵌套 private——直接操作段表/水位等引擎内部件）。</summary>
    private sealed class FaultInjector : IStorageEngineFaultInjector
    {
        private readonly StorageEngine _engine;
        private readonly Random _random = new(20260914); // seeded——概率注入可复现
        private readonly ConcurrentQueue<FaultRule> _failRules = new();
        private readonly ConcurrentQueue<FaultRule> _delayRules = new();
        private readonly ConcurrentQueue<FaultRule> _hangRules = new();
        private readonly object _stateGate = new();
        private readonly HashSet<EngineFaultState> _states = new();
        private CompactLease? _compactLease; // CompactActive 窗的排他占住（_stateGate 内读写）

        internal FaultInjector(StorageEngine engine) => _engine = engine;

        internal bool IsState(EngineFaultState state)
        {
            lock (_stateGate) return _states.Contains(state);
        }

        /// <summary>恢复窗等待——窗存续期间轮询挂起（Reset 放行；恢复 ct 取消即退出）。</summary>
        internal async Task WaitRecoveryWindowAsync(CancellationToken ct)
        {
            while (IsState(EngineFaultState.RecoveringWindow))
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(10, ct).ConfigureAwait(false);
            }
        }

        /// <summary>op 入口哨兵（同步版）——状态窗拒绝 → 失败规则抛 → 延迟规则睡 → 挂起规则阻塞。</summary>
        internal void OnOpEnter(string op)
        {
            if (_failRules.IsEmpty && _delayRules.IsEmpty && _hangRules.IsEmpty && !HasAnyState) return;
            ThrowIfRecoveringWindow(op);
            foreach (var rule in _failRules)
            {
                if (!ShouldFire(rule, op, out var n)) continue;
                var error = rule.Error ?? IOError.IOFailure;
                throw new FileIOException(error,
                    $"[EngineFaultInject] 注入 {error}（engine={_engine.EngineName}, op={op}, match#{n}）", null, op);
            }
            foreach (var rule in _delayRules)
            {
                if (!ShouldFire(rule, op, out _)) continue;
#pragma warning disable TCSG137 // 故障注入语义本体：同步前置延迟即 Thread.Sleep（确定性触发上层 watchdog/有界等待）
                Thread.Sleep(rule.Delay!.Value);
#pragma warning restore TCSG137
            }
            foreach (var rule in _hangRules)
            {
                if (!ShouldFire(rule, op, out _)) continue;
#pragma warning disable TCSG137 // 故障注入语义本体：永挂（释放仅供 Reset 拆除）——上层须以自身超时/取消路径先行处置
                rule.Gate!.Task.Wait();
#pragma warning restore TCSG137
            }
        }

        /// <summary>op 入口哨兵（异步版）——延迟走异步等、挂起等释放门（外部 ct 可取消 = 上层有界等待路径）。</summary>
        internal async Task OnOpEnterAsync(string op, CancellationToken ct)
        {
            if (_failRules.IsEmpty && _delayRules.IsEmpty && _hangRules.IsEmpty && !HasAnyState) return;
            ThrowIfRecoveringWindow(op);
            foreach (var rule in _failRules)
            {
                if (!ShouldFire(rule, op, out var n)) continue;
                var error = rule.Error ?? IOError.IOFailure;
                throw new FileIOException(error,
                    $"[EngineFaultInject] 注入 {error}（engine={_engine.EngineName}, op={op}, match#{n}）", null, op);
            }
            foreach (var rule in _delayRules)
            {
                if (!ShouldFire(rule, op, out _)) continue;
                await Task.Delay(rule.Delay!.Value, ct).ConfigureAwait(false);
            }
            foreach (var rule in _hangRules)
            {
                if (!ShouldFire(rule, op, out _)) continue;
                await rule.Gate!.Task.WaitAsync(ct).ConfigureAwait(false);
            }
        }

        private bool HasAnyState
        {
            get
            {
                lock (_stateGate) return _states.Count > 0;
            }
        }

        /// <summary>恢复窗存续期间的 op 拒绝（对齐 EnsureReady 异常类型——上层容忍路径可统一捕获）。</summary>
        private void ThrowIfRecoveringWindow(string op)
        {
            if (IsState(EngineFaultState.RecoveringWindow))
                throw new InvalidOperationException(
                    $"{_engine.EngineName} 处于注入恢复窗（EngineFaultState.RecoveringWindow），不可读写（op={op}）");
        }

        /// <summary>规则命中判定（含 MatchCount 记账与概率/atCallIndex 仲裁——与 fs 脸同语义）。</summary>
        private bool ShouldFire(FaultRule rule, string op, out long n)
        {
            if (rule.Pattern != "*" && rule.Pattern != op)
            {
                n = 0;
                return false;
            }
            n = Interlocked.Increment(ref rule.MatchCount);
            if (rule.AtCallIndex is { } idx) return n == idx;
            return _random.NextDouble() < rule.Probability;
        }

        private static void ValidateProbability(double probability)
        {
            if (probability is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(probability));
        }

        /// <inheritdoc/>
        public void FailOn(string opPattern, IOError? error = null, double probability = 1, long? atCallIndex = null)
        {
            ValidateProbability(probability);
            _failRules.Enqueue(new FaultRule
            {
                Pattern = opPattern,
                Error = error,
                Probability = probability,
                AtCallIndex = atCallIndex,
            });
        }

        /// <inheritdoc/>
        public void DelayOn(string opPattern, TimeSpan delay, double probability = 1)
        {
            ValidateProbability(probability);
            _delayRules.Enqueue(new FaultRule
            {
                Pattern = opPattern,
                Delay = delay,
                Probability = probability,
            });
        }

        /// <inheritdoc/>
        public void HangOn(string opPattern)
        {
            _hangRules.Enqueue(new FaultRule
            {
                Pattern = opPattern,
                Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            });
        }

        /// <inheritdoc/>
        public void EnterState(EngineFaultState state)
        {
            lock (_stateGate)
            {
                if (!_states.Add(state)) return; // 幂等——已在该窗则 no-op
                if (state == EngineFaultState.CompactActive)
                    _compactLease = AcquireCompactActiveLease();
            }
        }

        /// <summary>Compact 活动窗的排他占住——[MinAddress, 数据尾段段末)（占区间协议同真实 Compact：
        /// L19 扩段末阻断贴边追加；冲突写者经 AcquireExtent 自旋让位）。</summary>
        private CompactLease AcquireCompactActiveLease()
        {
            var from = _engine.MinAddress;
            var tail = _engine.CommittedTail;
            var to = tail;
            if (_engine._segmentTable.TryGetSegment(tail.SegId, out var view) && view is { IsValid: true })
                to = new LogicalAddress(tail.SegId, view.Value.GrowthLimit);
            return _engine._segmentTable.CompactLease(from, to);
        }

        /// <inheritdoc/>
        public void Reset()
        {
            while (_failRules.TryDequeue(out _)) { }
            while (_delayRules.TryDequeue(out _)) { }
            while (_hangRules.TryDequeue(out var hang)) hang.Gate?.TrySetResult();
            lock (_stateGate)
            {
                _states.Clear();
                if (_compactLease is { } lease)
                {
                    _compactLease = null;
                    try
                    {
                        lease.Dispose();
                    }
                    catch
                    {
                        // 拆除竞态（段表已拆）——Reset 不因状态窗释放失败中断
                    }
                }
            }
        }

        /// <summary>注入规则（三族共用载体——按族进入各自队列，家族字段互斥）。</summary>
        private sealed class FaultRule
        {
            /// <summary>op 名匹配（精确名或 "*"）。</summary>
            public required string Pattern { get; init; }

            /// <summary>失败规则的错误码（null = 抛出时落 IOFailure）。</summary>
            public IOError? Error { get; init; }

            /// <summary>延迟规则的时长；其它族为 null。</summary>
            public TimeSpan? Delay { get; init; }

            /// <summary>挂起规则的释放门（Reset 置位——仅供拆除）；其它族为 null。</summary>
            public TaskCompletionSource? Gate { get; init; }

            /// <summary>注入概率 [0,1]（默认 1 = 恒注入；与 AtCallIndex 互斥——都设时确定性优先）。</summary>
            public double Probability { get; init; } = 1;

            /// <summary>确定性注入：第 N 次（1 起）匹配调用注入一次（null = 概率模式）。</summary>
            public long? AtCallIndex { get; init; }

            public long MatchCount;
        }
    }
}
