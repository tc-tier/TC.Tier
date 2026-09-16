namespace TC.Tier.Core.Net.Channels;

/// <summary>
/// 请求回调投递语义·at-least-once 档（spec-12 §5.2——重传/确认是请求回调的附加能力，
/// 可靠 = 显式选择）：同 CorrId 重发 + 应答端去重窗口（识别已服务请求 → 缓存响应重放，
/// 不重复执行——handler 幂等是使用方约定，跨重启窗口不持久 §8.4）。
/// <para>★ 缺省不配置 = at-most-once（单发 + 超时）；数据报形态永不隐式重传。</para>
/// </summary>
/// <param name="MaxAttempts">总发送次数上限（含首次——缺省 3）。</param>
/// <param name="Backoff">每发等待应答窗口（窗口耗尽未应答即重发——缺省 200ms）。</param>
/// <param name="TotalBudget">总时限（缺省 null = MaxAttempts × Backoff 自然上界）。</param>
public sealed record RetryPolicy(
    int MaxAttempts = 3,
    TimeSpan Backoff = default,
    TimeSpan? TotalBudget = null)
{
    /// <summary>重发等待窗口缺省（200ms）。</summary>
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMilliseconds(200);

    /// <summary>生效等待窗口（default(TimeSpan) 视为未配置）。</summary>
    public TimeSpan EffectiveBackoff => Backoff == TimeSpan.Zero ? DefaultBackoff : Backoff;

    /// <summary>校验（装配期 fail-fast）。</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);
        if (EffectiveBackoff <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(Backoff), "重发等待窗口必须为正。");
        if (TotalBudget is { } budget && budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TotalBudget), "重试总时限必须为正。");
    }
}
