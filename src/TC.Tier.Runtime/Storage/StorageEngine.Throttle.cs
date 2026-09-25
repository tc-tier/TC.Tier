namespace TC.Tier.Runtime.Storage;

/// <summary>
/// CPU 限流 partial——引擎持 <see cref="CpuSampler"/>，在地址分配 / append 热路径做 CPU 背压。
/// <para>★ 武装制：仅在 <see cref="StorageEngineOptimization.SampleInterval"/> 非 null 时构造/启动采样器
///   （null = 限流关闭——热路径经 <c>EffectiveThrottleFactor</c> 恒 0 直通，零构造零开销）。</para>
/// <para>★ 三档（按限流系数）：系数 0（CPU ≤ 下阈）正常放行；系数 >0 自旋等待 CPU 回落（段表
///   <c>AllocateRaw</c> 同款：deadline + ct + WarnEvery + <see cref="TimeoutException"/>）；
///   自旋超时 = 直接报错。自旋预算走 <see cref="StorageEngineOptimization.ThrottleSpinMilliseconds"/>
///   （与段表 <c>SpinMilliseconds</c> 解耦），运行时 floor 至采样周期——小于一个采样周期的
///   deadline 观察不到回落。</para>
/// <para>★ WarnEvery 复用 <see cref="StorageEngineOptimization.WarnEvery"/> 同名参数——段表自旋与限流自旋同款告警节奏。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    private CpuSampler? _cpuSampler;

    /// <summary>CPU 采样限流器——仅在限流武装（<see cref="StorageEngineOptimization.SampleInterval"/> 非 null）时存在：
    /// 懒构造并进 <see cref="LifecycleBase{THints}"/>.Resources 统一释放，恢复后 <c>Start</c>；
    /// 未武装态访问即 bug，fail-fast。Dispose 由 Resources 统一编排（CpuSampler 无组件依赖，
    /// 无需 DisposeOverride 手动释放）。</summary>
    private CpuSampler CpuSampler
    {
        get
        {
            if (_cpuSampler is not null) return _cpuSampler;
            var interval = _options.Optimization.SampleInterval
                ?? throw new InvalidOperationException("CPU 采样未武装（SampleInterval=null）——限流关闭态不得构造采样器。");
            _cpuSampler = new CpuSampler(
                sampleInterval: interval,
                throttleHighCutoff: _options.Optimization.ThrottleHighCutoff,
                throttleLowCutoff: _options.Optimization.ThrottleLowCutoff,
                emaAlpha: _options.Optimization.EmaAlpha,
                logger: Logger);
            Resources.Add(_cpuSampler, ownership: ResourceOwnership.Owned); // ★ 统一资源管理器释放
            return _cpuSampler;
        }
    }

    /// <summary>限流自旋 deadline（毫秒）——旋钮与采样周期的较大者：小于一个采样周期的
    /// deadline 等不到下一次采样发布，观察不到 CPU 回落。</summary>
    private long ThrottleSpinDeadlineMs
    {
        get
        {
            var opt = _options.Optimization;
            var sampleMs = (long)(opt.SampleInterval ?? TimeSpan.FromSeconds(1)).TotalMilliseconds;
            return Math.Max(opt.ThrottleSpinMilliseconds, sampleMs);
        }
    }

    /// <summary>
    /// ★ CPU 限流自旋IO 热路径（Append / AppendAsync / Allocate）入口调。
    /// <para>★ 系数 0（≤ 下阈，含限流未武装）立即放行；系数 >0 自旋等待 CPU 回落：</para>
    /// <list type="bullet">
    /// <item>deadline = ct 可取消 ? <see cref="long.MaxValue"/> : now + <see cref="ThrottleSpinDeadlineMs"/></item>
    /// <item>每 <see>
    ///         <cref>_options.Optimization.WarnEvery</cref>
    ///     </see>
    ///     次退避打一条 warning（不刷屏）</item>
    /// <item>CPU 回落（系数→0）→ 放行</item>
    /// <item>超时 → <see cref="TimeoutException"/>（直接报错）</item>
    /// <item>外部 ct 取消 → <see cref="OperationCanceledException"/></item>
    /// </list>
    /// <para>★ 与段表 <c>AllocateRaw</c> 同款自旋模式（同 TimeoutException、独立预算）。<see cref="SpinWait.SpinOnce()"/>
    ///   混合自旋/让步/睡眠，初期短自旋后续让出 CPU，不会纯空转拉高 CPU。</para>
    /// </summary>
    /// <param name="ct">外部取消令牌（同步 Append/Allocate 传 default——仅超时；异步 AppendAsync 传调用方 ct）。</param>
    private void EnsureCpuCapacity(CancellationToken ct)
    {
        // ★ 节流有效系数——故障注入 ThrottleSaturated 窗强制饱和（拒绝/自旋路径的确定性触发）
        if (EffectiveThrottleFactor <= 0.0) return; // 正常——放行
        var deadline = ct.CanBeCanceled
            ? long.MaxValue
            : _clock.GetMsTimestamp() + ThrottleSpinDeadlineMs;
        var spinner = new SpinWait();
        long attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!ct.CanBeCanceled && _clock.GetMsTimestamp() > deadline)
                throw new TimeoutException(
                    $"CPU 限流自旋超时 factor={EffectiveThrottleFactor} attempts={attempts}");
            if (EffectiveThrottleFactor <= 0.0) return; // CPU 回落——放行
            if (++attempts % _options.Optimization.WarnEvery == 0)
                Logger?.LogWarning("CPU 限流退避 factor={factor} util={util} attempts={attempts}",
                    EffectiveThrottleFactor, _cpuSampler?.CpuUtilization, attempts);
            spinner.SpinOnce();
        }
    }
}