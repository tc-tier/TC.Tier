namespace TC.Tier.Core.Net.Raft;

/// <summary>
/// 复制应答档（spec-12 §6 增量——复制家族设计件 A）：完成等待者的水位选择。
/// <para>档位只影响"该等待者何时被完成"，不影响日志复制/commitIndex 推进/apply 管道本身
/// （commitIndex 照旧由多数派推进）。</para>
/// </summary>
public enum ReplicationAck
{
    /// <summary>多数派档（缺省）：等待者按 committed/applied 水位完成——现状语义不变。</summary>
    Majority,

    /// <summary>Leader 本地档：等待者在本地持久化（fsync 组提交）完成后即完成——不等多数派复制。
    /// <para>★ 返回的 index 不保证最终在多数派日志中（可能随换届回退）——leader 宕机时
    /// 未复制到任何其他成员的尾部写丢失（异步复制的物理边界）。调用方据此建模。</para></summary>
    LeaderLocal,
}
