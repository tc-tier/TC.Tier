namespace TC.Tier.Runtime.Storage;

/// <summary>
/// CPU 限流 partial——引擎持 <see cref="CpuSampler"/>，在地址分配 / append 热路径做 CPU 背压。
/// <para>★ 三档（按 <see cref="CpuSampler.ThrottleFactor"/>）：系数 0（CPU ≤70%）正常放行；系数 >0
///   自旋等待 CPU 回落（段表 <c>AllocateRaw</c> 同款：deadline + ct + WarnEvery + <see cref="TimeoutException"/>）；
///   自旋超时 = 直接报错。</para>
/// <para>★ 自旋参数（SpinMilliseconds / WarnEvery）复用 <see>
///         <cref>_options.Optimization</cref>
///     </see>
///     ——
///   段表自旋与 CPU 限流自旋同款参数、同款模式。</para>
/// </summary>
internal sealed partial class StorageEngine
{
    private CpuSampler? _cpuSampler;

    /// <summary>CPU 采样限流器（懒构造，首次访问时进 <see cref="LifecycleBase{THints}"/>.Resources 统一释放）。
    /// 恢复后 <c>Start</c>；Dispose 由 Resources 统一编排（CpuSampler 无组件依赖，无需 DisposeOverride 手动释放）。</summary>
    private CpuSampler CpuSampler
    {
        get
        {
            if (_cpuSampler is not null) return _cpuSampler;
            _cpuSampler = new CpuSampler(
                sampleInterval: _options.Optimization.SampleInterval,
                throttleHighCutoff: _options.Optimization.ThrottleHighCutoff,
                throttleLowCutoff: _options.Optimization.ThrottleLowCutoff,
                emaAlpha: _options.Optimization.EmaAlpha,
                logger: Logger);
            Resources.Add(_cpuSampler, ownership: ResourceOwnership.Owned); // ★ 统一资源管理器释放
            return _cpuSampler;
        }
    }

    /// <summary>
    /// ★ CPU 限流自旋IO 热路径（Append / AppendAsync / Allocate）入口调。
    /// <para>★ 系数 0（CPU ≤70%）立即放行；系数 >0 自旋等待 CPU 回落：</para>
    /// <list type="bullet">
    /// <item>deadline = ct 可取消 ? <see cref="long.MaxValue"/> : now + <see>
    ///         <cref>_options.Optimization.SpinMilliseconds</cref>
    ///     </see>
    /// </item>
    /// <item>每 <see>
    ///         <cref>_options.Optimization.WarnEvery</cref>
    ///     </see>
    ///     次退避打一条 warning（不刷屏）</item>
    /// <item>CPU 回落（系数→0）→ 放行</item>
    /// <item>超时 → <see cref="TimeoutException"/>（直接报错）</item>
    /// <item>外部 ct 取消 → <see cref="OperationCanceledException"/></item>
    /// </list>
    /// <para>★ 与段表 <c>AllocateRaw</c> 同款自旋模式（同参数、同 TimeoutException）。<see cref="SpinWait.SpinOnce()"/>
    ///   混合自旋/让步/睡眠，初期短自旋后续让出 CPU，不会纯空转拉高 CPU。</para>
    /// </summary>
    /// <param name="ct">外部取消令牌（同步 Append/Allocate 传 default——仅超时；异步 AppendAsync 传调用方 ct）。</param>
    private void EnsureCpuCapacity(CancellationToken ct)
    {
        if (CpuSampler.ThrottleFactor <= 0.0) return; // 正常——放行
        var deadline = ct.CanBeCanceled
            ? long.MaxValue
            : Environment.TickCount64 + _options.Optimization.SpinMilliseconds;
        var spinner = new SpinWait();
        long attempts = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!ct.CanBeCanceled && Environment.TickCount64 > deadline)
                throw new TimeoutException(
                    $"CPU 限流自旋超时 factor={CpuSampler.ThrottleFactor} attempts={attempts}");
            if (CpuSampler.ThrottleFactor <= 0.0) return; // CPU 回落——放行
            if (++attempts % _options.Optimization.WarnEvery == 0)
                Logger?.LogWarning("CPU 限流退避 factor={factor} util={util} attempts={attempts}",
                    CpuSampler.ThrottleFactor, CpuSampler.CpuUtilization, attempts);
            spinner.SpinOnce();
        }
    }
}