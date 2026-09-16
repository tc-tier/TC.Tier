namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 复制策略（spec-02 复制层配置化——条数/字节/时间三维度任一触发一次 AppendEntries 批量发送）。
/// <para>★ 默认全零 = 立即推送（append 即通知发送、窗口 1 流水线）。</para>
/// <para>★ 三维度语义：</para>
/// <para> - BatchSize：单次 AppendEntries 批上限（条数维度，默认 1000——spec-02 §2 攒批形态）</para>
/// <para> - BatchWindow：攒批时间窗口（时间维度——窗口内到达的 append 合并为一次发送；
///   0 = 立即推。真实网络下摊销 RTT 的关键参数；进程内场景收益小）</para>
/// <para> - MaxBatchBytes：批字节上限（字节维度；0 = 不限）</para>
/// <para>★ 窗口判定粒度 = 状态机 tick（5ms）——窗口配置自由，到期误差 ≤ tick 周期。</para>
/// </summary>
public sealed record ReplicationPolicy
{
    /// <summary>单次 AppendEntries 批上限（条数维度）。默认 1000。</summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>攒批时间窗口（时间维度）。0 = 立即推送（默认）。</summary>
    public TimeSpan BatchWindow { get; init; } = TimeSpan.Zero;

    /// <summary>lane 微攒批 linger（毫秒，0 = 关）：驱动循环首轮发送前等待片刻，让在途 append
    /// 落进同一批——亚 tick 粒度（不占状态机 5ms 判定窗），真实网络下摊销 per-RPC 开销。
    /// TCP/真实网络建议 1-2ms；进程内保持 0。</summary>
    public int LingerMilliseconds { get; init; }

    /// <summary>批字节上限（字节维度）。0 = 不限（默认）。</summary>
    public long MaxBatchBytes { get; init; }
}
