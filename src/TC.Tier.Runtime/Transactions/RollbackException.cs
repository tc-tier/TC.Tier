namespace TC.Tier.Runtime.Transactions;

/// <summary>
/// ★ 协调回合回滚异常——Session 管理层回执"本回合已按协议回滚"的规范形态
/// （复制决策 false/超时/异常 → Abort 已 Prepare 者 → 本异常回执；见 session-manager-design.md §6）。
/// <para>语义：结构侧悬干已截断（D2 边界回退），调用方可安全重试（staged 缓冲已清空——重试须重新 Stage）。</para>
/// </summary>
public sealed class RollbackException : Exception
{
    /// <summary>本回合占用的域 seq（已随回滚作废——批合并下同批回合共享同一作废 seq）。</summary>
    public long Seq { get; }

    public RollbackException(long seq, string message, Exception? inner = null)
        : base(message, inner)
    {
        Seq = seq;
    }
}
